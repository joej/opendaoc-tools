// LabProbe -- a drop-in OpenDAoC script for testing what the running server holds in memory.
//
// Purpose: confirm or refute docs/research/ops-tooling-inventory.md Appendix A
// (which data Core caches, and what refreshes each cache). Use it on a LAB server with a copy
// of the database, never on a live shard. Runbook: docs/runbooks/cache-probe.md.
//
// Install: copy this file into the server's scripts/ folder (Docker: the folder mounted at
// /app/scripts/custom) and restart. Run from the server console as `probe ...` or in game as
// `/probe ...` (Admin only).
//
// Reads use the server's in-memory state only. For the database side, use the mysql client, so
// the two sides of each comparison come from independent paths. Only `call` and `save` change
// anything, and each of them calls a method Core already has.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.GS;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;

namespace OpenDaocTools.Scripts
{
    [Cmd("&probe", ePrivLevel.Admin,
        "Lab probe: print what the server holds in memory (for cache testing)",
        "/probe item <id_nb>                  - ItemTemplate from the pre-cache",
        "/probe prop <key>                    - server property value in memory",
        "/probe spell <spellId>               - spell from SkillBase",
        "/probe npctemplate <templateId>      - NPC template from NpcTemplateMgr",
        "/probe mob <internalId>              - a spawned mob's live values",
        "/probe loot <templateName>           - LootGeneratorTemplate cache entry",
        "/probe loototd <mobName>             - LootGeneratorOneTimeDrop cache entry",
        "/probe quests [nameFilter]           - data quest cache",
        "/probe merchant <itemListId> <page>  - merchant page as the window would build it",
        "/probe player <exactName>            - an online player's level, money, RP, specs",
        "/probe call <loot-templates|loot-mobtemplates|loot-otd|quests> - run a refresh no command reaches",
        "/probe save <exactName>              - save an online player now (class C test)")]
    public class LabProbeCommand : AbstractCommandHandler, ICommandHandler
    {
        private static readonly DOL.Logging.Logger log =
            DOL.Logging.LoggerManager.Create(typeof(LabProbeCommand));

        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length < 2)
            {
                DisplaySyntax(client);
                return;
            }

            try
            {
                switch (args[1].ToLowerInvariant())
                {
                    case "item": ShowItem(client, Rest(args, 2)); break;
                    case "prop": ShowProp(client, Rest(args, 2)); break;
                    case "spell": ShowSpell(client, Arg(args, 2)); break;
                    case "npctemplate": ShowNpcTemplate(client, Arg(args, 2)); break;
                    case "mob": ShowMob(client, Arg(args, 2)); break;
                    case "loot": ShowLoot(client, Rest(args, 2)); break;
                    case "loototd": ShowLootOtd(client, Rest(args, 2)); break;
                    case "quests": ShowQuests(client, args.Length > 2 ? Rest(args, 2) : null); break;
                    case "merchant": ShowMerchant(client, Arg(args, 2), Arg(args, 3)); break;
                    case "player": ShowPlayer(client, Rest(args, 2)); break;
                    case "call": Call(client, Arg(args, 2)); break;
                    case "save": Save(client, Rest(args, 2)); break;
                    default: DisplaySyntax(client); break;
                }
            }
            catch (ArgumentException e)
            {
                Say(client, "probe: " + e.Message);
            }
            catch (Exception e)
            {
                Say(client, "probe: FAILED " + e.GetType().Name + ": " + e.Message);
                log.Error("probe failed: " + e);
            }
        }

        private static void ShowItem(GameClient client, string id)
        {
            // FindObjectByKey serves pre-cached tables from the cache, which is what we want to see.
            DbItemTemplate t = GameServer.Database.FindObjectByKey<DbItemTemplate>(id);
            if (t == null) { Say(client, "item " + id + ": not in cache"); return; }
            Say(client, "item " + t.Id_nb + ": name=" + t.Name + " level=" + t.Level +
                " dps_af=" + t.DPS_AF + " spd_abs=" + t.SPD_ABS + " quality=" + t.Quality +
                " bonus=" + t.Bonus + " price=" + t.Price);
        }

        private static void ShowProp(GameClient client, string key)
        {
            foreach (FieldInfo f in typeof(Properties).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = f.GetCustomAttribute<ServerPropertyAttribute>();
                if (attr == null || !string.Equals(attr.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                Say(client, "prop " + attr.Key + " = " + Render(f.GetValue(null)));
                return;
            }
            Say(client, "prop " + key + ": no such property");
        }

        private static void ShowSpell(GameClient client, string idText)
        {
            Spell s = SkillBase.GetSpellByID(ParseInt(idText, "spellId"));
            if (s == null) { Say(client, "spell " + idText + ": not found"); return; }
            Say(client, "spell " + s.ID + ": name=" + s.Name + " damage=" + s.Damage +
                " value=" + s.Value + " casttime=" + s.CastTime);
        }

        private static void ShowNpcTemplate(GameClient client, string idText)
        {
            NpcTemplate t = NpcTemplateMgr.GetTemplate(ParseInt(idText, "templateId"));
            if (t == null) { Say(client, "npctemplate " + idText + ": not found"); return; }
            Say(client, "npctemplate " + t.TemplateId + ": name=" + t.Name + " level=" + t.Level +
                " model=" + t.Model + " str=" + t.Strength + " maxspeed=" + t.MaxSpeed);
        }

        private static void ShowMob(GameClient client, string internalId)
        {
            foreach (Region region in WorldMgr.GetAllRegions())
            {
                GameNPC npc = WorldMgr.GetNPCsFromRegion(region.ID)
                    .FirstOrDefault(n => n != null && n.InternalID == internalId);
                if (npc == null)
                    continue;
                Say(client, "mob " + internalId + ": name=" + npc.Name + " level=" + npc.Level +
                    " region=" + region.ID + " template=" +
                    (npc.NPCTemplate == null ? "none" : npc.NPCTemplate.TemplateId.ToString()));
                return;
            }
            Say(client, "mob " + internalId + ": not spawned");
        }

        private static void ShowLoot(GameClient client, string templateName)
        {
            var cache = StaticField<Dictionary<string, Dictionary<string, DbLootTemplate>>>(
                typeof(LootGeneratorTemplate), "m_lootTemplates");
            if (cache == null) { Say(client, "loot: cache not built"); return; }
            if (!cache.TryGetValue(templateName.ToLowerInvariant(), out var entries))
            {
                Say(client, "loot " + templateName + ": not in cache");
                return;
            }
            Say(client, "loot " + templateName + ": " + entries.Count + " entries");
            foreach (DbLootTemplate e in entries.Values.OrderBy(e => e.ItemTemplateID))
                Say(client, "  " + e.ItemTemplateID + " chance=" + e.Chance + " count=" + e.Count);
        }

        private static void ShowLootOtd(GameClient client, string mobName)
        {
            var cache = StaticField<Dictionary<string, List<DbLootOtd>>>(
                typeof(LootGeneratorOneTimeDrop), "m_mobOTDList");
            if (cache == null) { Say(client, "loototd: cache not built"); return; }
            // Key casing is not normalised in one place, so try as given, then lower case.
            if (!cache.TryGetValue(mobName, out var list) &&
                !cache.TryGetValue(mobName.ToLowerInvariant(), out list))
            {
                Say(client, "loototd " + mobName + ": not in cache (" + cache.Count + " mobs cached)");
                return;
            }
            Say(client, "loototd " + mobName + ": " + list.Count + " entries");
            foreach (DbLootOtd e in list)
                Say(client, "  " + e.ItemTemplateID + " minlevel=" + e.MinLevel);
        }

        private static void ShowQuests(GameClient client, string filter)
        {
            List<DbDataQuest> all = GameObject.DataQuestCache;
            var shown = all.Where(q => filter == null ||
                    (q.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(20).ToList();
            Say(client, "quests: " + all.Count + " cached" +
                (filter == null ? "" : ", showing matches for '" + filter + "'"));
            foreach (DbDataQuest q in shown)
                Say(client, "  " + q.ID + " " + q.Name + " (start: " + q.StartName + ")");
        }

        private static void ShowMerchant(GameClient client, string listId, string pageText)
        {
            IDictionary page = new MerchantTradeItems(listId).GetItemsInPage(ParseInt(pageText, "page"));
            Say(client, "merchant " + listId + " page " + pageText + ": " +
                (page == null ? 0 : page.Count) + " items");
            if (page == null) return;
            foreach (DictionaryEntry e in page)
            {
                var t = e.Value as DbItemTemplate;
                Say(client, "  slot " + e.Key + ": " + (t == null ? "?" : t.Id_nb + " price=" + t.Price));
            }
        }

        private static void ShowPlayer(GameClient client, string name)
        {
            GamePlayer p = ClientService.Instance.GetPlayerByExactName(name);
            if (p == null) { Say(client, "player " + name + ": not online"); return; }
            Say(client, "player " + p.Name + ": level=" + p.Level + " money=" + p.GetCurrentMoney() +
                " rp=" + p.RealmPoints + " specpoints=" + p.SkillSpecialtyPoints);
            foreach (Specialization s in p.GetSpecList())
                Say(client, "  spec " + s.KeyName + " = " + s.Level);
        }

        private static void Call(GameClient client, string what)
        {
            switch (what.ToLowerInvariant())
            {
                case "loot-templates": LootGeneratorTemplate.ReloadLootTemplates(); break;
                case "loot-mobtemplates": LootGeneratorMobTemplate.ReloadLootTemplates(); break;
                case "loot-otd": LootGeneratorOneTimeDrop.ReloadLootOTD(); break;
                case "quests": GameObject.FillDataQuestCache(); break;
                default:
                    Say(client, "call: unknown target " + what +
                        " (loot-templates|loot-mobtemplates|loot-otd|quests)");
                    return;
            }
            Say(client, "call " + what + ": done");
            log.Info("probe call " + what + " by " + Who(client));
        }

        private static void Save(GameClient client, string name)
        {
            GamePlayer p = ClientService.Instance.GetPlayerByExactName(name);
            if (p == null) { Say(client, "save " + name + ": not online"); return; }
            p.SaveIntoDatabase();
            Say(client, "save " + p.Name + ": done");
            log.Info("probe save " + p.Name + " by " + Who(client));
        }

        // --- helpers -------------------------------------------------------------------------

        private static string Arg(string[] args, int i)
        {
            if (args.Length <= i || string.IsNullOrWhiteSpace(args[i]))
                throw new ArgumentException("missing argument " + i + "; see /probe for usage");
            return args[i];
        }

        // The console and chat split on spaces; names such as loot templates and mobs contain them.
        private static string Rest(string[] args, int i)
        {
            Arg(args, i);
            return string.Join(" ", args, i, args.Length - i);
        }

        private static int ParseInt(string text, string what)
        {
            if (!int.TryParse(text, out int v))
                throw new ArgumentException(what + " must be a number, got '" + text + "'");
            return v;
        }

        private static T StaticField<T>(Type owner, string name) where T : class
        {
            FieldInfo f = owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (f == null)
                throw new InvalidOperationException(owner.Name + "." + name +
                    " not found; Core changed, update the probe");
            return f.GetValue(null) as T;
        }

        private static string Render(object v)
        {
            if (v == null) return "(null)";
            if (v is string s) return "'" + s + "'";
            if (v is IEnumerable e) return "[" + string.Join(", ", e.Cast<object>()) + "]";
            return v.ToString();
        }

        private static string Who(GameClient client) =>
            client?.Account?.Name ?? "unknown";

        // Sends straight to the client's packet lib: AbstractCommandHandler.DisplayMessage runs the
        // text through string.Format, which would throw on braces in data.
        private static void Say(GameClient client, string message)
        {
            client?.Out?.SendMessage(message, eChatType.CT_System, eChatLoc.CL_SystemWindow);
            log.Info(message);
        }
    }
}
