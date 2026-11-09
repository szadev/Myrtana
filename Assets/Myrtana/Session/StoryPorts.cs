using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>The story's body facts from the actors' combat definitions (G3 IsHumanoid, CanAttack).</summary>
    public sealed class SessionActorTraits : IActorTraits
    {
        readonly ActorRegistry _actors;
        readonly NpcCombatSystem _combat;

        public SessionActorTraits(ActorRegistry actors, NpcCombatSystem combat)
        {
            _actors = actors ?? throw new ArgumentNullException(nameof(actors));
            _combat = combat;
        }

        public bool IsHumanoid(ActorId actor) =>
            actor.IsPlayer || _actors.TryGet(actor, out var state) && CombatWiring.CombatDefOf(state).Humanoid;

        public bool CanFight(ActorId actor)
        {
            if (actor.IsPlayer) return true;
            if (!_actors.TryGet(actor, out var state)) return false;
            return _combat == null || !_combat.Rules.NeverFights(CombatWiring.CombatDefOf(state));
        }
    }

    /// <summary>
    /// Pickpocketing in dialogs (Rules/Crime/Pickpocket.cs): who carries pickpocket loot (content <see cref="PickpocketDef"/>),
    /// who has been robbed (saved as "crime.pickpocket"), and one attempt with its loot, crime count and AB experience.
    /// Caught: G3 PickPocket calls AssessTarget(victim, hero, Theft) itself (<see cref="AssessTarget"/>: the victim shouts
    /// "DirtyThief", attacks and memorizes the theft); the dialog command ends the talk. Every attempt is published as
    /// <see cref="PickpocketAttempted"/> (fix-248: the HUD explains refusals and failures).
    /// </summary>
    public sealed class PickpocketService : IPickpocket
    {
        readonly Dictionary<ActorId, PickpocketDef> _victims = new Dictionary<ActorId, PickpocketDef>();
        readonly Dictionary<string, TreasureSetDef> _sets;
        readonly HashSet<ActorId> _robbed = new HashSet<ActorId>();
        readonly ActorRegistry _actors;
        readonly Hero _hero;
        readonly ProgressionService _progression;
        readonly ItemServices _items;
        readonly CrimeLedger _ledger;
        readonly RuleOptions _options;
        readonly IRandom _random;
        readonly IGameLog _log;
        readonly IEventBus _events;

        public PickpocketService(IContentDatabase content, ActorRegistry actors, Hero hero, ProgressionService progression, ItemServices items,
                                 CrimeLedger ledger, RuleOptions options, IRandom random, IGameLog log, IEventBus events = null)
        {
            _events = events;
            foreach (var v in content.All<PickpocketDef>()) _victims[v.Npc] = v;
            _sets = content.All<TreasureSetDef>().ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            _actors = actors;
            _hero = hero;
            _progression = progression;
            _items = items;
            _ledger = ledger;
            _options = options ?? RuleOptions.Default;
            _random = random;
            _log = log ?? NullGameLog.Instance;
        }

        /// <summary>G3 AssessTarget(victim, hero, reason) on a failure (the crime system's; null = nobody reacts).</summary>
        public Func<ActorId, ActorId, AttackReason, bool> AssessTarget { get; set; }

        public IEnumerable<ActorId> Robbed => _robbed.OrderBy(a => a.Name, StringComparer.Ordinal);

        public bool WasRobbed(ActorId victim) => _robbed.Contains(victim);

        public bool MayPickpocket(ActorId victim) =>
            _hero.Knows(PickpocketRules.OfferPerk) && _victims.ContainsKey(victim) && !_robbed.Contains(victim);

        public PickpocketVerdict Pickpocket(ActorId victim)
        {
            var verdict = Attempt(victim);
            if (verdict == PickpocketVerdict.Caught)
            {
                _log.Info($"{victim} caught the pickpocket");
                if (AssessTarget?.Invoke(victim, ActorId.Player, AttackReason.Theft) != true)
                    _log.Info($"{victim} caught the pickpocket but cannot attack.");
            }
            _events?.Publish(new PickpocketAttempted(victim, verdict, PickpocketRules.MissingPerk(verdict)));
            return verdict;
        }

        PickpocketVerdict Attempt(ActorId victim)
        {
            if (!_victims.TryGetValue(victim, out var def) || !_actors.TryGet(victim, out var state))
                return PickpocketVerdict.Impossible;
            var combat = CombatWiring.CombatDefOf(state);
            var verdict = PickpocketRules.Attempt(combat.LevelMax, _hero.Stats.Value(PickpocketRules.TheftAttribute), _hero.Knows, _random);
            if (verdict != PickpocketVerdict.Success) return verdict;

            foreach (var name in def.TreasureSets)
            {
                if (!_sets.TryGetValue(name, out var set)) continue;
                var stack = PickpocketRules.Loot(set, _random, out int amount);
                if (stack != null) _items.Inventories.Give(ActorId.Player, stack.Item, amount, stack.Quality);
            }
            _robbed.Add(victim);
            bool masterThief = _options.AlternativeBalancing && _hero.Knows(PickpocketRules.MasterThiefPerk);
            if (combat.Enclave != null && !masterThief) _ledger.Count(combat.Enclave, CrimeType.Theft);
            if (_options.AlternativeBalancing) _progression.AwardXp(PickpocketRules.AlternativeBalancingXp(combat.LevelMax));
            _log.Info($"pickpocketed {victim}");
            return verdict;
        }

        internal void Restore(IEnumerable<ActorId> robbed)
        {
            _robbed.Clear();
            foreach (var r in robbed) _robbed.Add(r);
        }
    }

    /// <summary>Save section "crime.pickpocket": the NPCs the hero has robbed (G3 PickedPocket).</summary>
    public sealed class PickpocketSaveSection : ISaveParticipant
    {
        readonly PickpocketService _service;

        public PickpocketSaveSection(PickpocketService service) => _service = service ?? throw new ArgumentNullException(nameof(service));

        public string Section => "crime.pickpocket";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteSortedStrings(_service.Robbed.Select(a => a.Name));

        public void Read(ISaveReader r, int version) => _service.Restore(r.ReadStrings().Select(n => new ActorId(n)));
    }
}
