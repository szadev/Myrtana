using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Everything item-related of the G3 pack in one place: item, skill and treasure-set definitions from the templates,
    /// world items, merchants and teachers from the world, and the item/trade/teaching rule numbers.
    /// </summary>
    public sealed class G3ItemContent
    {
        G3ItemContent() { }

        ItemImporter _importer;

        public List<ItemDef> Items { get; private set; }
        public List<SkillDef> Skills { get; private set; }
        public List<TreasureSetDef> TreasureSets { get; private set; }
        public List<SpellDef> Spells { get; private set; }
        public List<MapDef> Maps { get; private set; }
        public List<RecipeDef> Recipes { get; private set; }
        public List<DocumentDef> Documents { get; private set; }
        public WorldContentImporter World { get; private set; }
        public InteractiveObjectImporter Interactive { get; private set; }
        /// <summary>Canonical item and skill ids for the dialog importer.</summary>
        public ItemNames Names { get; private set; }

        /// <param name="switchableSectors">The story-switchable world sectors (NpcImporter.Sectors): their world items and
        /// interactive objects are imported too (also when off at the start) and carry the sector.</param>
        public static G3ItemContent Import(G3Install install, StringsImporter strings, IEnumerable<NpcSpawn> spawns, ImportReport report,
                                           IEnumerable<string> switchableSectors = null, IReadOnlyDictionary<ActorId, NpcSpawn> spawnOf = null,
                                           ISet<string> dialogOwners = null)
        {
            var sectors = new SwitchableSectors(switchableSectors);
            var importer = new ItemImporter(install, strings, report);
            var content = new G3ItemContent
            {
                _importer = importer,
                Items = importer.Items(),
                Skills = importer.Skills(),
                TreasureSets = importer.TreasureSets(),
                Spells = importer.Spells(),
                Maps = importer.Maps(),
                Recipes = importer.Recipes(),
                Documents = importer.Documents(),
            };
            content.Names = new ItemNames(content.Items.Select(i => i.Id), content.Skills.Select(s => s.Id));
            content.World = new WorldContentImporter(importer, content.TreasureSets, spawns, report, spawnOf) { Sectors = sectors };
            content.World.Import(install);
            content.Interactive = new InteractiveObjectImporter(importer, content.World.ActorOf, report) { Sectors = sectors, DialogOwners = dialogOwners };
            content.Interactive.Import(install);
            report.Note($"{content.Items.Count} items, {content.Skills.Count} skills, {content.TreasureSets.Count} treasure sets, " +
                        $"{content.Recipes.Count} recipes, {content.Documents.Count} documents, {content.World.WorldItems.Count} world items, {content.World.Merchants.Count} merchants, {content.World.Teachers.Count} teachers");
            return content;
        }

        /// <summary>The inventory item id of an item template (null when the template is not an inventory item).</summary>
        public string ItemOf(System.Guid template)
        {
            string name = _importer.NameOf(template);
            return name != null && _importer.IsItem(name) ? name : null;
        }

        public void Write(ContentPackWriter writer)
        {
            writer.WriteDefinitions(RulesSchema.ItemFolder, "items", Items);
            writer.WriteDefinitions(RulesSchema.SkillFolder, "skills", Skills);
            writer.WriteDefinitions(RulesSchema.TreasureFolder, "treasureSets", TreasureSets);
            writer.WriteDefinitions(RulesSchema.SpellFolder, "spells", Spells);
            writer.WriteDefinitions(RulesSchema.MapFolder, "maps", Maps);
            writer.WriteDefinitions(RulesSchema.RecipeFolder, "recipes", Recipes);
            writer.WriteDefinitions(RulesSchema.DocumentFolder, "documents", Documents);
            writer.WriteDefinitions(RulesSchema.CraftingRulesFolder, "craftingRules", new[] { G3CraftingImporter.Rules() });
            writer.WriteDefinitions(RulesSchema.MagicRulesFolder, "magicRules", new[] { G3MagicImporter.Rules() });
            writer.WriteDefinitions(RulesSchema.WorldItemFolder, "worldItems", World.WorldItems.OrderBy(w => w.Id, System.StringComparer.Ordinal));
            writer.WriteDefinitions(RulesSchema.MerchantFolder, "merchants", World.Merchants.OrderBy(m => m.Actor.Name, System.StringComparer.Ordinal));
            writer.WriteDefinitions(RulesSchema.TeacherFolder, "teachers", World.Teachers.OrderBy(t => t.Actor.Name, System.StringComparer.Ordinal));
            writer.WriteDefinitions(RulesSchema.InteractiveObjectFolder, "interactiveObjects", Interactive.Objects.OrderBy(o => o.Id, System.StringComparer.Ordinal));
            writer.WriteDefinitions(RulesSchema.LockRulesFolder, "lockRules", new[] { LockRules.Default });
            // CP 1.75 Script_Game OB_Light/Medium/Heavy/Faring_Boulder and PS_Interact_LiftOrcBoulder_Loop (fix-13)
            writer.WriteDefinitions(RulesSchema.BoulderRulesFolder, "boulderRules", new[] { BoulderRules.Default });
            writer.WriteDefinitions(RulesSchema.ItemRulesFolder, "itemRules", new[] { G3RulesImporter.ItemRules() });
            writer.WriteDefinitions(RulesSchema.TradeRulesFolder, "tradeRules", new[] { G3RulesImporter.TradeRules() });
            writer.WriteDefinitions(RulesSchema.TeachingRulesFolder, "teachingRules", new[] { G3RulesImporter.TeachingRules() });
            writer.WriteDefinitions(StorySchema.SharedLinesFolder, "sharedLines", new[] { new SharedLines(SharedLines.DefinitionId, "SVM_{voice}_{line}") });
        }
    }
}

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Which world sectors the item and object importers read (story-4): the enabled ones, plus the story-switchable
    /// sectors that are off at the start (G3 marks every entity of such a sector disabled; it enters the world when the
    /// story switches the sector on). Objects of a switchable sector carry its name.
    /// </summary>
    public sealed class SwitchableSectors
    {
        readonly HashSet<string> _names;

        public SwitchableSectors(IEnumerable<string> names) =>
            _names = new HashSet<string>(names ?? System.Array.Empty<string>(), System.StringComparer.OrdinalIgnoreCase);

        /// <summary>The sector's objects are imported.</summary>
        public bool Reads(global::G3.Formats.World.WorldLayout.Sector sector) =>
            !NpcSpawns.IsTestSector(sector.Name) && (sector.Enabled || _names.Contains(sector.Name));

        /// <summary>The name objects of this sector carry; null for an always-present sector.</summary>
        public string TagOf(global::G3.Formats.World.WorldLayout.Sector sector) => _names.Contains(sector.Name) ? sector.Name : null;

        /// <summary>An entity counts: enabled, or any entity of a sector that is off at the start.</summary>
        public static bool Counts(global::G3.Formats.World.WorldEntity e, global::G3.Formats.World.WorldLayout.Sector sector) =>
            e.Enabled || !sector.Enabled;
    }
}
