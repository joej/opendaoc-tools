# Admin host: edits to offline characters (design, 2026-11-21)

Follows the [first slice](admin-host.md), which changes only characters that are in the world. This
note covers characters that aren't: the edit is checked when it's requested, queued, and applied
through the same in-game path when the character next enters the world.

## Why queue instead of writing the database

Three ways to change an offline character were considered:

| Way | Problem |
|---|---|
| Write the character's row (`dolcharacters`) | Nothing checks it: on the lab, Slash raised by SQL loaded as Slash 2 with spec points at -2, and Core respecs an ordinary player whose total is negative ([live character edits](../research/live-character-edits.md#tested-on-a-lab-copy-2026-11-19-core-0ac41d443)). It also skips what training triggers (`CharacterClass.OnSkillTrained`, spec-dependent abilities and spells) and races a login |
| Build a `GamePlayer` from the row with a stand-in client, edit, save, discard | Untested in any project we found (below). Building a player runs Core's load path, which is asynchronous and has side effects (inventory and money loads, events), and two objects for one character can overwrite each other |
| **Queue the edit; apply it when the character enters the world** | The edit only ever touches a live, fully loaded character, which the first slice already handles and tests. No new mechanism, no stand-in client, no lock |

The cost of queuing: the database shows the old value until the character's next login. A status
op (`player.pending`) shows what is waiting.

### Prior art

The open-source single-player forks with bots (Offline DAoC and its descendants: OfflineDAoC,
OfflineDAoC-LivingServer, OfflineDAoC-True165, DividedRealms) were read for a connection-less player.
Their bots are `GameNPC`s that implement a new `IGamePlayer` interface, which takes changes across
Core, and are stored in their own tables. None loads a `dolcharacters` row into a `GamePlayer`
without a client. Two ideas are worth keeping for later: a `GameClient` built with a null socket,
and asking Core's own `VerifySpecPoints` for a spec-points total through a GM-level client (which
reports a negative total instead of respeccing).

## How a login reaches the player, in Core `0ac41d443`

- `WorldInitRequestHandler` (`async void`) awaits `client.LoadPlayer`, then calls
  `player.AddToWorld()`. The end of `AddToWorld` calls `ClientService.OnPlayerJoin`, which makes
  the player findable by name (`GetPlayerByExactName`). The same handler then sends the skills,
  points, stats and resists.
- The code after the `await` comes back through `GameServiceSynchronizationContext`, which posts it to
  the service, so it runs among `ClientService`'s posted actions, the same batch the host's drain
  runs in, and in parallel with it.
- The client then sends `PlayerInitRequest`. Its handler sets `player.EnteredGame = true` and fires
  `GamePlayerEvent.GameEntered`, after all of the initial skill and points packets.

So a player can be findable by name while its first packets are still being built on another
thread. **The host treats a player as in the world only when `EnteredGame` is true.** This also
narrows the first slice: today `player.spec.set` would act on a player found by name mid-login.

## The design

1. **Request.** `player.spec.set` with `queue: true` on a character that isn't in the world (not
   found by name, or found with `EnteredGame` false). Without `queue: true` the answer is still
   `not_online`, so a caller never mistakes a queued edit for an applied one.
2. **Check now.** Look up the character row by name. Refuse with `not_found` if there is none, and
   with `invalid` if the spec name is unknown or `level` is outside 1 to the character's level. Spec
   points are checked when the edit is applied, against the live object (see open questions).
3. **Queue.** Store the edit as pending and answer
   `{"queued": true, "pendingId": "...", "name": ..., "spec": ..., "level": ...}`. The audit gets a
   `queued` line.
4. **Apply.** A `GameEntered` handler, registered by the host at startup, runs on a client-packet
   thread, so it only enqueues an "apply pending for character X" job. The job runs in the host's
   drain on the next tick and replays each pending edit for that character, in the order they were
   queued, through the online path: the same checks, trainer calls, client updates, message and
   `SaveIntoDatabase()`. Each applied or refused edit is removed from the store and gets its own
   audit line (`applied` or `refused at login`, with the reason).

### The pending store

- A JSON file next to the socket (`<root>/admin-pending.json`), rewritten whole with a write to a
  temporary file and a rename. It survives restarts and stays readable by hand. Only the drain reads or
  writes it, so there are no concurrent writers.
- Entries are keyed by the character's `ObjectId`, not its name, so a rename doesn't strand them.
  Each has an id, the op and its arguments, the time, the actor and the peer uid.
- Edits set absolute values ("Slash to 5"), never deltas. If the server stops after the save but
  before the entry is removed, applying it again changes nothing.
- A new edit for the same character and spec replaces the older one; the answer names the replaced id.

### New ops

| Op | What it does |
|---|---|
| `player.pending` | Pending edits, all or for one character (`name`) |
| `player.pending.cancel` | Removes one pending edit by id; audited |

## Not in this slice

- Level, money, realm points and items. Each still needs its own "what the client must be told" check.
  Money is per account and realm (`accountxmoney`), so a pending money edit would apply when any
  character of that account and realm enters the world.
- Edits that only exist in the database: moving a character to another account, renaming,
  deleting. Those need the account to be fully logged out, not just at character select: the
  character rows are loaded into `client.Account.Characters` at account login. They also need a way to
  refuse a login during the write. The login check is `IServerRules.IsAllowedToConnect`, and Core picks
  one rules class per server type, scripts first, so a drop-in script can only hook it by replacing the
  shard's rules. To find a better hook first.
- Forcing an apply without the player logging in. It needs the player's credentials or a
  connection-less player, and no case for it has come up.

## Open questions, to settle on the lab

- Whether a spec edit sent just after `GameEntered` is shown correctly, or whether the client's own
  login traffic overwrites it.
- Spec points at queue time: the queue-time check could ask Core for the total (the GM-level
  `VerifySpecPoints` call above) instead of waiting for login. That would also let the online path drop
  its copy of the cost formula.
- Whether `EnteredGame` is reset on logout and on a region change (the handler fires `RegionChanged`
  instead of `GameEntered` when it is already true).

## Testing

On the lab copy with `DaocCli`, as for the first slice:

- Queue while offline: the answer says queued, the store has the entry, the database is unchanged.
  Then log in: the edit is applied after `PlayerInitRequest`, the client receives the skill and points
  updates, the database has it, the entry is gone, and the audit has `queued` and `applied`.
- Without `queue: true`, an offline character still gets `not_online`.
- Two specs queued for one character are applied in order. A second edit to the same spec replaces the first.
- Refused at login: queue a raise, lower the character's level offline so the points no longer cover
  it, log in. Nothing changes, the entry is removed, and the audit says why.
- `player.pending` and `player.pending.cancel`; a cancelled edit isn't applied.
- Restart between queuing and login: the edit survives. Re-adding an applied entry by hand and logging
  in again changes nothing.
- A player found by name with `EnteredGame` false (during login) is treated as not in the world.
