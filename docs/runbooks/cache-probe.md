# Runbook: testing what reaches a running server (cache probe)

Tests the claims in [Appendix A of the ops-tooling inventory](../research/ops-tooling-inventory.md#appendix-a--what-a-write-needs-to-reach-live-state):
for each kind of data, does a direct database write reach the running server, and what refreshes it?
The tool is [`server-scripts/probe/LabProbe.cs`](../../server-scripts/probe/LabProbe.cs), a drop-in
script that adds an Admin command, `probe`, which prints what the server holds **in memory**.

**Run this only against a lab instance with a copy of the database.** The tests edit rows directly
on purpose.

## How each test works

1. **Baseline.** `probe` (memory) and `SELECT` (database) agree.
2. **Direct write.** `UPDATE` the row with SQL, bypassing the server.
3. **Probe again.** This step is what makes the test meaningful:
   - **Class A** (read when used): the probe shows the new value.
   - **Class B** (cached): the probe still shows the **old** value. If it already shows the new
     one, the probe is reading the database, not the cache, and the test is invalid.
4. **Refresh.** Run the command or `probe call`, then probe again. It should show the new value.
5. **Record** the result in the table at the end.

The probe reads only memory; the database side always comes from the `mysql` client. The two
sides never share a code path.

## Setup

### 1. Copy the database

Use the same MariaDB server as the real instance, with a new database name. Adjust the client
invocation to your setup, e.g. `docker exec -i <db-container> mysql ...`:

```bash
mysqldump --single-transaction opendaoc > /tmp/lab_scratch.sql
mysql -e 'CREATE DATABASE lab_scratch'
mysql lab_scratch < /tmp/lab_scratch.sql
```

Turn off everything in the copy that talks to the outside world, so the lab instance can't post
to Discord, mail anyone, or announce itself:

```sql
-- in lab_scratch
UPDATE ServerProperty SET Value='False' WHERE `Key` IN ('Discord_Webhook_Active','serverlistupdate_enabled','atlas_api');
UPDATE ServerProperty SET Value=''      WHERE `Key` IN ('Discord_Webhook_ID','Discord_RVR_Webhook_ID','Discord_AlbChat_Webhook_ID',
                                                       'Discord_HibChat_Webhook_ID','Discord_MidChat_Webhook_ID',
                                                       'log_email_addresses','bug_report_email_addresses');
```

To reset at any point, drop `lab_scratch` and repeat this step.

### 2. A second server instance

Copy the server's build output (the folder holding `CoreServer` and `lib/`) to a new directory.
Edit its `config/serverconfig.xml`:

| Element | Lab value |
|---|---|
| `Port` | `10301` |
| `RegionPort`, `UdpPort` | `10401` |
| `ServerName` | something visibly "LAB" |
| `DBConnectionString` | `Database=lab_scratch` |
| `EnableUPnP` | `False` |

Record the Core commit of this build in the results.

### 3. Compile-check the probe, then install it

```bash
dotnet build server-scripts/ScriptCheck.csproj -p:CoreLibs=<lab build>/lib
cp server-scripts/probe/LabProbe.cs <lab build>/scripts/
```

The server compiles every file in `scripts/` into one assembly at startup. A script that fails to
compile takes all the others down with it, which is why the check comes first.

### 4. Start the lab instance with a usable console

Run the lab instance in the foreground inside `tmux` or `screen`, not as a service. Core's console
reads stdin, and a service whose stdin is `/dev/null` can't take commands.

```bash
tmux new -s lab 'cd <lab build> && dotnet CoreServer.dll'
```

At the console, type `probe`. It should print the usage list. If it says "Unknown command", the
script didn't load: check the startup log for compile errors.

### 5. For classes A and C: a client

Have a game client that can connect to port 10301, and two accounts in the lab DB:
- an Admin (plvl 3), for in-game commands;
- an ordinary test account with one character, `LabChar` below.

## Tests

`$SQL` stands for your mysql client on `lab_scratch`. Pick real ids from the lab DB. Note the
original value before each `UPDATE` so you can restore it, or reset the DB afterwards.

### Class B: cached, with a refresh path

**B1. Item template (pre-cached table)**
```sql
SELECT Id_nb, Price FROM ItemTemplate WHERE Id_nb='<id>';
UPDATE ItemTemplate SET Price=Price+1 WHERE Id_nb='<id>';
```
`probe item <id>` → expect the **old** price. Refresh: `item load <id>` (console or in game as Admin).
Then `probe item <id>` → the new price.

**B2. Server property**

`Discord_Webhook_ID` is harmless here because step 1 turned webhooks off.
```sql
UPDATE ServerProperty SET Value='probe-test' WHERE `Key`='Discord_Webhook_ID';
```
`probe prop Discord_Webhook_ID` → old (`''`). Refresh: console `serverproperties refresh`. Probe
→ `'probe-test'`. Set it back to `''` and refresh.

**B3. Spell**
```sql
UPDATE Spell SET Damage=Damage+1 WHERE SpellID=<n>;
```
`probe spell <n>` → old. Refresh: console `reload spells`. Probe → new.

**B4. NPC template, and a mob already spawned from it**
```sql
SELECT TemplateId, Level FROM NpcTemplate WHERE TemplateId=<t>;
UPDATE NpcTemplate SET Level='<new>' WHERE TemplateId=<t>;
```
`probe npctemplate <t>` → old. Refresh: console `reload npctemplates`. Probe → new.
Then `probe mob <internalId>` on a mob spawned from that template. Record whether it changed:
the expectation is "not until respawn".

**B5. Mob spawn, and `/reload mob` from the console**
```sql
SELECT Mob_ID, Name, Level, Region FROM Mob WHERE Mob_ID='<internalId>';
UPDATE Mob SET Level=Level+1 WHERE Mob_ID='<internalId>';
```
`probe mob <internalId>` → old. Console `reload mob` → expect a refusal: "Region reload not
supported from console". In game, as Admin, standing in the mob's region: `/reload mob` → probe →
new.

**B6. Loot template (no command reaches this refresh)**
```sql
SELECT TemplateName, ItemTemplateID, Chance FROM LootTemplate WHERE TemplateName='<tpl>';
UPDATE LootTemplate SET Chance=Chance+1 WHERE TemplateName='<tpl>' AND ItemTemplateID='<item>';
```
`probe loot <tpl>` → old chance. Refresh: `probe call loot-templates`. Probe → new.

**B7. One-time drops**
```sql
UPDATE LootOTD SET MinLevel=MinLevel+1 WHERE MobName='<mob>' AND ItemTemplateID='<item>';
```
`probe loototd <mob>` → old. Refresh: `probe call loot-otd`. Probe → new.

**B8. Data quests (no command reaches this refresh)**
```sql
UPDATE DataQuest SET Name=CONCAT(Name,' PROBE') WHERE ID=<q>;
```
`probe quests PROBE` → no match. Refresh: `probe call quests`. Probe → the quest shows.
Restore the name, then call again.

### Class A: read when used

**A1. Merchant list**
```sql
SELECT ItemListID, PageNumber, SlotPosition, ItemTemplateID FROM MerchantItem WHERE ItemListID='<list>' AND PageNumber=0;
UPDATE MerchantItem SET ItemTemplateID='<other item>' WHERE ItemListID='<list>' AND PageNumber=0 AND SlotPosition=<s>;
```
`probe merchant <list> 0` → expect the **new** item with no refresh. In game, the merchant window
should show it too. Note: the probe builds a fresh list object, so the in-game check is the one
that matters for merchants already in the world.

**A2. Ban, read at login**

`LabChar`'s account is logged in.
```sql
INSERT INTO Ban (Ban_ID, Author, Type, Ip, Account, DateBan, Reason)
VALUES (UUID(), 'probe', 'A', '', '<LabChar account>', NOW(), 'cache probe test');
```
Expect: the current session stays connected (nothing re-checks it). Log out and in again → login
refused. Delete the row afterwards. If the insert fails on a column, compare with `SHOW CREATE
TABLE Ban` and adjust.

### Class C: live objects that save back

**C1. Online character, a simple field**

`LabChar` is in the world.
```sql
SELECT Name, Level, Copper FROM DOLCharacters WHERE Name='LabChar';
UPDATE DOLCharacters SET Copper=Copper+5 WHERE Name='LabChar';
```
`probe player LabChar` → old money. `probe save LabChar`, then `SELECT` again → expect the
**old** value back in the database. The edit is lost.

**C2. Online character, specs**

This is the case in [live-character-edits.md](../research/live-character-edits.md). Same as C1
with `SerializedSpecs`: change one spec level, `probe save LabChar`, and confirm the database
reverts. Then log `LabChar` out to character select, repeat the `UPDATE`, log in, and
`probe player LabChar` → the new spec level (offline edit works).

### Class D: loaded only at startup

**D1. Keep**

Pick an unclaimed keep: keeps save themselves on claim, release, level change and reset, so a
claimed keep could write its old level back.
```sql
SELECT KeepID, Name, Level, ClaimedGuildName FROM Keep WHERE KeepID=<k>;
UPDATE Keep SET Level=Level+1 WHERE KeepID=<k>;
```
`probe keep <k>` → old level. Restart the lab instance → `probe keep <k>` → new level. Restore
the level and restart again.

### Optional, also class B

Each of these has a command. Test them as B1 if time allows:
- teleports: `reload teleports`
- doors: `reload doors`
- specialisations: `reload specs`
- starting locations and gear: `refresh list`, then the listed method
- NPC equipment: the `/mob` subcommand that re-initialises it

## Results

| Test | Expected | Observed | Pass? |
|---|---|---|---|
| B1 item template | stale until `/item load` | stale; `item load` from the **console** refreshed it | yes |
| B2 server property | stale until `serverproperties refresh` | stale; refresh showed the new value | yes |
| B3 spell | stale until `reload spells` | stale; reload showed the new damage | yes |
| B4 NPC template | stale until `reload npctemplates`; spawned mob unchanged | stale; reload updated the template; the spawned mob kept the old level | yes |
| B5 mob spawn | stale; console reload refused; in-game reload works | stale; console refused ("Region reload not supported from console"). In-game part not run | partly |
| B6 loot template | stale until `probe call loot-templates` | stale; call showed the new chance | yes |
| B7 one-time drop | stale until `probe call loot-otd` | stale; call showed the new level | yes |
| B8 data quest | stale until `probe call quests` | no match; call found the renamed quest | yes |
| A1 merchant list | live | new item with no refresh (probe only; in-game window not checked) | yes |
| A2 ban | next login refused; session kept | not run (needs a client) | |
| C1 online character, money | DB edit lost on save | not run (needs a client) | |
| C2 online character, specs | lost online; works offline | not run (needs a client) | |
| D1 keep | applies at restart only | unclaimed keep: old level after the write; new level after a restart | yes |

Core commit tested: `0ac41d443` (EoD fork, tag 2026.09.36)  Date: 2026-11-19

Every value was restored and refreshed afterwards. One probe bug turned up: arguments were cut at
the first space, so loot and mob names with spaces weren't found. Fixed in the same commit; the
fix was tested live on the same build.

Afterwards, update Appendix A: mark each row verified or corrected, with the commit and date.
Drop `lab_scratch` when finished.
