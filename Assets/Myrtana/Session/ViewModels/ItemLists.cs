using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>One line of an item list (inventory, merchant stock), already in the text language.</summary>
    public sealed class ItemRow
    {
        public ItemRow(string item, string name, string icon, int amount, bool worn, int price,
                       ItemCategory category = ItemCategory.Misc, int quality = 0, bool held = false, int acquired = 0)
        {
            Held = held;
            Item = item;
            Name = name;
            Icon = icon;
            Amount = amount;
            Worn = worn;
            Price = price;
            Category = category;
            Quality = quality;
            Acquired = acquired;
        }

        public string Item { get; }
        public string Name { get; }
        /// <summary>Icon key (<see cref="ItemDef.Icon"/>).</summary>
        public string Icon { get; }
        public int Amount { get; }
        public bool Worn { get; }
        /// <summary>Held in a hand without being worn (the burning torch, a drawn spell or scroll; <see cref="HeroHands"/>).</summary>
        public bool Held { get; }
        /// <summary>G3's blue "equipped" tile (<see cref="EquippedMarker"/>): worn or held.</summary>
        public bool Marked => Worn || Held;
        /// <summary>Gold per unit in this list (value, buy or sell price).</summary>
        public int Price { get; }
        public ItemCategory Category { get; }
        /// <summary>Quality bits of the item and its stacks (<see cref="ItemDef.Quality"/> | <see cref="ItemStack.Quality"/>).</summary>
        public int Quality { get; }
        /// <summary>Newest acquisition number of the item's stacks (<see cref="ItemStack.Acquired"/>); higher = newer.</summary>
        public int Acquired { get; }
    }

    /// <summary>The inventory pages in G3 category order (gEItemCategory: Misc 6 before Written 7).</summary>
    public enum ItemPage { Weapons, Armor, Artefacts, Alchemy, Misc, Books }

    /// <summary>What an item-panel row shows (G3 colours it by kind).</summary>
    public enum ItemLineKind { RequirementMet, RequirementUnmet, Damage, Protection, Modifier, Gold, Quality, ManaCost }

    /// <summary>One row of an item's numbers.</summary>
    public readonly struct ItemLine
    {
        public ItemLine(ItemLineKind kind, string label, string value, int quality = 0)
        {
            Kind = kind;
            Label = label;
            Value = value;
            Quality = quality;
        }

        public ItemLineKind Kind { get; }
        public string Label { get; }
        public string Value { get; }
        /// <summary>The one quality bit a <see cref="ItemLineKind.Quality"/> row stands for (its 16² icon); 0 otherwise.</summary>
        public int Quality { get; }
    }

    /// <summary>An item's name, description, icon and numbers, already in the text language.</summary>
    public sealed class ItemDetail
    {
        public ItemDetail(string name, string description, string icon, ItemCategory category, IReadOnlyList<ItemLine> lines)
        {
            Name = name;
            Description = description;
            Icon = icon;
            Category = category;
            Lines = lines;
        }

        public string Name { get; }
        public string Description { get; }
        public string Icon { get; }
        public ItemCategory Category { get; }
        public IReadOnlyList<ItemLine> Lines { get; }
    }

    /// <summary>A titled group of an item list (G3 inventory pages: weapons, armour, magic …).</summary>
    public sealed class ItemGroup
    {
        public ItemGroup(string title, IReadOnlyList<ItemRow> rows, ItemPage page = ItemPage.Misc)
        {
            Title = title;
            Rows = rows;
            Page = page;
        }

        public string Title { get; }
        public ItemPage Page { get; }
        public IReadOnlyList<ItemRow> Rows { get; }
    }

    /// <summary>G3 item quality bits (gCItem_PS.Quality | gCInventoryStack.Quality).</summary>
    public static class ItemQualityBits
    {
        public const int Poisoned = 0x04, Sharpened = 0x20, Blessed = 0x40, Forged = 0x80, Worn = 0x100;
    }

    /// <summary>
    /// What G3's stack compare needs of one row (gCInventoryStack::Compare): the item, its shown name and description,
    /// the spell it carries, its recipe, whether it is worn, and the row's acquisition number and price for the owner's
    /// sort modes (<see cref="ItemSorting"/>).
    /// </summary>
    public sealed class ItemSortKey
    {
        public ItemSortKey(ItemDef def, string name, string description = null, SpellDef spell = null, RecipeDef recipe = null,
                           bool recipeMakesSpellItem = false, bool worn = false, int quality = 0, int acquired = 0, int price = 0)
        {
            Def = def;
            Name = name ?? def.Id;
            Description = description ?? string.Empty;
            Spell = spell;
            Recipe = recipe;
            RecipeMakesSpellItem = recipeMakesSpellItem;
            Worn = worn;
            Quality = quality | def.Quality;
            Acquired = acquired;
            Price = price;
        }

        public ItemDef Def { get; }
        public string Name { get; }
        public string Description { get; }
        /// <summary>The spell the item casts (rune, scroll, spell book; gCItem_PS.Spell); null = none.</summary>
        public SpellDef Spell { get; }
        /// <summary>The recipe the item teaches (gCRecipe_PS); null = none.</summary>
        public RecipeDef Recipe { get; }
        /// <summary>The recipe's result carries a spell (gCRecipe_PS::CompareTo puts those last).</summary>
        public bool RecipeMakesSpellItem { get; }
        public bool Worn { get; }
        public int Quality { get; }
        public int Acquired { get; }
        public int Price { get; }
    }

    /// <summary>
    /// G3's inventory order (gCInventoryStack::Compare 100c2460, Docs/ui-skin.md). Across categories by gEItemCategory
    /// (rank table 102ddd24 = identity): Weapon, Armor, Artefact, Alchemy, Misc, Written, Spellbook, Skill. Within one:
    /// <list type="bullet">
    /// <item>Weapon (CompareWeapon 100c25c0): use-type rank, damage (higher first), template name (stricmp), quality.</item>
    /// <item>Armor (CompareArmor 100c2610): rank, PROT_BLADE (higher first), template name, worn first, gold value (higher first).</item>
    /// <item>Artefact (CompareArtefact 100c2660): rank, spell (none first; deity, required INT, max mana, then shown
    /// name), template name, worn first, gold value.</item>
    /// <item>Alchemy (CompareAlchemy 100c2690): rank, spell, template name (case-sensitive).</item>
    /// <item>Written (CompareDocument 100c26c0): rank, recipe (none first; craft, result without a spell first, then
    /// shown name), shown name, description.</item>
    /// <item>Spellbook: spell. Skill: unsorted. Everything else (CompareFocusName 100c2c00): shown name, description.</item>
    /// </list>
    /// Use-type ranks (table 102dde38); unranked use types (Axe, Halberd, Letter …) sort last. Quick-slot ties are not
    /// modelled (one row per item). Text compares are ordinal [I: bCUnicodeString::Compare taken as wcscmp].
    /// </summary>
    public static class ItemOrder
    {
        static readonly Dictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["TwoHanded"] = 101, ["OneHanded"] = 102, ["Bow"] = 103, ["Arrow"] = 104, ["Bolt"] = 105, ["CrossBow"] = 106,
            ["Armor"] = 201, ["Body"] = 201, ["Shield"] = 202, ["Helmet"] = 203, ["Hair"] = 203, ["Staff"] = 204,
            ["Amulet"] = 301, ["Ring"] = 302, ["Cast"] = 303,
            ["Potion"] = 401, ["Meat"] = 403, ["Fruit"] = 404, ["Bread"] = 405, ["Bowl"] = 406, ["Bottle"] = 407, ["Cup"] = 408,
            ["Modify"] = 409, ["Plant"] = 410,
        };

        static readonly ItemCategory[] CategoryOrder =
        {
            ItemCategory.Weapon, ItemCategory.Armor, ItemCategory.Artefact, ItemCategory.Alchemy, ItemCategory.Misc,
            ItemCategory.Written, ItemCategory.Spellbook, ItemCategory.Skill,
        };

        public static int Rank(string kind) => kind != null && Ranks.TryGetValue(kind, out int r) ? r : int.MaxValue;

        /// <summary>Position of a category in G3's order (the inventory groups).</summary>
        public static int CategoryRank(ItemCategory c) => Array.IndexOf(CategoryOrder, c);

        /// <summary>G3's compare of two items known only by definition and shown name (no spell, recipe or stack data).</summary>
        public static int Compare(ItemDef a, string nameA, ItemDef b, string nameB) =>
            Compare(new ItemSortKey(a, nameA), new ItemSortKey(b, nameB));

        public static int Compare(ItemSortKey a, ItemSortKey b)
        {
            int c = CategoryRank(a.Def.Category).CompareTo(CategoryRank(b.Def.Category));
            if (c != 0) return c;
            switch (a.Def.Category)
            {
                case ItemCategory.Weapon:
                    return First(Type(a, b), Damage(b.Def).CompareTo(Damage(a.Def)), Template(a, b), Quality(b).CompareTo(Quality(a)));
                case ItemCategory.Armor:
                    return First(Type(a, b), Blade(b.Def).CompareTo(Blade(a.Def)), Template(a, b), Slot(a, b), Value(a, b));
                case ItemCategory.Artefact:
                    return First(Type(a, b), Spell(a, b), Template(a, b), Slot(a, b), Value(a, b));
                case ItemCategory.Alchemy:
                    return First(Type(a, b), Spell(a, b), Sign(string.CompareOrdinal(a.Def.Id, b.Def.Id)));
                case ItemCategory.Written:
                    return First(Type(a, b), Recipe(a, b), FocusName(a, b));
                case ItemCategory.Spellbook:
                    return Spell(a, b);
                case ItemCategory.Skill:
                    return 0;
                default:
                    return FocusName(a, b);
            }
        }

        static int First(params int[] results)
        {
            foreach (int r in results)
                if (r != 0) return r;
            return 0;
        }

        static int Type(ItemSortKey a, ItemSortKey b) => Rank(a.Def.Kind).CompareTo(Rank(b.Def.Kind));

        static int Template(ItemSortKey a, ItemSortKey b) => StrICmp(a.Def.Id, b.Def.Id);

        /// <summary>C stricmp: case folded to lower case, then character order ('_' sorts before letters).</summary>
        public static int StrICmp(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[i]));
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        static int FocusName(ItemSortKey a, ItemSortKey b) =>
            First(Sign(string.CompareOrdinal(a.Name, b.Name)), Sign(string.CompareOrdinal(a.Description, b.Description)));

        static int Sign(int c) => c < 0 ? -1 : c > 0 ? 1 : 0;

        /// <summary>Worn stacks first (G3 CompareSlot: no slot counts as the last slot).</summary>
        static int Slot(ItemSortKey a, ItemSortKey b) => b.Worn.CompareTo(a.Worn);

        static int Value(ItemSortKey a, ItemSortKey b) => b.Def.GoldValue.CompareTo(a.Def.GoldValue);

        /// <summary>G3 CompareQuality: (bits without Worn)·4, plus 2 when worn out, else 4; higher first.</summary>
        static long Quality(ItemSortKey k) =>
            ((long)(k.Quality & ~ItemQualityBits.Worn) << 2) | ((k.Quality & ItemQualityBits.Worn) != 0 ? 2L : 4L);

        /// <summary>gCInventoryStack::CompareSpell + gCMagic_PS::CompareTo (100cf410): no spell first, then deity, required INT, max mana, shown name.</summary>
        static int Spell(ItemSortKey a, ItemSortKey b)
        {
            if (a.Spell == null) return b.Spell != null ? -1 : 0;
            if (b.Spell == null) return 1;
            int c = First(DeityRank(a.Spell.Deity).CompareTo(DeityRank(b.Spell.Deity)),
                          a.Spell.RequiredIntelligence.CompareTo(b.Spell.RequiredIntelligence),
                          a.Spell.MaxMana.CompareTo(b.Spell.MaxMana));
            return c != 0 ? c : FocusName(a, b);
        }

        /// <summary>gESpellDeity values: Adanos 0, Beliar 1, Innos 2, none 3.</summary>
        static int DeityRank(SpellDeity d)
        {
            switch (d)
            {
                case SpellDeity.Adanos: return 0;
                case SpellDeity.Beliar: return 1;
                case SpellDeity.Innos: return 2;
                default: return 3;
            }
        }

        /// <summary>gCInventoryStack::CompareRecipe + gCRecipe_PS::CompareTo (101a5d70): no recipe first, craft, result without a spell first, shown name.</summary>
        static int Recipe(ItemSortKey a, ItemSortKey b)
        {
            if (a.Recipe == null) return b.Recipe != null ? -1 : 0;
            if (b.Recipe == null) return 1;
            int c = First(a.Recipe.Craft.CompareTo(b.Recipe.Craft), a.RecipeMakesSpellItem.CompareTo(b.RecipeMakesSpellItem));
            return c != 0 ? c : FocusName(a, b);
        }

        static int Damage(ItemDef d) => d.Damage?.Amount ?? 0;

        /// <summary>gCItem_PS::GetModValue("PROT_BLADE"): the AddToVal modifiers on the tag.</summary>
        static int Blade(ItemDef d) => d.Modifiers.Where(m => m.Attribute == "PROT_BLADE" && m.Op == ModifierOp.AddToVal).Sum(m => m.Amount);
    }

    /// <summary>Item lists and item descriptions for the inventory and trade screens.</summary>
    public static class ItemLists
    {
        static readonly TextId GoldValueText = new TextId("HUD_GoldValue"), ReqSkillText = new TextId("HUD_Req_Skill"),
                               BonusSkillText = new TextId("HUD_Bonus_Skill"), ReqManaText = new TextId("HUD_Req_MP");

        /// <summary>Quality rows of the item panel in G3 order (FUN_100a3df0): bit, label, value.</summary>
        static readonly (int bit, TextId label, string value)[] QualityRows =
        {
            (ItemQualityBits.Forged, new TextId("HUD_Quality_Selfmade"), "(+10)"),
            (ItemQualityBits.Sharpened, new TextId("HUD_Quality_Sharp"), "(+10)"),
            (ItemQualityBits.Blessed, new TextId("HUD_Quality_Blessed"), "(+10)"),
            (ItemQualityBits.Poisoned, new TextId("HUD_Quality_Poison"), string.Empty),
            (ItemQualityBits.Worn, new TextId("HUD_Quality_Worn"), string.Empty),
        };

        /// <summary>
        /// Page order (G3 category order) and titles (HL_INV_* strings); skill items are not listed (skills show on the
        /// character screen), nor are spell books (G3 inventory tabs filter categories 1, 2, 3, 4, 6; spells are on the
        /// spell book page, <see cref="SpellBook"/>).
        /// </summary>
        static readonly (ItemPage page, ItemCategory[] categories, TextId title)[] Pages =
        {
            (ItemPage.Weapons, new[] { ItemCategory.Weapon }, new TextId("HL_INV_Weapons")),
            (ItemPage.Armor, new[] { ItemCategory.Armor }, new TextId("HL_INV_Armor")),
            (ItemPage.Artefacts, new[] { ItemCategory.Artefact }, new TextId("HL_INV_Artefacts")),
            (ItemPage.Alchemy, new[] { ItemCategory.Alchemy }, new TextId("HL_INV_Alchemy")),
            (ItemPage.Misc, new[] { ItemCategory.Misc }, new TextId("HL_INV_Misc")),
            (ItemPage.Books, new[] { ItemCategory.Written }, new TextId("HL_Books")),
        };

        /// <summary>A page's title (HL_INV_* string), also when the page is empty.</summary>
        public static string PageTitle(GameSession session, ItemPage page) =>
            session.Localizer.Text(Pages.First(p => p.page == page).title);

        /// <summary>
        /// The actor's items grouped by page; <paramref name="price"/> gives each row's gold (default: the value);
        /// <paramref name="sort"/> orders the rows inside each group (the groups keep G3's order).
        /// </summary>
        public static IReadOnlyList<ItemGroup> Of(GameSession session, ActorId owner, Func<ItemDef, int> price = null, bool includeGold = false,
                                                  ItemSortMode sort = ItemSortMode.G3) =>
            Build(session, session.Items.Inventories.Of(owner).Stacks.Select(s => new Entry(s.Item, s.Amount, s.Quality, s.Acquired)),
                  owner == ActorId.Player, price, includeGold, sort);

        /// <summary>
        /// Some stacks (a body's loot) grouped by page like an inventory; <paramref name="hero"/> marks worn items. Later
        /// stacks count as newer.
        /// </summary>
        public static IReadOnlyList<ItemGroup> Of(GameSession session, IEnumerable<KeyValuePair<string, int>> stacks, bool hero,
                                                  Func<ItemDef, int> price = null, bool includeGold = false, ItemSortMode sort = ItemSortMode.G3) =>
            Build(session, stacks.Select((s, i) => new Entry(s.Key, s.Value, 0, i + 1)), hero, price, includeGold, sort);

        readonly struct Entry
        {
            public Entry(string item, int amount, int quality, int acquired)
            {
                Item = item;
                Amount = amount;
                Quality = quality;
                Acquired = acquired;
            }

            public readonly string Item;
            public readonly int Amount, Quality, Acquired;
        }

        static IReadOnlyList<ItemGroup> Build(GameSession session, IEnumerable<Entry> stacks, bool hero, Func<ItemDef, int> price,
                                              bool includeGold, ItemSortMode sort)
        {
            var db = session.Content.Database;
            var equipment = session.Items.Equipment;
            var hands = session.Items.Hands;
            string gold = session.Items.GoldItem;
            var lookup = new Lookup(db);
            var rows = stacks
                .Where(s => includeGold || s.Item != gold)
                .GroupBy(s => s.Item)
                .Select(g => (def: db.TryGet(g.Key, out ItemDef d) ? d : null, amount: g.Sum(s => s.Amount),
                              quality: g.Aggregate(0, (q, s) => q | s.Quality), acquired: g.Max(s => s.Acquired)))
                .Where(x => x.def != null && x.def.Category != ItemCategory.Skill)
                .ToList();
            var groups = new List<ItemGroup>();
            foreach (var (pageId, categories, title) in Pages)
            {
                var page = rows.Where(r => categories.Contains(r.def.Category))
                    .Select(r =>
                    {
                        bool worn = hero && equipment.IsWorn(r.def.Id);
                        int gp = price?.Invoke(r.def) ?? r.def.GoldValue;
                        var row = new ItemRow(r.def.Id, Name(session, r.def), r.def.Icon, r.amount, worn, gp, r.def.Category,
                                              r.def.Quality | r.quality, hero && hands.Holds(r.def.Id), r.acquired);
                        return (row, key: lookup.Key(session, r.def, row.Name, worn, row.Quality, r.acquired, gp));
                    })
                    .ToList();
                page.Sort((x, y) => ItemSorting.Compare(sort, x.key, y.key));
                var sorted = page.Select(x => x.row).ToList();
                if (sorted.Count > 0) groups.Add(new ItemGroup(session.Localizer.Text(title), sorted, pageId));
            }
            return groups;
        }

        /// <summary>The spells and recipes behind items, built once per list.</summary>
        sealed class Lookup
        {
            readonly IContentDatabase _db;
            Dictionary<string, SpellDef> _spells;

            public Lookup(IContentDatabase db) => _db = db;

            public SpellDef SpellOf(string item)
            {
                if (_spells == null)
                {
                    _spells = new Dictionary<string, SpellDef>(StringComparer.Ordinal);
                    foreach (var s in _db.All<SpellDef>().OrderBy(s => s.Id, StringComparer.Ordinal))
                        foreach (var i in s.Items.Concat(s.Scrolls))
                            if (!_spells.ContainsKey(i)) _spells[i] = s;
                }
                return item != null && _spells.TryGetValue(item, out var spell) ? spell : null;
            }

            public ItemSortKey Key(GameSession session, ItemDef def, string name, bool worn, int quality, int acquired, int price)
            {
                var recipe = def.Category == ItemCategory.Written && _db.TryGet(def.Id, out RecipeDef r) ? r : null;
                bool byName = def.Category != ItemCategory.Weapon && def.Category != ItemCategory.Armor;
                string description = byName && !def.Description.IsEmpty ? session.Localizer.Text(def.Description) : string.Empty;
                return new ItemSortKey(def, name, description, SpellOf(def.Id), recipe, recipe != null && SpellOf(recipe.Result) != null,
                                       worn, quality, acquired, price);
            }
        }

        /// <summary>
        /// One carried item of the hero as a row (the quick bar's spell books, which no inventory page lists); null when the
        /// item is unknown or not carried.
        /// </summary>
        public static ItemRow HeroRow(GameSession session, string item)
        {
            if (item == null || !session.Content.Database.TryGet(item, out ItemDef def)) return null;
            var inventory = session.Items.Inventories.Of(ActorId.Player);
            int amount = inventory.Count(item);
            if (amount <= 0) return null;
            return new ItemRow(def.Id, Name(session, def), def.Icon, amount, session.Items.Equipment.IsWorn(def.Id), def.GoldValue,
                               def.Category, def.Quality | inventory.QualityOf(item), session.Items.Hands.Holds(def.Id));
        }

        public static int Gold(GameSession session, ActorId owner) => session.Items.Inventories.Of(owner).Count(session.Items.GoldItem);

        public static string Name(GameSession session, ItemDef def) => def.Name.IsEmpty ? def.Id : session.Localizer.Text(def.Name);

        /// <summary>Name, description and the item's numbers (damage, requirements, modifiers, value) as labelled lines.</summary>
        public static (string name, string description, IReadOnlyList<LabelledValue> lines) Describe(GameSession session, string item)
        {
            var detail = Detail(session, item);
            return (detail.Name, detail.Description, detail.Lines.Select(l => new LabelledValue(l.Label, l.Value)).ToList());
        }

        /// <summary>The item panel of a listed row: its stack quality and its price in that list (trade prices in trade).</summary>
        public static ItemDetail Detail(GameSession session, ItemRow row) =>
            row == null ? null : Detail(session, row.Item, row.Quality, row.Price);

        /// <summary>
        /// The item panel's content in G3 order (gCHUDTableStackStats, FUN_100a3690; notes gui_inventory_trade §1.2):
        /// <list type="number">
        /// <item>Requirements: attributes from the last template slot to the first (HUD_Req_&lt;TAG&gt;; a forged item needs 10
        /// less, never below 0), then the required skills, last first (HUD_Req_Skill, the skill's item name); blue when the
        /// hero meets them, red otherwise.</item>
        /// <item>Damage (HUD_Damage_&lt;type&gt;): halved when worn out, +10 each for sharpened, blessed, forged (OnPlayerGetDamage).</item>
        /// <item>Qualities with their icons (not for bodies): forged, sharpened, blessed "(+10)", poisoned, worn out.</item>
        /// <item>Protections (ATTRIB_PROT_*), last slot first, "%+d", halved when worn out.</item>
        /// <item>Other modifiers, last slot first: ATTRIB_&lt;TAG&gt; (+"Max" for AddToMax/AddPercentageToMax), "%+d" (+"%" for the
        /// percentage ops), halved when worn out; then the bonus skills, last first (HUD_Bonus_Skill, the skill's item name).</item>
        /// <item>Mana cost of an artefact's spell (HUD_Req_MP: "min" or "min - max").</item>
        /// <item>Gold value (HUD_GoldValue): <paramref name="price"/> when given (the list's price), else the item's value.</item>
        /// </list>
        /// Modifiers with value 0 are skipped. <paramref name="quality"/> &lt; 0 = the template's own quality.
        /// </summary>
        public static ItemDetail Detail(GameSession session, string item, int quality = -1, int? price = null)
        {
            var db = session.Content.Database;
            if (!db.TryGet(item, out ItemDef def)) return new ItemDetail(item, string.Empty, null, ItemCategory.Misc, Array.Empty<ItemLine>());
            var text = session.Localizer;
            int q = quality < 0 ? def.Quality : quality | def.Quality;
            bool worn = (q & ItemQualityBits.Worn) != 0;
            var lines = new List<ItemLine>();

            // OnPlayerGetEquipBonus 1006b840: forged −10, the same delta HeroEquipment.Check uses (items rules)
            var rules = session.Items.Rules;
            int equipBonus = rules.ForgedQuality != 0 && (q & rules.ForgedQuality) != 0 ? rules.ForgedRequirementDelta : 0;
            foreach (var r in def.Requirements.Reverse())
            {
                int need = Math.Max(0, r.Minimum + equipBonus);
                bool met = session.Hero.Stats.Value(r.Attribute) >= need;
                lines.Add(new ItemLine(met ? ItemLineKind.RequirementMet : ItemLineKind.RequirementUnmet,
                                       RequirementName(session, r.Attribute), need.ToString(CultureInfo.InvariantCulture)));
            }
            foreach (var skill in def.RequiredSkills.Reverse())
                lines.Add(new ItemLine(session.Hero.HasLearned(skill) ? ItemLineKind.RequirementMet : ItemLineKind.RequirementUnmet,
                                       text.Text(ReqSkillText), SkillName(session, skill)));

            if (def.Damage != null)
                lines.Add(new ItemLine(ItemLineKind.Damage, def.Damage.Name.IsEmpty ? def.Damage.Type : text.Text(def.Damage.Name),
                                       ShownDamage(def.Damage.Amount, q).ToString(CultureInfo.InvariantCulture)));

            if (def.Kind != "Body")
                foreach (var (bit, label, value) in QualityRows)
                    if ((q & bit) != 0) lines.Add(new ItemLine(ItemLineKind.Quality, text.Text(label), value, bit));

            var modifiers = def.Modifiers.Reverse().Where(m => m.Amount != 0).ToList();
            foreach (var m in modifiers.Where(IsProtection))
                lines.Add(new ItemLine(ItemLineKind.Protection, AttributeName(session, m.Attribute), Signed(worn ? m.Amount / 2 : m.Amount)));
            foreach (var m in modifiers.Where(m => !IsProtection(m)))
                lines.Add(new ItemLine(ItemLineKind.Modifier, ModifierName(session, m), FormatModifier(m, worn)));
            foreach (var skill in def.SkillBonuses.Reverse())
                lines.Add(new ItemLine(ItemLineKind.Modifier, text.Text(BonusSkillText), SkillName(session, skill)));

            if (def.Category == ItemCategory.Artefact)
            {
                var spell = new Lookup(db).SpellOf(def.Id);
                if (spell != null && (spell.MinMana > 0 || spell.MaxMana > 0))
                    lines.Add(new ItemLine(ItemLineKind.ManaCost, text.Text(ReqManaText), ManaCost(spell.MinMana, spell.MaxMana)));
            }

            lines.Add(new ItemLine(ItemLineKind.Gold, text.Text(GoldValueText), (price ?? def.GoldValue).ToString(CultureInfo.InvariantCulture)));
            return new ItemDetail(Name(session, def), def.Description.IsEmpty ? string.Empty : ShownDescription(text.Text(def.Description)),
                                  def.Icon, def.Category, lines);
        }

        /// <summary>
        /// The description as the panels show it: the G3 string table holds a lone "-" for items without one (ITEMDESC_*:
        /// 260 of 912 in Russian, e.g. It_Scavenger_Egg; also skills and spells). G3 prints it verbatim (gCHUDViewItem2
        /// 1007ee85 → loc string → SetWindowText, no filter); the port leaves the line empty instead [owner, fix-85].
        /// Sorting still uses the raw text (G3 Compare).
        /// </summary>
        public static string ShownDescription(string text) =>
            string.IsNullOrEmpty(text) || text.Trim() == "-" ? string.Empty : text;

        /// <summary>OnPlayerGetDamage (1006ae30): halved when worn out, +10 each for sharpened, blessed and forged.</summary>
        public static int ShownDamage(int amount, int quality)
        {
            int d = (quality & ItemQualityBits.Worn) != 0 ? amount / 2 : amount;
            foreach (int bit in new[] { ItemQualityBits.Sharpened, ItemQualityBits.Blessed, ItemQualityBits.Forged })
                if ((quality & bit) != 0) d += 10;
            return d;
        }

        /// <summary>FUN_100a4130: "min" when min ≥ max, else "min - max".</summary>
        public static string ManaCost(int min, int max) =>
            min >= max
                ? min.ToString(CultureInfo.InvariantCulture)
                : min.ToString(CultureInfo.InvariantCulture) + " - " + max.ToString(CultureInfo.InvariantCulture);

        static bool IsProtection(AttributeGain m) => m.Attribute.StartsWith("PROT_", StringComparison.Ordinal);

        static string RequirementName(GameSession session, string attribute) =>
            session.Content.Database.TryGet(attribute, out AttributeDef a) && !a.RequirementName.IsEmpty
                ? session.Localizer.Text(a.RequirementName)
                : AttributeName(session, attribute);

        static string AttributeName(GameSession session, string attribute) =>
            session.Content.Database.TryGet(attribute, out AttributeDef a) ? session.Localizer.Text(a.Name) : attribute;

        /// <summary>ATTRIB_&lt;TAG&gt;, or ATTRIB_&lt;TAG&gt;Max for AddToMax and AddPercentageToMax (FUN_100a45e0).</summary>
        static string ModifierName(GameSession session, AttributeGain m)
        {
            bool max = m.Op == ModifierOp.AddToMax || m.Op == ModifierOp.AddPercentageToMax;
            if (!max) return AttributeName(session, m.Attribute);
            if (session.Content.Database.TryGet(m.Attribute, out AttributeDef a) && !a.MaximumName.IsEmpty) return session.Localizer.Text(a.MaximumName);
            return session.Localizer.TryText(new TextId("ATTRIB_" + m.Attribute + "Max"), out string t) ? t : AttributeName(session, m.Attribute);
        }

        /// <summary>"%+d", plus "%" for AddPercentageToVal and AddPercentageToMax; worn-out items show half.</summary>
        public static string FormatModifier(AttributeGain m, bool worn = false)
        {
            string value = Signed(worn ? m.Amount / 2 : m.Amount);
            return m.Op == ModifierOp.AddPercentageToVal || m.Op == ModifierOp.AddPercentageToMax ? value + "%" : value;
        }

        static string Signed(int v) => (v >= 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture);

        /// <summary>A skill as G3 names it in the panel: the name of its item (FO_It_&lt;skill&gt;).</summary>
        static string SkillName(GameSession session, string skill)
        {
            var db = session.Content.Database;
            if (db.TryGet(skill, out SkillDef s) && s.Item != null && db.TryGet(s.Item, out ItemDef item) && !item.Name.IsEmpty)
                return session.Localizer.Text(item.Name);
            return session.Localizer.TryText(new TextId("FO_It_" + skill), out string t) ? t : skill;
        }
    }
}
