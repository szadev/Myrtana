using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Gives kill/defeat XP (§8) once per NPC (knocking out and later killing the same NPC pays once, like G3's
    /// DefeatedByPlayer flag): for NPCs defeated by the player's side (G3 Kill/Defeat: the hero or a member he leads), and
    /// [owner, fix-93] for NPCs the hero's own hits had hurt that any other NPC (a guard, a summon, a passing wolf) finished
    /// off ("we practically killed it"). An NPC the hero never hurt pays nothing unless his party defeats it.
    /// </summary>
    public sealed class KillXpAwarder : IDisposable, IActorStore
    {
        readonly ProgressionService _progression;
        readonly HashSet<ActorId> _rewarded = new HashSet<ActorId>();
        readonly IDisposable _subscription;

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            _rewarded.Remove(actor);
        }

        public bool Holds(ActorId actor) => _rewarded.Contains(actor);

        public KillXpAwarder(ProgressionService progression, IEventBus events)
        {
            _progression = progression;
            _subscription = events.Subscribe<ActorDefeated>(OnDefeated);
        }

        public IEnumerable<ActorId> Rewarded => _rewarded;

        internal void MarkRewarded(ActorId victim) => _rewarded.Add(victim);

        public void Dispose() => _subscription.Dispose();

        void OnDefeated(ActorDefeated e)
        {
            if (!(e.ByPlayerParty || e.HurtByPlayer) || e.Victim.IsEmpty || !_rewarded.Add(e.Victim)) return;
            var victim = new DefeatedVictim(e.VictimLevel, e.AmbientCreature, e.Species);
            _progression.AwardXp(ProgressionMath.KillXp(_progression.Rules.KillXp, _progression.Options, victim));
        }
    }
}
