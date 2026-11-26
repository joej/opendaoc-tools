// AdminHostPackets -- TEST SERVERS ONLY. Adds packet capture to AdminHost.
//
// Design: docs/design/admin-host-packets.md. A capture records every packet one account sends and
// receives, in both directions, for a set time, and keeps it in memory for packet.capture.get.
// It shows everything the player sends, chat and whispers included, so never install this file on a
// server that real players use.
//
// It uses Core's PacketTrace (GameServer/packets/Server/PacketTrace.cs), which logs one line per
// packet at Trace level to the NLog logger "DOL.GS.PacketTrace" and masks the login password and the
// encryption keys. While any capture runs, this file adds an NLog rule for that logger to its own
// target; once none runs, it removes the rule again, and Core stops formatting packets.
//
// Install: copy this file next to AdminHost.cs in the server's scripts/ folder and restart. It is a
// part of AdminHost's partial class and doesn't work without AdminHost.cs.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DOL.Database;
using DOL.GS;
using DOL.GS.PacketHandler;

namespace OpenDaocTools.Scripts
{
    public static partial class AdminHost
    {
        private const string TraceLogger = "DOL.GS.PacketTrace";
        private const int MaxRunningCaptures = 4;
        private const int MaxStoredCaptures = 8;
        private static readonly TimeSpan CaptureKeep = TimeSpan.FromHours(1);
        // Core's log lines can arrive a moment after the packet: the rule stays this long after the last capture ends.
        private static readonly TimeSpan CaptureGrace = TimeSpan.FromSeconds(2);

        static partial void AddOps(Dictionary<string, OpSpec> ops)
        {
            ops["packet.capture.start"] = new OpSpec { Run = CaptureStart };
            ops["packet.capture.get"] = new OpSpec { Run = CaptureGet };
            ops["packet.capture.stop"] = new OpSpec { Run = CaptureStop };
            ops["packet.capture.list"] = new OpSpec { Run = CaptureList };
            ops["packet.capture.delete"] = new OpSpec { Run = CaptureDelete };
        }

        static partial void StopExtras()
        {
            List<Capture> running;
            lock (_capLock) running = _captures.Values.Where(c => c.Running).ToList();
            foreach (Capture c in running) FinishCapture(c, "server stopping");
            RemoveRule();
        }

        // ---- state -------------------------------------------------------------------------------

        private sealed class Capture
        {
            public string Id, Account, CreatedBy, StopReason;
            public DateTime Started, Ends;
            public DateTime? Stopped;
            public int Max, Dropped;
            public readonly List<string[]> Lines = new();
            public Timer Timer;
            public bool Running => Stopped == null;
        }

        private sealed class CaptureTarget : NLog.Targets.Target
        {
            protected override void Write(NLog.LogEventInfo e)
            {
                // logconfig.xml sets throwExceptions: never let anything out of here.
                try { CaptureLine(e.FormattedMessage); } catch { }
            }
        }

        // _capLock guards the captures and is the only lock taken on NLog's writing thread.
        // _ruleLock guards the NLog rule; it is never held together with _capLock.
        private static readonly object _capLock = new();
        private static readonly object _ruleLock = new();
        private static readonly Dictionary<string, Capture> _captures = new();
        private static NLog.Config.LoggingConfiguration _ruleConfig;
        private static NLog.Config.LoggingRule _rule;
        private static CaptureTarget _target;
        private static Timer _ruleTimer;

        // ---- ops ---------------------------------------------------------------------------------

        private static object CaptureStart(Request req)
        {
            string name = OptStringArg(req, "name"), account = OptStringArg(req, "account");
            if ((name == null) == (account == null))
                throw new OpError("bad_request", "give args.name (an online player) or args.account (an account name)");
            int seconds = RangeArg(req, "seconds", 60, 1, 900);
            int max = RangeArg(req, "max", 5000, 1, 20000);

            if (name != null)
            {
                GamePlayer p = OnlinePlayer(req);
                account = p.Client?.Account?.Name ?? throw new OpError("failed", name + " has no account");
            }
            else
            {
                // Need not be logged in: a capture started first records the login itself.
                DbAccount a = GameServer.Database.FindObjectByKey<DbAccount>(account)
                    ?? throw new OpError("not_found", "no account " + account);
                account = a.Name;
            }

            Capture c;
            lock (_capLock)
            {
                PruneCaptures();
                if (_captures.Values.Count(x => x.Running) >= MaxRunningCaptures)
                    throw new OpError("busy", MaxRunningCaptures + " captures are already running");
                if (_captures.Count >= MaxStoredCaptures)
                {
                    Capture oldest = _captures.Values.Where(x => !x.Running).OrderBy(x => x.Started).FirstOrDefault()
                        ?? throw new OpError("busy", MaxStoredCaptures + " captures are stored and none has finished");
                    _captures.Remove(oldest.Id);
                }

                DateTime now = DateTime.UtcNow;
                c = new Capture
                {
                    Id = NewCaptureId(), Account = account, CreatedBy = req.Actor ?? "unknown",
                    Started = now, Ends = now.AddSeconds(seconds), Max = max,
                };
                _captures[c.Id] = c;
            }

            if (!AddRule())
            {
                lock (_capLock) _captures.Remove(c.Id);
                throw new OpError("failed", "NLog didn't enable Trace on " + TraceLogger + "; nothing is captured");
            }
            Capture started = c;
            c.Timer = new Timer(_ => FinishCapture(started, "time"), null, seconds * 1000, Timeout.Infinite);

            object info = CaptureInfo(c);
            Audit(req, null, info, info);
            return info;
        }

        // Lines from `from` (0-based), at most `limit`; optional filters `dir` (C>S, S>C, EVT) and `opcode` (hex).
        private static object CaptureGet(Request req)
        {
            Capture c = CaptureById(req);
            int from = RangeArg(req, "from", 0, 0, int.MaxValue);
            int limit = RangeArg(req, "limit", 500, 1, 5000);
            string dir = OptStringArg(req, "dir");
            if (dir != null && dir != "C>S" && dir != "S>C" && dir != "EVT")
                throw new OpError("invalid", "args.dir must be C>S, S>C or EVT");
            // Hex. A number is read as its digits (adminctl.py sends opcode=12 as 12, meaning 0x12).
            string opcode = Get(req.Args, "opcode") switch
            {
                null => null,
                double d when d >= 0 && d == Math.Floor(d) => ((long) d).ToString(CultureInfo.InvariantCulture),
                string s => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s,
                _ => "?",
            };
            opcode = opcode?.ToUpperInvariant();
            if (opcode != null && !byte.TryParse(opcode, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                throw new OpError("invalid", "args.opcode must be a hex byte, such as A1");
            if (opcode != null) opcode = opcode.PadLeft(2, '0');

            var lines = new List<object>();
            int next;
            Dictionary<string, object> info;
            lock (_capLock)
            {
                int i = from;
                for (; i < c.Lines.Count && lines.Count < limit; i++)
                {
                    string[] l = c.Lines[i];
                    if (dir != null && l[3] != dir) continue;
                    if (opcode != null && l[5] != opcode) continue;
                    lines.Add(LineInfo(i, l));
                }
                next = i;
                info = CaptureInfo(c);
            }
            info["from"] = from;
            info["next"] = next;
            info["returned"] = lines.Count;

            // Who read whose packets, not what they said.
            Audit(req, null, null, new Dictionary<string, object> { ["id"] = c.Id, ["account"] = c.Account, ["returned"] = lines.Count });
            info["packets"] = lines;
            return info;
        }

        private static object CaptureStop(Request req)
        {
            Capture c = CaptureById(req);
            bool changed = FinishCapture(c, "stopped by " + (req.Actor ?? "unknown"));
            Dictionary<string, object> info;
            lock (_capLock) info = CaptureInfo(c);
            info["changed"] = changed;
            Audit(req, null, null, info);
            return info;
        }

        private static object CaptureList(Request req)
        {
            lock (_capLock)
            {
                PruneCaptures();
                return new Dictionary<string, object>
                {
                    ["traceOn"] = TraceOn(),
                    ["captures"] = _captures.Values.OrderBy(c => c.Started).Select(CaptureInfo).ToList(),
                };
            }
        }

        private static object CaptureDelete(Request req)
        {
            List<Capture> gone;
            lock (_capLock)
            {
                if (BoolArg(req, "all", false))
                    gone = _captures.Values.ToList();
                else
                    gone = new List<Capture> { CaptureById(req) };
            }
            foreach (Capture c in gone) FinishCapture(c, "deleted");
            var deleted = new List<object>();
            lock (_capLock)
                foreach (Capture c in gone)
                {
                    _captures.Remove(c.Id);
                    deleted.Add(CaptureInfo(c));
                }
            var result = new Dictionary<string, object> { ["deleted"] = deleted };
            Audit(req, null, null, result);
            return result;
        }

        // ---- capturing ---------------------------------------------------------------------------

        // One PacketTrace line: time, connection, account, direction, transport, opcode, length, body.
        private static void CaptureLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string[] cols = message.Split('\t', 8);
            if (cols.Length < 8) return;

            lock (_capLock)
            {
                DateTime? time = null;
                foreach (Capture c in _captures.Values)
                {
                    if (!string.Equals(cols[2], c.Account, StringComparison.OrdinalIgnoreCase)) continue;
                    time ??= DateTime.TryParseExact(cols[0], "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime t) ? t : DateTime.UtcNow;
                    // Keep what happened inside the window, even if the line arrives a moment later.
                    if (time < c.Started || time > (c.Stopped ?? c.Ends)) continue;
                    if (c.Lines.Count < c.Max) c.Lines.Add(cols);
                    else c.Dropped++;
                }
            }
        }

        private static bool FinishCapture(Capture c, string reason)
        {
            bool changed;
            lock (_capLock)
            {
                changed = c.Running;
                if (changed)
                {
                    DateTime now = DateTime.UtcNow;
                    c.Stopped = now < c.Ends ? now : c.Ends;
                    c.StopReason = reason;
                }
                c.Timer?.Dispose();
                c.Timer = null;
                if (_captures.Values.Any(x => x.Running)) return changed;
            }
            // None runs: drop the rule once late lines have arrived (unless a new capture starts first).
            lock (_ruleLock)
            {
                _ruleTimer?.Dispose();
                _ruleTimer = new Timer(_ =>
                {
                    bool any;
                    lock (_capLock) any = _captures.Values.Any(x => x.Running);
                    if (!any) RemoveRule();
                }, null, (int) CaptureGrace.TotalMilliseconds, Timeout.Infinite);
            }
            return changed;
        }

        private static bool AddRule()
        {
            lock (_ruleLock)
            {
                _ruleTimer?.Dispose();
                _ruleTimer = null;
                NLog.Config.LoggingConfiguration config = NLog.LogManager.Configuration;
                if (config == null) return false;
                // A reloaded logconfig.xml drops our rule: add it again to the configuration in use.
                if (_rule == null || !ReferenceEquals(config, _ruleConfig) || !config.LoggingRules.Contains(_rule))
                {
                    _target ??= new CaptureTarget { Name = "admin-host-capture" };
                    _rule = new NLog.Config.LoggingRule(TraceLogger, NLog.LogLevel.Trace, NLog.LogLevel.Trace, _target);
                    config.LoggingRules.Insert(0, _rule);
                    _ruleConfig = config;
                    NLog.LogManager.ReconfigExistingLoggers();
                }
                return TraceOn();
            }
        }

        private static void RemoveRule()
        {
            lock (_ruleLock)
            {
                if (_rule == null) return;
                try
                {
                    _ruleConfig?.LoggingRules.Remove(_rule);
                    NLog.LogManager.ReconfigExistingLoggers();
                }
                catch (Exception ex) { log.Error("AdminHost could not remove the packet capture rule", ex); }
                _rule = null;
                _ruleConfig = null;
            }
        }

        private static bool TraceOn() => NLog.LogManager.GetLogger(TraceLogger).IsTraceEnabled;

        // ---- helpers -----------------------------------------------------------------------------

        private static void PruneCaptures()
        {
            DateTime cutoff = DateTime.UtcNow - CaptureKeep;
            foreach (Capture c in _captures.Values.Where(x => !x.Running && x.Stopped < cutoff).ToList())
                _captures.Remove(c.Id);
        }

        private static Capture CaptureById(Request req)
        {
            string id = StringArg(req, "id");
            lock (_capLock)
                return _captures.TryGetValue(id, out Capture c) ? c : throw new OpError("not_found", "no capture " + id);
        }

        private static string NewCaptureId()
        {
            string id;
            do id = "c-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            while (_captures.ContainsKey(id));
            return id;
        }

        private static Dictionary<string, object> CaptureInfo(Capture c) => new()
        {
            ["id"] = c.Id,
            ["account"] = c.Account,
            ["createdBy"] = c.CreatedBy,
            ["started"] = c.Started.ToString("o"),
            ["ends"] = c.Ends.ToString("o"),
            ["running"] = c.Running,
            ["stopped"] = c.Stopped?.ToString("o"),
            ["stopReason"] = c.StopReason,
            ["lines"] = c.Lines.Count,
            ["max"] = c.Max,
            ["dropped"] = c.Dropped,
        };

        private static Dictionary<string, object> LineInfo(int n, string[] l) => new()
        {
            ["n"] = n,
            ["time"] = l[0],
            ["connection"] = l[1],
            ["dir"] = l[3],
            ["transport"] = l[4],
            ["opcode"] = l[5],
            ["name"] = OpcodeName(l[3], l[5]),
            ["length"] = int.TryParse(l[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int len) ? len : -1,
            ["body"] = l[7],
        };

        private static Dictionary<byte, string> _serverNames, _clientNames;

        // Core's own names (eServerPackets, eClientPackets); the first name wins where an opcode has two.
        private static string OpcodeName(string dir, string opcode)
        {
            if (!byte.TryParse(opcode, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code)) return null;
            Dictionary<byte, string> names = dir switch
            {
                "S>C" => _serverNames ??= Names<eServerPackets>(),
                "C>S" => _clientNames ??= Names<eClientPackets>(),
                _ => null,
            };
            return names != null && names.TryGetValue(code, out string name) ? name : null;
        }

        private static Dictionary<byte, string> Names<T>() where T : struct, Enum
        {
            var d = new Dictionary<byte, string>();
            foreach (string n in Enum.GetNames(typeof(T)))
                d.TryAdd(Convert.ToByte(Enum.Parse(typeof(T), n), CultureInfo.InvariantCulture), n);
            return d;
        }
    }
}
