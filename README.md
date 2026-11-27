# opendaoc-tools

Operator- and player-facing tools for running and using an [OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core)
server, built to be shareable across shards rather than tied to any one.

Most DAoC freeshards ship their own account portal and admin tooling, each locked to that one
server. The pieces here aim to be generic instead: they work against any
OpenDAoC server given its address and your credentials, because OpenDAoC accounts are per-server
(there is no central account system).

## Contents so far

- [`docs/research/ops-tooling-inventory.md`](docs/research/ops-tooling-inventory.md) — survey of
  existing OpenDAoC operational tooling, gaps, and what a write needs to reach a running server.
- [`server-scripts/probe/`](server-scripts/probe/) — `LabProbe.cs`, a drop-in script for lab
  servers that prints what the server holds in memory; used by the
  [cache-probe runbook](docs/runbooks/cache-probe.md).
- [`server-scripts/admin-host/`](server-scripts/admin-host/) — `AdminHost.cs`, a drop-in script that
  lets operator tools read and change the running server over a local Unix socket (JSON lines,
  audited), with [`tools/adminctl.py`](tools/adminctl.py) as a command-line client. It does
  status, online players and their state (position, health, money, inventory, effects), setting a
  spec, level, money, backpack items and position, live or queued until an offline character's next
  login; reviving and healing; spawning NPCs, boats and siege rams for client tests (boarding,
  sailing a path; never saved); kicking, muting, server announcements and the appeal queue; and cache
  reloads. Design and test results:
  [docs/design/admin-host.md](docs/design/admin-host.md),
  [docs/design/admin-host-queued-edits.md](docs/design/admin-host-queued-edits.md),
  [docs/design/admin-host-move-money.md](docs/design/admin-host-move-money.md),
  [docs/design/admin-host-revive-items-level.md](docs/design/admin-host-revive-items-level.md),
  [docs/design/admin-host-spawns.md](docs/design/admin-host-spawns.md) and
  [docs/design/admin-host-moderation.md](docs/design/admin-host-moderation.md).
- [`server-scripts/admin-host-test/`](server-scripts/admin-host-test/) — `AdminHostPackets.cs`, for
  test servers only: adds packet capture to the admin host (every packet one account sends and
  receives, chat included, for a set time) and sends a crafted server packet to an online player. Design and test results:
  [docs/design/admin-host-packets.md](docs/design/admin-host-packets.md).
- [`server-scripts/ScriptCheck.csproj`](server-scripts/ScriptCheck.csproj) — compile check for the
  drop-in scripts against an OpenDAoC-Core build.
- [`navmesh/`](navmesh/) — building OpenDAoC navmeshes on Linux and macOS without BuildNav's
  Windows-only `RecastDemo.exe`, plus tools to check `.nav` files. Start with
  [its README](navmesh/README.md).

## Planned contents

This repository is the home for the following, added as they're written:

- **Account / user management** — create and manage accounts deliberately, for a server run with
  auto-account-creation turned off.
- **Server status / Camelot Herald** — read-only views of a server's state for players.
- **Operational tooling** — admin and oversight helpers, database maintenance, routine fixes.
- **Command-system documentation** — a reference for OpenDAoC's in-game command system.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE). This matches OpenDAoC's license so code and findings can
be shared with the ecosystem.

## Status

Early scaffolding. Not yet published or released.
