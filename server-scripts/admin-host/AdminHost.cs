// AdminHost -- a drop-in OpenDAoC script that lets operator tools read and change the RUNNING server.
//
// Design: docs/design/admin-host.md. Requests arrive as one JSON object per line on a Unix domain
// socket (mode 0600, so only the server's own user can connect: ssh is the authentication):
//
//   {"id":"7","actor":"joe","op":"player.spec.set","args":{"name":"LabChar","spec":"Slash","level":5}}
//   {"id":"7","ok":true,"result":{...}}      or      {"id":"7","ok":false,"error":{"code":"...","message":"..."}}
//
// Every operation runs on the game loop: the host posts one action per tick to ClientService, whose
// posted actions finish before any client's packets for that tick are handled. Every change is
// audited, one JSON line per request in logs/admin-host.jsonl.
//
// Install: copy this file into the server's scripts/ folder and restart. Socket path:
// <server root>/admin.sock, or the OPENDAOC_ADMIN_SOCKET environment variable. Client: tools/adminctl.py.
//
// Only System.* types that Core itself loads are used: a script that references an assembly Core
// hasn't loaded at script-compile time (System.Text.Json, Newtonsoft.Json) breaks script compilation
// for the whole server. Hence the small JSON reader and writer at the end of this file.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DOL.Database;
using DOL.Events;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;

namespace OpenDaocTools.Scripts
{
    public static class AdminHost
    {
        private static readonly DOL.Logging.Logger log = DOL.Logging.LoggerManager.Create(typeof(AdminHost));

        private const int MaxLineBytes = 64 * 1024;
        private const int MaxConnections = 8;
        private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(5);

        private static Socket _listener;
        private static string _socketPath;
        private static string _auditPath;
        private static readonly object _auditLock = new();
        private static int _connections;
        private static volatile bool _stopping;

        // ---- lifecycle ---------------------------------------------------------------------------

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            if (OperatingSystem.IsWindows())
            {
                // The socket's file mode is the access control, and Windows can't set one.
                log.Warn("AdminHost not started: it needs a Unix file mode on its socket (Linux or macOS)");
                return;
            }

            try
            {
                string root = GameServer.Instance.Configuration.RootDirectory;
                _socketPath = Environment.GetEnvironmentVariable("OPENDAOC_ADMIN_SOCKET");
                if (string.IsNullOrWhiteSpace(_socketPath))
                    _socketPath = Path.Combine(root, "admin.sock");
                Directory.CreateDirectory(Path.Combine(root, "logs"));
                _auditPath = Path.Combine(root, "logs", "admin-host.jsonl");

                if (File.Exists(_socketPath))
                    File.Delete(_socketPath); // left over from a crash; a live server would hold it open

                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
                // Restrict before Listen(): nobody can connect until the socket listens.
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _listener.Listen(MaxConnections);

                var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "AdminHost" };
                thread.Start();
                log.Info("AdminHost listening on " + _socketPath + " (audit: " + _auditPath + ")");
            }
            catch (Exception ex)
            {
                // Never take the server down: without the host it runs as before.
                log.Error("AdminHost failed to start; the server runs without it", ex);
            }
        }

        [GameServerStoppedEvent]
        public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
        {
            _stopping = true;
            try { _listener?.Close(); } catch { }
            try { if (_socketPath != null && File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
        }

        private static void AcceptLoop()
        {
            while (!_stopping)
            {
                Socket conn;
                try { conn = _listener.Accept(); }
                catch (Exception) { if (_stopping) return; continue; }

                if (Interlocked.Increment(ref _connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref _connections);
                    try { conn.Close(); } catch { }
                    continue;
                }

                new Thread(() => Serve(conn)) { IsBackground = true, Name = "AdminHost conn" }.Start();
            }
        }

        // ---- one connection ----------------------------------------------------------------------

        private static void Serve(Socket conn)
        {
            int peerUid = PeerUid(conn);
            try
            {
                using var stream = new NetworkStream(conn, ownsSocket: true);
                var buffer = new List<byte>();
                var chunk = new byte[4096];
                bool overlong = false;

                while (!_stopping)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) return;

                    for (int i = 0; i < n; i++)
                    {
                        if (chunk[i] != (byte) '\n')
                        {
                            if (buffer.Count < MaxLineBytes) buffer.Add(chunk[i]);
                            else overlong = true;
                            continue;
                        }

                        string line = Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
                        buffer.Clear();
                        string reply = overlong
                            ? Error(null, "bad_request", "line longer than " + MaxLineBytes + " bytes")
                            : Handle(line, peerUid);
                        overlong = false;
                        if (reply == null) continue; // blank line

                        byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                // client went away
            }
            catch (Exception ex)
            {
                log.Error("AdminHost connection failed", ex);
            }
            finally
            {
                Interlocked.Decrement(ref _connections);
            }
        }

        // SO_PEERCRED (Linux): struct ucred { pid_t pid; uid_t uid; gid_t gid; }. -1 where unavailable.
        private static int PeerUid(Socket s)
        {
            try
            {
                var cred = new byte[12];
                int len = s.GetRawSocketOption(1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, cred);
                return len >= 8 ? BitConverter.ToInt32(cred, 4) : -1;
            }
            catch { return -1; }
        }

        // ---- one request -------------------------------------------------------------------------

        private sealed class OpError : Exception
        {
            public readonly string Code;
            public OpError(string code, string message) : base(message) { Code = code; }
        }

        private sealed class Request
        {
            public object Id;
            public string Actor;
            public string Op;
            public Dictionary<string, object> Args;
            public int PeerUid;
        }

        private static string Handle(string line, int peerUid)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            Request req;
            try
            {
                if (Json.Parse(line) is not Dictionary<string, object> obj)
                    return Error(null, "bad_request", "expected a JSON object");
                req = new Request
                {
                    Id = obj.TryGetValue("id", out var id) ? id : null,
                    Actor = obj.TryGetValue("actor", out var a) ? a as string : null,
                    Op = obj.TryGetValue("op", out var op) ? op as string : null,
                    Args = obj.TryGetValue("args", out var ar) && ar is Dictionary<string, object> d ? d : new(),
                    PeerUid = peerUid,
                };
            }
            catch (FormatException ex)
            {
                return Error(null, "bad_request", "invalid JSON: " + ex.Message);
            }

            if (string.IsNullOrEmpty(req.Op))
                return Error(req.Id, "bad_request", "missing \"op\"");
            if (!Ops.TryGetValue(req.Op, out var spec))
                return Error(req.Id, "unknown_op", "unknown op '" + req.Op + "'; known: " + string.Join(", ", Ops.Keys));

            Job job = new(() => spec.Run(req));
            Enqueue(job);

            if (!job.Done.Wait(JobTimeout))
            {
                // Not started yet: it never will be. Already running: its result (and audit line) still lands.
                bool cancelled = job.TryCancel();
                return Error(req.Id, "timeout", cancelled
                    ? "the game loop didn't run the request within " + JobTimeout.TotalSeconds + " s; nothing was changed"
                    : "the request is still running on the game loop; check the audit log for its result");
            }

            if (job.Failure is OpError oe)
                return Error(req.Id, oe.Code, oe.Message);
            if (job.Failure != null)
            {
                log.Error("AdminHost op " + req.Op + " failed", job.Failure);
                return Error(req.Id, "failed", job.Failure.GetType().Name + ": " + job.Failure.Message);
            }

            return Json.Write(new Dictionary<string, object> { ["id"] = req.Id, ["ok"] = true, ["result"] = job.Result });
        }

        private static string Error(object id, string code, string message) =>
            Json.Write(new Dictionary<string, object>
            {
                ["id"] = id, ["ok"] = false,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message },
            });

        // ---- running on the game loop ------------------------------------------------------------

        private sealed class Job
        {
            private readonly Func<object> _work;
            private int _state; // 0 queued, 1 running, 2 cancelled
            public readonly ManualResetEventSlim Done = new(false);
            public object Result;
            public Exception Failure;

            public Job(Func<object> work) { _work = work; }

            public bool TryCancel() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

            public void Run()
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return; // cancelled by a timeout
                try { Result = _work(); }
                catch (Exception ex) { Failure = ex; }
                finally { Done.Set(); }
            }
        }

        private static readonly ConcurrentQueue<Job> _jobs = new();
        private static int _drainPosted;

        private static void Enqueue(Job job)
        {
            _jobs.Enqueue(job);
            // One posted action per tick: posted actions run in parallel with each other, so the host
            // keeps its own requests in order by draining its queue inside a single action.
            if (Interlocked.CompareExchange(ref _drainPosted, 1, 0) == 0)
                ClientService.Instance.Post(static _ => Drain(), 0);
        }

        private static void Drain()
        {
            Volatile.Write(ref _drainPosted, 0);
            int count = _jobs.Count; // later arrivals wait for the next tick's drain
            for (int i = 0; i < count && _jobs.TryDequeue(out Job job); i++)
                job.Run();
            if (!_jobs.IsEmpty && Interlocked.CompareExchange(ref _drainPosted, 1, 0) == 0)
                ClientService.Instance.Post(static _ => Drain(), 0);
        }

        // ---- operations --------------------------------------------------------------------------

        private sealed class OpSpec
        {
            public Func<Request, object> Run;
        }

        private static readonly Dictionary<string, OpSpec> Ops = new()
        {
            ["server.status"] = new OpSpec { Run = ServerStatus },
            ["player.list"] = new OpSpec { Run = PlayerList },
            ["player.get"] = new OpSpec { Run = PlayerGet },
            ["player.spec.set"] = new OpSpec { Run = PlayerSpecSet },
            ["cache.reload"] = new OpSpec { Run = CacheReload },
        };

        private static object ServerStatus(Request req)
        {
            GameServer gs = GameServer.Instance;
            List<GamePlayer> players = ClientService.Instance.GetPlayers();
            return new Dictionary<string, object>
            {
                ["name"] = gs.Configuration.ServerName,
                ["status"] = gs.ServerStatus.ToString(),
                ["startedAt"] = gs.StartupTime.ToUniversalTime().ToString("o"),
                ["uptimeSeconds"] = (long) (DateTime.Now - gs.StartupTime).TotalSeconds,
                ["playersOnline"] = players.Count,
            };
        }

        private static object PlayerList(Request req) =>
            ClientService.Instance.GetPlayers()
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => (object) new Dictionary<string, object>
                {
                    ["name"] = p.Name,
                    ["account"] = p.Client?.Account?.Name,
                    ["level"] = (int) p.Level,
                    ["class"] = p.CharacterClass?.Name,
                    ["realm"] = RealmName(p.Realm),
                    ["region"] = (int) p.CurrentRegionID,
                    ["zone"] = p.CurrentZone?.Description,
                })
                .ToList();

        private static object PlayerGet(Request req)
        {
            GamePlayer p = OnlinePlayer(req);
            return new Dictionary<string, object>
            {
                ["name"] = p.Name,
                ["account"] = p.Client?.Account?.Name,
                ["level"] = (int) p.Level,
                ["class"] = p.CharacterClass?.Name,
                ["realm"] = RealmName(p.Realm),
                ["money"] = p.GetCurrentMoney(),
                ["realmPoints"] = p.RealmPoints,
                // Core's VerifySpecPoints respecs an ordinary player whose total is negative; the
                // client's own points update reads it the same way, so reading it here adds no risk.
                ["specPoints"] = p.SkillSpecialtyPoints,
                ["specs"] = p.GetSpecList()
                    .Select(s => (object) new Dictionary<string, object>
                    {
                        ["key"] = s.KeyName, ["name"] = s.Name, ["level"] = s.Level, ["trainable"] = s.Trainable,
                    })
                    .ToList(),
            };
        }

        // Same calls as Core's trainer (commands/playercommands/train.cs), minus the trainer window.
        private static object PlayerSpecSet(Request req)
        {
            GamePlayer p = OnlinePlayer(req);
            string specName = StringArg(req, "spec");
            int level = IntArg(req, "level");

            Specialization spec = p.GetSpecializationByName(specName);
            if (spec == null)
                throw new OpError("not_found", p.Name + " has no spec '" + specName + "'");
            if (!spec.Trainable)
                throw new OpError("invalid", "'" + spec.KeyName + "' is not a trainable spec");
            if (level < 1 || level > p.BaseLevel)
                throw new OpError("invalid", "level must be 1 to " + p.BaseLevel + " (the player's level)");

            int before = spec.Level;
            if (level == before)
                return SpecResult(p, spec, before, saved: false, changed: false);

            if (level > before)
            {
                // Spec points are computed, not stored. Core's VerifySpecPoints respecs ALL lines of an
                // ordinary player whose total goes negative, so check the cost before changing anything.
                int available = p.SkillSpecialtyPoints;
                int cost = UsedPoints(p, spec, level) - UsedPoints(p, spec, before);
                if (cost > available)
                    throw new OpError("invalid", "raising " + spec.KeyName + " from " + before + " to " + level +
                        " costs " + cost + " spec points; " + p.Name + " has " + available);
            }

            spec.Level = level;
            if (level > before)
                p.CharacterClass.OnSkillTrained(p, spec);
            p.RefreshSpecDependantSkills(true);
            p.Out.SendUpdatePoints();
            p.Out.SendUpdatePlayer();
            p.Out.SendCharResistsUpdate();
            p.Out.SendCharStatsUpdate();
            p.Out.SendUpdatePlayerSkills(true);
            p.Out.SendMessage("An administrator set your " + spec.Name + " to " + level + ".",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            p.SaveIntoDatabase();

            object result = SpecResult(p, spec, before, saved: true, changed: true);
            Audit(req, before, level, result);
            return result;
        }

        // A trainable spec's share of used points, as VerifySpecPoints counts it (GamePlayer.cs).
        private static int UsedPoints(GamePlayer p, Specialization spec, int level)
        {
            int saved = spec.Level;
            try
            {
                spec.Level = level; // GetAutoTrainPoints(spec, 0) reads spec.Level; mode 0 has no side effects
                return (level * (level + 1) - 2) / 2 - p.GetAutoTrainPoints(spec, 0);
            }
            finally { spec.Level = saved; }
        }

        private static object SpecResult(GamePlayer p, Specialization spec, int before, bool saved, bool changed) =>
            new Dictionary<string, object>
            {
                ["name"] = p.Name, ["spec"] = spec.KeyName, ["before"] = before, ["after"] = spec.Level,
                ["changed"] = changed, ["specPoints"] = p.SkillSpecialtyPoints, ["saved"] = saved,
            };

        private static object CacheReload(Request req)
        {
            string what = StringArg(req, "what").ToLowerInvariant();
            string note = null;
            switch (what)
            {
                case "loot-templates": LootGeneratorTemplate.ReloadLootTemplates(); break;
                case "loot-mobtemplates": LootGeneratorMobTemplate.ReloadLootTemplates(); break;
                case "loot-otd": LootGeneratorOneTimeDrop.ReloadLootOTD(); break;
                case "quests": GameObject.FillDataQuestCache(); break;
                case "npctemplates": NpcTemplateMgr.Reload(); break;
                case "spells": SkillBase.ReloadSpells(); SkillBase.ReloadSpellLines(); break;
                case "properties":
                    Properties.Refresh();
                    note = "some properties are copied into static fields when their class loads and change only " +
                           "at restart (the ROG_* item-generation settings and the server-list login, in Core 0ac41d443)";
                    break;
                case "item":
                    string id = StringArg(req, "id");
                    if (!GameServer.Database.UpdateInCache<DbItemTemplate>(id))
                        throw new OpError("not_found", "no item template '" + id + "' in the database");
                    break;
                default:
                    throw new OpError("bad_request", "what must be one of: loot-templates, loot-mobtemplates, loot-otd, " +
                        "quests, npctemplates, spells, properties, item");
            }

            var result = new Dictionary<string, object> { ["reloaded"] = what };
            if (what == "item") result["id"] = req.Args["id"];
            if (note != null) result["note"] = note;
            Audit(req, null, null, result);
            return result;
        }

        // ---- helpers -----------------------------------------------------------------------------

        // eRealm has alias names (_FirstPlayerRealm = Albion), so ToString() can't be used.
        private static string RealmName(eRealm realm) => (int) realm switch
        {
            1 => "Albion", 2 => "Midgard", 3 => "Hibernia", _ => realm.ToString(),
        };

        private static GamePlayer OnlinePlayer(Request req)
        {
            string name = StringArg(req, "name");
            GamePlayer p = ClientService.Instance.GetPlayerByExactName(name);
            if (p == null)
                throw new OpError("not_online", name + " is not online");
            return p;
        }

        private static string StringArg(Request req, string key)
        {
            if (!req.Args.TryGetValue(key, out object v) || v is not string s || s.Length == 0)
                throw new OpError("bad_request", "args." + key + " (string) is required");
            return s;
        }

        private static int IntArg(Request req, string key)
        {
            if (!req.Args.TryGetValue(key, out object v) || v is not double d || d != Math.Floor(d) ||
                d < int.MinValue || d > int.MaxValue)
                throw new OpError("bad_request", "args." + key + " (integer) is required");
            return (int) d;
        }

        private static void Audit(Request req, object before, object after, object result)
        {
            string line = Json.Write(new Dictionary<string, object>
            {
                ["time"] = DateTime.UtcNow.ToString("o"),
                ["peerUid"] = req.PeerUid,
                ["actor"] = req.Actor,
                ["op"] = req.Op,
                ["args"] = req.Args,
                ["before"] = before,
                ["after"] = after,
                ["result"] = result,
            });
            log.Info("AdminHost " + line);
            try
            {
                lock (_auditLock)
                    File.AppendAllText(_auditPath, line + "\n");
            }
            catch (Exception ex)
            {
                log.Error("AdminHost could not write the audit log " + _auditPath, ex);
            }
        }

        // ---- minimal JSON ------------------------------------------------------------------------
        // Objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double,
        // plus string, bool and null. Enough for requests; strict about syntax.

        private static class Json
        {
            public static object Parse(string text)
            {
                int i = 0;
                object v = Value(text, ref i);
                Ws(text, ref i);
                if (i != text.Length) throw new FormatException("trailing characters at " + i);
                return v;
            }

            private static void Ws(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            }

            private static object Value(string s, ref int i)
            {
                Ws(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end");
                char c = s[i];
                if (c == '{') return Obj(s, ref i);
                if (c == '[') return Arr(s, ref i);
                if (c == '"') return Str(s, ref i);
                if (Lit(s, ref i, "true")) return true;
                if (Lit(s, ref i, "false")) return false;
                if (Lit(s, ref i, "null")) return null;
                return Num(s, ref i);
            }

            private static bool Lit(string s, ref int i, string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
                i += word.Length;
                return true;
            }

            private static Dictionary<string, object> Obj(string s, ref int i)
            {
                var d = new Dictionary<string, object>();
                i++; // {
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("expected a key at " + i);
                    string key = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                    i++;
                    d[key] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("expected ',' or '}' at " + i);
                }
            }

            private static List<object> Arr(string s, ref int i)
            {
                var l = new List<object>();
                i++; // [
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("expected ',' or ']' at " + i);
                }
            }

            private static string Str(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++; // opening quote
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new FormatException("control character in string at " + (i - 1));
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length ||
                                !int.TryParse(s.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                                throw new FormatException("bad \\u escape at " + i);
                            sb.Append((char) cp);
                            i += 4;
                            break;
                        default: throw new FormatException("bad escape \\" + e + " at " + (i - 1));
                    }
                }
                throw new FormatException("unterminated string");
            }

            private static double Num(string s, ref int i)
            {
                int start = i;
                if (i < s.Length && s[i] == '-') i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-'))
                    i++;
                if (i == start || !double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw new FormatException("unexpected character at " + start);
                return d;
            }

            public static string Write(object v)
            {
                var sb = new StringBuilder();
                Write(sb, v);
                return sb.ToString();
            }

            private static void Write(StringBuilder sb, object v)
            {
                switch (v)
                {
                    case null: sb.Append("null"); return;
                    case string s: WriteString(sb, s); return;
                    case bool b: sb.Append(b ? "true" : "false"); return;
                    case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                    case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                    case int or long or short or byte or sbyte or ushort or uint or ulong:
                        sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                    case IDictionary dict:
                        sb.Append('{');
                        bool first = true;
                        foreach (DictionaryEntry e in dict)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                            sb.Append(':');
                            Write(sb, e.Value);
                        }
                        sb.Append('}');
                        return;
                    case IEnumerable list:
                        sb.Append('[');
                        bool firstItem = true;
                        foreach (object item in list)
                        {
                            if (!firstItem) sb.Append(',');
                            firstItem = false;
                            Write(sb, item);
                        }
                        sb.Append(']');
                        return;
                    default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                }
            }

            private static void WriteString(StringBuilder sb, string s)
            {
                sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int) c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }
        }
    }
}
