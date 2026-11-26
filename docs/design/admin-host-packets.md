# Admin host: packet capture, for test servers only (design and test results, 2026-11-25)

Follows [spawns, boats and rams](admin-host-spawns.md). A client test usually needs to see what the
client sends back, not only what the server sent. A capture records every packet one account sends
and receives, in both directions, for a set time. It keeps the packets in memory, where the tester
reads them with `packet.capture.get`.

A capture shows everything the player sends, including chat and whispers. In the lab test the
capture held `&say capturetest` as typed. So the ops are in their own file,
[`server-scripts/admin-host-test/AdminHostPackets.cs`](../../server-scripts/admin-host-test/AdminHostPackets.cs),
which goes only on test servers. `AdminHost.cs` on its own has no capture ops.

## How it fits into the host

Core compiles every file in `scripts/` into one assembly, so `AdminHostPackets.cs` is another part
of `AdminHost`'s partial class. `AdminHost.cs` declares two partial methods:

- `AddOps`, which it calls at startup before it listens;
- `StopExtras`, which it calls at shutdown.

Without the test-only file those calls compile to nothing; with it, `AddOps` registers the
`packet.capture.*` ops. The host logs a warning that names the test-only ops it loaded. An unknown op's
error also lists every op the host knows, so a client can tell whether capture is installed.

Install: copy `AdminHostPackets.cs` next to `AdminHost.cs` in the server's `scripts/` folder, then
restart. To remove it, delete the file and restart.

## Ops

| Op | Args | What it does |
|---|---|---|
| `packet.capture.start` | `name` (an online player) or `account`, optional `seconds` (60, 1 to 900), `max` (5000 lines, up to 20000) | Starts a capture; returns its id (`c-` plus 8 hex digits) and end time |
| `packet.capture.get` | `id`, optional `from` (0), `limit` (500, up to 5000), `dir` (`C>S`, `S>C` or `EVT`), `opcode` (hex) | Packets from line `from` on, filtered, and `next` to continue from |
| `packet.capture.stop` | `id` | Stops it early; `changed: false` if it had already stopped |
| `packet.capture.list` | | Every stored capture, and whether Core is tracing at all (`traceOn`) |
| `packet.capture.delete` | `id`, or `all=true` | Stops and forgets it |

Each packet comes back as `n`, `time`, `connection`, `dir`, `transport` (TCP or UDP), `opcode`,
`name`, `length` and `body` (hex). The name is Core's own (`eServerPackets` for S>C,
`eClientPackets` for C>S), such as `MovingObjectCreate` or `CommandHandler`. `EVT` lines are Core's
connection events, with `-` as the opcode and text as the body.

`opcode` is read as hex: `A1`, `0xA1` or `12`. `adminctl.py` sends `opcode=12` as the number 12, and
the op reads a number's digits as hex, so `opcode=12` means 0x12.

**Before login.** `account` need not be logged in, so a capture started first records the login. Core
labels a connection with its account only once the login is accepted. The capture starts at
`LoginGranted` (0x2A), and the login request, which carries the password, isn't in it. Core masks the
password and the encryption keys anyway.

**Limits.** At most 4 captures run at once and 8 are stored. When a 9th starts, the oldest finished
one is dropped; if none has finished, the start is refused. A finished capture is kept for an hour.
A capture that reaches `max` keeps counting the packets it drops (`dropped`).

**Audit.** Start, stop and delete write one audit line each, as the other ops do. So does every
`get`: it records who read which account's capture and how many packets came back, but not the
packets themselves.

## Where the packets come from

Core `0ac41d443` has `PacketTrace` (`GameServer/packets/Server/PacketTrace.cs`). It logs one
tab-separated line per packet, at Trace level, to the NLog logger `DOL.GS.PacketTrace`. The line holds
the time, connection, account, direction, transport, opcode, length and body hex. `logconfig.xml` has
a disabled rule for it.

While at least one capture runs, the script inserts an NLog rule that sends that logger, at Trace
level only, to a target of its own. It then calls `LogManager.ReconfigExistingLoggers()`. The target
keeps the lines of the accounts being captured, and only those inside each capture's window. Two
seconds after the last capture stops, the script removes the rule, because Core's log lines can
arrive a moment after the packet. Core's `IsTraceEnabled` then reads false again, and Core stops
formatting packets.

While the rule is on, Core formats every client's packets, not only the captured account's. That costs
CPU on a busy server, which a test server can afford. A reload of `logconfig.xml` drops the rule; the
next `start` adds it back, and `packet.capture.list` shows `traceOn: false` in the meantime.

## Tested (2026-11-25, Core `0ac41d443`)

On the lab copy with the headless client (`DaocCli`), logging LabChar in during a capture.

| Check | Result |
|---|---|
| Loaded | `Script compilation: True`, and a warning naming the five test-only ops. `AdminHost.cs` alone also compiles (`ScriptCheck.csproj` without the test-only folder) |
| Capture by account, before login | Started with `traceOn: true`; 247 to 273 lines over one login, from `LoginGranted` on. No `MASKED` lines: the login request isn't labelled with the account yet |
| Contents | C>S `CommandHandler` (0xAF) bodies `&say capturetest` and `&quit`, as typed; S>C `MovingObjectCreate` (0x12, 40 bytes) for a boat spawned during the capture; `ObjectUpdate`, `PingReply`, `QuestEntry` and the rest of world entry, named |
| Second capture, by online name, `max=10` | Ran alongside the first; stopped by time with `lines: 10`, `dropped: 26` |
| Filters | `dir=S>C opcode=12` gave the one 0x12. `from=5 limit=2` gave lines 5 and 6 with `next: 7`. `opcode` 12, `A1` and `0xa1` accepted |
| Stop | `stopReason: "stopped by labtest"`; stopping again gave `changed: false`; `traceOn: false` 3 s later |
| End by time | A 5 s capture ended with `stopReason: "time"`, and `traceOn` read false afterwards |
| Busy | A 5th running capture refused (`busy`); `delete all=true` cleared all 6, then `traceOn: false` |
| Refusals | Neither or both of `name` and `account`; an unknown account; `name` of a player who isn't online; `seconds=0`; `max=99999`; an unknown id; `dir=X`; `opcode` `ZZ` and `123` |
| Audit | Start, get (with `returned`, not the packets), stop and delete each wrote a line |

Not tested: a real client, a capture across a zone change, and a `logconfig.xml` reload during a
capture.
