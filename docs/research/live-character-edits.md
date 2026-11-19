# Editing a character while it is online (case note, 2026-11-09)

## The case

On a test server a level 20 Wizard had 1 point in each of its three magic lines, so it had no spells
to test with. The quick fix was an SQL update to `dolcharacters.SerializedSpecs`. That only works
while the character is **offline**:

- The server keeps an online character in memory (`GamePlayer`) and writes it back to the database
  when it saves (on logout, and periodically). The player never sees a database edit made while
  they're in the world, and the next save can overwrite it (see "Tested" below for exactly when).
- So the player had to log out to character select, wait for the edit, and log back in. Moving a
  character between accounts by SQL has the same problem, and it surprised a player who was logged
  in to the source account at the time.

A tool that edits characters only through the database (as most shard admin panels do) has this
gap. It needs either a "player must be offline" check, or a path through the running server.

## Tested on a lab copy (2026-11-19, Core `0ac41d443`)

Core writes only the columns that changed. Each row object keeps a snapshot from when it was loaded or
last saved, and a save sends `UPDATE` for the columns whose in-memory value differs from it
(`DataObject.GetDirtyBindings`, `SqlObjectDatabase.cs`), then takes a new snapshot. So for an
online character:

- **Nothing reloads the edit.** The player keeps the old value in memory for the whole session.
- **The edit is lost if the server changed that column in memory.** A column the server didn't
  change survives the save and takes effect at the next login. With a headless client: a
  `SerializedSpecs` edit and a `Copper` edit both survived `SaveIntoDatabase()` and the logout
  save. After a `/jump`, an edit to `Xpos` was overwritten by the save and an edit to `Copper` in
  the same row was not.
- **Money is the exception, and is always overwritten.** This fork keeps money per account and realm in
  `accountxmoney`; `dolcharacters.Copper` etc. are no longer read. The save re-reads that row and sets
  every money field from memory, so an online edit there was lost at the first save.
- **An offline edit works, but nothing checks it.** Raising `Slash` from 1 to 2 at character
  select loaded as `Slash = 2`, with spec points at -2. A tool has to go through Core's own logic
  (or do its sums) to keep a character valid.

So in the 2026-11-09 case, the spec string most likely also changed in memory before the save
(the player trained, or the server rewrote it); that wasn't recorded at the time. An edit that "usually survives" is worse than one
that never does: whether it lands depends on what the player does before the next save.

## What the tool needs

A way to change a character **in the running server's memory**, applied immediately and saved
by the server itself:

- Specs and spec points, level, realm points/rank, money, items, position (move / summon to a
  player), stats, privilege level.
- Spawning test NPCs (e.g. a hostile mob next to a player) without a GM having to log in.
- Safe when the character is offline too: fall back to the database, or refuse and say so.
- Audited: who changed what, when.

## Ways to get there (to evaluate)

1. **In-game GM commands driven remotely.** OpenDAoC already has most of the edits as GM commands
   (`/player level`, `/player ...`, `/jump to <player>`, `/mob create`, `/mob fastcreate`, `/item`).
   A headless GM client (log in, send commands, log out) can run them today with no server change.
   Cost: a GM account and a client session per operation; output is only chat text.
2. **A server-side console bridge.** A server script that accepts commands (local socket or HTTP,
   authenticated) and runs them as the server console or a GM, against in-memory players.
   Prior art: `Darku11/daoc_cms_utilities` (in-game console bridge script + HTTP→TCP bridge).
3. **A small admin API in a server script** with typed operations (set spec, set level, move,
   spawn), each acting on the in-memory `GamePlayer` if online, else on the database row.
   Cleanest for a web tool; most code.

Check before relying on any of these _(verify)_: which `/player` subcommands exist and whether they
update the client at once (specs and spell lists need the client's skill and spell windows
refreshed), and whether changing specs in memory needs a respec or recalculation call.
