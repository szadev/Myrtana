using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// The hero's interactive world objects: locks, chests, doors, switches, sleeping in beds, orc boulders, ore veins,
    /// treasure spots, water barrels and shrines.
    /// </summary>
    public sealed class InteractionServices
    {
        internal InteractionServices(InteractiveObjects objects, HeroSleep sleep, OrcBoulders boulders, ObjectUses uses,
                                     LockedChestMarks chestMarks)
        {
            Objects = objects;
            ChestMarks = chestMarks;
            Sleep = sleep;
            Boulders = boulders;
            Uses = uses;
        }

        public InteractiveObjects Objects { get; }
        public HeroSleep Sleep { get; }
        public OrcBoulders Boulders { get; }
        /// <summary>Ore veins, treasure spots, water barrels and the shrine refusal (par-interact).</summary>
        public ObjectUses Uses { get; }
        /// <summary>Locked chests the hero found but could not open, for the map pages (fix-102, [owner]).</summary>
        public LockedChestMarks ChestMarks { get; }
    }

    /// <summary>
    /// G3 <c>PlayerSleep</c> (Script 0x10076440): the clock jumps to the next given hour and the hero's hit points, stamina
    /// and mana are refilled. A bed asks first: <c>PS_Interact_SleepBed</c> calls <c>AssessTalk(hero, bed)</c>, which opens
    /// the bed's own dialog when it has infos (every G3 bed template has: sleep until morning 8, noon 12, evening 20,
    /// midnight 0, plus the hero's general "End"); only a bed without infos sleeps straight to
    /// <see cref="LockRules.SleepUntilHour"/> (08:00).
    /// </summary>
    public sealed class HeroSleep
    {
        readonly GameClock _clock;
        readonly CombatService _combat;
        readonly LockRules _rules;
        readonly IEventBus _events;

        internal HeroSleep(GameClock clock, CombatService combat, LockRules rules, IEventBus events)
        {
            _clock = clock;
            _combat = combat;
            _rules = rules;
            _events = events;
        }

        public float UntilHour => _rules.SleepUntilHour;

        /// <summary>Game seconds the hero would sleep if he lay down now.</summary>
        public double Duration => _clock.SecondsUntilHour(_rules.SleepUntilHour);

        /// <summary>Sleeps until morning; returns the game seconds skipped.</summary>
        public double SleepUntilMorning() => SleepUntil(_rules.SleepUntilHour);

        /// <summary>Sleeps until the next time the clock shows <paramref name="hour"/> (also the story's Sleep command).</summary>
        public double SleepUntil(float hour)
        {
            double from = _clock.GameSeconds, skip = _clock.SecondsUntilHour(hour);
            _clock.Skip(skip);
            _combat.Vitals.RestoreFully(ActorId.Player);
            _events.Publish(new HeroSlept(from, _clock.GameSeconds));
            return skip;
        }
    }

    internal static class InteractionWiring
    {
        public static InteractionServices Create(IContentDatabase content, GameClock clock, Hero hero, ItemServices items,
                                                 CombatService combat, IRandom random, IEventBus events, IGameEventFlags flags,
                                                 ProgressionService progression)
        {
            var rules = content.TryGet(LockRules.DefinitionId, out LockRules r) ? r : LockRules.Default;
            var objects = new InteractiveObjects(content, rules, items.Inventories, hero.Knows, random, events, items.NpcLoot?.Plunder)
            {
                // Objects of story sectors follow SetSectorStatus like the world items (WorldStateWiring sets the predicate).
                SectorEnabled = items.World.SectorEnabled,
            };
            var boulderRules = content.TryGet(BoulderRules.DefinitionId, out BoulderRules b) ? b : BoulderRules.Default;
            // AddStrength: the base value (and the maximum where the attribute has one, as teaching raises it), printed
            // "<attribute> + 5 = value" (PSPlayerMemory::PrintAddValue)
            var boulders = new OrcBoulders(boulderRules, id => objects.TryGet(id, out var def) ? def : null, hero.Stats.Value, flags,
                                           g => progression.Raise(new AttributeGain(g.Attribute, hero.Stats.HasMaximum(g.Attribute) ? ModifierOp.AddToMax : ModifierOp.AddToVal, g.Amount)),
                                           events);
            var uses = new ObjectUses(ObjectUseRules.Default, content, objects, items.Inventories, hero.Knows, flags,
                                      hp => combat.Vitals.AddHitPoints(ActorId.Player, hp), events);
            return new InteractionServices(objects, new HeroSleep(clock, combat, rules, events), boulders, uses,
                                           new LockedChestMarks(objects, events));
        }
    }
}
