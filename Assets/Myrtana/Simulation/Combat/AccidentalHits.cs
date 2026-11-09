using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// What a landed hit means to a victim that is already fighting (G3 AssessAgressor 0x10024490, AI mode Combat) and the
    /// port's "Forgive accidental hits" rule (fix-162, [owner]). Pure; <see cref="AccidentalHits"/> feeds it.
    /// </summary>
    public static class AccidentalHitRules
    {
        /// <summary>Hits of the hero on one NPC forgiven within <see cref="WindowSeconds"/>; the next one counts [owner, port].</summary>
        public const int ForgivenHits = 3;
        /// <summary>Real seconds a forgiven hit is remembered for the count [port].</summary>
        public const double WindowSeconds = 8;
        /// <summary>
        /// Real seconds an NPC still counts as fighting the hero's enemy after its last blow exchanged with one (the foe just
        /// fell and the hero's swing runs on) [port].
        /// </summary>
        public const double GraceSeconds = 5;

        public enum Verdict { Counts, Forgiven }

        /// <summary>
        /// The victims the rule spares: friendly or neutral to the hero (the attitudes G3's DoLogicalDamage spares from his
        /// blade when they fight someone else).
        /// </summary>
        public static bool SparedSide(Attitude victimToHero) => victimToHero == Attitude.Friendly || victimToHero == Attitude.Neutral;

        /// <summary>
        /// The NPC's foe is the hero's enemy too: angry or hostile to him (Angry: a wolf that threatens, Hostile, Predator),
        /// fighting him or his party, or hit by him a moment ago.
        /// </summary>
        public static bool CommonEnemy(Attitude foeToHero, bool foeFightsHeroSide, bool heroHitFoe) =>
            foeToHero == Attitude.Angry || foeToHero == Attitude.Hostile || foeToHero == Attitude.Predator || foeFightsHeroSide || heroHitFoe;

        /// <summary>
        /// Whether a landed hit is forgiven (no fight back, no fight sound, only a complaint line). The rule needs the option,
        /// the hero as the attacker and a hit that neither knocks out nor kills; the hero's party is always forgiven; any
        /// other victim must be on the spared side, in a fight against a common enemy (or just out of one) and not fighting
        /// the hero; <paramref name="hitsInWindow"/> (this one included) above <see cref="ForgivenHits"/> counts again, so a
        /// deliberate attack still starts the fight.
        /// </summary>
        public static Verdict Judge(bool enabled, bool attackerIsPlayer, bool defeats, bool victimInPlayerParty,
                                    Attitude victimToHero, bool victimFightsHeroSide, bool inCommonFight, int hitsInWindow)
        {
            if (!enabled || !attackerIsPlayer || defeats) return Verdict.Counts;
            if (victimInPlayerParty) return Verdict.Forgiven;
            if (victimFightsHeroSide || !SparedSide(victimToHero) || !inCommonFight) return Verdict.Counts;
            return hitsInWindow <= ForgivenHits ? Verdict.Forgiven : Verdict.Counts;
        }

        /// <summary>
        /// G3 AssessAgressor for a victim in AI mode Combat (CP 0x10024490): hit by the player, a Friend-type NPC or a
        /// member the player leads keeps its target (AssessTarget(current target, ReactToDamage)); otherwise a hitter that is
        /// not the target takes its place when it also landed the hit before (AssessHit 0x1002b580 moves CurrentAttacker
        /// into LastAttacker before storing the new one) or when the victim has no attack token (CombatState 0, passive);
        /// an active fighter hit once by a stray blow keeps its target.
        /// </summary>
        public static bool SwitchesToHitter(bool hitterIsTarget, bool hitterIsPlayer, bool victimFriendType, bool victimInHittersParty,
                                            bool hitterLandedTheHitBefore, bool victimHoldsToken)
        {
            if (hitterIsTarget) return false;
            if (hitterIsPlayer && (victimFriendType || victimInHittersParty)) return false;
            return hitterLandedTheHitBefore || !victimHoldsToken;
        }
    }

    /// <summary>
    /// The state behind <see cref="AccidentalHitRules"/>: who landed the last hits on each NPC (G3 Current/LastAttacker),
    /// when an NPC last traded blows with an enemy of the hero, the hero's recent hits per NPC (all of them for the count, the
    /// counted ones as "the hero fights it"), and the verdict of the hit
    /// being handled (judged once: <see cref="NpcCombatSystem"/>, <see cref="CombatBroadcasts"/> and the cheers ask for the
    /// same <see cref="HitResolved"/>). Owned by <see cref="NpcCombatSystem.Accidents"/>; not saved.
    /// </summary>
    public sealed class AccidentalHits : IActorStore
    {
        readonly NpcCombatSystem _combat;
        readonly Func<double> _now;
        readonly Dictionary<ActorId, (ActorId current, ActorId last)> _attackers = new Dictionary<ActorId, (ActorId, ActorId)>();
        readonly Dictionary<ActorId, double> _commonFightAt = new Dictionary<ActorId, double>();
        readonly Dictionary<ActorId, double> _heroHitAt = new Dictionary<ActorId, double>();
        readonly Dictionary<ActorId, List<double>> _heroHits = new Dictionary<ActorId, List<double>>();
        HitOutcome _judged;
        AccidentalHitRules.Verdict _verdict;
        bool _landedBefore;

        internal AccidentalHits(NpcCombatSystem combat, Func<double> now)
        {
            _combat = combat;
            _now = now;
        }

        /// <summary>The rule is on (the session's <c>CombatPortOptions.ForgiveAccidentalHits</c>).</summary>
        bool Enabled => _combat.Port?.ForgiveAccidentalHits == true;

        /// <summary>The hit is forgiven (judged on first ask, the same answer for every later ask about it).</summary>
        public bool IsForgiven(in HitResolved e) => Judge(e) == AccidentalHitRules.Verdict.Forgiven;

        /// <summary>G3 LastAttacker == hitter: the hitter also landed the victim's previous hit.</summary>
        public bool LandedTheHitBefore(in HitResolved e)
        {
            Judge(e);
            return _landedBefore;
        }

        AccidentalHitRules.Verdict Judge(in HitResolved e)
        {
            if (e.Outcome == null || e.Outcome.Result == HitResult.Ignored) return AccidentalHitRules.Verdict.Counts;
            if (ReferenceEquals(e.Outcome, _judged)) return _verdict;
            double now = _now();
            _judged = e.Outcome;
            _landedBefore = Remember(e.Attacker, e.Victim);
            Note(e.Attacker, e.Victim, now);
            _verdict = Decide(e, now);
            // a counted hit of the hero makes its victim his foe for a while (an NPC neutral to him that he attacks)
            if (e.Attacker.IsPlayer && !e.Victim.IsEmpty && _verdict == AccidentalHitRules.Verdict.Counts) _heroHitAt[e.Victim] = now;
            return _verdict;
        }

        /// <summary>AssessHit: LastAttacker = CurrentAttacker, CurrentAttacker = the hitter; returns LastAttacker == hitter.</summary>
        bool Remember(ActorId attacker, ActorId victim)
        {
            if (victim.IsEmpty || victim.IsPlayer || attacker.IsEmpty) return false;
            _attackers.TryGetValue(victim, out var a);
            _attackers[victim] = (attacker, a.current);
            return a.current == attacker;
        }

        /// <summary>A blow between two NPCs, one of them the hero's enemy: the other one fights on the hero's side.</summary>
        void Note(ActorId attacker, ActorId victim, double now)
        {
            if (attacker.IsPlayer || attacker.IsEmpty || victim.IsEmpty || victim.IsPlayer) return;
            if (EnemyOfHero(victim, now)) _commonFightAt[attacker] = now;
            if (EnemyOfHero(attacker, now)) _commonFightAt[victim] = now;
        }

        AccidentalHitRules.Verdict Decide(in HitResolved e, double now)
        {
            if (!Enabled || !e.Attacker.IsPlayer || e.Victim.IsPlayer || e.Victim.IsEmpty) return AccidentalHitRules.Verdict.Counts;
            bool defeats = e.Outcome.Killed || e.Outcome.KnockedOut;
            bool party = _combat.PartyBond?.Invoke(e.Victim, ActorId.Player) == true;
            int hits = CountHeroHit(e.Victim, now);
            var brain = _combat.Get(e.Victim);
            bool fightsHeroSide = brain != null && brain.IsFighting && brain.State.Phase != CombatPhase.Flee && HeroSide(brain.State.Target);
            bool common = brain != null && brain.IsAttacking && !HeroSide(brain.State.Target) && EnemyOfHero(brain.State.Target, now);
            if (!common && _commonFightAt.TryGetValue(e.Victim, out var at) && now - at <= AccidentalHitRules.GraceSeconds) common = true;
            var toHero = _combat.AttitudeOf?.Invoke(e.Victim, ActorId.Player) ?? Attitude.Neutral;
            return AccidentalHitRules.Judge(true, true, defeats, party, toHero, fightsHeroSide, common, hits);
        }

        int CountHeroHit(ActorId victim, double now)
        {
            if (!_heroHits.TryGetValue(victim, out var list)) _heroHits[victim] = list = new List<double>(4);
            list.RemoveAll(t => now - t > AccidentalHitRules.WindowSeconds);
            list.Add(now);
            return list.Count;
        }

        /// <summary>The hero or a member of his party.</summary>
        bool HeroSide(ActorId actor) => actor.IsPlayer || (!actor.IsEmpty && _combat.PartyBond?.Invoke(actor, ActorId.Player) == true);

        bool EnemyOfHero(ActorId foe, double now)
        {
            if (foe.IsEmpty || HeroSide(foe)) return false;
            var foeBrain = _combat.Get(foe);
            bool fightsHero = foeBrain != null && foeBrain.IsAttacking && HeroSide(foeBrain.State.Target);
            bool hitByHero = _heroHitAt.TryGetValue(foe, out var at) && now - at <= AccidentalHitRules.WindowSeconds;
            var toHero = _combat.AttitudeOf?.Invoke(foe, ActorId.Player) ?? Attitude.Neutral;
            return AccidentalHitRules.CommonEnemy(toHero, fightsHero, hitByHero);
        }

        public void Forget(ActorId actor)
        {
            _attackers.Remove(actor);
            _commonFightAt.Remove(actor);
            _heroHitAt.Remove(actor);
            _heroHits.Remove(actor);
        }

        public bool Holds(ActorId actor) =>
            _attackers.ContainsKey(actor) || _commonFightAt.ContainsKey(actor) || _heroHitAt.ContainsKey(actor) || _heroHits.ContainsKey(actor);

        internal void Clear()
        {
            _attackers.Clear();
            _commonFightAt.Clear();
            _heroHitAt.Clear();
            _heroHits.Clear();
            _judged = null;
        }
    }
}
