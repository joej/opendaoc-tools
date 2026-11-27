# Admin host: kick, mute, announcements and appeals (design and test results, 2026-11-26)

Follows [spawns](admin-host-spawns.md). These are the live-server jobs an operator does today by
logging in a GM character: removing a player, muting one, telling everyone something, and working
the appeal queue. Each op does what Core's own GM command does (`/kick`, `/mute`, `/announce`,
`/gmappeal close`), and uses the actor's name where the command would use the GM's character name.
They are production ops, in `AdminHost.cs`.

| Op | Args | What it does |
|---|---|---|
| `player.kick` | `name` (a character in the world, also link-dead) or `account` (a logged-in account, also at the character screen), optional `message`, `force`, `mark` | As `/kick`: the message (default Core's "You have been removed from the server!") in the system and chat windows, the quit packet, then the disconnect half a second later. `force=true`: the character is saved and removed at once, with no link death. `mark=true`: players in view see the kicked character as a ghost for the link death |
| `player.mute` | `name` or `account`, `muted` (true or false), optional `accountWide` | Mutes or unmutes. `name` alone: that character, until it logs out. `accountWide=true` or `account`: the account, saved, for every character |
| `server.announce` | `message` (up to 1000 characters), optional `style` (`log`), `realm` (1 to 3), `region` | As `/announce`, to every player in the world, or those of one realm or region; returns how many got it |
| `appeal.list` | optional `all` (true) | The appeal queue: account, character, severity, status, time, text, and whether the player is in the world. `all=false`: only players in the world, as `/gmappeal list` |
| `appeal.close` | `account`, or `name` (the character that made it) | As `/gmappeal close` (player in the world: they and the staff are told) or `closeoffline` |

**Kick.** Core's `Disconnect()` makes a player in the world link-dead: the character stays for 60
seconds, then quits. The same happens with `/kick`. Until it quits, the account can't log in again
(`AccountIsInLogoutProcedure`), and `player.list` still lists the character. At the character
screen, or while entering the world, Core removes the connection at once.

**Forced kick.** `force=true` does at once what Core's link-death timer does when its 60 seconds
run out: a dead player is released, the character is saved, then `GameClient.LinkDeathQuit()`
closes the socket and removes the character. The account can log in again straight away. It also
works on a character that is already link-dead, which a plain kick leaves alone (`linkDead: true`).

It exists for a frozen client. Core starts a "soft" link death when a client sends no position for
5 seconds, but any position or skill packet cancels it. On the test server, a retail client froze
after a crafted packet and kept the character in the world as an ordinary player, with the 60-second
timer never running out, until the server was restarted. A plain kick of such a client only starts
another 60 seconds.

The result says what happened: `removed` (the character or connection is gone) and `linkDead` (it
stays for Core's 60 seconds).

**The kick message arrives.** Core sends queued packets at the end of each game-loop tick. Core's
`/kick` queues the message and the quit packet, then closes the socket at once, so the kicked player
never sees them. The op closes the connection half a second after queuing them; in the lab test the
kicked client received both lines and the quit packet, then the socket closed 0.5 s later.

**Kick mark.** `mark=true` shows the players around the kicked character what happened, for the 60
seconds it stays link-dead:

- once, "<name> has been kicked from the server." in their system window and in the middle of their
  screen;
- the character drawn as a ghost (model 1676), sent again every 3 seconds, so a player who comes
  into view later sees it too (Core sends a newcomer the real model when the client asks for the
  figure).

Only packets to those players change: the character's model, name and guild are untouched and
nothing is saved, and the ghost goes when Core removes the character. The ghost keeps the nameplate,
guild line and the shield and weapon in hand. A player who can't see a stealthed character gets
nothing. A GM or Admin character is stealthed from the moment it enters the world, so players never
see it, and marking one shows them nothing. `mark` needs the link death, so it can't be combined
with `force`.

**Mute.** It works like `/mute`. A character mute is the in-memory `IsMuted` flag and ends at logout.
An account mute sets `account.IsMuted` and saves the account. Core copies the account flag to each
character at login, so it lasts. With `account`, the player need not be online: the op then changes
the account row. If the account is logged in, it changes Core's own account object instead, which
Core saves later. A character can't be unmuted while its account is muted. A muted player can't use
`/say`, `/yell`, `/region`, `/broadcast`, `/trade`, `/lfg`, `/advice`, guild chat, emotes,
`/appeal` or `/report`; each refuses with "You have been muted. You cannot talk." Whispers (`/send`)
are not blocked, so a muted player can still reach staff. The player is told in the staff channel,
as `/mute` does ("You have been muted from public channels by staff member <actor>!").

`muted` must be given: an absolute value, as the other edits take, never `/mute`'s toggle. Muting or
unmuting again returns `changed: false`.

**Announce styles.** As `/announce`:

- `log`: "[Announce]: <message>" in the system window;
- `send`: the same in the chat window;
- `center`: the message in the middle of the screen;
- `window`: a text window titled "Announce from <actor>";
- `confirm`: a dialog box with an OK button.

**Appeals.** Core keeps the queue in memory and writes changes to the `appeal` table at its periodic
save. A closed appeal disappears from `appeal.list` at once, and its row goes at the next save.
Changing an appeal's status (`/gmappeal assist`, `release`) isn't an op yet. Core's staff notice
on close starts with "[Appeals]: [Appeals]:", Core's own doubled prefix.

Every op writes one audit line; mute's has the before and after flags, and close's has the appeal.

## Tested (2026-11-26, Core `0ac41d443`)

On the lab copy with the headless client, LabChar (an Admin account), reading what the client got
through a [packet capture](admin-host-packets.md).

| Check | Result |
|---|---|
| Appeal | LabChar's `/appeal stuck I am stuck in a wall here` listed: severity 4 (Critical), status Open, `online: true` |
| Announce | `log`, `center`, `window` and `realm=1` each reached LabChar (`sent: 1`); `region=999` reached no one (`sent: 0`). The client got "[Announce]: lab announce log", the centred text and "[Announce]: only albion" |
| Character mute | Mute, mute again (`changed: false`), unmute; the client got the muted and unmuted notices |
| Account mute | `accountWide=true`: both flags true, and the notice "This mute has been placed on all characters for this account." Unmuting the character alone was refused |
| Mute survives login | Muted with `account=labprobe` while LabChar was offline (`IsMuted` 1 in the table). After login, LabChar's `/say mutedtest` got "You have been muted. You cannot talk." and nothing was said |
| Offline unmute | `account=labprobe muted=false` wrote 0; again: `changed: false` |
| Close appeal | `playerTold: true`; LabChar got "labtest [TEAM] has closed your appeal." and the staff notice; `appeal.list` empty |
| Kick | By name (with a message) and by account: the client was disconnected ("The server closed the connection"), and Core logged "Linkdead player LabChar(labprobe) will quit in 60 seconds"; a login during those 60 s was refused |
| Forced kick (2026-11-27) | In the world, by name: `removed: true`, gone from `player.list` within 2 s, Core logged `player.Delete()` and "just disconnected" and no link death; a login right after was granted |
| Forced kick of a link-dead player | A plain kick left LabChar link-dead; kicking again without `force` changed nothing (`linkDead: true`); `force=true` then removed it, and a login right after was granted |
| Forced kick saves | LabChar moved with `player.move`, then force-kicked by account: the `dolcharacters` row had the new position |
| Forced kick at the character screen | By account: `state: CharScreen`, `removed: true`; Core logged "just disconnected" at once |
| Kick message delivered | The kicked client got the message twice and the quit packet (0xA4), then the socket closed 0.5 s later |
| Kick mark | A second character (an ordinary player account) kicked with `mark=true`, watched by LabChar: the watcher got the kicked line in the system window and the centre of the screen once, and the ghost model (0xDB, 1676) at once and every 3 s after, 21 in all; they stopped when Core removed the character 60 s later. `force` with `mark` refused |
| Kick mark, player arriving later | Watcher moved next to the kicked character 6 s after the kick: ghost packets for it arrived within 3.5 s |
| Ghost on a retail client (packet.send, before the op existed) | Another player's figure drawn as a dark spectre, keeping its nameplate, guild line, shield and sword; Core removed it 60 s after a plain kick |
| Refusals | Announce: an unknown style, a blank message, `realm=4`. Mute: no `muted`, both `name` and `account`, an unknown account. Kick: a character or account not online. Close: an account with no appeal |
| Audit | One line per kick, mute, announce and close |

Not tested: a real client (the `window` and `confirm` styles show as a window and a dialog there), a
plain kick at the character screen, the appeal row's deletion at the periodic save, a forced kick of
a dead player (the release), and a forced kick of a frozen client in a soft link death (the headless
client keeps sending positions, so it can't reproduce one). The kick mark itself, made by the op, on a
retail client (the ghost was seen there only from `packet.send`), and a retail player arriving
during the mark (the headless client doesn't ask the server for figures, so it never had one).
