# Editing a character while it is online (case note, 2026-11-09)

## The case

On a test server a level 20 Wizard had 1 point in each of its three magic lines, so it had no spells
to test with. The quick fix was an SQL update to `dolcharacters.SerializedSpecs`. That only works
while the character is **offline**:

- The server keeps an online character in memory (`GamePlayer`) and writes it back to the database
  when it saves (on logout, and periodically). A database edit made while the player is in the world
  is overwritten at the next save, and the player never sees it.
- So the player had to log out to character select, wait for the edit, and log back in. Moving a
  character between accounts by SQL has the same problem, and it surprised a player who was logged
  in to the source account at the time.

A tool that edits characters only through the database (as most shard admin panels do) has this
gap. It needs either a "player must be offline" check, or a path through the running server.

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
