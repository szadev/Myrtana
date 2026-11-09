using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Content kinds of the rules module.</summary>
    public static class RulesSchema
    {
        public const string AttributeFolder = "attributes";
        public const string ProgressionFolder = "progression";
        public const string FameGroupFolder = "fame";
        public const string ItemFolder = "items";
        public const string ItemRulesFolder = "itemRules";
        public const string TreasureFolder = "treasure";
        public const string TradeRulesFolder = "tradeRules";
        public const string MerchantFolder = "merchants";
        public const string TeacherFolder = "teachers";
        public const string SkillFolder = "skills";
        public const string TeachingRulesFolder = "teachingRules";
        public const string WorldItemFolder = "worldItems";
        public const string CombatRulesFolder = "combatRules";
        public const string AttitudeRulesFolder = "attitudeRules";
        public const string CrimeRulesFolder = "crimeRules";
        public const string SpellFolder = "spells";
        public const string MagicRulesFolder = "magicRules";
        public const string AiZoneFolder = "aiZones";
        public const string EnclaveFolder = "enclaves";
        public const string InteractiveObjectFolder = "interactiveObjects";
        public const string LockRulesFolder = "lockRules";
        public const string BoulderRulesFolder = "boulderRules";
        public const string HeroStartFolder = "heroStart";
        public const string MapFolder = "maps";
        public const string TeleportFolder = "teleports";
        public const string PlaceFolder = "places";
        public const string PickpocketFolder = "pickpockets";
        public const string RecipeFolder = "recipes";
        public const string DocumentFolder = "documents";
        public const string CraftingRulesFolder = "craftingRules";
        public const string NpcInventoryFolder = "npcInventories";

        public static void Register(ContentSchema schema)
        {
            schema.AddKind<AttributeDef>(AttributeFolder);
            schema.AddKind<ProgressionRules>(ProgressionFolder);
            schema.AddKind<FameGroupDef>(FameGroupFolder);
            schema.AddKind<ItemDef>(ItemFolder);
            schema.AddKind<ItemRules>(ItemRulesFolder);
            schema.AddKind<TreasureSetDef>(TreasureFolder);
            schema.AddKind<TradeRules>(TradeRulesFolder);
            schema.AddKind<MerchantDef>(MerchantFolder);
            schema.AddKind<TeacherDef>(TeacherFolder);
            schema.AddKind<SkillDef>(SkillFolder);
            schema.AddKind<TeachingRules>(TeachingRulesFolder);
            schema.AddKind<WorldItemDef>(WorldItemFolder);
            schema.AddKind<CombatRules>(CombatRulesFolder);
            schema.AddKind<AttitudeRules>(AttitudeRulesFolder);
            schema.AddKind<CrimeRules>(CrimeRulesFolder);
            schema.AddKind<SpellDef>(SpellFolder);
            schema.AddKind<MagicRules>(MagicRulesFolder);
            schema.AddKind<AiZoneDef>(AiZoneFolder);
            schema.AddKind<EnclaveDef>(EnclaveFolder);
            schema.AddKind<InteractiveObjectDef>(InteractiveObjectFolder);
            schema.AddKind<LockRules>(LockRulesFolder);
            schema.AddKind<BoulderRules>(BoulderRulesFolder);
            schema.AddKind<HeroStartKit>(HeroStartFolder);
            schema.AddKind<MapDef>(MapFolder);
            schema.AddKind<TeleportDestinationDef>(TeleportFolder);
            schema.AddKind<WorldPlaceDef>(PlaceFolder);
            schema.AddKind<PickpocketDef>(PickpocketFolder);
            schema.AddKind<RecipeDef>(RecipeFolder);
            schema.AddKind<DocumentDef>(DocumentFolder);
            schema.AddKind<CraftingRules>(CraftingRulesFolder);
            schema.AddKind<NpcInventoryDef>(NpcInventoryFolder);
        }
    }
}
