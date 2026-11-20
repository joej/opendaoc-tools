# Operational / administrative tooling — inventory

Survey of what already exists for running and administering an OpenDAoC server, so that gaps can
be identified before anything is built here. Captured 2026-11-06.

**How to read this:** Part 1 comes from reading OpenDAoC-Core source at commit
[`b9b32cf`](https://github.com/OpenDAoC/OpenDAoC-Core/commit/b9b32cf032231db0442bd1e6df64b8e113e8fbb7)
(2026-11-06). Behaviour described there was read from code, **not exercised against a running
server** — items marked _(verify)_ need a live test before anyone relies on them. Part 2 is the
external ecosystem (other repos, shards, community projects).

---

## Part 1 — Built into OpenDAoC-Core

### 1.1 The web API (`GameServer/API/`)

An ASP.NET Core minimal-API host started in-process by `GameServer.cs` when the server property
`atlas_api` is `true` (default `false`). It is inherited from the Atlas freeshard — note the
`atlas` property category.

**Enabling / configuration**

| Server property (`serverproperty` table) | Default | Purpose |
|---|---|---|
| `atlas_api` | `false` | Starts the API host |
| `api_password` | `""` | Shared secret for the privileged `/utils/*` endpoints. Empty = those endpoints always refuse |
| `force_discord_link` | `false` | Reported by `/utils/discordrequired` |

- **Listen address is not configured in code** (the `ConfigureKestrel(... 9874)` line is commented
  out), so ASP.NET defaults apply. The property description says "port :5000", which is the
  bare-metal default (`http://localhost:5000`, loopback only). In the official Docker image the
  `mcr.microsoft.com/dotnet/aspnet` base sets `ASPNETCORE_HTTP_PORTS=8080`, which would make it
  listen on **all interfaces, port 8080** inside the container _(verify)_. The shipped
  `docker-compose.yml` publishes only 10300/10400, so the API is not reachable from the host
  there without adding a port mapping.
- `ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS` env vars are the way to set the address.
- No TLS, no CORS policy, no rate limiting, no auth other than the password-in-path described below.
- Swagger UI is served at `/docs` (and `/` redirects there), from `wwwroot/docs`.
  The bundled `swagger.json` documents the 18 public routes but **omits all `/utils/*` routes**.

**Endpoints** — all `GET`, JSON unless noted. Responses are memory-cached (TTL in brackets).

| Route | Returns | Notes |
|---|---|---|
| `/stats` | Online count per realm + total, timestamp [1 min] | Text body containing JSON |
| `/stats/uptime` | Uptime as d/h/m/s + string [30 s] | Shares a cache key with `/stats`, so the two evict each other |
| `/stats/rp` | Top 10 characters by RP (<7M RP) [2 h] | Declares an unused `guildName` parameter _(verify whether it is required)_ |
| `/player/{name}` | Character: name, guild, realm, race, class, level, RP, RR, kill/deathblow/solo counts | [1 min] |
| `/player/{name}/specs` | Specs + realm abilities | Honours the character's `HideSpecializationAPI` flag |
| `/player/{name}/tradeskills` | Tradeskill levels | Unknown name probably 500s rather than 404 _(verify)_ |
| `/player/getAll` | Every character played in the last 31 days [2 h] | No staff/GM filtering |
| `/guild/{name}`, `/guild/{name}/members`, `/guild/getAll`, `/guild/topRP` | Guild info, roster, RP rankings | |
| `/realm/{alb\|mid\|hib}` | Keeps owned by that realm | |
| `/realm/df` | Darkness Falls owner | |
| `/realm/bg` | Battleground status | |
| `/relic` | All relics and holders | |
| `/news/all`, `/news/realm/{realm}`, `/news/type/{type}` | Herald-style news entries (`News` table) | |
| `/utils/query_max_cap` | `max_players` property | Undocumented |
| `/utils/discordrequired` | `force_discord_link` | Undocumented |
| `/utils/discordstatus/{accountName}` | Whether an account has a Discord ID linked | Undocumented, **unauthenticated**. Takes a login account name; an unknown name looks like it throws (500) while a known one returns 200 — an account-name existence check _(verify)_ |
| `/utils/query_clients/{password}` | Every connected client: **account name**, privlevel, client state, tester flag | Password-gated |
| `/utils/shutdown/{password}` | **Immediately shuts the server down** (`CountDown(0)`) | Password-gated, via `GET` |

**Assessment**

- Mostly a **player-facing Herald data source** (rankings, keeps, relics, news), not an operator
  interface. The only operational data is online counts, uptime, max cap, and the connected-client
  list.
- "Read-only" is not accurate: `/utils/shutdown` is a state-changing `GET` with the secret in the
  URL path, where reverse proxies and access logs will record it. Anyone who enables the API and
  sets `api_password` gets it; with `api_password` empty it is inert.
- Missing for operations: per-zone/region load, game-loop tick health, DB health, memory/GC,
  queue state, error rates, metrics format (Prometheus etc.), anything write-side (accounts,
  bans, broadcasts).
- Safe-exposure posture if enabled: bind to loopback / private network, leave `api_password` empty
  unless the shutdown endpoint is wanted, front with a proxy that blocks `/utils/*` from the public
  internet.

### 1.2 Server console

`CoreServer` runs an interactive stdin loop: `exit`, `clear`, and anything else is run as a game
command via a console pseudo-client (so `/shutdown`, `/announce`, `/serverproperties`, etc. work
from the console). Also has Windows service install/start/stop actions. No remote console (no
telnet/RCON) — remote admin means attaching to the process's stdin (e.g. `docker attach`).

### 1.3 In-game Admin / GM commands

Parsed from `[Cmd]` attributes. Account privlevel 3 = Admin, 2 = GM.

**Admin (15):** `account`, `benchmark`, `code` (run C# at runtime), `dfowner`, `fixnpcspawn`,
`freeze`, `keepdoorteleport`, `plvl`, `refresh`, `reload`, `serverproperties`, `shutdown`,
`snoop`, `speedhack`, `unloadxmldb` (dump DB tables to XML).

**GM (62)** — grouped:
- *Moderation / players:* `ban`, `mute`, `player` (edit characters), `clientlist [full]` (incl. IPs
  and accounts), `sameip`, `gmappeal`, `adviceteam`, `titlegm`, `clearinventory`, `resetability`,
  `morph`, `speclock`
- *Communication:* `announce`, `GMinfo`, `gmvote`
- *World/content editing:* `mob`, `xmob`, `spammob`, `npc`, `ngg`, `object`, `door`, `item`,
  `merchant`, `path`, `walk`, `stop`, `area`, `zone`, `zonepoint`, `zonebonus`, `instance`,
  `teleport`, `jp`, `jump`, `offlinejump`, `addbind`, `addhookpoint`, `weather`, `translate`,
  `setsalvage`, `faction`, `crafting`, `removehouse`
- *RvR:* `keep`, `keepcomponent`, `keepguard`, `siegeweapon`, `gmrelic`, `gmrelicpad`, `minorelic`
- *Testing/debug:* `cast`, `heal`, `harm`, `speed`, `blink`, `debug`, `diag`, `genuniques`,
  `gmstealth`, `mountgm`, `team`

All of this requires being logged into the game client (or the console). There is no web or CLI
front-end for any of it.

### 1.4 Logging, audit and stats in the database

| Mechanism | Controlled by | Where it lands |
|---|---|---|
| Audit log — account create/login/failed login/logout/password & email change/delete, character create/delete/rename/login/logout, public & private chat | `enable_audit_log` | `AuditEntry` table |
| Performance stats | `statsave_interval` (minutes), `statprint_frequency` (console) | `serverstats` table |
| GM command log | `log_all_gm_commands` | `gmactions` logger |
| Inventory log (trade/craft/loot/quest/merchant) | `log_inventory*`, `log_trades` | log files |
| Keep captures | `log_keep_captures` | `KeepCaptureLog` table |
| Cheat detection | `ban_hackers`, `speedhack` command | `cheats` logger |
| Bans / appeals | `/ban`, `/gmappeal` | `Ban`, `Appeal` tables |

The data is recorded, but nothing in Core reads it back for an operator.

### 1.5 Discord integration

Outbound webhooks only (`Discord_Webhook_*`, plus per-realm chat and RvR webhooks), and an
account↔Discord link field (`DiscordID`, enforced by `force_discord_link`). The server posts
"open for connections" etc. Nothing comes back the other way from Discord.

### 1.6 Deployment

`Dockerfile` (+ GHCR image `ghcr.io/opendaoc/opendaoc-core`) and `docker-compose.yml` (MariaDB
10.6 + game server). The DB is seeded from the
[OpenDAoC-Database](https://github.com/OpenDAoC/OpenDAoC-Database) repo at image build time. No
backup, migration or upgrade tooling ships with it.

### 1.7 Dead / vestigial

- `queue_api_url` server property — defined but referenced nowhere else in Core.
- `log_email_addresses` (mails logs via `MailMgr`) and `bug_report_email_addresses` (`/report`
  bug reports) are wired up; whether `MailMgr` SMTP config works on a modern host is unchecked.

---

## Part 2 — External ecosystem

Gathered from GitHub (`gh` repo/code search, READMEs, file trees), the DOLSharp wiki, the
opendaoc.com site source, and web search. **Nothing here was installed or run.** Capabilities
are as self-described in READMEs unless marked _(inferred)_ (from file names/code). "Last push" is
GitHub `pushed_at`. Target: **ODC** = OpenDAoC, **DOL** = DOLSharp generic, **Both**, **Shard** =
tied to one shard or fork.

**Scope:** this part also includes repos whose main purpose is gameplay rather than tooling,
such as shard forks and rule-set conversions, when they contain operator tooling worth learning
from (install, backup, admin, diagnostics). Only their tooling side is described here.

### 2.1 OpenDAoC GitHub org (11 repos)

| Repo | Last push | What it is | Ops relevance |
|---|---|---|---|
| [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) | 2026-11-06 | The server | Part 1 |
| [OpenDAoC-Database](https://github.com/OpenDAoC/OpenDAoC-Database) | 2026-11-01 | World DB, one `.sql` per table; recombine with [claitz/SQLSplit](https://github.com/claitz/SQLSplit) | Import only — no migration/backup/upgrade scripts |
| [OpenDAoC-AccountManager](https://github.com/OpenDAoC/OpenDAoC-AccountManager) | 2023-09-25 | Next.js self-service portal: Discord OAuth registration + password recovery, writes the game DB directly; Docker image | Player-facing account mgmt; idle ~3 years; no license file |
| [OpenDAoC-AccountManager-LEGACY](https://github.com/OpenDAoC/OpenDAoC-AccountManager-LEGACY) | 2023-09 | PHP predecessor | Archived |
| [OpenDAoC-QueueService](https://github.com/OpenDAoC/OpenDAoC-QueueService) | 2022-09-10 | Python login-queue / whitelist API, calls the game API _(inferred, empty README)_ | Atlas-era; likely the consumer of Core's dead `queue_api_url` |
| [OpenDAoC-Launcher](https://github.com/OpenDAoC/OpenDAoC-Launcher) | 2023-04-06 | WPF launcher/patcher, hardcoded Atlas hosts _(inferred, no README)_ | Atlas-specific |
| [OpenDAoC-RedBot-Cogs](https://github.com/OpenDAoC/OpenDAoC-RedBot-Cogs) | 2023-03-27 | Red-DiscordBot `!online` realm-population chart from `/stats` | Status |
| [OpenDAoC-Website](https://github.com/OpenDAoC/OpenDAoC-Website) | 2026-07-22 | opendaoc.com source (Starlight) | Docs, see 2.6 |
| [OpenDAoC-BuildNav](https://github.com/OpenDAoC/OpenDAoC-BuildNav) | 2026-07-18 | Navmesh builder from client zone files | Content pipeline |
| [OpenDAoC-ClientSimulator](https://github.com/OpenDAoC/OpenDAoC-ClientSimulator) | 2026-07-23 | Headless client for debugging / stress testing | Load testing |
| [AtlasPlanner](https://github.com/OpenDAoC/AtlasPlanner) | 2022-09 | Character planner | Not ops |

### 2.2 Admin / GM consoles and moderation

| Tool | Last push | What it does | Stack | License | Target |
|---|---|---|---|---|---|
| [futuretim/opendaoc-manager](https://github.com/futuretim/opendaoc-manager) | 2026-10-27 (created 2026-10-15) | Staff web console: account search/detail, password reset, privlevel/status/mute/warn/notes, rename; bans; character edit (level, RR, RP/BP, rename, move between accounts); staff mgmt; its own audit log; column-scoped writes to the game DB. Stats dashboard explicitly deferred | React + Fastify (TS), MySQL, Nix, compose | MIT | ODC |
| [Darku11/daoc_cms](https://github.com/Darku11/daoc_cms) + [daoc_cms_utilities](https://github.com/Darku11/daoc_cms_utilities) | 2026-08 | Full shard CMS from the Aldhran shard: admin panel, herald, RvR map, item/mob editors, account mgmt, item shop, forum, Discord. Server side: in-game console bridge script, HTTP→TCP console bridge, live kill/keep-capture event push, guild-chat→Discord | PHP 8.2 + C# scripts | GPL-3.0 | Both |
| [catastrophic-impact/daoc-divided-linux](https://github.com/catastrophic-impact/daoc-divided-linux) | 2026-11-04 | Linux packaging of the Divided Realms fork. Its **in-process admin host** speaks JSON lines over a Unix socket; clients are a Textual TUI and a C# CLI (see the live-link section below) | C#, Python (Textual) | GPL-3.0 | Fork |
| [trozzelle/OfflineDAoC-macOS-server](https://github.com/trozzelle/OfflineDAoC-macOS-server) | 2026-10-19 | macOS fork; description mentions an admin host + TUI (not inspected) | C# | GPL-3.0 | Fork |
| [DOL Remote Control](https://sourceforge.net/projects/dolrecon/) | 2015 | Configure/control a DOL shard without a game client; alpha | C# WinForms | GPL-2.0 | DOL, dead |
| [Dawn-of-Light/DOLNancyWeb](https://github.com/Dawn-of-Light/DOLNancyWeb) | 2016 | Embedded web server PoC | C# | MIT | DOL, dead |

#### Live link vs. database writes — the distinction that matters

A tool that writes the game DB directly cannot affect what the running server holds in memory.
Accounts are read fresh at each login, so account-level changes (password, privlevel, ban) apply
at the player's next login. Character and inventory state is held in memory while the player is
online and saved back periodically and on logout, so a DB edit to an online character is ignored
and then **overwritten** by the next save. Changes to world data (mobs, items, properties) need
the matching `/reload`/`/refresh` or a restart. Only code running *inside* the server can act on
live state.

How each tool stands:

| Tool | Path into the server | Live? |
|---|---|---|
| futuretim/opendaoc-manager | Direct MySQL; guesses "is the owner online" from `LastLogin` vs `LastPlayed` before editing characters/inventory. Its spec defers live actions: "no out-of-process hook exists for this today" | No |
| camelot-studio | C# installer plugin applies releases at server boot (`IDatabaseUpdater`) | At restart |
| Core API (1.1) | In-process, but read-only apart from shutdown | Read only |
| **daoc_cms AldhranBridge** | In-process script with a TCP command channel (below) | **Yes** |
| **daoc-divided-linux Admin.Host** | In-process admin host on a 0600 Unix socket, JSON lines (below) | **Yes** |

**daoc-divided-linux `Admin.Host` / `Admin.Protocol` / `tui/`**
([catastrophic-impact/daoc-divided-linux](https://github.com/catastrophic-impact/daoc-divided-linux),
GPL-3.0, pushed 2026-11-04; read, not run). This is the cleanest design found.

```
tui/daoc_admin_tui.py (Textual)  ─┐
DaocAdmin CLI (C#)               ─┴─ Unix socket run/admin.sock (mode 0600) ─→ Admin.Host inside the server process
```

- **Wire format: JSON lines.** One JSON object per line, many requests per connection:
  `{"id":1,"op":"accounts.list","args":{...}}` →
  `{"id":1,"ok":true,"result":...}` or `{"id":1,"ok":false,"error":{"code":"not_found|invalid|refused|failed","message":"..."}}`.
  The protocol types sit in their own small assembly (`Admin.Protocol`) shared by host and CLI.
- **Ops:** `server.status`, `server.stop`, `accounts.list/show/create/setRole`,
  `options.list/set`, `rules.list/set/reset`, `rvr.status`, plus fork-specific ones
  (`bots.*`, `population.*`, `goals.*`, `bracket.*`).
- **Acts through the server's own objects and ORM** (live client list, `DOLDB<DbAccount>`), so
  there is no race with saves and no schema duplication.
- **Auth is the filesystem:** the socket is mode 0600, so only the server's OS user can connect;
  remote use is `ssh` to that user. There is no port, password, or TLS to get wrong. Socket path is
  configurable (`$DAOC_ADMIN_SOCKET`). It also has a max line length and a connection-slot limit.
- Its tests include a fake-socket smoke test for the TUI (`asyncio.start_unix_server` with
  canned replies), so the client can be tested without a server.
- **Not drop-in for OpenDAoC:** the host is built into this fork's DOL-lineage server and bot
  system. For stock OpenDAoC the host would be reimplemented against Core; the protocol shape and
  the TUI approach carry over.

**AldhranBridge / AldhranConsole** ([daoc_cms_utilities](https://github.com/Darku11/daoc_cms_utilities)
@ `d4da316`, 2026-08-14; read, not run):

```
CMS (PHP) --HTTP :5100, X-Aldhran-Secret header--> AldhranConsole (ASP.NET, net10)
          --TCP :2000, line-delimited JSON--> AldhranBridge.cs (script inside DOL/OpenDAoC)
```

- **Wire format:** one request per TCP connection: a line holding the shared secret, then one line
  of JSON `{action, ...}`; the bridge answers with one JSON object (`{ok, error?, ...}`).
- **Actions:** `status` (online players with account, class, region, privlevel), `kick`, `mute`,
  `freeze`, `privlevel`, `teleport`, `giveitem`, `setstats` (hp/mana/end/level/xp/money),
  `heal`, `revive`, `broadcast`, `guildchat`, `restart`, and `raw` (any slash command). The
  Console adds item search, a shop, and "world-forge" upload/sync endpoints.
- **`raw` runs as an online GM:** it borrows the named executor or the first online
  privlevel ≥ 2 client, and fails if no GM is logged in. It calls the handler directly,
  **skipping Core's privlevel check**; only `shutdown` and `quit` are blocked.
- **Security as shipped:** listens on `IPAddress.Any`, so it is reachable from the network unless
  firewalled; plaintext TCP; the secret is a compile-time constant (`CHANGE_ME_BRIDGE_SECRET`)
  compared with `!=`; a wrong secret's first and last three characters are logged; no
  per-action audit inside the server.
- Runs on both DOL and OpenDAoC through a reflection compatibility shim.

The pattern (an in-process script exposing structured actions over a local socket) is the
working answer to "change things without a restart or relog". This implementation needs
hardening before it should be exposed: bind to loopback, secret from config, and audit.

### 2.3 Status / herald / Discord

| Tool | Last push | What it does | Target |
|---|---|---|---|
| OpenDAoC-RedBot-Cogs | 2023-03 | `!online` population chart | ODC |
| [kimma86/OpenDAoC_DiscordBot](https://github.com/kimma86/OpenDAoC_DiscordBot) | 2023-11 | Slash commands over the Core API: `/who`, `/top`, `/stats`, `/ra`, `/spec` | ODC (MIT) |
| daoc_cms herald / RvR map / live events | 2026 | See 2.2 | Both |
| [mauricevogel/atlas-herald](https://github.com/mauricevogel/atlas-herald) | 2023-04 | Alternative herald for Atlas's API | Shard |
| Uthgard / Phoenix / Eden herald bots and sites (e.g. [thekroko/uthgard-herald](https://github.com/thekroko/uthgard-herald), [mattiasr/phoenix-herald](https://github.com/mattiasr/phoenix-herald), [tsujin/Eden-Herald-Bot](https://github.com/tsujin/Eden-Herald-Bot)) | 2017–2022 | Scrape or consume that shard's herald | Shard, mostly dead |
| DAoC Portal server-list update | — | Built into DOL via `serverlistupdate_*` properties | DOL |

### 2.4 Monitoring, metrics, logs, backup

| Tool | Last push | What it does | Target |
|---|---|---|---|
| [gz-42/devops-opendaoc](https://github.com/gz-42/devops-opendaoc) | 2026-11-02 | Terraform/EKS + ArgoCD + Helm, Prometheus/Grafana/AlertManager, Velero backups. **Infrastructure** metrics only — no game-level metrics in the README | ODC (GPL-3.0) |
| [DOLSharp wiki: backup-database](https://github.com/Dawn-of-Light/DOLSharp/wiki/backup-database) | wiki 2026-01 | cron + `mysqldump`, 7 daily rotations | DOL |
| [Eve-of-Darkness/db-public](https://github.com/Eve-of-Darkness/db-public) | 2026-10-22 | DOL DB as JSON; Rust CLI exports to MySQL/SQLite with `--update-only` (content tables only, keeps accounts/characters) | DOL (MIT) |
| [jdeadstar/daoc-dol-server](https://github.com/jdeadstar/daoc-dol-server) | 2026-04 | DOL compose stack bundling phpMyAdmin | DOL |
| camelot-studio `docs/admin.md` | 2026-11 | Has a backups section (see 2.5) | ODC |
| [Pactor/gaheris](https://github.com/Pactor/gaheris) `install.sh` | 2026-10 | Applies numbered SQL migrations (`sort -V`). The migrations are idempotent, so re-running is the update path. Takes an automatic DB backup first (`--no-backup` skips it). Also has `--restore` (newest backup), `--diff` (tables that differ from the newest backup), `--dry-run`, and per-feature install/`--uninstall` with dependencies resolved from `sql/features.conf`. Runs `mysql` inside the DB container via `docker exec`. The closest thing found to OpenDAoC migration/rollback tooling. | ODC (GPL-3.0) |

Searches for an OpenDAoC Prometheus exporter, Grafana dashboard, systemd unit, `mysqldump`
script, or cheat-/GM-action-log analyser returned **nothing**.

### 2.5 Content editing and release pipelines

| Tool | Last push | What it does | Target |
|---|---|---|---|
| [camelot-forge/camelot-studio](https://github.com/camelot-forge/camelot-studio) | 2026-11-06 (created 2026-09-10) | Browser world editor that journals every edit with exact revert; signed releases promoted local → staging → production; launcher pulls a signed feed; a **server console** showing applied release and connected players, with "install update" (pull, restart, apply); server-bridge C# plugin; compose deploy stack | ODC (MIT) |
| [Dawn-of-Light/DOLToolbox](https://github.com/Dawn-of-Light/DOLToolbox) | 2023-10 | WinForms DB editor: mobs, NPC templates/equipment, items, loot, merchants, spells, quests _(inferred from tree)_. Windows only (`net48`), and builds against DOLSharp's `DOLDatabase` via a git submodule. No licence file. Author's own copy: [DeceivingPanda/DOLToolbox](https://github.com/DeceivingPanda/DOLToolbox) (2023-10). It writes the DB directly, so edits reach a running server only after a `/reload` or restart (2.2). | DOL, probably partly ODC-compatible _(inferred)_ |
| [TTom03/DOLServer-DAoC-ToolBox](https://github.com/TTom03/DOLServer-DAoC-ToolBox) | 2026-01 | **A binary-only re-upload of DOLToolbox** (`DOLToolbox.exe` plus a bundled `GameServer.dll`/`DOLDatabase.dll`, no source, no licence), described as "a working version" because others didn't work. Its shipped `.exe.Config` points at a database named `opendaoc`, so it is apparently used against OpenDAoC. Schema compatibility with Core's renamed `Db*` tables isn't verified. The config also commits a LAN DB root password. Binaries of unknown provenance: **don't run them**; build from source if needed. Setup guide: [daoc.ndlp.info reward-quests guide](https://daoc.ndlp.info/losojos-001-site1.btempurl.com/Guides/Quests/doltoolboxrewardquests.html). | ODC _(claimed by config)_ |
| [DOL-Avalonia/DB-ToolBox](https://github.com/DOL-Avalonia/DB-ToolBox) | 2025-12 | "DAOC Database editor tool" (one-line README) | Shard/DOL |
| [ThatFontGuy/daoc-class-editor](https://github.com/ThatFontGuy/daoc-class-editor) | 2026-11-06 | Clone a class into a custom class, deploy via SSH, restart. SQLite-backed servers only | ODC forks |
| [Merec/DAoC-MapCreator](https://github.com/Merec/DAoC-MapCreator) | 2023-10 | Render zone maps from client files (herald/RvR map backgrounds) | client-generic |
| [Pactor/gaheris](https://github.com/Pactor/gaheris) | 2026-10 (created 2026-09, one author) | Not a tool but a pattern: a rule-set conversion shipped as an **overlay** on stock Core/DB images. It is about 80 drop-in scripts plus about 130 SQL migrations plus a compose delta, with zero Core files changed. Its `scripts/README.md` is the best practical guide found to drop-in scripts; see gap 4. | ODC (GPL-3.0) |
| Legacy DOL editors: DolServerEditor, ItemCreator, DataQuestBuilder, QuestDesigner, DBClass-Generator, [DOLConfig](https://github.com/Dawn-of-Light/DOLConfig) (serverconfig.xml GUI) | 2015–2023 | WinForms | DOL, mostly dead |

### 2.6 Deployment

- **Official:** Docker image + compose, configured by env vars; opendaoc.com suggests Portainer CE
  and Watchtower. The DB docs cover import only; the launcher docs page is an empty stub.
- **gz-42/devops-opendaoc** (Kubernetes/AWS), **camelot-studio `deploy/`** (compose + Coolify notes).
- **gaheris compose:**
  - Mounts `./scripts` at `/app/scripts/custom` in the stock image, so the stock image compiles drop-in scripts without a rebuild.
  - Pins the compose project name (`name: opendaoc`), so running from a differently named checkout doesn't create fresh, empty volumes.
  - Has a `sync-to-live.sh` that copies scripts and migrations into an existing deployment.
  - Counter-example: it tracks `opendaoc-core:latest`, so a Core update can break the scripts without warning.
- Many DOL Docker setups (Dawn-of-Light/docker-dawn-of-light, CJohnson0228, jdeadstar, ich777
  Unraid, ahungry, eod-docker), 2017–2026.
- Client launchers/patchers an operator must pick one of: OpenDAoC-Launcher (Atlas-bound),
  [Darku11/DAoCPortalNR](https://github.com/Darku11/DAoCPortalNR) (multi-shard, backend
  unpublished), camelot-studio's signed-feed launcher, [jeremv42/amteconnect](https://github.com/jeremv42/amteconnect),
  [BifrostTitan/DAoC-Connect](https://github.com/BifrostTitan/DAoC-Connect),
  [armodillodelmuerte/nostalgia-launcher](https://github.com/armodillodelmuerte/nostalgia-launcher)
  (its account-registration Discord bot is not published).

### 2.7 Testing / dev tooling (adjacent)

OpenDAoC-ClientSimulator; [gartmeier/daoc-sdk](https://github.com/gartmeier/daoc-sdk) (headless TS
client + bot SDK against a local server); [jpbriend/daoc-packet-logger](https://github.com/jpbriend/daoc-packet-logger);
[Dawn-of-Light/PacketLogConverter](https://github.com/Dawn-of-Light/PacketLogConverter);
gaheris `scripts/diagnostics/`: drop-in probes that log client opcodes Core has no handler for, with
the player's circumstances (class, region, target, moving). Core drops unhandled opcodes silently.

### 2.8 Shards with no published operator tooling

- **Atlas** — no GitHub org; its tooling is what now sits in the OpenDAoC org.
- **Eden** — launcher binaries only. Runs its own closed web service behind the launcher
  (herald, character-planner profiles, fight logs) — a reference for herald features, not reusable.
- **Uthgard** — pathing/encryption/regionserver open-sourced ([uthgard-opensource](https://github.com/thekroko/uthgard-opensource)) plus a herald; no admin tools.
- **Phoenix, Blackthorn** — player-side tools only.

Closed shard portals (Eden, Blackthorn, Phoenix) and Discord-only tools are invisible to this
kind of search. The OpenDAoC Discord has not been searched.

---

## Part 3 — Gaps

Interpretation, from Parts 1 and 2:

1. **Game-level monitoring.** Core records perf stats (`serverstats`) and has `/stats`, but nothing
   exports them: no Prometheus exporter, no tick-rate / population / per-region load dashboard,
   no alerting on game health. gz-42 covers infrastructure only.
2. **Reading back what Core records.** `AuditEntry`, GM-action log, cheat log, inventory/trade
   log, `KeepCaptureLog`, appeals — all written, nothing to search or alert on them.
   opendaoc-manager has its *own* audit log, not a viewer for Core's.
3. **Backup, restore, database comparison and rollback.** No OpenDAoC-specific backup tooling
   or docs. There is also no way to:
   - pull a newer OpenDAoC-Database content set without clobbering accounts/characters;
   - compare two database states, e.g. live against a backup, or ours against upstream
     content;
   - roll back a migration or a bad content change.

   Prior art: db-public's `--update-only` (DOL), and gaheris's `install.sh` (2.4), which backs
   up before migrating and has restore and per-table diff on an OpenDAoC compose stack.
4. **A safe live link into the running server.** This is the central gap. DB-only tools can't
   touch online players and get overwritten by saves (2.2). Core's only write action is the
   shutdown `GET`. Two live channels exist, neither usable on stock OpenDAoC as-is: AldhranBridge
   (works on OpenDAoC, but network-exposed by default, and its `raw` action bypasses privlevel
   checks) and daoc-divided-linux's Admin.Host (good design, but fork-specific). Wanted: an
   in-process admin host for stock Core, modelled on Admin.Host. That means typed ops as JSON lines
   over a 0600 Unix socket, so ssh is the auth, with an audit entry per action. An HTTP front
   (localhost, header token, POST for changes) can sit on top for web tools. It would cover
   the online list with live state, kick, mute, message/broadcast, the appeal queue, and live player
   edits.

   **How to deliver it — three options:**

   | Option | Fork Core? | Notes |
   |---|---|---|
   | (a) Add write routes to `ApiHost` | Yes | Reuses the existing listener, but mixes admin writes with the public read-only stats on one port. Routes are all `MapGet`, auth is a password in the URL, and the bind address isn't set in code (1.1). Carries a merge burden against upstream. |
   | (b) A separate in-process host compiled into Core | Yes | The clean design, as in Admin.Host, but still a fork change. |
   | (c) The same host as a **drop-in script** | No | **Preferred.** Ships from this repo, works on any shard. See below. |

   *Drop-in scripts.* Core compiles every `.cs` file under `<server root>/scripts/` at startup
   (Roslyn, referencing all loaded assemblies), writes the result to `lib/GameServerScripts.dll`,
   and loads it into the server process (`GameServer.CompileScripts`, `ScriptMgr.CompileScripts`).
   It's on by default; `EnableCompilation=false` in `serverconfig.xml` loads a pre-built DLL
   instead. Methods marked `[ScriptLoadedEvent]` / `[GameServerStartedEvent]` run at startup,
   and `[CmdAttribute]` classes become slash commands. A script therefore has the same access to
   live objects as Core's own code. AldhranBridge is delivered exactly this way, and its README says
   it runs unmodified on DOL and OpenDAoC.

   This is not to be confused with `GameServer/scripts/` in Core's source tree, which is built
   into `GameServer.dll` at normal build time.

   Caveats (read from code, not yet exercised):
   - **No hot reload.** Adding or changing a script means one server restart.
   - **A broken script can block startup.** Compilation is a startup step, so a script that fails
     to compile probably stops the server from coming up. Check on a test server first.
   - **No stable API.** Scripts call Core internals, so a Core upgrade can break compilation.
   - **Full trust.** A script can do anything the server process can.
   - **Docker.** The stock image compiles scripts mounted at `/app/scripts/custom` (as gaheris
     does, 2.6). Not yet exercised here.
   - **Names are data.** `mob.ClassType` and similar columns store fully qualified class names,
     so renaming a script class or its namespace silently breaks the world objects that use it.
     Per gaheris, packet handlers must sit in a namespace ending in the client version
     (`…Client.v168`) or they are skipped (verify).
   - **Only assemblies Core has already loaded.** Scripts compile against the assemblies loaded at
     that moment. Using another one (seen with `System.Text.Json` and Newtonsoft.Json on Core
     `0ac41d443`) produces an empty `GameServerScripts.dll` with no error shown, and the server stops
     at startup with "Bad IL format". Verified on a lab server, 2026-11-20.
   - **Check compilation before deploying.** gaheris builds the folder with a
     `GameServerScripts.csproj` before restarting. Here that belongs in CI.

   Leave `ApiHost` read-only and harden it separately (gap 5). Any HTTP front for web tools
   (opendaoc-manager, a web console) belongs in the script as its own localhost listener.

   *Existing DB-writing tools.* These (DOLToolbox, opendaoc-manager, camelot-studio, …) can be
   made to go through the host instead of writing directly. There are two ways:
   - **Retarget the tool.** Swap its data layer for a host client. This needs source, a licence
     that allows it, and a data layer that sits in one place.
   - **Leave the tool unchanged and point it at a staging DB.** Our tooling then compares
     staging with live and applies the differences through the host. This works for any tool,
     even binary-only ones. It is also the DB comparison and rollback work in gap 3.

   Either way, the host's operations must be typed (`item.update`, `loot.set`, …), not generic
   row writes. Each one declares how it reaches live state. Appendix A maps that per data type.

   *Live character edits* are the most common case. The concrete operations an operator needs
   are in [live-character-edits.md](live-character-edits.md): specs, level, realm points, money,
   items, position, stats, privilege level, spawning test NPCs, an offline fallback, and audit.
   That note also covers a stopgap that needs no server change: a headless GM client that logs
   in and drives the existing `/player`, `/jump`, `/mob` and `/item` commands. It works today, but
   needs a GM session per operation and returns only chat text.
5. **A hardened, documented API exposure.** The built-in API has no TLS, real auth, or rate
   limiting, and its docs omit the privileged routes.
6. **Account management for auto-create-off servers.** The official manager is idle and
   Discord-coupled; opendaoc-manager (3 weeks old, one author) is the active option.
   Worth evaluating before building — it may cover this category.

**Already on this project's backlog** (planned before this survey), mapped to the gaps:
account creation/management with auto-create off (gap 6), a decision on the auth approach for it
(in-server command vs. an external identity provider vs. a stock admin framework), Herald and a
server status page (built-in API covers the data, 1.1), operator helpers for DB fixes and
account/character management (gaps 2–4), and a re-login-lock exemption for staff/test accounts
after link-death (a Core change, not a tool).

**Candidates to evaluate before building anything here:** futuretim/opendaoc-manager,
camelot-forge/camelot-studio (console + backups), Darku11/daoc_cms (if a PHP stack is
acceptable), and Eve-of-Darkness/db-public (upgrade-without-clobber approach).

---

## Appendix A — What a write needs to reach live state

A design input for gap 4. For each kind of data: how Core holds it, and what, if anything,
makes a change visible without a restart. Read from Core `b9b32cf`. Rows marked **verified** were
tested on a lab copy on 2026-11-19 (Core `0ac41d443`, [cache-probe runbook](../runbooks/cache-probe.md#results));
the others still need a live test before an operation is built on them.

| Data (tables) | How Core holds it | Existing way to refresh | Class |
|---|---|---|---|
| Bans (`Bans`) | Queried at every login (`AbstractServerRules.cs:58,71`) | None needed for new logins; an online target must also be kicked | A |
| Merchant lists (`MerchantItem`) | Queried per page when the window is built (`MerchantTradeItems.GetItemsInPage`) | None needed; the item templates come from the item cache (below). **Verified** with the probe; the in-game window not yet checked | A |
| Accounts (`Account`) | Read at login (`LoginRequestHandler.cs:270`); an online session holds its own object | Offline: none needed. Online: change the session's object | A / C |
| Item templates (`ItemTemplate`) | **`PreCache = true`**: the whole table is held by the DB layer. Writes through Core's DB layer update the cache; direct SQL does not | `/item load <id_nb>`, `/item loadpackage <pkg>` (`UpdateInCache`); works from the console. **Verified** | B |
| Server properties (`serverproperty`) | Loaded at boot into static fields | `/serverproperties refresh` (`Properties.Refresh()`). **Verified** for the property itself. But static fields that copy a property when their class loads never see a refresh: 12 in `GeneratedUniqueItem.cs` (the `ROG_*` weights and qualities), 2 in `OnlineStatusUpdate.cs`. Those properties are effectively D | B |
| Spells, spell lines | `SkillBase` caches | `/reload spells`. **Verified** | B |
| Specialisations | `SkillBase` | `/reload specs` | B |
| NPC templates (`NpcTemplate`) | `NpcTemplateMgr` | `/reload npctemplates`. **Verified**; a mob already spawned from the template keeps its old values | B |
| NPC equipment (`NPCEquipment`) | `GameNpcInventoryTemplate`, built at boot | Re-initialised by a `/mob` subcommand (`mob.cs:1772`) | B |
| Mob spawns (`Mob`) | Loaded with the region | `/reload mob [realm\|name]` for **the GM's current region only**; refused from the console ("Region reload not supported from console"). Console refusal **verified**; in-game reload not yet run | B |
| Static objects (`WorldObject`) | Loaded with the region | `/reload object` (same region limit) | B |
| Teleports, doors | `WorldMgr`, `DoorMgr` | `/reload teleports`, `/reload doors` | B |
| Starting locations, starter equipment | Static caches in scripts | `/refresh` (`[RefreshCommand]` methods) | B |
| Loot (`LootGenerator`, `LootTemplate`, `MobXLootTemplate`, `LootOTD`) | `LootMgr` registry built at boot; static template caches | `ReloadLootTemplates()` / `ReloadLootOTD()` exist but **no command calls them**; `RefreshGenerators(mob)` via `/mob`. Template and one-time-drop reloads **verified** (called by the lab probe) | B (host only) |
| Data quests (`DataQuest`) | Static cache filled once (`GameObject` static constructor) | `FillDataQuestCache()` is public; **no command calls it**. **Verified** (called by the lab probe) | B (host only) |
| Online characters, their inventory | Live `GamePlayer` objects, saved on autosave/logout. Saves write only the columns that changed in memory | None. **Verified:** the session never sees a direct edit. The edit survives the save unless the server changed that column in memory (then it's overwritten). Money (`accountxmoney`) is always overwritten. Details: [live-character-edits.md](live-character-edits.md) | C |
| Guilds | `GuildMgr` in memory, saved by `SaveAllGuilds` | None: DB edits are overwritten | C |
| Offline characters | DB only | None needed, but the character must not log in during the edit | A |
| Keeps, hook points, guards; regions, zones, areas | Loaded at boot (`KeepManager`, `WorldMgr`, `AreaMgr`) | None found. Keep level **verified** (applies at restart only); regions, zones and areas not tested | D |

**Classes:**
- **A — read when used.** A write through Core's DB layer is live. The host op writes, plus
  kicks or refreshes an online session where needed.
- **B — cached, with a refresh path.** The host op writes through Core's DB layer, then calls
  the refresh method directly. "Host only" rows have refresh methods that no GM command reaches,
  so the host can do better than an operator can today.
- **C — live objects that save back.** The host op must change the live object and let Core
  save it. A direct DB write is never seen by the live object, and is lost or applied at the next
  login depending on what the player does meanwhile.
- **D — boot only.** The host op stages the change and reports "applies at restart".

**Consequences for the design:**
- Proxying GM commands (as AldhranBridge's `raw` does) is not enough. `/reload mob` works only
  in the issuing GM's region, and loot and quest refreshes have no command at all. The host
  calls Core's methods itself.
- Direct SQL against `ItemTemplate` is invisible to the server until `/item load`, because of
  the pre-cache. That alone makes "edit the DB with a desktop tool" unreliable on a running
  server.
- A staging-diff applier can sort changed rows by table into A–D and apply each one by its
  class. The D rows go into a "pending restart" list.
- Changing a live object is not the end of the job. The client must be told too. For example,
  after changing specs the skill and spell windows need re-sending, and possibly a stat
  recalculation or respec call _(verify which calls Core's `/player` commands make)_.
- Refreshes and live-object changes must run on the game loop, not on the socket thread
  _(verify how commands are dispatched today)_.
