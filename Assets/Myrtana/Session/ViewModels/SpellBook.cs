using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// The spell book page (G3 <c>IDD_PAGE_SPELLS_2</c>, Docs/formats-gui.md §6.8), already in the text language: the
    /// spell-book items (category Spellbook) by the deity of their spell in three lists — Innos, Adanos, Beliar (G3 filter
    /// values 2, 0, 1 of gESpellDeity; spells of no deity are not shown) — sorted like the inventory (template name). G3
    /// shows the hero's spell stacks, and the hero carries one of every spell, learned or not: an unlearned spell has the
    /// grey icon unless a found item of it is carried (gCInventoryStack::GetIcon: learned or amount &gt; 0). The detail
    /// view lists the attribute requirements and the mana (<c>HUD_Req_MP</c>, "min - max" when they differ).
    /// </summary>
    public sealed class SpellBook
    {
        public sealed class Spell
        {
            public Spell(string id, string item, string name, string description, string icon, bool learned, bool carried,
                         IReadOnlyList<LabelledValue> requirements)
            {
                Id = id;
                Item = item;
                Name = name;
                Description = description;
                Icon = icon;
                Learned = learned;
                Carried = carried;
                Requirements = requirements;
            }

            /// <summary>The spell ("Spl_Fireball").</summary>
            public string Id { get; }
            /// <summary>Its spell-book item ("It_Spell_Fireball"): readied, put on the quick bar.</summary>
            public string Item { get; }
            public string Name { get; }
            public string Description { get; }
            public string Icon { get; }
            /// <summary>G3 HasLearnedSpell: only a learned spell can be readied or put on the quick bar.</summary>
            public bool Learned { get; }
            /// <summary>The hero carries the item (a learned spell always; an unlearned one when found).</summary>
            public bool Carried { get; }
            /// <summary>The icon is in colour (G3: learned, or amount &gt; 0).</summary>
            public bool Coloured => Learned || Carried;
            /// <summary>Attribute requirements and the mana.</summary>
            public IReadOnlyList<LabelledValue> Requirements { get; }
        }

        /// <summary>The three lists in page order (G3 IDC_LISTCTRL_INNOS_2 / _ADANOS_2 / _BELIAR_2).</summary>
        public static readonly SpellDeity[] Deities = { SpellDeity.Innos, SpellDeity.Adanos, SpellDeity.Beliar };

        readonly Dictionary<SpellDeity, IReadOnlyList<Spell>> _lists;

        SpellBook(Dictionary<SpellDeity, IReadOnlyList<Spell>> lists) => _lists = lists;

        public IReadOnlyList<Spell> Of(SpellDeity deity) => _lists.TryGetValue(deity, out var list) ? list : System.Array.Empty<Spell>();

        /// <summary>Mana as G3 shows it: one number, or "min - max".</summary>
        public static string ManaText(int min, int max) => min == max ? min.ToString() : $"{min} - {max}";

        public static SpellBook Of(GameSession session)
        {
            var db = session.Content.Database;
            var text = session.Localizer;
            var spells = session.HeroSpells;
            var inventory = session.Items.Inventories.Of(ActorId.Player);
            var lists = new Dictionary<SpellDeity, List<(ItemDef def, Spell spell)>>();
            foreach (var item in spells.Items)
            {
                string spellId = spells.SpellOf(item);
                if (!db.TryGet(item, out ItemDef def) || !session.Magic.TryGetSpell(spellId, out var spellDef)) continue;
                if (spellDef.Deity == SpellDeity.None) continue;
                var requirements = new List<LabelledValue>();
                if (db.TryGet(spellId, out SkillDef skill))
                    requirements.AddRange(skill.Requirements.Select(r => new LabelledValue(RequirementName(session, r.Attribute), r.Minimum.ToString())));
                requirements.Add(new LabelledValue(RequirementName(session, "MP"), ManaText(spellDef.MinMana, spellDef.MaxMana)));
                var spell = new Spell(spellId, item, ItemLists.Name(session, def), def.Description.IsEmpty ? string.Empty : ItemLists.ShownDescription(text.Text(def.Description)),
                                      def.Icon, spells.IsLearned(spellId), inventory.Has(item), requirements);
                if (!lists.TryGetValue(spellDef.Deity, out var list)) lists[spellDef.Deity] = list = new List<(ItemDef, Spell)>();
                list.Add((def, spell));
            }
            var sorted = new Dictionary<SpellDeity, IReadOnlyList<Spell>>();
            foreach (var kv in lists)
            {
                kv.Value.Sort((x, y) => ItemOrder.Compare(x.def, x.spell.Name, y.def, y.spell.Name));
                sorted[kv.Key] = kv.Value.Select(x => x.spell).ToList();
            }
            return new SpellBook(sorted);
        }

        static string RequirementName(GameSession session, string attribute) =>
            session.Content.Database.TryGet(attribute, out AttributeDef a) && !a.RequirementName.IsEmpty
                ? session.Localizer.Text(a.RequirementName) : attribute;
    }
}
