using System;
using System.Linq;
using System.Collections.Generic;
using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The G3 (CP 1.75) player start kit as content. G3 has no data file for it: the new-game script
    /// (OnGameStartUp → InventoryPopulate → InventoryAssureStartupItems in Script_Game.dll) assures these stacks in the
    /// hero's inventory and equips the sword; the PC_Hero template and world entity carry no stacks.
    /// Values and the reading of the call arguments: Docs/formats-items.md §3.5.
    /// </summary>
    public static class G3HeroStart
    {
        /// <summary>Item stacks: template, quality bits, amount, G3 quick-slot key (−1 none), equipped.</summary>
        static readonly (string Item, int Quality, int Amount, int Key, bool Equipped)[] Stacks =
        {
            ("It_1H_OrcSlayer_01", 0, 1, 1, true),
            ("It_Torch_Burning", 8, 1, 2, false),
            ("It_Bow_Hunting", 0, 1, 3, false),
            ("Arrow", 0, 50, 4, false),
            ("It_Potion_Health", 0, 10, 5, false),
            ("It_Gold", 0, 123, -1, false),
            ("It_Key_Lockpick", 0, 3, -1, false),
            ("It_Potion_Mana", 0, 5, -1, false),
            ("It_Scroll_Light", 0, 1, -1, false),
            ("It_Scroll_Fireball", 0, 3, -1, false),
            ("It_Scroll_HealOther", 0, 1, -1, false),
        };

        /// <summary>
        /// The perk stacks G3 adds with the learned flag set. It also adds every other perk and spell unlearned, which is
        /// how G3 lists skills; our hero keeps only a set of known skills, so those add nothing.
        /// </summary>
        static readonly string[] LearnedSkills =
        {
            "Perk_PotionHP", "Perk_Bow_1", "Perk_Smith_1", "Perk_1H_1", "Perk_LockPick_1", "Perk_Sneak_1",
        };

        /// <param name="startXp">XP of the CP start helper's info (<see cref="StartXpOf"/>).</param>
        /// <summary>CP's game start spawns this helper entity (a template, no world position); its info gives the start XP.</summary>
        public const string StartHelper = "Ardea_Spawn_Point";

        /// <summary>
        /// The XP the start helper's infos give (CP 1.75 OnGameStartUp spawns <see cref="StartHelper"/>; its only info,
        /// Important with a General condition, runs GiveXP 500). It is no NPC, so the info itself never runs here: the
        /// start kit carries the XP instead.
        /// </summary>
        public static int StartXpOf(IEnumerable<Myrtana.Story.DialogEntryDef> entries) =>
            entries.Where(e => string.Equals(e.Owner.Name, StartHelper, StringComparison.OrdinalIgnoreCase))
                   .SelectMany(e => e.Commands).OfType<Myrtana.Story.GiveXp>().Sum(x => x.Amount);

        public static HeroStartKit Kit(ItemNames names, ImportReport report, int startXp = 0)
        {
            var items = new List<StartItem>();
            foreach (var s in Stacks)
            {
                var id = names.Item(s.Item);
                if (id == null) { report.Count("hero start: unknown item", s.Item); continue; }
                items.Add(new StartItem(id, s.Amount, s.Quality, s.Key < 0 ? -1 : QuickSlots.SlotOfDigit(s.Key), s.Equipped));
            }
            var skills = new List<string>();
            foreach (var name in LearnedSkills)
            {
                var id = names.Skill(name);
                if (id == null) report.Count("hero start: unknown skill", name);
                else skills.Add(id);
            }
            report.Note($"hero start kit: {items.Count} stacks, {skills.Count} skills, start XP {startXp}");
            return new HeroStartKit(HeroStartKit.DefinitionId, items, skills, startXp);
        }
    }
}
