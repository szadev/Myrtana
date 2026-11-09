using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Published after the hero used up an item.</summary>
    public readonly struct ItemUsed : IGameEvent
    {
        public ItemUsed(string item) => Item = item;
        public string Item { get; }
    }

    /// <summary>
    /// A use effect G3 announces (gCPlayerMemory_PS::Consume): "&lt;attribute&gt; = &lt;old&gt; + &lt;amount&gt;", the attribute named
    /// <c>ATTRIB_&lt;id&gt;Max</c> for the maximum of HP/MP/SP, <c>ATTRIB_&lt;id&gt;</c> otherwise.
    /// </summary>
    public readonly struct ItemEffectAnnounced : IGameEvent
    {
        public ItemEffectAnnounced(string attribute, bool maximum, int oldValue, int amount)
        {
            Attribute = attribute;
            Maximum = maximum;
            OldValue = oldValue;
            Amount = amount;
        }

        public string Attribute { get; }
        /// <summary>The maximum was raised (HP/MP/SP AddToMax).</summary>
        public bool Maximum { get; }
        public int OldValue { get; }
        public int Amount { get; }
    }

    /// <summary>An item taught the hero a skill he did not know (gCInventory_PS::ConsumeItems: "GO_Learned" + FO_It_&lt;skill&gt;).</summary>
    public readonly struct SkillLearnedFromItem : IGameEvent
    {
        public SkillLearnedFromItem(string item, string skill)
        {
            Item = item;
            Skill = skill;
        }

        public string Item { get; }
        public string Skill { get; }
    }

    /// <summary>
    /// Potions that cast a spell on the drinker (G3 OnPlayerUseStack, use type Potion with a Spell → PS_MagicPotion → the
    /// spell's instant effect on the hero): the antidote It_Potion_CurePoison (Spl_CurePoison) and It_Potion_CureDisease.
    /// </summary>
    public interface IDrunkSpells
    {
        /// <summary>The item is a potion whose spell takes effect when drunk.</summary>
        bool CastsWhenDrunk(string item);
        /// <summary>The potion's spell takes effect on <paramref name="drinker"/> (the potion itself is used up by <see cref="ItemUse"/>).</summary>
        void CastDrunk(ActorId drinker, string item);
    }

    /// <summary>
    /// Using food, potions and skill items (items.json "use"): every use effect is applied permanently with its
    /// operation, granted skills are learned, and one unit is used up. There are no timed effects. CP 1.75 announces the
    /// raise of a vital's maximum (AddToMax on HP/MP/SP) and of any other attribute's value (AddToVal)
    /// (<see cref="ItemEffectAnnounced"/>), and every newly learned skill (<see cref="SkillLearnedFromItem"/>); healing is
    /// not announced. The presentation plays the eat/drink motion first (<c>_AI_UseInventoryItem</c>) and calls
    /// <see cref="Use"/> when it is done.
    /// </summary>
    public sealed class ItemUse
    {
        /// <summary>The spell potions (fix-127: the antidote could not be drunk); null = none.</summary>
        public IDrunkSpells DrunkSpells { get; set; }

        readonly ProgressionService _progression;
        readonly Inventories _inventories;
        readonly IContentDatabase _content;
        readonly IEventBus _events;

        public ItemUse(ProgressionService progression, Inventories inventories, IContentDatabase content, IEventBus events)
        {
            _progression = progression;
            _inventories = inventories;
            _content = content;
            _events = events;
        }

        public bool CanUse(string item) =>
            _content.TryGet(item, out ItemDef def) && (def.IsUsable || DrunkSpells?.CastsWhenDrunk(item) == true) &&
            _inventories.Of(ActorId.Player).Has(item);

        public bool Use(string item)
        {
            if (!CanUse(item)) return false;
            var def = _content.TryGet(item, out ItemDef d) ? d : null;
            var stats = _progression.Hero.Stats;
            foreach (var effect in def.UseEffects)
            {
                bool vital = IsVital(effect.Attribute);
                if (Announces(effect.Attribute, effect.Op))
                    _events.Publish(new ItemEffectAnnounced(effect.Attribute, vital,
                                                            vital ? stats.Maximum(effect.Attribute) : stats.Value(effect.Attribute), effect.Amount));
                _progression.Apply(effect);
            }
            foreach (var skill in def.GrantedSkills)
            {
                bool known = _progression.Hero.HasLearned(skill);
                _progression.LearnSkill(skill);
                if (!known && _progression.Hero.HasLearned(skill)) _events.Publish(new SkillLearnedFromItem(item, skill));
            }
            if (DrunkSpells?.CastsWhenDrunk(item) == true) DrunkSpells.CastDrunk(ActorId.Player, item);
            _inventories.Consume(ActorId.Player, item, 1);
            _events.Publish(new ItemUsed(item));
            return true;
        }

        /// <summary>HP, MP and SP: Consume announces only their maximum.</summary>
        public static bool IsVital(string attribute) => attribute == "HP" || attribute == "MP" || attribute == "SP";

        /// <summary>gCPlayerMemory_PS::Consume prints AddToMax on a vital and AddToVal on any other attribute.</summary>
        public static bool Announces(string attribute, ModifierOp op) =>
            !string.IsNullOrEmpty(attribute) && (IsVital(attribute) ? op == ModifierOp.AddToMax : op == ModifierOp.AddToVal);
    }
}
