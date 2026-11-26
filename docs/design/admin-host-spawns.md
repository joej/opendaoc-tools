# Admin host: spawns, boats and rams (design and test results, 2026-11-25)

Follows [revive, items and level](admin-host-revive-items-level.md). Client and protocol work keeps
needing a particular object in front of a test character, so a tester can watch how the client
reacts to it:

- a boat to board and sail along a path (the moving-object packet `0x12`, riding `0xC8`, position
  updates `0xA1`);
- a siege ram to sit in and take control of;
- a plain NPC at a chosen spot, for line-of-sight and targeting tests.

Until now this meant a GM finding a boat, building a ram, or using `/mob create` and remembering
to delete the mob afterwards (a `/mob` spawn is saved to the `mob` table).

| Op | Args | What it does |
|---|---|---|
| `spawn.npc` | position, optional `label`, `model` (408), `level` (1), `size` (50), `speed` (200), `realm` (0), `brain` (`none` or `standard`), `peace` | An NPC. `brain=none` stands still; `standard` is Core's mob brain |
| `spawn.boat` | position, optional `label`, `model` (2648, the scout boat), `speed` (500), `realm` | A `GameBoat`. The model sets the seats, as Core's boat table does (8 to 64). Other models are accepted with 8 seats |
| `spawn.ram` | position, optional `size` (`mini`, `light`, `medium`, `heavy`), `label`, `realm` | A `GameSiegeRam` with the level, name and model that Core's ram summon spell gives each size |
| `spawn.list` | | Every admin spawn: id, kind, object id, model, realm, position, moving, riders, controller, path |
| `spawn.delete` | `id`, or `all=true` | Removes it. Riders are dismounted, and whoever controls a ram loses control |
| `spawn.board` | `id`, `name`, optional `control` (true) | Seats an online player as a double-click does (`MountSteed`, forced). For a ram, `control` also gives them the siege controls, as summoning one does |
| `spawn.unboard` | `name` | Dismounts the player and releases any siege weapon they control |
| `spawn.path` | `id`, `points`, optional `speed`, `loop` | Moves the spawn through the points, in a straight line from each to the next |
| `spawn.stop` | `id` | Stops the path and the movement |

**Position.** Either `near=<player>`, which spawns `distance` units in front of the player
(defaults: NPC and ram 150, boat 300), facing them, in their region; or `region`, `x`, `y`, `z` and an
optional `heading`. Both are checked like `player.move`: the region must be loaded, with a zone under
x, y. The realm defaults to the `near` player's realm (NPCs: none), and `realm` (0 to 3) sets it.

**Points.** `"x,y,z;x,y,z"` (works from `adminctl.py`) or a JSON array of `[x, y, z]`. At most 64
points, each checked against the spawn's region. The movement is Core's `WalkTo`, not `PathTo`, so it
doesn't use the navmesh: a boat on water has none, and a straight line is what a test path usually
wants. A timer on the spawn sends it on to the next point when it arrives. With `loop=true` it starts
over at the first point.

## Never saved

Each spawn is one of Core's classes (`GameNPC`, `GameBoat`, `GameSiegeRam`) with `SaveIntoDatabase`
and `DeleteFromDatabase` overridden to do nothing. So:

- the client receives exactly the packets the real object would send;
- a GM command that saves the targeted object (`/mob save`) does nothing to it;
- a restart clears them all.

The boat is created without Core's `BoatMgr`, which would add a `playerboats` row. Its `OwnerID` is
empty, because `GameBoat.Interact` refuses a double-click on a boat that has an owner.

Core can also remove a spawn itself. A ram decays and is deleted once its health runs out, and an
empty boat is deleted 15 minutes after its last rider leaves. `spawn.list` then drops it. At most 50
spawns exist at once.

Every create, delete, board, path and stop writes one audit line, as the player edits do.

## Riders on a moving boat

The server doesn't move riders with the boat: a rider's position is whatever their client reports.
In the lab test, the boat sailed 600 units east and 600 north with LabChar seated. `spawn.list`
showed the boat at its last point, while `player.get` kept LabChar at the boarding spot, because
the headless client sends no position while seated. A real client reports its own position as the
boat moves.

## Tested (2026-11-25, Core `0ac41d443`)

On the lab copy with the headless client (`DaocCli --log`).

| Check | Result |
|---|---|
| Spawn each kind near LabChar | NPC, boat and ram in front of LabChar, facing it, with object ids. The client got `0xDA` for the NPC and `0x12` for the boat and the ram |
| Board the boat | `seat: 1`, `riders: ["LabChar"]`; the client got `0xC8` (riding) and `0x04` (moved onto the boat). Boarding again: `changed: false` |
| Path | Two points 600 units apart at speed 300: moving with `next: 1` after 3 s, at the last point with `finished: true` after 8 s. The client got the boat's `0xA1` updates throughout |
| Unboard | `0xC8` again; unboarding again: `changed: false` |
| Ram | Board with control: `controlledBy: LabChar`; unboard released it |
| Loop and stop | An NPC looping between two points stopped where it was |
| Delete | One by id, then `all=true`; `spawn.list` empty |
| Refusals | Boarding an NPC, an unknown id, a point with two numbers, a point outside any zone, `size=huge`, `realm=5`, `near` a player who isn't online, a spawn outside any zone |
| Nothing saved | `mob` 97,737 rows and `playerboats` 0, before and after |

Not tested:

- a real client boarding by double-click (`spawn.board` makes the same calls as Core's
  `GameNPC.Interact`), or a real client steering the ram;
- Core removing a spawn itself (the 15-minute boat removal, ram decay), and `spawn.list` dropping it;
- `brain=standard` with aggression.

## Packet capture: what Core offers

Client tests also want to see what the client sends back, not only what the server sent. Core
`0ac41d443` already has `PacketTrace`: one tab-separated line per packet in both directions (TCP and
UDP), with the body in hex, the account name in the third column, and the login password and
encryption keys masked. It logs at Trace level to the NLog logger `DOL.GS.PacketTrace`.
`logconfig.xml` has a disabled rule for it, so turning it on currently means editing the file and
restarting.

A restart isn't needed. Core's `IsTraceEnabled` asks NLog each time, and a script can add an NLog
rule at runtime. On the lab, a throwaway command (not in this repo) did that:

1. added a rule for `DOL.GS.PacketTrace` at Trace to a target defined in the script, which kept only
   one account's lines;
2. called `LogManager.ReconfigExistingLoggers()`;
3. removed the rule eight seconds later.

It recorded 40 lines in those eight seconds: the account's `0xA9` and `0xA3`, and the server's
`0xA1`, `0xAF`, `0x29` and the `0x12` of a boat spawned during the window, in full hex. Afterwards
`IsTraceEnabled` was false again, and nothing went to the configured trace file. Scripts compile
against NLog without trouble, since Core has already loaded it.

So `packet.capture` (one account, N seconds, decoded opcodes) is practical without changing Core.
While a capture runs, every client's packets are formatted, which costs CPU on a busy server; a test
server can afford it. The capture shows everything the player sends, including chat, so it belongs
with the raw packet sender in a separate test-only script, not in this host.
