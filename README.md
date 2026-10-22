# opendaoc-tools

Operator- and player-facing tools for running and using an [OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core)
server, built to be shareable across shards rather than tied to any one.

Most DAoC freeshards ship their own account portal and admin tooling, each locked to that one
server. The pieces here aim to be generic instead: they work against any
OpenDAoC server given its address and your credentials, because OpenDAoC accounts are per-server
(there is no central account system).

## Planned contents

Nothing is built yet — this repository is the home for the following, added as they're written:

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
