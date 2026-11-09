namespace Myrtana.Rules
{
    /// <summary>The item-related state and services of a game, bundled for the modules and views that use them.</summary>
    public sealed class ItemServices
    {
        public ItemServices(ItemRules rules, Inventories inventories, HeroEquipment equipment, ItemUse use, WorldItems world,
                            ActorRoles roles, TradeService trade, TeachingService teaching, NpcLoot npcLoot = null)
        {
            Rules = rules;
            Inventories = inventories;
            Equipment = equipment;
            Use = use;
            World = world;
            Roles = roles;
            Trade = trade;
            Teaching = teaching;
            NpcLoot = npcLoot;
            QuickSlots = new QuickSlots(inventories.Of(Core.ActorId.Player));
        }

        public ItemRules Rules { get; }
        public Inventories Inventories { get; }
        public HeroEquipment Equipment { get; }
        public ItemUse Use { get; }
        public WorldItems World { get; }
        public ActorRoles Roles { get; }
        public TradeService Trade { get; }
        public TeachingService Teaching { get; }
        /// <summary>What NPCs carry, their plunder and the loot of downed and dead bodies (null when not wired).</summary>
        public NpcLoot NpcLoot { get; }
        /// <summary>The hero's quick bar (keys 1…0).</summary>
        public QuickSlots QuickSlots { get; }
        /// <summary>What the hero holds without wearing it (torch, drawn spell); set by the hand views.</summary>
        public HeroHands Hands { get; } = new HeroHands();

        public string GoldItem => Rules.GoldItem;
    }
}
