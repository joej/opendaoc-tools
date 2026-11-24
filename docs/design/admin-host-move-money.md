# Admin host: move, money and read-back (design and test results, 2026-11-24)

Follows the [first slice](admin-host.md) and [queued edits](admin-host-queued-edits.md). The
operations come from a survey of the tasks that need a person in-game on a test server. The ones
asked for most:

- put a test character at a spot, and put it back afterwards;
- give it money, for merchant, trainer and siege tests;
- read its state back, so that a headless run can check a result instead of someone watching.

A GM can do the first two with `/jump` and `/money`, but GM exemptions hide behaviour that's being
tested (speed checks, the quit timer). The host acts on an ordinary player.

## Operations

| Op | Args | What it does |
|---|---|---|
| `player.move` | `name`, `region`, `x`, `y`, `z`, optional `heading` (0 to 4095; default: keep), `queue` | `GamePlayer.MoveTo`, as `/jump` calls it, then `SaveIntoDatabase()`. The answer has `before` and `after`, so a caller can move the character back |
| `player.money.set` | `name`, then `amount` (copper) or any of `mithril`, `platinum`, `gold`, `silver`, `copper`; `queue` | Sets an absolute amount with `AddMoney` or `RemoveMoney` (both send `0xFA`), then `SaveIntoDatabase()` (`accountxmoney`) |
| `player.get` | `name` | Adds client state and link-death, position and zone, alive, health, power and endurance, money as text, inventory (slot, id, name, count) and effects (name, type, spell, icon, time left) |

Both edits follow the first slice's rules. "Online" means `EnteredGame`. The player gets a line in
their system window. Each change writes one audit line, and an edit that changes nothing is not
audited.

### Checks

- **Position:** the region must be loaded and have a zone under x, y; the heading must be 0 to 4095.
  It's checked when the move is requested and again when a queued move is applied. If `MoveTo`
  refuses (the server rules don't allow the region), the answer is `invalid`.
- **Money:** whole numbers, no negatives, and at most 999 mithril, 999 platinum, 999 gold,
  99 silver, 99 copper. Core keeps each denomination modulo the next one up
  (`Money.GetMithril` is `% 1000`), so a larger amount would wrap.

### Queued

`queue: true` on a character that isn't in the world queues the edit
([queued edits](admin-host-queued-edits.md)). A newer edit of the same kind for the same character
replaces the older one.

- **Move:** applied after the character enters the world. The client loads the saved position first,
  then gets the jump or the region change. A pre-login hook could avoid that first load, but the
  client picks its region from the saved row at character select, before any world event. The answer
  shows the saved position, with `Direction` masked to its 12 heading bits: Core can save a flag
  above them (lab: 7212 for heading 3116).
- **Money:** applied when the named character enters the world. Money is shared by an account's
  characters in one realm, and the answer says so. The `saved` value is the realm's row in
  `accountxmoney`, or the character's own columns if there is none.
- **At login:** edits apply in queue order, with moves last, because a move to another region takes
  the player out of the world.

### The client must follow a server move

The server takes the position the client reports in `0xA9`. After a move, the client must take the
new position from `0x04` (same region) or follow `0xB7` (new region) straight away. Otherwise its next
`0xA9` reports the old spot and the server believes it. A headless client that ignored `0x04` was put
back within a second. One that ignored `0xB7` went link-dead.

## Tested (2026-11-24, Core `0ac41d443`)

On the lab copy. The headless client follows `0x04` and `0xB7` the way the game client's network layer does.

| Check | Result |
|---|---|
| Startup | Compiles in the server's own script compiler |
| `player.get` | Position, zone, health, power, endurance, client state, 9 inventory items and one effect, all matching what the client was sent |
| Money online | 0 → 5 platinum → unchanged (`changed: false`) → 1 gold 2 silver 3 copper. `accountxmoney` matched after each step |
| Money refusals | No amount, a negative part, a fraction, and 1000 mithril are each refused with `bad_request` or `invalid` |
| Move refusals | Unknown region, no zone at x, y, heading 5000 and a missing coordinate: `invalid` or `bad_request` |
| Move in region | The client got `0x04` 60 ms after the call. `player.get` and `dolcharacters` show the new spot and heading |
| Move to another region | The client got `0xB7` and re-entered in the new region 160 ms after the call; `player.get` found the player in the new region 1 s later. The database has it |
| Same spot again | `changed: false`, no packet, no audit line |
| Queued | A move, two money edits (the second replaced the first) and a spec edit were queued for an offline character; the database was unchanged. At login, after `0xE8`: money (`0xFA`), then the spec, then the move (`0xB7`, re-entry in the new region). The store was empty afterwards, and the audit had `queued` and `applied` lines |
| Queued refusals | Unknown character: `not_found`. No zone at x, y: `invalid` |
| Restore | Position, money and spec queued back to the baseline and applied at the next login |

Not tested: `MoveTo` refused by the server rules (the lab's rules allow every region), a move while
mounted, in a house or with a pet, and `player.get` on a link-dead player.
