// AdminHost -- a drop-in OpenDAoC script that lets operator tools read and change the RUNNING server.
//
// Design: docs/design/admin-host.md. Requests arrive as one JSON object per line on a Unix domain
// socket (mode 0600, so only the server's own user can connect: ssh is the authentication):
//
//   {"id":"7","actor":"joe","op":"player.spec.set","args":{"name":"LabChar","spec":"Slash","level":5}}
//   {"id":"7","ok":true,"result":{...}}      or      {"id":"7","ok":false,"error":{"code":"...","message":"..."}}
//
// An edit to a character that isn't in the world can be queued (queue=true) and is applied when the
// character next enters the world: docs/design/admin-host-queued-edits.md. Pending edits are kept in
// admin-pending.json next to the socket. Moving a player and setting money: docs/design/admin-host-move-money.md.
// Revive, items and level: docs/design/admin-host-revive-items-level.md. Admin spawns (NPCs, boats,
// rams; never saved): docs/design/admin-host-spawns.md.
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
using DOL.AI;
using DOL.AI.Brain;
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
        private static string _pendingPath;
        private static List<Dictionary<string, object>> _pending; // null: the store couldn't be read
        private static readonly DOLEventHandler _onGameEntered = OnGameEntered;
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
                _pendingPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_socketPath)), "admin-pending.json");
                LoadPending();
                GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, _onGameEntered);

                if (File.Exists(_socketPath))
                    File.Delete(_socketPath); // left over from a crash; a live server would hold it open

                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
                // Restrict before Listen(): nobody can connect until the socket listens.
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _listener.Listen(MaxConnections);

                var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "AdminHost" };
                thread.Start();
                log.Info("AdminHost listening on " + _socketPath + " (audit: " + _auditPath + ", pending: " + _pendingPath + ")");
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
            try { GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, _onGameEntered); } catch { }
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
            ["player.move"] = new OpSpec { Run = PlayerMove },
            ["player.money.set"] = new OpSpec { Run = PlayerMoneySet },
            ["player.revive"] = new OpSpec { Run = PlayerRevive },
            ["player.item.set"] = new OpSpec { Run = PlayerItemSet },
            ["player.level.set"] = new OpSpec { Run = PlayerLevelSet },
            ["player.pending"] = new OpSpec { Run = PlayerPending },
            ["player.pending.cancel"] = new OpSpec { Run = PlayerPendingCancel },
            ["cache.reload"] = new OpSpec { Run = CacheReload },
            ["spawn.npc"] = new OpSpec { Run = SpawnNpc },
            ["spawn.boat"] = new OpSpec { Run = SpawnBoat },
            ["spawn.ram"] = new OpSpec { Run = SpawnRam },
            ["spawn.list"] = new OpSpec { Run = SpawnList },
            ["spawn.delete"] = new OpSpec { Run = SpawnDelete },
            ["spawn.board"] = new OpSpec { Run = SpawnBoard },
            ["spawn.unboard"] = new OpSpec { Run = SpawnUnboard },
            ["spawn.path"] = new OpSpec { Run = SpawnPath },
            ["spawn.stop"] = new OpSpec { Run = SpawnStop },
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
                    ["inWorld"] = p.EnteredGame,
                    ["region"] = (int) p.CurrentRegionID,
                    ["zone"] = p.CurrentZone?.Description,
                })
                .ToList();

        private static object PlayerGet(Request req)
        {
            GamePlayer p = OnlinePlayer(req);
            var position = Pos(p.CurrentRegionID, p.X, p.Y, p.Z, p.Heading);
            position["zone"] = p.CurrentZone?.Description;

            List<object> inventory;
            lock (p.Inventory.Lock)
                inventory = p.Inventory.AllItems
                    .Where(i => i != null)
                    .OrderBy(i => i.SlotPosition)
                    .Select(i => (object) new Dictionary<string, object>
                    {
                        ["slot"] = i.SlotPosition, ["id"] = i.Id_nb, ["name"] = i.Name, ["count"] = i.Count,
                    })
                    .ToList();

            List<object> effects = p.effectListComponent.GetEffects()
                .Select(e => (object) new Dictionary<string, object>
                {
                    ["name"] = e.Name,
                    ["type"] = e.EffectType.ToString(),
                    ["spell"] = e.SpellHandler?.Spell?.Name,
                    ["icon"] = (int) e.Icon,
                    ["remainingMs"] = e.GetRemainingTimeForClient(), // 0: no duration
                })
                .ToList();

            long money = p.GetCurrentMoney();
            return new Dictionary<string, object>
            {
                ["name"] = p.Name,
                ["account"] = p.Client?.Account?.Name,
                ["level"] = (int) p.Level,
                ["class"] = p.CharacterClass?.Name,
                ["realm"] = RealmName(p.Realm),
                ["clientState"] = p.Client?.ClientState.ToString(),
                ["linkDead"] = p.Client?.ClientState == GameClient.eClientState.Linkdead,
                ["position"] = position,
                ["alive"] = p.IsAlive,
                ["health"] = p.Health, ["maxHealth"] = p.MaxHealth,
                ["mana"] = p.Mana, ["maxMana"] = p.MaxMana,
                ["endurance"] = p.Endurance, ["maxEndurance"] = p.MaxEndurance,
                ["money"] = money,
                ["moneyText"] = Money.GetString(money),
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
                ["inventory"] = inventory,
                ["effects"] = effects,
            };
        }

        // In the world: set it now. Not in the world and queue=true: check it against the saved
        // character, queue it, and apply it through SetSpec when the character enters the world
        // (docs/design/admin-host-queued-edits.md).
        private static object PlayerSpecSet(Request req)
        {
            string name = StringArg(req, "name");
            string specName = StringArg(req, "spec");
            int level = IntArg(req, "level");
            bool queue = BoolArg(req, "queue", false);

            GamePlayer p = InWorld(name);
            if (p != null)
            {
                object result = SetSpec(p, specName, level, out int before, out bool changed);
                if (changed) Audit(req, before, level, result);
                return result;
            }

            if (!queue)
                throw new OpError("not_online", name + " is not online (queue=true applies it at their next login)");
            return QueueSpec(req, name, specName, level);
        }

        // Same calls as Core's trainer (commands/playercommands/train.cs), minus the trainer window.
        private static object SetSpec(GamePlayer p, string specName, int level, out int before, out bool changed)
        {
            changed = false;
            Specialization spec = p.GetSpecializationByName(specName);
            if (spec == null)
                throw new OpError("not_found", p.Name + " has no spec '" + specName + "'");
            if (!spec.Trainable)
                throw new OpError("invalid", "'" + spec.KeyName + "' is not a trainable spec");
            if (level < 1 || level > p.BaseLevel)
                throw new OpError("invalid", "level must be 1 to " + p.BaseLevel + " (the player's level)");

            before = spec.Level;
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

            changed = true;
            return SpecResult(p, spec, before, saved: true, changed: true);
        }

        // Region, x, y, z and an optional heading (0-4095; default: keep the current one).
        private static object PlayerMove(Request req)
        {
            string name = StringArg(req, "name");
            int region = IntArg(req, "region"), x = IntArg(req, "x"), y = IntArg(req, "y"), z = IntArg(req, "z");
            int? heading = OptIntArg(req, "heading");
            bool queue = BoolArg(req, "queue", false);
            CheckPosition(region, x, y, heading);

            GamePlayer p = InWorld(name);
            if (p != null)
            {
                object result = Move(p, region, x, y, z, heading, out object before, out object after, out bool changed);
                if (changed) Audit(req, before, after, result);
                return result;
            }

            if (!queue)
                throw new OpError("not_online", name + " is not online (queue=true applies it at their next login)");
            DbCoreCharacter ch = SavedCharacter(name);
            var args = new Dictionary<string, object>
            {
                ["region"] = (double) region, ["x"] = (double) x, ["y"] = (double) y, ["z"] = (double) z,
            };
            if (heading != null) args["heading"] = (double) heading.Value;
            Dictionary<string, object> pending = QueueEdit(req, ch, args, _ => true, out string replaced);

            // The saved Direction can carry a flag above the 12 heading bits (lab: 7212 for heading 3116).
            var saved = Pos(ch.Region, ch.Xpos, ch.Ypos, ch.Zpos, ch.Direction & 0xFFF);
            var to = Pos(region, x, y, z, 0);
            to["heading"] = heading; // null: keep the heading the character has after login
            var queued = new Dictionary<string, object>
            {
                ["queued"] = true, ["pendingId"] = pending["id"], ["name"] = ch.Name, ["saved"] = saved, ["to"] = to,
                ["replaced"] = replaced,
                ["note"] = "applied once the character is in the world: it appears at the saved position, then moves",
            };
            Audit(req, saved, to, queued, "queued");
            return queued;
        }

        // GamePlayer.MoveTo, as /jump uses it: it checks the server rules, handles houses, steeds, siege
        // weapons and pets, and sends the region change or the jump. The position is then saved at once.
        private static object Move(GamePlayer p, int region, int x, int y, int z, int? heading,
            out object before, out object after, out bool changed)
        {
            CheckPosition(region, x, y, heading);
            before = Pos(p.CurrentRegionID, p.X, p.Y, p.Z, p.Heading);
            ushort h = (ushort) (heading ?? p.Heading);
            bool regionChanged = p.CurrentRegionID != region;
            changed = regionChanged || p.X != x || p.Y != y || p.Z != z || p.Heading != h;

            if (changed)
            {
                if (!p.MoveTo((ushort) region, x, y, z, h))
                    throw new OpError("invalid", "Core refused the move: the server rules don't let " + p.Name +
                        " into region " + region + ", or the player couldn't leave their region");
                p.Out.SendMessage("An administrator moved you.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                p.SaveIntoDatabase();
            }

            after = Pos(region, x, y, z, h);
            return new Dictionary<string, object>
            {
                ["name"] = p.Name, ["before"] = before, ["after"] = after, ["changed"] = changed,
                ["regionChanged"] = changed && regionChanged, ["saved"] = changed,
            };
        }

        // An absolute amount: args.amount in copper, or any of mithril, platinum, gold, silver, copper.
        private static object PlayerMoneySet(Request req)
        {
            string name = StringArg(req, "name");
            long amount = MoneyArg(req);
            bool queue = BoolArg(req, "queue", false);

            GamePlayer p = InWorld(name);
            if (p != null)
            {
                object result = SetMoney(p, amount, out long before, out bool changed);
                if (changed) Audit(req, before, amount, result);
                return result;
            }

            if (!queue)
                throw new OpError("not_online", name + " is not online (queue=true applies it at their next login)");
            DbCoreCharacter ch = SavedCharacter(name);
            Dictionary<string, object> pending = QueueEdit(req, ch,
                new Dictionary<string, object> { ["amount"] = (double) amount }, _ => true, out string replaced);
            long saved = SavedMoney(ch);
            var queued = new Dictionary<string, object>
            {
                ["queued"] = true, ["pendingId"] = pending["id"], ["name"] = ch.Name,
                ["saved"] = saved, ["amount"] = amount, ["amountText"] = Money.GetString(amount), ["replaced"] = replaced,
                ["note"] = "money is shared by the account's characters in one realm; this applies when " + ch.Name +
                           " enters the world",
            };
            Audit(req, saved, amount, queued, "queued");
            return queued;
        }

        // AddMoney and RemoveMoney send the money update; SaveIntoDatabase writes accountxmoney.
        private static object SetMoney(GamePlayer p, long amount, out long before, out bool changed)
        {
            before = p.GetCurrentMoney();
            changed = amount != before;
            if (changed)
            {
                if (amount > before)
                    p.AddMoney(amount - before);
                else if (!p.RemoveMoney(before - amount))
                    throw new OpError("failed", "Core refused to remove " + (before - amount) + " copper");
                p.Out.SendMessage("An administrator set your money to " + Money.GetString(amount) + ".",
                    eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                p.SaveIntoDatabase();
            }

            long after = p.GetCurrentMoney();
            return new Dictionary<string, object>
            {
                ["name"] = p.Name, ["before"] = before, ["after"] = after, ["afterText"] = Money.GetString(after),
                ["changed"] = changed, ["saved"] = changed,
            };
        }

        // Online only: being dead doesn't outlast a session. A dead player is raised where they lie, as a
        // resurrection spell does it but at full health, power and endurance and without resurrection
        // illness. A living one is healed, as /heal does. clearPenalty also ends resurrection illness,
        // restores constitution lost at death and resets the death count (which scales the next death's
        // penalty). It doesn't give back experience: Core records the loss it meant to take
        // (DEATH_EXP_LOSS_PROPERTY), not what it took after stopping at the start of the level, so giving
        // that back could add experience that was never lost.
        private static object PlayerRevive(Request req)
        {
            GamePlayer p = OnlinePlayer(req);
            bool clearPenalty = BoolArg(req, "clearPenalty", false);
            bool wasDead = !p.IsAlive;
            int conRestored = 0, illnessEnded = 0, deathCount = p.DeathCount;

            // First, so that the heal below fills the maximum health the restored constitution gives.
            if (clearPenalty)
            {
                foreach (ECSGameEffect effect in p.effectListComponent.GetEffects())
                {
                    if (effect.EffectType is eEffect.ResurrectionIllness or eEffect.RvrResurrectionIllness)
                    {
                        effect.End();
                        illnessEnded++;
                    }
                }
                conRestored = p.TotalConstitutionLostAtDeath;
                if (conRestored > 0)
                {
                    p.TotalConstitutionLostAtDeath = 0; // as the healer NPC does
                    p.Out.SendCharStatsUpdate();
                }
                p.DeathCount = 0;
            }

            if (wasDead)
            {
                // Constitution is only charged when the player releases; a resurrection never charges it.
                p.TempProperties.RemoveProperty(GamePlayer.DEATH_CONSTITUTION_LOSS_PROPERTY);
                // OnRevive (registered at death) skips resurrection illness when this is 0, and unregisters itself.
                p.TempProperties.SetProperty(GamePlayer.RESURRECT_REZ_SICK_EFFECTIVENESS, 0.0);
                p.Notify(GamePlayerEvent.Revive, p);
                p.Health = p.MaxHealth;
                p.Mana = p.MaxMana;
                p.Endurance = p.MaxEndurance;
                p.StopReleaseTimer();
                p.Out.SendPlayerRevive(p);
                p.StartPowerRegeneration();
                p.StartEnduranceRegeneration();
            }
            else
            {
                p.Health = p.MaxHealth;
                p.Mana = p.MaxMana;
                p.Endurance = p.MaxEndurance;
            }


            p.Out.SendMessage(wasDead ? "An administrator revived you." : "An administrator healed you.",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            p.SaveIntoDatabase();

            var result = new Dictionary<string, object>
            {
                ["name"] = p.Name, ["wasDead"] = wasDead, ["alive"] = p.IsAlive,
                ["health"] = p.Health, ["mana"] = p.Mana, ["endurance"] = p.Endurance,
                ["conRestored"] = conRestored, ["illnessEnded"] = illnessEnded,
                ["deathCount"] = (int) p.DeathCount, ["deathCountBefore"] = deathCount,
            };
            Audit(req, wasDead ? "dead" : "alive", "alive", result);
            return result;
        }

        // The count of one item template in the backpack, set to an absolute number: items are added
        // (stacked where they fit) as a merchant sale adds them, or removed as Core removes a template.
        private static object PlayerItemSet(Request req)
        {
            string name = StringArg(req, "name");
            DbItemTemplate template = ItemTemplate(StringArg(req, "id"));
            int count = IntArg(req, "count");
            if (count < 0 || count > MaxItemCount)
                throw new OpError("invalid", "count must be 0 to " + MaxItemCount);
            bool queue = BoolArg(req, "queue", false);

            GamePlayer p = InWorld(name);
            if (p != null)
            {
                object result = SetItems(p, template, count, out int before, out bool changed);
                if (changed) Audit(req, before, count, result);
                return result;
            }

            if (!queue)
                throw new OpError("not_online", name + " is not online (queue=true applies it at their next login)");
            DbCoreCharacter ch = SavedCharacter(name);
            Dictionary<string, object> pending = QueueEdit(req, ch,
                new Dictionary<string, object> { ["id"] = template.Id_nb, ["count"] = (double) count },
                a => Get(a, "id") is string k && k.Equals(template.Id_nb, StringComparison.OrdinalIgnoreCase), out string replaced);
            int saved = SavedItemCount(ch, template.Id_nb);
            var queued = new Dictionary<string, object>
            {
                ["queued"] = true, ["pendingId"] = pending["id"], ["name"] = ch.Name, ["id"] = template.Id_nb,
                ["item"] = template.Name, ["saved"] = saved, ["count"] = count, ["replaced"] = replaced,
                ["note"] = "whether the items fit in the backpack is checked when they are applied",
            };
            Audit(req, saved, count, queued, "queued");
            return queued;
        }

        // A backpack of 40 slots, each holding at most one full stack.
        private const int MaxItemCount = 40 * 1000;

        private static object SetItems(GamePlayer p, DbItemTemplate template, int count, out int before, out bool changed)
        {
            const eInventorySlot first = eInventorySlot.FirstBackpack, last = eInventorySlot.LastBackpack;
            before = p.Inventory.CountItemTemplate(template.Id_nb, first, last);
            changed = count != before;
            if (count > before)
            {
                // All or nothing: AddTemplate checks that everything fits before it adds anything.
                if (!p.Inventory.AddTemplate(GameInventoryItem.Create(template), count - before, first, last))
                    throw new OpError("invalid", "adding " + (count - before) + " " + template.Name + " doesn't fit in " +
                        p.Name + "'s backpack (" + p.Inventory.CountSlots(false, first, last) + " free slots)");
                InventoryLogging.LogInventoryAction("(admin host)", p, eInventoryActionType.Other, template, count - before);
            }
            else if (count < before)
            {
                if (!p.Inventory.RemoveTemplate(template.Id_nb, before - count, first, last))
                    throw new OpError("failed", "Core refused to remove " + (before - count) + " " + template.Name);
                InventoryLogging.LogInventoryAction(p, "(admin host)", eInventoryActionType.Other, template, before - count);
            }

            if (changed)
            {
                p.Out.SendMessage("An administrator set your " + template.Name + " to " + count + ".",
                    eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                p.SaveIntoDatabase();
            }
            return new Dictionary<string, object>
            {
                ["name"] = p.Name, ["id"] = template.Id_nb, ["item"] = template.Name, ["before"] = before,
                ["after"] = p.Inventory.CountItemTemplate(template.Id_nb, first, last), ["changed"] = changed, ["saved"] = changed,
            };
        }

        private static DbItemTemplate ItemTemplate(string id) =>
            GameServer.Database.FindObjectByKey<DbItemTemplate>(id)
            ?? throw new OpError("not_found", "no item template '" + id + "'");

        private static int SavedItemCount(DbCoreCharacter ch, string id) =>
            DOLDB<DbInventoryItem>.SelectObjects(DB.Column("OwnerID").IsEqualTo(ch.ObjectId).And(DB.Column("ITemplate_Id").IsEqualTo(id)))
                .Where(i => i.SlotPosition >= (int) eInventorySlot.FirstBackpack && i.SlotPosition <= (int) eInventorySlot.LastBackpack)
                .Sum(i => i.Count);

        // As the GM command /player level does it: raising sets Level, which runs Core's level-up (stats,
        // spec points, abilities); lowering first resets the character to level 1, which respecs every
        // line and realm ability, then raises it. Experience is set to the start of the new level.
        private static object PlayerLevelSet(Request req)
        {
            string name = StringArg(req, "name");
            int level = IntArg(req, "level");
            if (level < 1 || level > GamePlayer.MAX_LEVEL)
                throw new OpError("invalid", "level must be 1 to " + GamePlayer.MAX_LEVEL);
            bool queue = BoolArg(req, "queue", false);

            GamePlayer p = InWorld(name);
            if (p != null)
            {
                object result = SetLevel(p, level, out int before, out bool changed);
                if (changed) Audit(req, before, level, result);
                return result;
            }

            if (!queue)
                throw new OpError("not_online", name + " is not online (queue=true applies it at their next login)");
            DbCoreCharacter ch = SavedCharacter(name);
            Dictionary<string, object> pending = QueueEdit(req, ch,
                new Dictionary<string, object> { ["level"] = (double) level }, _ => true, out string replaced);
            var queued = new Dictionary<string, object>
            {
                ["queued"] = true, ["pendingId"] = pending["id"], ["name"] = ch.Name, ["saved"] = ch.Level,
                ["level"] = level, ["replaced"] = replaced,
                ["note"] = level < ch.Level ? "lowering a level respecs every line and realm ability" : null,
            };
            Audit(req, ch.Level, level, queued, "queued");
            return queued;
        }

        private static object SetLevel(GamePlayer p, int level, out int before, out bool changed)
        {
            before = p.Level;
            changed = level != before;
            bool respecced = level < before;
            if (changed)
            {
                if (respecced)
                    p.Reset();
                if (level > p.Level)
                    p.Level = (byte) level;
                p.Experience = p.ExperienceForCurrentLevel;
                p.IsLevelSecondStage = false;

                p.RefreshSpecDependantSkills(true);
                p.Out.SendUpdatePlayer();
                p.Out.SendUpdatePoints();
                p.Out.SendCharStatsUpdate();
                p.Out.SendCharResistsUpdate();
                p.Out.SendUpdatePlayerSkills(true);
                p.Out.SendMessage("An administrator set your level to " + level + (respecced ? "; your skills were respecced." : "."),
                    eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                p.SaveIntoDatabase();
            }
            return new Dictionary<string, object>
            {
                ["name"] = p.Name, ["before"] = before, ["after"] = (int) p.Level, ["changed"] = changed,
                ["respecced"] = changed && respecced, ["specPoints"] = p.SkillSpecialtyPoints, ["saved"] = changed,
            };
        }

        // ---- queued edits to characters that aren't in the world ---------------------------------
        // The store is a list of JSON objects in <socket dir>/admin-pending.json. Only jobs on the
        // game loop (Drain) read or write it, so it needs no lock.

        private static object QueueSpec(Request req, string name, string specName, int level)
        {
            DbCoreCharacter ch = SavedCharacter(name);

            // Match against the saved specs first: SkillBase.GetSpecialization throws on an unknown key
            // in Core 0ac41d443 (it dereferences the result before its null check).
            int savedLevel = 0;
            string key = null;
            foreach (string entry in Util.SplitCSV(ch.SerializedSpecs ?? ""))
            {
                string[] kv = entry.Split('|');
                if (kv.Length >= 2 && kv[0].Equals(specName, StringComparison.OrdinalIgnoreCase))
                {
                    key = kv[0];
                    int.TryParse(kv[1], out savedLevel);
                    break;
                }
            }
            if (key == null)
                throw new OpError("not_found", ch.Name + " has no saved spec '" + specName + "'");
            Specialization spec = SkillBase.GetSpecialization(key, false);
            if (spec == null || !spec.Trainable)
                throw new OpError("invalid", "'" + key + "' is not a trainable spec");
            // A queued level change applies before spec edits at login, so check against it if there is one.
            int charLevel = ch.Level;
            Dictionary<string, object> queuedLevel = _pending?.FirstOrDefault(e =>
                (string) e["characterId"] == ch.ObjectId && (string) e["op"] == "player.level.set");
            if (queuedLevel != null && ((Dictionary<string, object>) queuedLevel["args"])["level"] is double ql)
                charLevel = (int) ql;
            if (level < 1 || level > charLevel)
                throw new OpError("invalid", "level must be 1 to " + charLevel + (queuedLevel != null
                    ? " (the character's queued level)" : " (the character's level)"));

            Dictionary<string, object> pending = QueueEdit(req, ch,
                new Dictionary<string, object> { ["spec"] = key, ["level"] = (double) level },
                a => Get(a, "spec") is string k && k.Equals(key, StringComparison.OrdinalIgnoreCase), out string replaced);

            var result = new Dictionary<string, object>
            {
                ["queued"] = true, ["pendingId"] = pending["id"], ["name"] = ch.Name, ["spec"] = key,
                ["savedLevel"] = savedLevel, ["level"] = level, ["replaced"] = replaced,
            };
            Audit(req, savedLevel, level, result, "queued");
            return result;
        }

        // Adds one pending edit. An edit for the same character and op whose args match sameTarget
        // replaces the older one and keeps its place in the order.
        private static Dictionary<string, object> QueueEdit(Request req, DbCoreCharacter ch, Dictionary<string, object> args,
            Func<Dictionary<string, object>, bool> sameTarget, out string replaced)
        {
            List<Dictionary<string, object>> store = PendingStore();
            replaced = null;
            int at = store.FindIndex(e => (string) e["characterId"] == ch.ObjectId && (string) e["op"] == req.Op &&
                sameTarget((Dictionary<string, object>) e["args"]));
            var pending = new Dictionary<string, object>
            {
                ["id"] = NewPendingId(store),
                ["characterId"] = ch.ObjectId,
                ["name"] = ch.Name,
                ["op"] = req.Op,
                ["args"] = args,
                ["queuedAt"] = DateTime.UtcNow.ToString("o"),
                ["actor"] = req.Actor,
                ["peerUid"] = (double) req.PeerUid,
            };
            var previous = new List<Dictionary<string, object>>(store);
            if (at >= 0)
            {
                replaced = (string) store[at]["id"];
                store[at] = pending;
            }
            else
                store.Add(pending);
            SavePendingOrRollBack(previous);
            return pending;
        }

        private static DbCoreCharacter SavedCharacter(string name)
        {
            PendingStore(); // refuse before the lookup if queued edits are off
            return DOLDB<DbCoreCharacter>.SelectObject(DB.Column("Name").IsEqualTo(name))
                ?? throw new OpError("not_found", "no character named " + name);
        }

        // The realm's money as the next login will load it: accountxmoney, else the character's own columns.
        private static long SavedMoney(DbCoreCharacter ch)
        {
            DbAccount account = DOLDB<DbAccount>.SelectObject(DB.Column("Name").IsEqualTo(ch.AccountName));
            DbAccountXMoney row = account == null ? null : DOLDB<DbAccountXMoney>.SelectObject(
                DB.Column("AccountID").IsEqualTo(account.ObjectId).And(DB.Column("Realm").IsEqualTo(ch.Realm)));
            return row != null
                ? Money.GetMoney(row.Mithril, row.Platinum, row.Gold, row.Silver, row.Copper)
                : Money.GetMoney(ch.Mithril, ch.Platinum, ch.Gold, ch.Silver, ch.Copper);
        }

        // GameEntered fires inside the player-init packet handler, on a client-packet thread: only enqueue.
        private static void OnGameEntered(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is GamePlayer p)
                Enqueue(new Job(() => { ApplyPending(p); return null; }));
        }

        private static void ApplyPending(GamePlayer p)
        {
            if (_pending == null || p.DBCharacter == null) return;
            string id = p.DBCharacter.ObjectId;
            // In queue order, except: a level change first (spec edits are checked against the level, and
            // lowering one respecs), and moves last (a move to another region takes the player out of the world).
            List<Dictionary<string, object>> mine = _pending.Where(e => (string) e["characterId"] == id)
                .OrderBy(e => (string) e["op"] switch { "player.level.set" => 0, "player.move" => 2, _ => 1 }).ToList();
            if (mine.Count == 0) return;
            if (p.ObjectState != GameObject.eObjectState.Active || !p.EnteredGame) return; // gone again: keep them

            foreach (Dictionary<string, object> entry in mine)
            {
                var req = new Request
                {
                    Id = entry["id"], Actor = entry.TryGetValue("actor", out var a) ? a as string : null,
                    Op = (string) entry["op"], Args = (Dictionary<string, object>) entry["args"],
                    PeerUid = entry.TryGetValue("peerUid", out var u) && u is double d ? (int) d : -1,
                };
                try
                {
                    // Edits set absolute values: if the server stops after this save but before the store
                    // is rewritten, applying the entry again at the next login changes nothing.
                    object before, after, result;
                    switch (req.Op)
                    {
                        case "player.spec.set":
                            int level = IntArg(req, "level");
                            result = SetSpec(p, StringArg(req, "spec"), level, out int specBefore, out _);
                            before = specBefore;
                            after = level;
                            break;
                        case "player.move":
                            result = Move(p, IntArg(req, "region"), IntArg(req, "x"), IntArg(req, "y"), IntArg(req, "z"),
                                OptIntArg(req, "heading"), out before, out after, out _);
                            break;
                        case "player.money.set":
                            long amount = MoneyArg(req);
                            result = SetMoney(p, amount, out long moneyBefore, out _);
                            before = moneyBefore;
                            after = amount;
                            break;
                        case "player.item.set":
                            int count = IntArg(req, "count");
                            result = SetItems(p, ItemTemplate(StringArg(req, "id")), count, out int itemsBefore, out _);
                            before = itemsBefore;
                            after = count;
                            break;
                        case "player.level.set":
                            int newLevel = IntArg(req, "level");
                            result = SetLevel(p, newLevel, out int levelBefore, out _);
                            before = levelBefore;
                            after = newLevel;
                            break;
                        default:
                            throw new OpError("invalid", "no way to apply a queued '" + req.Op + "'");
                    }
                    Audit(req, before, after, result, "applied");
                }
                catch (OpError oe)
                {
                    log.Warn("AdminHost pending " + req.Id + " for " + p.Name + " refused at login: " + oe.Message);
                    Audit(req, null, null, new Dictionary<string, object> { ["code"] = oe.Code, ["message"] = oe.Message },
                        "refused at login");
                }
                catch (Exception ex)
                {
                    log.Error("AdminHost pending " + req.Id + " for " + p.Name + " failed", ex);
                    Audit(req, null, null, new Dictionary<string, object> { ["code"] = "failed", ["message"] = ex.Message },
                        "failed at login");
                }
                _pending.Remove(entry);
            }
            SavePending();
        }

        private static object PlayerPending(Request req)
        {
            List<Dictionary<string, object>> store = PendingStore();
            string name = req.Args.TryGetValue("name", out object n) ? n as string : null;
            return store.Where(e => name == null || ((string) e["name"]).Equals(name, StringComparison.OrdinalIgnoreCase))
                .Cast<object>().ToList();
        }

        private static object PlayerPendingCancel(Request req)
        {
            List<Dictionary<string, object>> store = PendingStore();
            string id = StringArg(req, "id");
            Dictionary<string, object> entry = store.FirstOrDefault(e => (string) e["id"] == id);
            if (entry == null)
                throw new OpError("not_found", "no pending edit '" + id + "'");
            var previous = new List<Dictionary<string, object>>(store);
            store.Remove(entry);
            SavePendingOrRollBack(previous);
            var result = new Dictionary<string, object> { ["cancelled"] = entry };
            Audit(req, entry, null, result, "cancelled");
            return result;
        }

        private static List<Dictionary<string, object>> PendingStore() =>
            _pending ?? throw new OpError("failed", "the pending store " + _pendingPath +
                " could not be read at startup; fix or move it and restart (see the server log)");

        private static string NewPendingId(List<Dictionary<string, object>> store)
        {
            while (true)
            {
                string id = "p-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!store.Any(e => (string) e["id"] == id)) return id;
            }
        }

        private static void LoadPending()
        {
            try
            {
                if (!File.Exists(_pendingPath)) { _pending = new(); return; }
                if (Json.Parse(File.ReadAllText(_pendingPath)) is not List<object> list)
                    throw new FormatException("expected a JSON array");
                var store = new List<Dictionary<string, object>>();
                foreach (object o in list)
                {
                    if (o is not Dictionary<string, object> e || Get(e, "id") is not string ||
                        Get(e, "characterId") is not string || Get(e, "name") is not string ||
                        Get(e, "op") is not string || Get(e, "args") is not Dictionary<string, object>)
                        throw new FormatException("an entry lacks id, characterId, name, op or args");
                    store.Add(e);
                }
                _pending = store;
                if (store.Count > 0) log.Info("AdminHost: " + store.Count + " pending edit(s) in " + _pendingPath);
            }
            catch (Exception ex)
            {
                // Leave the file alone, and refuse queue ops, rather than overwrite edits we couldn't read.
                _pending = null;
                log.Error("AdminHost could not read the pending store " + _pendingPath + "; queued edits are off", ex);
            }
        }

        // Write to a temporary file and rename it over the store, so a crash leaves the old or the new list.
        private static void SavePending()
        {
            string tmp = _pendingPath + ".tmp";
            File.WriteAllText(tmp, Json.Write(_pending) + "\n");
            if (!OperatingSystem.IsWindows()) // the host never starts there; this keeps the analyzer quiet
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _pendingPath, overwrite: true);
        }

        private static void SavePendingOrRollBack(List<Dictionary<string, object>> previous)
        {
            try { SavePending(); }
            catch (Exception ex)
            {
                _pending.Clear();
                _pending.AddRange(previous);
                log.Error("AdminHost could not write the pending store " + _pendingPath, ex);
                throw new OpError("failed", "could not write the pending store: " + ex.Message);
            }
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

        // ---- admin spawns ------------------------------------------------------------------------
        // Objects the host puts in the world for a test: a standing NPC, a boat, a siege ram. They are
        // Core's own classes with saving switched off, so they send the client the same packets as the
        // real thing but never reach the database; a restart clears them. The registry lives on the
        // game loop (only Drain touches it); each path runs on its own ECS timer.

        public sealed class AdminSpawnNpc : GameNPC
        {
            public AdminSpawnNpc(ABrain brain) : base(brain) { }
            public override void SaveIntoDatabase() { }
            public override void DeleteFromDatabase() { }
        }

        public sealed class AdminSpawnBoat : GameBoat
        {
            public AdminSpawnBoat(byte type) : base(type) { }
            public override void SaveIntoDatabase() { }
            public override void DeleteFromDatabase() { }
        }

        public sealed class AdminSpawnRam : GameSiegeRam
        {
            public override void SaveIntoDatabase() { }
            public override void DeleteFromDatabase() { }
        }

        private sealed class Spawn
        {
            public string Id, Kind, Actor;
            public DateTime Created;
            public GameNPC Npc;
            // Path state, written by the path timer and read by Drain; the game loop runs them one after the other.
            public List<Point3D> Path;
            public int Next;
            public bool Loop, Finished;
            public short Speed;
            public long Issued;
            public ECSGameTimer Timer;
        }

        private static readonly Dictionary<string, Spawn> _spawns = new();
        private const int MaxSpawns = 50;
        private const int MaxPathPoints = 64;

        // Boat models and the GameBoat type that sets their seats, as GameBoat.LoadFromDatabase maps them.
        private static readonly Dictionary<int, byte> BoatTypes = new()
        {
            [1616] = 0, [2648] = 1, [2646] = 2, [2647] = 3, [1615] = 4, [1595] = 5, [1612] = 6, [1613] = 7, [1614] = 8,
        };

        // As SummonSiegeRam sets them, by its spell's damage value.
        private static readonly Dictionary<string, (byte level, string name, ushort model)> RamSizes = new()
        {
            ["mini"] = (0, "mini siege ram", 2605),
            ["light"] = (1, "light siege ram", 2600),
            ["medium"] = (2, "medium siege ram", 2601),
            ["heavy"] = (3, "heavy siege ram", 2602),
        };

        private static object SpawnNpc(Request req)
        {
            string brain = OptStringArg(req, "brain") ?? "none";
            if (brain != "none" && brain != "standard")
                throw new OpError("bad_request", "args.brain must be none (stands still) or standard (Core's mob brain)");
            var npc = new AdminSpawnNpc(brain == "standard" ? new StandardMobBrain() : new BlankBrain())
            {
                Name = OptStringArg(req, "label") ?? "admin spawn",
                Model = (ushort) RangeArg(req, "model", 408, 1, ushort.MaxValue),
                Level = (byte) RangeArg(req, "level", 1, 0, 255),
                Size = (byte) RangeArg(req, "size", 50, 1, 255),
            };
            npc.MaxSpeedBase = (short) RangeArg(req, "speed", 200, 1, 2000);
            if (BoolArg(req, "peace", false)) npc.Flags |= GameNPC.eFlags.PEACE;
            return AddSpawn(req, "npc", npc, 150, 0);
        }

        private static object SpawnBoat(Request req)
        {
            int model = RangeArg(req, "model", 2648, 1, ushort.MaxValue);
            var boat = new AdminSpawnBoat(BoatTypes.TryGetValue(model, out byte type) ? type : (byte) 0)
            {
                Name = OptStringArg(req, "label") ?? "admin boat",
                Model = (ushort) model,
                MaxSpeedBase = (short) RangeArg(req, "speed", 500, 1, 2000),
                OwnerID = string.Empty, // GameBoat refuses a double-click unless OwnerID is empty
            };
            return AddSpawn(req, "boat", boat, 300, null);
        }

        private static object SpawnRam(Request req)
        {
            string size = OptStringArg(req, "size") ?? "light";
            if (!RamSizes.TryGetValue(size, out var r))
                throw new OpError("bad_request", "args.size must be one of " + string.Join(", ", RamSizes.Keys));
            var ram = new AdminSpawnRam { Level = r.level, Name = OptStringArg(req, "label") ?? r.name, Model = r.model };
            return AddSpawn(req, "ram", ram, 150, null);
        }

        // Position: near=<player> (distance in front of them, facing them), or region, x, y, z and heading.
        // Realm: args.realm (0-3), else defaultRealm, else the near player's realm.
        private static object AddSpawn(Request req, string kind, GameNPC npc, int defaultDistance, int? defaultRealm)
        {
            PruneSpawns();
            if (_spawns.Count >= MaxSpawns)
                throw new OpError("invalid", "at most " + MaxSpawns + " admin spawns at once; spawn.delete some first");

            ushort region, heading;
            int x, y, z;
            GamePlayer near = null;
            if (req.Args.ContainsKey("near"))
            {
                string name = StringArg(req, "near");
                near = InWorld(name) ?? throw new OpError("not_online", name + " is not online");
                int distance = RangeArg(req, "distance", defaultDistance, 0, 5000);
                Point2D at = near.GetPointFromHeading(near.Heading, distance);
                (region, x, y, z) = (near.CurrentRegionID, at.X, at.Y, near.Z);
                heading = (ushort) ((near.Heading + 2048) & 0xFFF);
            }
            else
            {
                int rgn = IntArg(req, "region");
                (x, y, z) = (IntArg(req, "x"), IntArg(req, "y"), IntArg(req, "z"));
                int? h = OptIntArg(req, "heading");
                CheckPosition(rgn, x, y, h);
                (region, heading) = ((ushort) rgn, (ushort) (h ?? 0));
            }

            int realm = RangeArg(req, "realm", defaultRealm ?? (int) (near?.Realm ?? 0), 0, 3);
            npc.Realm = (eRealm) realm;
            npc.CurrentRegionID = region;
            (npc.X, npc.Y, npc.Z, npc.Heading) = (x, y, z, heading);
            if (!npc.AddToWorld())
                throw new OpError("failed", "Core didn't add the " + kind + " to the world");

            var s = new Spawn { Id = NewSpawnId(), Kind = kind, Actor = req.Actor, Created = DateTime.UtcNow, Npc = npc };
            _spawns[s.Id] = s;
            var result = SpawnInfo(s);
            Audit(req, null, result, result);
            return result;
        }

        private static object SpawnList(Request req)
        {
            PruneSpawns();
            return new Dictionary<string, object> { ["spawns"] = _spawns.Values.OrderBy(s => s.Created).Select(SpawnInfo).ToList() };
        }

        // id=<spawn id>, or all=true. Riders are put down and a siege weapon's controller let go first.
        private static object SpawnDelete(Request req)
        {
            PruneSpawns();
            List<Spawn> gone = BoolArg(req, "all", false)
                ? _spawns.Values.ToList()
                : new List<Spawn> { SpawnById(req) };
            var deleted = new List<object>();
            foreach (Spawn s in gone)
            {
                var info = SpawnInfo(s);
                StopPath(s, false);
                if (s.Npc is GameSiegeWeapon sw) sw.ReleaseControl();
                s.Npc.Delete();
                _spawns.Remove(s.Id);
                deleted.Add(info);
            }
            var result = new Dictionary<string, object> { ["deleted"] = deleted };
            if (deleted.Count > 0) Audit(req, deleted, null, result);
            return result;
        }

        // Puts a player in a seat of a boat or ram, as a double-click does (GameNPC.Interact). For a ram,
        // control=true (the default) also gives them the siege controls, as summoning one does.
        private static object SpawnBoard(Request req)
        {
            Spawn s = SpawnById(req);
            GameNPC n = s.Npc;
            GamePlayer p = OnlinePlayer(req);
            if (s.Kind == "npc")
                throw new OpError("invalid", s.Id + " is an NPC; only boats and rams have seats");
            if (p.CurrentRegionID != n.CurrentRegionID)
                throw new OpError("invalid", p.Name + " is in region " + p.CurrentRegionID + ", the " + s.Kind + " in " + n.CurrentRegionID);

            bool changed = false;
            if (n.RiderSlot(p) == -1)
            {
                if (n.GetFreeArrayLocation() == -1)
                    throw new OpError("invalid", "every seat of " + s.Id + " is taken (" + n.MAX_PASSENGERS + ")");
                if (p.IsRiding) p.DismountSteed(true);
                if (p.IsOnHorse) p.IsOnHorse = false;
                if (!p.MountSteed(n, true) || p.Steed != n)
                    throw new OpError("failed", "Core didn't seat " + p.Name);
                changed = true;
            }

            if (n is GameSiegeWeapon sw && BoolArg(req, "control", true) && sw.Owner != p)
            {
                sw.TakeControl(p); // tells the player itself when it can't
                changed |= sw.Owner == p;
            }

            var result = new Dictionary<string, object>
            {
                ["name"] = p.Name, ["seat"] = n.RiderSlot(p), ["changed"] = changed, ["spawn"] = SpawnInfo(s),
            };
            if (changed) Audit(req, null, s.Id, result);
            return result;
        }

        private static object SpawnUnboard(Request req)
        {
            GamePlayer p = OnlinePlayer(req);
            GameNPC steed = p.Steed;
            bool controlled = p.SiegeWeapon != null;
            if (controlled) p.SiegeWeapon.ReleaseControl();
            bool dismounted = steed != null && p.DismountSteed(true);
            var result = new Dictionary<string, object>
            {
                ["name"] = p.Name, ["changed"] = dismounted || controlled,
                ["from"] = steed == null ? null : steed.Name + " (object " + steed.ObjectID + ")",
            };
            if (dismounted || controlled) Audit(req, steed?.Name, null, result);
            return result;
        }

        // Moves a spawn through points in a straight line from one to the next (WalkTo: no navmesh, so a boat
        // can cross water). points: "x,y,z;x,y,z" or a JSON array of [x,y,z]. Optional speed and loop.
        private static object SpawnPath(Request req)
        {
            Spawn s = SpawnById(req);
            GameNPC n = s.Npc;
            if (!req.Args.TryGetValue("points", out object raw) || raw == null)
                throw new OpError("bad_request", "args.points is required: \"x,y,z;x,y,z\" or [[x,y,z],...]");
            List<Point3D> points = ParsePoints(raw);
            if (points.Count == 0 || points.Count > MaxPathPoints)
                throw new OpError("invalid", "give 1 to " + MaxPathPoints + " points");
            foreach (Point3D pt in points)
                CheckPosition(n.CurrentRegionID, pt.X, pt.Y, null);
            short speed = (short) RangeArg(req, "speed", n.MaxSpeedBase, 1, 2000);
            bool loop = BoolArg(req, "loop", false);

            StopPath(s, true);
            (s.Path, s.Next, s.Loop, s.Finished, s.Speed) = (points, 0, loop, false, speed);
            n.WalkTo(points[0], speed);
            s.Issued = GameLoop.GameLoopTime;
            s.Timer = new ECSGameTimer(n, _ => PathTick(s), 200);

            var result = SpawnInfo(s);
            Audit(req, null, result, result);
            return result;
        }

        private static object SpawnStop(Request req)
        {
            Spawn s = SpawnById(req);
            bool wasMoving = s.Path != null || s.Npc.IsMoving;
            StopPath(s, true);
            var result = SpawnInfo(s);
            if (wasMoving) Audit(req, null, result, result);
            return result;
        }

        private static int PathTick(Spawn s)
        {
            GameNPC n = s.Npc;
            if (s.Path == null || n.ObjectState != GameObject.eObjectState.Active)
            {
                s.Timer = null;
                return 0;
            }

            Point3D target = s.Path[s.Next];
            if (n.IsWithinRadius(target, 16))
            {
                if (++s.Next >= s.Path.Count)
                {
                    if (!s.Loop)
                    {
                        n.StopMoving();
                        (s.Path, s.Finished, s.Timer) = (null, true, null);
                        return 0;
                    }
                    s.Next = 0;
                }
                n.WalkTo(s.Path[s.Next], s.Speed);
                s.Issued = GameLoop.GameLoopTime;
            }
            else if (!n.IsMoving && GameLoop.GameLoopTime - s.Issued > 1000)
            {
                n.WalkTo(target, s.Speed); // stopped short (something else stopped it): go on
                s.Issued = GameLoop.GameLoopTime;
            }
            return 200;
        }

        private static void StopPath(Spawn s, bool stopMoving)
        {
            s.Timer?.Stop();
            (s.Timer, s.Path) = (null, null);
            if (stopMoving && s.Npc.ObjectState == GameObject.eObjectState.Active) s.Npc.StopMoving();
        }

        private static List<Point3D> ParsePoints(object raw)
        {
            var points = new List<Point3D>();
            IEnumerable<object> items = raw switch
            {
                string text => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                List<object> list => list,
                _ => throw new OpError("bad_request", "args.points must be a string or an array"),
            };
            foreach (object item in items)
            {
                List<double> c = item switch
                {
                    string t => t.Split(',').Select(v => double.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out double d) ? d : double.NaN).ToList(),
                    List<object> l => l.Select(v => v is double d ? d : double.NaN).ToList(),
                    _ => null,
                };
                if (c == null || c.Count != 3 || c.Any(v => double.IsNaN(v) || v != Math.Floor(v) || v < int.MinValue || v > int.MaxValue))
                    throw new OpError("bad_request", "each point is three whole numbers x,y,z");
                points.Add(new Point3D((int) c[0], (int) c[1], (int) c[2]));
            }
            return points;
        }

        private static Dictionary<string, object> SpawnInfo(Spawn s)
        {
            GameNPC n = s.Npc;
            var info = new Dictionary<string, object>
            {
                ["id"] = s.Id, ["kind"] = s.Kind, ["name"] = n.Name, ["objectId"] = (int) n.ObjectID, ["model"] = (int) n.Model,
                ["level"] = (int) n.Level, ["realm"] = RealmName(n.Realm),
                ["inWorld"] = n.ObjectState == GameObject.eObjectState.Active,
                ["position"] = Pos(n.CurrentRegionID, n.X, n.Y, n.Z, n.Heading),
                ["moving"] = n.IsMoving,
                ["createdBy"] = s.Actor, ["created"] = s.Created.ToString("o"),
            };
            if (s.Kind != "npc")
            {
                info["seats"] = n.MAX_PASSENGERS;
                info["riders"] = n.CurrentRiders.Select(r => (object) r.Name).ToList();
            }
            if (n is GameSiegeWeapon sw) info["controlledBy"] = sw.Owner?.Name;
            if (s.Path != null || s.Finished)
                info["path"] = new Dictionary<string, object>
                {
                    ["points"] = s.Path?.Count, ["next"] = s.Path == null ? null : (object) s.Next, ["loop"] = s.Loop,
                    ["speed"] = (int) s.Speed, ["finished"] = s.Finished,
                };
            return info;
        }

        // Drops spawns that Core removed itself: a ram decays and dies, an empty boat goes after 15 minutes.
        private static void PruneSpawns()
        {
            foreach (string id in _spawns.Where(kv => kv.Value.Npc.ObjectState == GameObject.eObjectState.Deleted)
                         .Select(kv => kv.Key).ToList())
                _spawns.Remove(id);
        }

        private static Spawn SpawnById(Request req)
        {
            string id = StringArg(req, "id");
            PruneSpawns();
            return _spawns.TryGetValue(id, out Spawn s) ? s : throw new OpError("not_found", "no admin spawn " + id + " (spawn.list)");
        }

        private static string NewSpawnId()
        {
            string id;
            do id = "s-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            while (_spawns.ContainsKey(id));
            return id;
        }

        private static string OptStringArg(Request req, string key) =>
            req.Args.TryGetValue(key, out object v) && v != null ? StringArg(req, key) : null;

        private static int RangeArg(Request req, string key, int fallback, int min, int max)
        {
            int v = OptIntArg(req, key) ?? fallback;
            if (v < min || v > max)
                throw new OpError("invalid", "args." + key + " must be " + min + " to " + max);
            return v;
        }

        // ---- helpers -----------------------------------------------------------------------------

        // eRealm has alias names (_FirstPlayerRealm = Albion), so ToString() can't be used.
        private static string RealmName(eRealm realm) => (int) realm switch
        {
            0 => "None", 1 => "Albion", 2 => "Midgard", 3 => "Hibernia", _ => realm.ToString(),
        };

        private static GamePlayer OnlinePlayer(Request req)
        {
            string name = StringArg(req, "name");
            return InWorld(name) ?? throw new OpError("not_online", name + " is not online");
        }

        // A player is findable by name from the end of AddToWorld, while the world-init handler may still
        // be sending its first packets on another thread. Only EnteredGame (set by the player-init
        // handler, just before GameEntered) means it is fully in the world.
        private static GamePlayer InWorld(string name)
        {
            GamePlayer p = ClientService.Instance.GetPlayerByExactName(name);
            return p != null && p.EnteredGame && p.ObjectState == GameObject.eObjectState.Active ? p : null;
        }

        private static object Get(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) ? v : null;

        private static bool BoolArg(Request req, string key, bool fallback)
        {
            if (!req.Args.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is bool b) return b;
            throw new OpError("bad_request", "args." + key + " must be true or false");
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

        private static int? OptIntArg(Request req, string key) =>
            req.Args.TryGetValue(key, out object v) && v != null ? IntArg(req, key) : null;

        // Money's largest value: Core keeps each denomination modulo its next one (Money.GetMithril: % 1000).
        private static readonly long MaxMoney = Money.GetMoney(999, 999, 999, 99, 99);

        private static long MoneyArg(Request req)
        {
            long total;
            if (req.Args.ContainsKey("amount"))
            {
                if (req.Args["amount"] is not double d || d != Math.Floor(d) || d < 0 || d > MaxMoney)
                    throw new OpError("bad_request", "args.amount must be a whole number of copper, 0 to " + MaxMoney);
                total = (long) d;
            }
            else
            {
                string[] parts = { "mithril", "platinum", "gold", "silver", "copper" };
                if (!parts.Any(req.Args.ContainsKey))
                    throw new OpError("bad_request", "give args.amount (copper) or any of " + string.Join(", ", parts));
                int[] n = parts.Select(k => OptIntArg(req, k) ?? 0).ToArray();
                if (n.Any(v => v < 0))
                    throw new OpError("bad_request", "money parts can't be negative");
                total = Money.GetMoney(n[0], n[1], n[2], n[3], n[4]);
            }
            if (total > MaxMoney)
                throw new OpError("invalid", "at most " + Money.GetString(MaxMoney));
            return total;
        }

        // A position Core can put a player at: a loaded region with a zone under x, y.
        private static void CheckPosition(int region, int x, int y, int? heading)
        {
            Region rgn = region is >= 0 and <= ushort.MaxValue ? WorldMgr.GetRegion((ushort) region) : null;
            if (rgn == null)
                throw new OpError("invalid", "no region " + region + " is loaded");
            if (rgn.GetZone(x, y) == null)
                throw new OpError("invalid", "region " + region + " (" + rgn.Description + ") has no zone at x " + x + ", y " + y);
            if (heading is < 0 or > 4095)
                throw new OpError("invalid", "heading must be 0 to 4095");
        }

        private static Dictionary<string, object> Pos(int region, int x, int y, int z, int heading) => new()
        {
            ["region"] = region, ["x"] = x, ["y"] = y, ["z"] = z, ["heading"] = heading,
        };

        private static void Audit(Request req, object before, object after, object result, string phase = null)
        {
            var entry = new Dictionary<string, object>
            {
                ["time"] = DateTime.UtcNow.ToString("o"),
                ["peerUid"] = req.PeerUid,
                ["actor"] = req.Actor,
                ["op"] = req.Op,
                ["args"] = req.Args,
                ["before"] = before,
                ["after"] = after,
                ["result"] = result,
            };
            if (phase != null) entry["phase"] = phase; // queued, applied, refused at login, failed at login, cancelled
            string line = Json.Write(entry);
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
