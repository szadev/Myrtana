using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>Starts (or escalates) a fight of an NPC (the NPC combat system).</summary>
    public delegate bool EngageFight(ActorId npc, ActorId target, AttackReason reason);
    public delegate bool EngageFlight(ActorId npc, ActorId from);

    /// <summary>
    /// Delivers every <see cref="PerceptionBroadcast"/> on the bus to the NPCs that can receive it, picks each one's
    /// reaction and carries it out: attacks go to the NPC combat system, and an attack on the player for a criminal
    /// reason is memorized (enclave or personal). Other reactions are published as <see cref="NpcReacted"/> for the
    /// presentation. Docs/perception-crime.md §4.
    /// </summary>
    public sealed class WitnessSystem : IDisposable
    {
        readonly PerceptionRules _rules;
        readonly AttitudeService _attitudes;
        readonly CrimeCatalogue _catalogue;
        readonly CrimeLedger _ledger;
        readonly ReactionSelector _selector;
        readonly IPerceptionWorld _world;
        readonly ILineOfSight _lineOfSight;
        readonly EngageFight _engage;
        readonly IEventBus _events;
        readonly IDisposable _subscription;

        public WitnessSystem(PerceptionRules rules, AttitudeService attitudes, CrimeCatalogue catalogue, CrimeLedger ledger,
                             IPerceptionWorld world, ILineOfSight lineOfSight, EngageFight engage, IEventBus events)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _attitudes = attitudes ?? throw new ArgumentNullException(nameof(attitudes));
            _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _lineOfSight = lineOfSight ?? OpenLineOfSight.Instance;
            _engage = engage ?? throw new ArgumentNullException(nameof(engage));
            _events = events;
            _selector = new ReactionSelector(attitudes, catalogue);
            _subscription = events?.Subscribe<PerceptionBroadcast>(e => Deliver(e));
        }

        /// <summary>Delivers one broadcast; returns who reacted and how (for tests and debug views).</summary>
        public IReadOnlyList<KeyValuePair<ActorId, Reaction>> Deliver(in PerceptionBroadcast b)
        {
            var reactions = new List<KeyValuePair<ActorId, Reaction>>();
            _attitudes.Facts.TryGetProfile(b.Source, out var source);
            _attitudes.Facts.TryGetProfile(b.Victim, out var victim);
            foreach (var receiver in _world.Receivers())
            {
                if (!_attitudes.Facts.TryGetProfile(receiver.Id, out var witness)) continue;
                bool ownSide = BroadcastReception.OwnSide(witness, source, victim);
                if (!BroadcastReception.CanReceive(receiver, b, witness, _rules, _lineOfSight, Revolution(b, witness, victim), ownSide)) continue;
                float toWork = b.Kind == BroadcastKind.TakeItem || b.Kind == BroadcastKind.UseInteractObject
                    ? _world.DistanceToWorkplace(receiver.Id, b.Origin) : float.MaxValue;
                var reaction = _selector.Select(b, witness, source, victim, receiver.HomeZone, toWork);
                if (reaction.Kind == ReactionKind.None) continue;
                Apply(witness, reaction, b.Kind);
                reactions.Add(new KeyValuePair<ActorId, Reaction>(receiver.Id, reaction));
            }
            return reactions;
        }

        /// <summary>
        /// CP AssessFightSound: while the receiver's enclave is in Attack and its revolution attitude toward the victim is
        /// Friendly or Hostile (G3 GetRevolutionAttitude), the fight sound carries the revolution range (4000 instead of 2000).
        /// fix-216: never passed before, so an alarmed enclave (the Nordmar mine's orcs) heard its fights only half as far.
        /// </summary>
        bool Revolution(in PerceptionBroadcast b, in AttitudeProfile witness, in AttitudeProfile victim)
        {
            if (b.Kind != BroadcastKind.FightSound || witness.Enclave == null || _attitudes.Enclaves == null) return false;
            if (_attitudes.Enclaves.Of(witness.Enclave).Status != EnclaveStatus.Attack) return false;
            var attitude = _attitudes.RevolutionOf(witness, victim);
            return attitude == Attitude.Friendly || attitude == Attitude.Hostile;
        }

        /// <summary>Carries out a reaction of <paramref name="witness"/> (also used by the active perception steps).</summary>
        public void Apply(in AttitudeProfile witness, Reaction reaction, BroadcastKind cause)
        {
            if (reaction.Kind == ReactionKind.Attack)
            {
                if (!_engage(witness.Id, reaction.Target, reaction.Reason)) return;
                if (reaction.Target.IsPlayer) _catalogue.Memorize(_ledger, witness, CrimeCatalogue.CrimeOf(reaction.Reason));
            }
            // ZS_Flee: the witness runs from the attacker, winner or killer (the combat brain's flight; "RunAway").
            else if (reaction.Kind == ReactionKind.Flee && Flee != null && !Flee(witness.Id, reaction.Target)) return;
            _events?.Publish(new NpcReacted(witness.Id, reaction, cause));
        }

        /// <summary>Starts a witness's flight (G3 ZS_Flee); null = fleeing witnesses only publish their reaction. Set by the session.</summary>
        public EngageFlight Flee { get; set; }

        public void Dispose() => _subscription?.Dispose();
    }
}
