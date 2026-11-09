using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// Who stands on which side of a reported touch (Docs/combat.md §9.9): the attacker's and the victim's current
    /// targets, the attitudes between attacker, victim and the attacker's target, party membership and whether the victim
    /// is in combat. Fills the situation part of a <see cref="HitContext"/> that the party and bystander gates of
    /// <see cref="CombatResolver"/> read (G3 OnHit / OnTouchDamage / DoLogicalDamage). Pure over the session.
    /// </summary>
    public static class HitSides
    {
        /// <summary>
        /// The NPC's current target (G3 PSNpc.GetCurrentTarget): the actor its fight (or flight) is against, none when it
        /// does not fight. Not the shown opponent: a swing that is still running when the foe died, or a foe that is not
        /// shown, has no target of the presentation, yet the NPC's target is what G3 compares the touched body with
        /// (fix-59: a companion's follow-through cut the hero because "no opponent" counted as "the target").
        /// </summary>
        public static ActorId NpcTargetOf(GameSession session, ActorId npc)
        {
            var brain = npc.IsEmpty || npc.IsPlayer ? null : session.NpcCombat.Get(npc);
            return brain != null && brain.IsFighting ? brain.State.Target : default;
        }

        /// <summary>
        /// <paramref name="member"/> is in the party <paramref name="leader"/> leads (G3 PSParty members of the leader): the
        /// hero's companions, followers, controlled and summoned creatures; the hero himself for the NPC guiding him (G3
        /// StartGuidePlayer makes the guide his party leader); the creatures an NPC summoned.
        /// </summary>
        public static bool InPartyOf(GameSession session, ActorId member, ActorId leader)
        {
            if (member.IsEmpty || leader.IsEmpty || member == leader) return false;
            var party = session.Party?.Members;
            if (leader.IsPlayer) return party != null && party.IsLedByPlayer(member);
            if (member.IsPlayer) return party?.Guide != null && party.Guide.Npc == leader;
            return session.Summoning != null && session.Summoning.SummonerOf(member) == leader;
        }

        /// <summary>
        /// Fills the sides of <paramref name="hit"/>: <paramref name="attackerTarget"/> and <paramref name="victimTarget"/>
        /// are the two actors' current targets (none = empty). For an NPC attacker <see cref="HitContext.VictimIsTarget"/>
        /// is set here (the touched body is its target); the player's is left to the caller (his focus rules).
        /// </summary>
        public static void Fill(GameSession session, HitContext hit, ActorId attacker, ActorId victim, ActorId attackerTarget,
                                ActorId victimTarget)
        {
            var attitudes = session.Crime.Attitudes;
            hit.AttackerToVictim = attitudes.Of(attacker, victim);
            hit.VictimToAttacker = attitudes.Of(victim, attacker);
            hit.VictimInAttackersParty = InPartyOf(session, victim, attacker);
            if (!attacker.IsPlayer) hit.VictimIsTarget = !attackerTarget.IsEmpty && attackerTarget == victim;
            hit.AttackerHasTarget = !attackerTarget.IsEmpty;
            if (hit.AttackerHasTarget)
            {
                hit.AttackerToTarget = attitudes.Of(attacker, attackerTarget);
                hit.TargetToAttacker = attitudes.Of(attackerTarget, attacker);
            }
            hit.VictimTargetsAttacker = !victimTarget.IsEmpty && victimTarget == attacker;
            // AI mode Combat: an NPC that fights or flees; the hero has no AI mode here and counts as in combat [interp].
            hit.VictimInCombat = victim.IsPlayer || (session.NpcCombat.Get(victim)?.IsFighting ?? false);
        }
    }
}
