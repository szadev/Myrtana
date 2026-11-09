using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Turns resolved hits into the perception broadcasts of Docs/combat.md §7 and settles what a fight's end means
    /// for crime (Docs/perception-crime.md §4.3): every hit makes a fight sound, a knock-out a defeat, a kill a murder.
    /// A knock-out by the player is an Attack crime the victim remembers; the player's kill of an enclave's loyal member
    /// raises the enclave's murder count and its alarm. Subscribes to the bus next to <see cref="NpcCombatSystem"/>
    /// without changing it; the fight's reason is read from the NPC combat brains.
    /// </summary>
    public sealed class CombatBroadcasts : IDisposable
    {
        readonly NpcCombatSystem _combat;
        readonly AttitudeService _attitudes;
        readonly CrimeCatalogue _catalogue;
        readonly CrimeLedger _ledger;
        readonly IPerceptionWorld _world;
        readonly IEventBus _events;
        readonly Func<double> _gameHours;
        readonly Action<string, ActorId> _alarm;
        readonly Dictionary<(ActorId, ActorId), AttackReason> _reasons = new Dictionary<(ActorId, ActorId), AttackReason>();
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <param name="alarm">Raises an enclave's alarm against an attacker (<see cref="CrimeSystem.RaiseEnclaveAlarm"/>).</param>
        public CombatBroadcasts(NpcCombatSystem combat, AttitudeService attitudes, CrimeCatalogue catalogue, CrimeLedger ledger,
                                IPerceptionWorld world, IEventBus events, Func<double> gameHours, Action<string, ActorId> alarm = null)
        {
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _attitudes = attitudes ?? throw new ArgumentNullException(nameof(attitudes));
            _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _gameHours = gameHours ?? throw new ArgumentNullException(nameof(gameHours));
            _alarm = alarm;
            _subscriptions.Add(events.Subscribe<HitResolved>(OnHit));
        }

        /// <summary>
        /// Remembers the reasons of the fights in progress. A knock-out ends the victim's fight before the hit is
        /// reported, so the reason must be known from the frame before; call once per frame.
        /// </summary>
        public void Tick()
        {
            _reasons.Clear();
            foreach (var brain in _combat.Brains)
                if (brain.IsFighting && !brain.State.Target.IsEmpty)
                    _reasons[(brain.Self, brain.State.Target)] = brain.State.Reason;
        }

        /// <summary>The reason of the fight between two actors (the NPC side's, the higher one if both fight).</summary>
        public AttackReason ReasonOf(ActorId a, ActorId b)
        {
            var reason = AttackReason.None;
            if (Current(a, b) is AttackReason ra) reason = ra;
            if (Current(b, a) is AttackReason rb && rb > reason) reason = rb;
            if (_reasons.TryGetValue((a, b), out var ma) && ma > reason) reason = ma;
            if (_reasons.TryGetValue((b, a), out var mb) && mb > reason) reason = mb;
            return reason;
        }

        AttackReason? Current(ActorId self, ActorId target)
        {
            var brain = _combat.Get(self);
            return brain != null && brain.IsFighting && brain.State.Target == target ? brain.State.Reason : (AttackReason?)null;
        }

        /// <summary>What a landed hit did to its victim.</summary>
        public enum HitEffect { Hurt, KnockedOut, Killed }

        void OnHit(HitResolved e)
        {
            if (e.Outcome.Result == HitResult.Ignored) return;
            // fix-162 [owner]: a forgiven stray hit of the hero (AccidentalHits) is no attack: no fight sound, nobody joins.
            if (_combat.Accidents.IsForgiven(e)) return;
            Report(e.Attacker, e.Victim, e.Outcome.Killed ? HitEffect.Killed : e.Outcome.KnockedOut ? HitEffect.KnockedOut : HitEffect.Hurt);
        }

        /// <summary>A hit of <paramref name="attacker"/> on <paramref name="victim"/> landed (called for every resolved hit).</summary>
        public void Report(ActorId attacker, ActorId victim, HitEffect effect)
        {
            if (attacker.IsEmpty || attacker == victim) return;
            var reason = ReasonOf(attacker, victim);
            if (!_world.TryGetPose(victim, out var origin)) return;
            string indoor = _world.IndoorZoneOf(victim);
            switch (effect)
            {
                case HitEffect.Killed: OnKill(attacker, victim, reason, origin, indoor); break;
                case HitEffect.KnockedOut: OnKnockOut(attacker, victim, reason, origin, indoor); break;
                default:
                    WorldPose? at = _world.TryGetPose(attacker, out var a) ? a : (WorldPose?)null; // heard near either fighter
                    _events.Publish(new PerceptionBroadcast(BroadcastKind.FightSound, attacker, victim, origin, reason, sourceIndoorZone: indoor,
                                                            sourcePose: at));
                    break;
            }
        }

        void OnKnockOut(ActorId winner, ActorId loser, AttackReason reason, WorldPose origin, string indoor)
        {
            double now = _gameHours();
            bool transformed = PlayerTransformed(); // G3 Defeat: no fight memory against a transformed hero (Docs/combat.md §12.5)
            if (winner.IsPlayer && !transformed && _attitudes.Facts.TryGetProfile(loser, out var victim))
            {
                if (CrimeCatalogue.KnockOutIsCrime(victim, reason)) _ledger.RememberPersonally(loser, CrimeType.Attack);
                _ledger.RecordFight(loser, reason == AttackReason.Duel ? FightResult.Friendly : FightResult.PlayerWon, reason, now);
            }
            else if (loser.IsPlayer && !winner.IsPlayer && !transformed)
                _ledger.RecordFight(winner, reason == AttackReason.Duel ? FightResult.Friendly : FightResult.PlayerLost, reason, now);
            _events.Publish(new PerceptionBroadcast(BroadcastKind.Defeat, winner, loser, origin, reason, sourceIndoorZone: indoor));
        }

        /// <summary>
        /// The enclave alarm of a kill (G3 Kill → NotifyEnclave mode 0): enclave, killer, victim, whether the victim was the
        /// enclave's boss. When set it replaces the constructor's alarm for murders and also hears boss kills by anyone.
        /// </summary>
        public Action<string, ActorId, ActorId, bool> KillAlarm { get; set; }

        void OnKill(ActorId killer, ActorId victimId, AttackReason reason, WorldPose origin, string indoor)
        {
            bool known = _attitudes.Facts.TryGetProfile(victimId, out var victim);
            if (known && killer.IsPlayer && !PlayerTransformed() && CrimeCatalogue.IsMurderWorthy(victim, reason))
            {
                string enclave = _catalogue.MurderCountedAgainst(victim);
                if (enclave != null)
                {
                    _ledger.Count(enclave, CrimeType.Murder);
                    // editor-runner-12: a murdered boss still counts as a boss kill (the murder is not Known yet at
                    // this point, so without the flag the Geldern alarm never fired when the hero killed Grok)
                    if (KillAlarm != null) KillAlarm(enclave, killer, victimId, victim.Boss && victim.Enclave == enclave);
                    else _alarm?.Invoke(enclave, killer);
                }
            }
            else if (known && victim.Boss && victim.Enclave != null && !killer.IsEmpty)
                KillAlarm?.Invoke(victim.Enclave, killer, victimId, true); // a boss's death alarms the enclave (any killer)
            _events.Publish(new PerceptionBroadcast(BroadcastKind.Murder, killer, victimId, origin, reason, sourceIndoorZone: indoor));
        }

        bool PlayerTransformed() => _attitudes.Facts.TryGetProfile(ActorId.Player, out var p) && p.Transformed;

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
