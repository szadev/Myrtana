using System;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// What a witness does about a broadcast it received (G3 AssessFightSound, AssessDefeat, AssessMurder,
    /// AssessTakeItem …; Docs/combat.md §7 and Docs/perception-crime.md §4.2). Pure: profiles and attitudes in, a
    /// <see cref="Reaction"/> out.
    /// </summary>
    public sealed class ReactionSelector
    {
        readonly AttitudeService _attitudes;
        readonly CrimeCatalogue _crimes;

        public ReactionSelector(AttitudeService attitudes, CrimeCatalogue crimes)
        {
            _attitudes = attitudes ?? throw new ArgumentNullException(nameof(attitudes));
            _crimes = crimes ?? throw new ArgumentNullException(nameof(crimes));
        }

        /// <param name="witness">The receiver (already filtered by <see cref="BroadcastReception"/>).</param>
        /// <param name="source">Who did it.</param>
        /// <param name="victim">Who it was done to (default for thefts and zone entries).</param>
        /// <param name="homeZone">The zone of the witness's sleeping place (for intruders).</param>
        /// <param name="distanceToWorkplace">Centimetres from the theft to the witness's workplace.</param>
        public Reaction Select(in PerceptionBroadcast b, in AttitudeProfile witness, in AttitudeProfile source, in AttitudeProfile victim,
                               string homeZone = null, float distanceToWorkplace = float.MaxValue)
        {
            switch (b.Kind)
            {
                case BroadcastKind.FightSound: return FightSound(b.Reason, witness, source, victim);
                case BroadcastKind.Defeat: return Defeat(witness, source, victim, b.Reason);
                case BroadcastKind.Murder: return Murder(b.Reason, witness, source, victim);
                case BroadcastKind.CallHelp: return CallHelp(b.Reason, witness, source, victim);
                case BroadcastKind.TakeItem:
                case BroadcastKind.UseInteractObject:
                    return Theft(b, witness, source, distanceToWorkplace);
                case BroadcastKind.Warn:
                    return Reaction.Of(ReactionKind.Observe, b.Victim);
                case BroadcastKind.EnterZone:
                    return homeZone != null && homeZone == b.Zone.Id && AttitudeOf(witness, source) != Attitude.Friendly
                        ? Reaction.Of(ReactionKind.GoToIntruder, b.Source) : Reaction.None;
                default:
                    return Reaction.None;
            }
        }

        /// <summary>
        /// How the fight's reason makes a friend of one side join in (Docs/combat.md §7). <see cref="Watch"/>: nobody joins,
        /// the witness is a bystander (CP AssessFightSound 0x1002a510: the switch's default case) [fix-129].
        /// </summary>
        public enum JoinGroup { Normal, Theft, Enemy, Watch }

        public static JoinGroup GroupOf(AttackReason reason)
        {
            switch (reason)
            {
                case AttackReason.StopThief:
                case AttackReason.Theft:
                case AttackReason.UseInteractObject:
                    return JoinGroup.Theft;
                case AttackReason.Intruder:
                case AttackReason.Enemy:
                case AttackReason.Revolution:
                case AttackReason.Murder:
                    return JoinGroup.Enemy;
                case AttackReason.None:
                case AttackReason.ReactToWeapon:
                case AttackReason.StopFight:
                case AttackReason.ReactToDamage:
                    return JoinGroup.Normal;
                default:
                    // fix-129: LeaveZone, Livestock, Angry, GateGuard, Prey, DriveAway, PlayerCommand (and the watched Duel,
                    // Arena, Kill): CP AssessFightSound's default case sends the friend to the bystander branch (flee or
                    // ZS_WatchFight). Before, they joined with StopFight [interp], so the hero's fight with one rebel
                    // (a provoked guard, an angry NPC) brought the whole camp down on him.
                    return JoinGroup.Watch;
            }
        }

        Reaction FightSound(AttackReason reason, in AttitudeProfile w, in AttitudeProfile attacker, in AttitudeProfile victim)
        {
            // CP AssessFightSound: the player's party members ignore the sound while waiting, in livestock fights, duels and
            // the arena; otherwise they defend their leader.
            if (w.InPlayerParty && (w.PartyWaiting || reason == AttackReason.Livestock || reason == AttackReason.Duel || reason == AttackReason.Arena))
                return Reaction.None;
            // CP AssessFightSound excludes only Duel (17) and Arena (18) for the party: a scripted Kill attack (info command
            // Attack) on the hero is defended too (fix-81; was watched).
            if (!w.InPlayerParty && reason == AttackReason.Kill) return Bystander(w, attacker, victim);
            if (reason == AttackReason.Duel || reason == AttackReason.Arena) return Bystander(w, attacker, victim);

            if (w.InPlayerParty)
            {
                // The hero hurt his own companion or guide (only an aimed spell missile can, fix-30): the others do not
                // turn on it; a fight inside the hero's side would end at once anyway (PlayerParty.IsFightWithinSide)
                // [interp: G3 AssessFightSound has no such check].
                if (attacker.IsPlayer && (victim.InPlayerParty || (!attacker.PartyLeader.IsEmpty && attacker.PartyLeader == victim.Id)))
                    return Reaction.None;
                if (attacker.IsPlayer) return Reaction.Attack(victim.Id, AttackReason.PlayerCommand);
                if (victim.IsPlayer) return Reaction.Attack(attacker.Id, AttackReason.PlayerCommand);
            }
            if (_crimes.IsLivestock(victim, w) && AttitudeOf(w, attacker) != Attitude.Friendly)
                return Reaction.Attack(attacker.Id, AttackReason.Livestock);

            bool friendOfVictim = AttitudeOf(w, victim) == Attitude.Friendly;
            bool friendOfAttacker = AttitudeOf(w, attacker) == Attitude.Friendly;
            var group = GroupOf(reason);
            if (friendOfVictim && !friendOfAttacker && group != JoinGroup.Watch) return Join(w, attacker.Id, reason);
            // Friends of the attacker help it; NPCs of an enclave only for theft and enemy fights.
            if (friendOfAttacker && !friendOfVictim && group != JoinGroup.Watch && (w.Enclave == null || group != JoinGroup.Normal))
                return Join(w, victim.Id, reason);
            return Bystander(w, attacker, victim);
        }

        static Reaction Join(in AttitudeProfile w, ActorId target, AttackReason reason)
        {
            switch (GroupOf(reason))
            {
                case JoinGroup.Theft: return w.Guard ? Reaction.Attack(target, AttackReason.StopThief) : Reaction.Of(ReactionKind.Watch, target);
                case JoinGroup.Enemy: return Reaction.Attack(target, AttackReason.Enemy);
                default: return Reaction.Attack(target, AttackReason.StopFight);
            }
        }

        /// <summary>Not involved: panicky creatures flee, humanoids watch, others carry on.</summary>
        Reaction Bystander(in AttitudeProfile w, in AttitudeProfile attacker, in AttitudeProfile victim)
        {
            if (w.CannotFight || AttitudeOf(w, attacker) == Attitude.Panic) return Reaction.Of(ReactionKind.Flee, attacker.Id);
            return w.Humanoid ? Reaction.Watch(attacker.Id, victim.Id) : Reaction.None;
        }

        /// <summary>A fight whose knock-out is not a crime and calls nobody in: duels and the arena.</summary>
        public static bool Sanctioned(AttackReason reason) => reason == AttackReason.Duel || reason == AttackReason.Arena;

        Reaction Defeat(in AttitudeProfile w, in AttitudeProfile winner, in AttitudeProfile loser, AttackReason reason)
        {
            // CP AssessDefeat: the player's party members attack the player's opponent; a waiting member ignores the defeat.
            // In duels and the arena they react like anyone else.
            if (w.InPlayerParty && w.PartyWaiting) return Reaction.None;
            if (w.InPlayerParty && !Sanctioned(reason) && (winner.IsPlayer || loser.IsPlayer))
                return Reaction.Attack(winner.IsPlayer ? loser.Id : winner.Id, AttackReason.PlayerCommand);
            // A duel or arena fight is sanctioned: the knock-out ends it, nobody steps in for the loser; the watchers only
            // comment (G3 AssessDefeat: watch reason → defeat comment). Finishing the loser off is still a murder (Murder).
            // CP AssessDefeat 0x10029fb0 tests HasAttackReasonToWatch (Duel, Arena and Kill) here: a knock-out in a scripted
            // Kill fight is only commented on as well (fix-129).
            if (Sanctioned(reason) || reason == AttackReason.Kill) return w.Humanoid ? Reaction.Comment(winner.Id, loser.Id) : Reaction.None;
            bool friendOfLoser = AttitudeOf(w, loser) == Attitude.Friendly;
            bool friendOfWinner = AttitudeOf(w, winner) == Attitude.Friendly;
            if (friendOfLoser && !friendOfWinner && !w.CannotFight) return Reaction.Attack(winner.Id, AttackReason.StopFight);
            if (friendOfWinner) return Reaction.Comment(winner.Id, loser.Id);
            if (w.CannotFight || AttitudeOf(w, winner) == Attitude.Panic) return Reaction.Of(ReactionKind.Flee, winner.Id);
            // AssessDefeat: panic toward the loser flees from the loser.
            if (AttitudeOf(w, loser) == Attitude.Panic) return Reaction.Of(ReactionKind.Flee, loser.Id);
            return Reaction.None;
        }

        Reaction Murder(AttackReason killerReason, in AttitudeProfile w, in AttitudeProfile killer, in AttitudeProfile victim)
        {
            if (!CrimeCatalogue.IsMurderWorthy(victim, killerReason)) return Reaction.None;
            // CP AssessMurder: panic toward the killer flees; a friend of the killer who is no friend of the victim, or a
            // neutral one who disliked the victim (Angry, Hostile), comments on it (StartSayDefeatComment).
            var toKiller = AttitudeOf(w, killer);
            var toVictim = AttitudeOf(w, victim);
            if (toKiller == Attitude.Panic) return Reaction.Of(ReactionKind.Flee, killer.Id);
            if (w.Humanoid && ((toKiller == Attitude.Friendly && toVictim != Attitude.Friendly) ||
                               (toKiller == Attitude.Neutral && (toVictim == Attitude.Angry || toVictim == Attitude.Hostile))))
                return Reaction.Comment(killer.Id, victim.Id);
            bool friendOfVictim = toVictim == Attitude.Friendly;
            Reaction attack = Reaction.None;
            if (!w.Humanoid)
            {
                if (friendOfVictim) attack = Reaction.Attack(killer.Id, AttackReason.Murder);
            }
            else if (victim.Humanoid && friendOfVictim) attack = Reaction.Attack(killer.Id, AttackReason.Murder);
            else if (!victim.Humanoid && (friendOfVictim || _crimes.IsLivestock(victim, w))) attack = Reaction.Attack(killer.Id, AttackReason.Livestock);
            else if (w.Enclave != null && w.Enclave == victim.Enclave && _attitudes.LoyalToEnclave(w)) attack = Reaction.Attack(killer.Id, AttackReason.StopFight);

            if (attack.Kind == ReactionKind.Attack && !w.CannotFight) return attack;
            if (w.CannotFight || AttitudeOf(w, killer) == Attitude.Panic) return Reaction.Of(ReactionKind.Flee, killer.Id);
            return Reaction.None;
        }

        Reaction CallHelp(AttackReason reason, in AttitudeProfile w, in AttitudeProfile attacker, in AttitudeProfile victim)
        {
            if (!w.Guard || GroupOf(reason) == JoinGroup.Theft) return Reaction.None;
            bool friendOfVictim = AttitudeOf(w, victim) == Attitude.Friendly;
            bool friendOfAttacker = AttitudeOf(w, attacker) == Attitude.Friendly;
            return friendOfVictim && !friendOfAttacker ? Join(w, attacker.Id, reason) : Reaction.None;
        }

        Reaction Theft(in PerceptionBroadcast b, in AttitudeProfile w, in AttitudeProfile thief, float distanceToWorkplace)
        {
            if (!thief.IsPlayer) return Reaction.None;
            bool interact = b.Kind == BroadcastKind.UseInteractObject;
            if (!_crimes.Bothers(w, b.Item, interact, b.Zone, distanceToWorkplace)) return Reaction.None;
            if (w.CannotFight) return Reaction.Of(ReactionKind.Flee, thief.Id);
            return Reaction.Attack(thief.Id, interact ? AttackReason.UseInteractObject : AttackReason.Theft);
        }

        Attitude AttitudeOf(in AttitudeProfile self, in AttitudeProfile other) =>
            other.Id.IsEmpty ? Attitude.Neutral : _attitudes.Of(self, other);
    }
}
