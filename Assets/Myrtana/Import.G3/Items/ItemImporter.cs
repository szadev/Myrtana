using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Genome;
using G3.Formats.Items;
using G3.Formats.Templates;
using Myrtana.Core;
using Myrtana.Rules;
using G3Category = G3.Formats.Items.ItemCategory;
using ItemCategory = Myrtana.Rules.ItemCategory;
using EquipSlot = Myrtana.Rules.EquipSlot;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// G3 item templates (Templates volume, formats-items.md §2) → <see cref="ItemDef"/>s of the 724 categorised items,
    /// the learnable skills (perk and spell templates) → <see cref="SkillDef"/>s, and the treasure sets →
    /// <see cref="TreasureSetDef"/>s. Every G3 enum and naming rule (FO_/ITEMDESC_ strings, use types, slots,
    /// modifier tags) is translated here.
    /// </summary>
    public sealed class ItemImporter
    {
        readonly TemplateArchive _templates;
        readonly ItemCatalog _catalog;
        readonly StringsImporter _strings;
        readonly ImportReport _report;

        public ItemImporter(G3Install install, StringsImporter strings, ImportReport report)
        {
            using (var fs = install.Open("Templates")) _templates = TemplateArchive.Load(fs);
            _catalog = ItemCatalog.Build(_templates);
            _strings = strings;
            _report = report;
        }

        /// <summary>Item template name by GUID (either header), or null.</summary>
        public string NameOf(Guid template) => template == Guid.Empty ? null : _catalog.Get(template)?.Name ?? _templates.Get(template)?.Name;

        public bool IsItem(string name) => Inventoriable(_catalog.Get(name));

        public List<ItemDef> Items() =>
            _catalog.Items.Where(Inventoriable).OrderBy(i => i.Name, StringComparer.Ordinal).Select(Translate).ToList();

        public List<SkillDef> Skills()
        {
            var skills = new List<SkillDef>();
            foreach (var t in _templates.Files.Where(f => !f.IsDeleted && f.Main != null))
            {
                var ps = t.Main.PropertySets.FirstOrDefault(p => p.ClassName == "gCSkill_PS" || p.ClassName == "gCMagic_PS");
                if (ps == null) continue;
                var g = t.Main.Source;
                string item = ps.ClassName == "gCSkill_PS" ? PerkItem(t.Name) : SpellItem(t.Guid, t.Main.Guid);
                if (item == null) _report.Count("skill without an inventory item (no name/icon)", t.Name);
                var requirements = Requirements(g, ps, "ReqAttrib", 4);
                var group = ps.ClassName == "gCSkill_PS" ? "Skill" + Props.Enum(ps, "Category") : "Magic";
                skills.Add(new SkillDef(t.Name, item, Props.Int(ps, "LearnPoints"), requirements, NameOf(Props.Proxy(ps, "ReqSkill1")), group));
            }
            return skills.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Map items (<c>gCMap_PS</c>, Docs/map-teleport.md §1.1): bitmap and the world corners, G3 cm → metres with no axis
        /// flip, and the marker list (gCMap_PS post data, <see cref="MapMarkerList"/>). Deleted templates and
        /// maps that are not inventory items are skipped.
        /// </summary>
        public List<MapDef> Maps()
        {
            const float centimetresToMetres = 0.01f;
            var maps = new List<MapDef>();
            foreach (var t in _templates.Files.Where(f => !f.IsDeleted && f.Main != null && IsItem(f.Name)))
            {
                var ps = t.Main.PropertySets.FirstOrDefault(p => p.ClassName == "gCMap_PS");
                if (ps == null) continue;
                var topLeft = Props.Vec3(ps, "WorldTopLeft");
                var bottomRight = Props.Vec3(ps, "WorldBottomRight");
                if (topLeft == null || bottomRight == null)
                {
                    _report.Count("map without world corners", t.Name);
                    continue;
                }
                var bitmap = Props.Str(t.Main.Source, ps, "Bitmap");
                int psIndex = t.Main.PropertySets.IndexOf(ps);
                var markers = psIndex < t.Main.PostDataStart.Count
                    ? MapMarkerList.Read(t.Main.Source, t.Main.PostDataStart[psIndex], ps.ClassVersion)
                    : new List<MapMarker>();
                maps.Add(new MapDef(t.Name, string.IsNullOrEmpty(bitmap) ? null : Path.GetFileNameWithoutExtension(bitmap),
                                    topLeft[0] * centimetresToMetres, topLeft[2] * centimetresToMetres,
                                    bottomRight[0] * centimetresToMetres, bottomRight[2] * centimetresToMetres,
                                    markers.Select(m => new MapMarkerDef(new TextId(m.Label ?? string.Empty), m.X * centimetresToMetres,
                                                                         m.Z * centimetresToMetres, m.Visible)).ToList()));
            }
            return maps.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Recipes (<c>gCRecipe_PS</c> on inventory items, Docs/interactive-objects.md §6): G3 gESkillCategory Craft 3/5/7 →
        /// smithing/alchemy/cooking, ingredient and result templates → item ids, ReqSkill1 → the perk id.
        /// </summary>
        public List<RecipeDef> Recipes()
        {
            var recipes = new List<RecipeDef>();
            foreach (var info in _catalog.Items.Where(i => i.Recipe != null && Inventoriable(i)))
            {
                var r = info.Recipe;
                CraftKind craft;
                switch (r.Craft)
                {
                    case 3: craft = CraftKind.Smithing; break;
                    case 5: craft = CraftKind.Alchemy; break;
                    case 7: craft = CraftKind.Cooking; break;
                    default:
                        _report.Count("recipe with an unknown craft (skipped)", $"{info.Name} craft {r.Craft}");
                        continue;
                }
                string result = InventoryItem(r.Result);
                if (result == null)
                {
                    _report.Count("recipe without a result item (skipped)", info.Name);
                    continue;
                }
                var ingredients = new List<RecipeIngredient>();
                foreach (var pair in r.Ingredients)
                {
                    string item = InventoryItem(pair.Key);
                    if (item == null) _report.Count("recipe ingredient not an item (dropped)", info.Name);
                    else ingredients.Add(new RecipeIngredient(item, pair.Value));
                }
                string skill = r.ReqSkill == Guid.Empty ? null : NameOf(r.ReqSkill);
                recipes.Add(new RecipeDef(info.Name, craft, ingredients, result, r.ResultAmount, r.ResultQuality, skill));
            }
            return recipes.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Letter and book texts of inventory items (<c>gCLetter_PS</c> Header/Text, <c>gCBook_PS</c> Header/LeftText/RightText;
        /// string-table keys). A template with a filled book set is a book; maps are left to <see cref="Maps"/>.
        /// </summary>
        public List<DocumentDef> Documents()
        {
            var documents = new List<DocumentDef>();
            foreach (var t in _templates.Files.Where(f => !f.IsDeleted && f.Main != null && IsItem(f.Name)))
            {
                var sets = t.Main.PropertySets;
                if (sets.Any(p => p.ClassName == "gCMap_PS")) continue;
                var g = t.Main.Source;
                var book = sets.FirstOrDefault(p => p.ClassName == "gCBook_PS");
                var letter = sets.FirstOrDefault(p => p.ClassName == "gCLetter_PS");
                TextId Key(PropertyObject ps, string name) => _strings.Resolve(Props.Str(g, ps, name));
                if (book != null && (!Key(book, "Header").IsEmpty || !Key(book, "LeftText").IsEmpty || !Key(book, "RightText").IsEmpty))
                    documents.Add(new DocumentDef(t.Name, DocumentKind.Book, Key(book, "Header"), Key(book, "LeftText"), Key(book, "RightText")));
                else if (letter != null && (!Key(letter, "Header").IsEmpty || !Key(letter, "Text").IsEmpty))
                    documents.Add(new DocumentDef(t.Name, DocumentKind.Letter, Key(letter, "Header"), Key(letter, "Text"), default));
                else if (book != null || letter != null) _report.Count("document without text", t.Name);
            }
            return documents.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        }

        string InventoryItem(Guid template)
        {
            string name = NameOf(template);
            return name != null && IsItem(name) ? name : null;
        }

        /// <summary>The spells and the items that cast them (<see cref="SpellImporter"/>).</summary>
        public List<SpellDef> Spells() => SpellImporter.Import(_templates, _catalog, Inventoriable, _report);

        public List<TreasureSetDef> TreasureSets()
        {
            var sets = new List<TreasureSetDef>();
            foreach (var t in _templates.Files.Where(f => !f.IsDeleted))
            {
                var set = TreasureSet.From(t);
                if (set == null) continue;
                var stacks = new List<TreasureStack>();
                foreach (var s in set.Inventory?.Stacks ?? new List<InventoryStack>())
                {
                    var item = _catalog.Get(s.Template);
                    if (!Inventoriable(item))
                    {
                        _report.Count("treasure-set stack of an unknown item", $"{set.Name}: {s.Template}");
                        continue;
                    }
                    stacks.Add(new TreasureStack(item.Name, Math.Max(1, s.Amount), s.Quality | item.Quality));
                }
                sets.Add(new TreasureSetDef(set.Name, (TreasureUse)set.Distribution, set.MinStacks, set.MaxStacks, stacks));
            }
            return sets.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Items without a category that scripts still put into the hero's hand: PS_Magic_FlameSword creates
        /// It_1H_MagicFlameSword_01 (category 0, no requirements, quality 8 Burning) with AssureItems. They import as weapons.
        /// </summary>
        static readonly HashSet<string> Conjured = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SpellEffectRules.FlameSwordItem };

        static bool Inventoriable(ItemInfo i) =>
            i != null && (i.Category != G3Category.None && i.Category != G3Category.Empty_A && i.Category != G3Category.Empty_B || Conjured.Contains(i.Name));

        ItemDef Translate(ItemInfo i)
        {
            var name = _strings.Resolve(i.NameKey);
            if (name.IsEmpty) _report.Count("item without a display name (FO_)", i.Name);
            var slot = SlotOf(i.UseType);
            bool usable = slot == EquipSlot.None && UsableKinds.Contains(i.UseType);
            var modifiers = i.Modifiers.Select(m => new AttributeGain(m.Tag, (ModifierOp)m.Op, m.Value)).ToList();
            var granted = new[] { i.ActivateSkill1, i.ActivateSkill2 }.Select(NameOf).Where(n => n != null).ToList();
            if (i.Category == G3Category.Skill && i.Permanent) usable = false; // perk items are learned, not used
            return new ItemDef(
                id: i.Name,
                name: name,
                description: _strings.Resolve(i.DescriptionKey),
                category: i.Category == G3Category.None && Conjured.Contains(i.Name) ? ItemCategory.Weapon : CategoryOf(i.Category),
                kind: i.UseType.ToString(),
                slot: slot,
                goldValue: i.GoldValue,
                quality: i.Quality,
                icon: string.IsNullOrEmpty(i.Icon) ? null : Path.GetFileNameWithoutExtension(i.Icon),
                visual: VisualOf(i),
                requirements: i.Requirements.Where(r => r.Op == 0 && r.Value > 0).Select(r => new AttributeRequirement(r.Tag, r.Value)).ToList(),
                requiredSkills: new[] { i.RequiredSkill1, i.RequiredSkill2 }.Select(NameOf).Where(n => n != null).ToList(),
                equipModifiers: slot != EquipSlot.None ? modifiers : null,
                useEffects: usable ? modifiers : null,
                grantedSkills: usable ? granted : null,
                damage: i.DamageAmount != 0 ? new ItemDamage(i.DamageType.ToString(), i.DamageAmount, _strings.Resolve("HUD_Damage_" + i.DamageType)) : null,
                flags: (i.MissionItem ? ItemFlags.Mission : 0) | (i.Robe ? ItemFlags.Robe : 0) | (i.FullBody ? ItemFlags.FullBody : 0),
                takenAs: NameOf(i.ItemInventory),
                material: slot != EquipSlot.None ? MaterialOf(i) : null,
                modifiers: modifiers,
                skillBonuses: granted,
                projectile: ProjectileOf(i));
        }

        /// <summary>The arrow's or bolt's touch behaviour (gCProjectile_PS, fix-176); null for items without one.</summary>
        static ItemProjectile ProjectileOf(ItemInfo i) =>
            i.ProjectileTouch < 0 || (i.UseType != UseType.Arrow && i.UseType != UseType.Bolt) ? null
                : new ItemProjectile(((ProjectileTouch)i.ProjectileTouch).ToString(), i.ProjectileLinkToBones);

        /// <summary>The collision material of the item's template for impact sounds ("Metal", "Wood" …); null when it has none.</summary>
        string MaterialOf(ItemInfo i)
        {
            int material = TemplateShapes.FirstMaterial(_templates.Get(i.Guid));
            return material > 0 ? TemplateShapes.MaterialName(material) : null;
        }

        /// <summary>Food, drinks, herbs and potions are consumed on use (items.json "use").</summary>
        static readonly HashSet<UseType> UsableKinds = new HashSet<UseType>
        {
            UseType.Potion, UseType.Plant, UseType.Meat, UseType.Fruit, UseType.Bread, UseType.Bowl, UseType.Bottle, UseType.Cup,
            UseType.Smoke, UseType.Letter,
        };

        static EquipSlot SlotOf(UseType use)
        {
            switch (use)
            {
                case UseType.OneHanded:
                case UseType.TwoHanded:
                case UseType.Axe:
                case UseType.Halberd:
                case UseType.Staff: return EquipSlot.MeleeWeapon;
                case UseType.Bow:
                case UseType.CrossBow: return EquipSlot.RangedWeapon;
                case UseType.Arrow:
                case UseType.Bolt: return EquipSlot.Ammo;
                case UseType.Shield: return EquipSlot.Shield;
                case UseType.Body: return EquipSlot.Armor;
                case UseType.Helmet: return EquipSlot.Helmet;
                case UseType.Amulet: return EquipSlot.Amulet;
                case UseType.Ring: return EquipSlot.Ring;
                default: return EquipSlot.None;
            }
        }

        static ItemCategory CategoryOf(G3Category c)
        {
            switch (c)
            {
                case G3Category.Weapon: return ItemCategory.Weapon;
                case G3Category.Armor: return ItemCategory.Armor;
                case G3Category.Artefact: return ItemCategory.Artefact;
                case G3Category.Alchemy: return ItemCategory.Alchemy;
                case G3Category.Written: return ItemCategory.Written;
                case G3Category.Spellbook: return ItemCategory.Spellbook;
                case G3Category.Skill: return ItemCategory.Skill;
                default: return ItemCategory.Misc;
            }
        }

        static ItemVisual VisualOf(ItemInfo i)
        {
            string body = string.IsNullOrEmpty(i.Actor) ? null : Path.GetFileNameWithoutExtension(i.Actor);
            if (string.IsNullOrEmpty(i.Mesh) && body == null) return null;
            return new ItemVisual(string.IsNullOrEmpty(i.Mesh) ? null : i.Mesh, body, i.MaterialSwitch);
        }

        /// <summary>A perk template "Perk_X" is shown through its skill item "It_Perk_X".</summary>
        string PerkItem(string perk) => Inventoriable(_catalog.Get("It_" + perk)) ? "It_" + perk : null;

        /// <summary>A spell template is shown through the spell-book or rune item that names it.</summary>
        string SpellItem(params Guid[] spell) =>
            _catalog.Items.Where(i => Inventoriable(i) && spell.Contains(i.Spell))
                          .OrderBy(i => i.Category == G3Category.Spellbook ? 0 : 1).Select(i => i.Name).FirstOrDefault();

        static List<AttributeRequirement> Requirements(GenomeReader g, PropertyObject ps, string prefix, int count)
        {
            var list = new List<AttributeRequirement>();
            for (int i = 1; i <= count; i++)
            {
                var tag = Props.Str(g, ps, $"{prefix}{i}Tag");
                int value = Props.Int(ps, $"{prefix}{i}Value");
                if (!string.IsNullOrEmpty(tag) && value > 0) list.Add(new AttributeRequirement(tag, value));
            }
            return list;
        }
    }
}
