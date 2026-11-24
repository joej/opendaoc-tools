# Admin host: revive, items and level (design and test results, 2026-11-24)

Follows [move and money](admin-host-move-money.md). These are the next most-requested player edits
from the same survey of tasks that need someone in-game:

- bring a test character back after a test death;
- give it items for merchant, loot and crafting tests;
- set its level for each class being tested.

| Op | Args | What it does |
|---|---|---|
| `player.revive` | `name`, optional `clearPenalty` | Online only. A dead player is raised where they lie at full health, power and endurance, without resurrection illness. A living player is healed, as `/heal` does |
| `player.item.set` | `name`, `id` (item template), `count`, `queue` | Sets how many of one item template the backpack holds (an absolute number). Items are added as a merchant sale adds them, or removed as Core removes a template |
| `player.level.set` | `name`, `level` (1 to 50), `queue` | Sets the level as the GM command `/player level` does. Experience is set to the start of the new level |

As before: "online" means `EnteredGame`, each change writes one audit line, nothing is audited when
nothing changes, the player gets a line in their system window, and the result is saved straight away.

## Revive

A dead player gets the calls a resurrection spell makes (`ResurrectSpellHandler.ResurrectLiving`):

- `RESURRECT_REZ_SICK_EFFECTIVENESS` is set to 0, so the `OnRevive` handler that Core registered at
  death adds no illness and unregisters itself.
- `Notify(GamePlayerEvent.Revive)`, then health, power and endurance are set to their maximum.
- `StopReleaseTimer()` and `SendPlayerRevive`.
- The pending constitution loss is dropped. Core charges constitution only when the player releases,
  and a resurrection never does.

Being dead doesn't outlast a session, so there is no queued revive.

`clearPenalty: true` also:

- ends resurrection illness (as `/heal` does);
- gives back constitution lost at earlier deaths (`TotalConstitutionLostAtDeath = 0`, as the healer
  NPC does);
- resets `DeathCount`, which scales the next death's penalty.

This runs before the heal, so the heal fills the higher maximum health that the restored constitution gives.

`clearPenalty` doesn't give back experience. Core stores the loss it meant to take
(`DEATH_EXP_LOSS_PROPERTY`), but `GainExperience` stops at the start of the current level. On the
lab, a death at exactly the start of level 10 printed "You lose some experience!" and recorded a
3,328-point loss, while the experience didn't change. Giving back the recorded amount would have
added experience that was never lost.

## Items

- **Which slots:** the backpack only (slots 40 to 79). Equipped items and the vault don't count.
- **Adding:** `AddTemplate` from `GameInventoryItem.Create(template)`, the merchant's call. It stacks
  where it can and checks that everything fits before it adds anything. If it doesn't fit, the answer is
  `invalid` with the number of free slots, and nothing changes.
- **Removing:** `RemoveTemplate`. Both directions go to Core's inventory log as `[OTHER]` with
  `(admin host)` on the admin side.
- **Limits:** a template that doesn't exist is `not_found`, and a count above 40,000 (40 slots of
  1,000) is `invalid`.
- **Queued:** checked when it's applied. The queue answer shows the saved count, read from
  `inventory` by `ITemplate_Id`. A newer edit for the same template replaces the older one.

## Level

The same steps as `/player level`:

- **Raising** sets `Level`, which runs Core's level-up: stats, spec points, abilities and the
  level-up messages.
- **Lowering** first calls `Reset()`. That takes the character to level 1, respecs every line and
  realm ability, and takes back the stats gained per level. Then it raises the character to the target.

So lowering always respecs; the answer has `respecced: true`, and a queued lowering says so in its note.
In both directions, experience is set to the start of the new level, and the client gets the
trainer's updates (points, player, stats, resists, skills).

### Queued edits at login

- Edits apply in queue order, with two exceptions: a level change goes first and moves go last. Spec
  edits are checked against the level, and lowering one respecs.
- When a spec edit is queued, its level is checked against the queued level if one is waiting,
  otherwise against the saved level.

## Tested (2026-11-24, Core `0ac41d443`)

On the lab copy with the headless client. A lab-only command (not in this repo) killed the character;
Core's `/player kill` refuses GMs, and the lab account is one.

| Check | Result |
|---|---|
| Level up | 10 → 11: spec points 47 → 58, experience 459,950 (the start of level 11), saved at once |
| Level down | 11 → 10: `respecced: true`, every line back to 1, 54 points, experience 203,950 (the start of level 10) |
| Same level | `changed: false`, no audit line |
| Level refusals | 0 and 51: `invalid` |
| Items, stackable | 0 → 25 → 250 (two stacks) → 10 → 0. The database `inventory` rows matched; the client got `0x02` inventory updates |
| Items, not stackable | 3 daggers took 3 slots. 60 were refused ("57 … doesn't fit, 35 free slots") and nothing changed |
| Item refusals | Unknown template: `not_found`. Count -1: `invalid` |
| Heal | A living player at 138 of 213 health went to 213 |
| Revive | After a death (Core's own "You have died"): raised in place, alive at full health, no effects, `DeathCount` 1, constitution loss not charged |
| Release, then `clearPenalty` | After `/release` (resurrection illness, 1 or 2 constitution lost): illness ended, constitution back, health at the restored maximum (213 of 213), `DeathCount` 0 |
| Queued | Item 5 then 7 (replaced), daggers 2, level 11, Slash 11. At login: level, then the items, then the spec, which was refused for points (63 needed, 58 available). With the level checked first, the refusal is about points, not level |
| Queue-time level check | With level 11 queued, Slash 11 was accepted and Slash 12 refused ("the character's queued level") |
| Restore | Level 10, specs, items 0, queued together and applied at the next login. The character matched its starting row exactly |

Not tested:

- a revive in RvR (other illness spell), in a duel, or while the release timer is about to fire;
- a queued item edit that no longer fits at login (the online refusal and the "refused at login" path
  are each tested);
- levels above 40 (second stage, half levels).
