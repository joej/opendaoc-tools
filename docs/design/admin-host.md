# Admin host: first slice (design, 2026-11-20)

The in-process admin host from gap 4 of the [ops-tooling inventory](../research/ops-tooling-inventory.md#part-3--gaps):
a drop-in script that lets an operator tool read and change the **running** server, not just its
database. This note fixes the first slice. It builds on two results:

- [Appendix A](../research/ops-tooling-inventory.md#appendix-a--what-a-write-needs-to-reach-live-state), now
  tested on a lab copy: cached data needs an explicit refresh, and several refreshes have no command.
- [Live character edits](../research/live-character-edits.md#tested-on-a-lab-copy-2026-11-19-core-0ac41d443):
  an online character never sees a database edit, and whether the next save keeps or overwrites it
  depends on what the player does in the meantime. Changes to online characters have to go through
  the live object.

## Shape

| | Choice | Why |
|---|---|---|
| Delivery | One drop-in script, `server-scripts/admin-host/AdminHost.cs` | No Core fork; same route as the probe ([gap 4, option c](../research/ops-tooling-inventory.md#part-3--gaps)) |
| Transport | Unix domain socket, mode 0600, one JSON object per line each way | Only the server's own user can connect, so ssh is the authentication. No port to expose |
| Socket path | `<server root>/admin.sock`, overridable with the `OPENDAOC_ADMIN_SOCKET` environment variable | Works for systemd and Docker (mount the directory) |
| Start | `[GameServerStartedEvent]`; stops on `[GameServerStoppedEvent]` | Opens only once the world is loaded |
| Audit | Every request that changes something: one JSON line in `logs/admin-host.jsonl` (time, peer uid, actor, op, args, before, after, result), and an `INF` line in the server log | Append-only, greppable, survives a crash |

### Requests and answers

```json
{"id": "7", "actor": "joe", "op": "player.spec.set", "args": {"name": "LabChar", "spec": "Slash", "level": 5}}
{"id": "7", "ok": true, "result": {"before": 4, "after": 5, "specPoints": 3, "saved": true}}
{"id": "8", "ok": false, "error": {"code": "not_online", "message": "LabChar is not online"}}
```

`actor` is what the calling tool says about who is acting. The host also records the connecting
process's uid (`SO_PEERCRED`), which the caller can't fake. Error codes are fixed strings
(`bad_request`, `unknown_op`, `not_found`, `not_online`, `invalid`, `timeout`, `failed`), so tools
can act on them.

### Threading

Checked in Core `0ac41d443`:

- The game loop runs its services in a fixed order each tick (`GameLoop.BuildTickSequence`). Inside
  one service, objects are processed in parallel on the loop's worker threads.
- Players' packets, slash commands included, are handled in `ClientService.BeginTick`, in parallel
  across clients.
- Console commands run on the console thread, outside the loop. Core already does
  `serverproperties refresh` and `reload ...` this way.
- Core has a queue for outside work: `GameServiceBase.Post`. Work posted to `ClientService` runs at
  the start of `ClientService.BeginTick`, and finishes before any client's packets for that tick
  are handled.

So the socket thread only reads and parses. Every operation, reads included, is posted to
`ClientService` and runs on the loop, at a point where no player packet is being handled. Posted
actions run in parallel with each other, so the host posts **one** action per tick that works
through its own queue in order. The socket thread waits for the result with a timeout (5 s) and
answers `timeout` if the loop is stuck. It never blocks the loop.

## Operations in the first slice

| Op | What it does | Class (Appendix A) |
|---|---|---|
| `server.status` | Uptime, players online, tick rate (from what `StatPrint` already measures) | read |
| `player.list` | Online players: name, account, level, class, region, zone | read |
| `player.get` | One online player: level, money, realm points, specs with levels, spec points | read |
| `player.spec.set` | Sets one spec's level on an **online** player through the live object | C |
| `cache.reload` | `loot-templates`, `loot-mobtemplates`, `loot-otd`, `quests`, `npctemplates`, `spells`, `properties`, `item <id_nb>` | B |

### `player.spec.set`, step by step

The same calls as Core's trainer (`commands/playercommands/train.cs`), minus the trainer window:

1. Find the online player and the spec (`GetSpecializationByName`). Not online: `not_online`.
   Offline edits stay out of the first slice.
2. Check: `level` is from 1 to the player's base level. Raising costs points, and spec points are
   computed rather than stored (`VerifySpecPoints`). So refuse with `invalid` if the new level would
   make them negative, unless `"force": true`. Forced changes are flagged in the audit line.
3. Set `spec.Level`. When raising, call `CharacterClass.OnSkillTrained(player, spec)` as training does.
4. `RefreshSpecDependantSkills(true)`, then send the client what `train.cs` sends:
   `SendUpdatePoints`, `SendUpdatePlayer`, `SendCharResistsUpdate`, `SendCharStatsUpdate`,
   `SendUpdatePlayerSkills(true)`. Tell the player in their system window that an admin changed the spec.
5. `SaveIntoDatabase()`, so the change is in the database at once rather than at the next autosave.

### `cache.reload`

Calls the same methods as the console commands and the probe's `call`. `item <id_nb>` reloads one
item template into the pre-cache, as `/item load` does. `properties` has a known limit: 14 static
fields copy their property when their class loads, so `Properties.Refresh()` never reaches them
(Appendix A). The answer says so when it applies.

## Not in the first slice

- Offline character edits (database path with the same checks), level, realm points, money, items,
  position. Each needs its own "what the client must be told" check, as specs did.
- Kick, mute, broadcast, the appeal queue.
- The HTTP front for web tools, and any non-loopback access.
- Staging-diff apply (gap 3).

## Testing

On the lab copy with the headless client (`DaocCli`), as in the [cache-probe runbook](../runbooks/cache-probe.md):

- `player.spec.set` online: `player.get` shows the new level right away. The database has it after
  the call, and a relog keeps it. Refused when not online, when above base level, and when short of
  points without `force`. The client receives the skill and points updates: check with
  `DaocCli --log` packet codes.
- `cache.reload`: each target turns a stale probe value into the new one, as in the B tests.
- Transport: a second user can't connect (0600). A malformed line gets `bad_request` and the
  connection stays open. A loop stall gives `timeout` without hanging the socket thread.
- Audit: each change writes exactly one line with before and after.
- A small client for operators: `tools/adminctl.py` (one request per call, prints the answer).
