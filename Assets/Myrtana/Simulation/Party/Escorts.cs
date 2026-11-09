using System;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>How an NPC escorts the hero instead of following its routine (Docs/party.md §3, §4).</summary>
    public enum EscortMode
    {
        None,
        /// <summary>A party member that follows the hero (G3 ZS_FollowPlayer).</summary>
        Follow,
        /// <summary>A party member told to wait, or left behind by a teleport (G3 ZS_WaitForPlayer): stands where it is.</summary>
        Wait,
        /// <summary>Leads the hero to a place on a FollowNpc quest (G3 ZS_GuidePlayer).</summary>
        Guide,
    }

    /// <summary>How fast an escort moves (G3 gEWalkMode without Sneak, plus standing still).</summary>
    public enum EscortGait { Stand, Walk, Run, Sprint }

    /// <summary>
    /// The party as the NPC simulation sees it: who escorts the hero in which way, how close a follower keeps, where a
    /// guide goes and where the hero is. The session implements it over the party, the named places and the hero's pose.
    /// </summary>
    public interface IEscorts
    {
        EscortMode ModeOf(ActorId npc);
        /// <summary>The follow distance in metres (G3 (slot + 3) × 1.5 m).</summary>
        float FollowDistance(ActorId npc);
        /// <summary>Where a guide leads the hero; false when the destination is unknown.</summary>
        bool TryGetDestination(ActorId guide, out WorldPose destination);
        /// <summary>The hero's pose; false before the presentation reported it.</summary>
        bool TryGetLeader(out WorldPose leader);
    }

    /// <summary>
    /// A follower's movement decisions (G3 ZS_FollowPlayer_Loop / OnFollowPlayer; Docs/party.md §3). Once per second the
    /// follower checks whether it is farther from the hero than its follow distance and then goes to him; it stops as
    /// soon as it is within that distance (OnFollowPlayer StopAIGoto). Speed: the hero's gait, sprinting when far behind
    /// (CP "companions sprint when far behind") <b>[interp: G3's _AI_Goto_Ext arguments were not decoded; threshold
    /// 15 m]</b>. A follower that is stuck or more than <see cref="CatchUpMetres"/> away is put next to the hero
    /// <b>[interp: as the walker puts a stuck routine NPC on its point]</b>.
    /// </summary>
    public sealed class FollowPlanner
    {
        public const float RecheckSeconds = 1f;
        public const float SprintBehindMetres = 15f;
        public const float CatchUpMetres = 50f;

        float _sinceCheck = RecheckSeconds;
        float _sinceSwimCheck;

        /// <summary>The follower is on its way to the hero.</summary>
        public bool Going { get; private set; }
        /// <summary>
        /// Waiting because the hero swims (OnFollowPlayer → ZS_WaitForPlayer without the Waiting flag); ends at the first
        /// one-second check after he left the water (ZS_WaitForPlayer_Loop).
        /// </summary>
        public bool WaitingForSwimmer { get; private set; }

        public void Reset()
        {
            _sinceCheck = RecheckSeconds;
            Going = false;
            WaitingForSwimmer = false;
        }

        /// <param name="distance">Metres from the follower to the hero.</param>
        /// <param name="followDistance">The follower's follow distance, metres.</param>
        /// <param name="leaderGait">How the hero moves now.</param>
        /// <param name="leaderSwimming">The hero swims: the follower waits on the shore.</param>
        /// <returns>How the follower moves this frame (Stand = stays).</returns>
        public EscortGait Update(float dt, float distance, float followDistance, EscortGait leaderGait, bool leaderSwimming = false)
        {
            if (leaderSwimming && !WaitingForSwimmer)
            {
                WaitingForSwimmer = true;
                _sinceSwimCheck = 0;
            }
            if (WaitingForSwimmer)
            {
                Going = false;
                _sinceSwimCheck += dt;
                if (_sinceSwimCheck < RecheckSeconds) return EscortGait.Stand;
                _sinceSwimCheck = 0;
                if (leaderSwimming) return EscortGait.Stand;
                WaitingForSwimmer = false;
                _sinceCheck = RecheckSeconds;
            }
            _sinceCheck += dt;
            if (distance <= followDistance) Going = false;
            else if (_sinceCheck >= RecheckSeconds)
            {
                _sinceCheck = 0;
                Going = true;
            }
            if (!Going) return EscortGait.Stand;
            if (distance > SprintBehindMetres) return EscortGait.Sprint;
            return leaderGait == EscortGait.Stand ? EscortGait.Run : leaderGait;
        }

        /// <summary>Too far to walk: put the follower next to the hero.</summary>
        public static bool NeedsCatchUp(float distance) => distance > CatchUpMetres;
    }

    /// <summary>
    /// A guide's decisions (G3 ZS_GuidePlayer / OnGuidePlayer / ZS_GuidePlayer_Wait, CP 1.75; Docs/party.md §4). A walking
    /// guide (OnGuidePlayer) waits when the hero is more than 10 m away, unless (CP) the hero is in its view and at least
    /// as close as the guide to the point it walks to now (GetCurrentDestinationPoint: the current path corner). A waiting
    /// guide (ZS_GuidePlayer_Wait) goes on only once the hero is within 8 m — no view test there, so the 10 m / 8 m
    /// hysteresis holds (editor-runner-21: the mirrored view test made a guide that turned to face the hero walk at once,
    /// turn away, wait again: a walk/wait flip every frame). It also goes on when the hero already stands at the
    /// destination <b>[interp: party-1 verify, a hero who ran past the waiting guide left it standing short of the
    /// destination]</b>. At the destination it faces the hero and waits. Gait: the hero's walk or run <b>[interp]</b>.
    /// </summary>
    public sealed class GuidePlanner
    {
        public const float FallBehindMetres = 10f;
        public const float ResumeMetres = 8f;
        public const float ArriveMetres = 1f;

        public bool Waiting { get; private set; }

        public void Reset() => Waiting = false;

        /// <param name="toHero">Metres from the guide to the hero.</param>
        /// <param name="heroInView">The hero is within the guide's field of view.</param>
        /// <param name="heroToPoint">Metres from the hero to the point the guide walks to now (its current path corner).</param>
        /// <param name="guideToPoint">Metres from the guide to that point.</param>
        /// <param name="heroToDestination">Metres from the hero to the destination.</param>
        /// <param name="guideToDestination">Metres from the guide to the destination.</param>
        public EscortGait Update(float toHero, bool heroInView, float heroToPoint, float guideToPoint,
                                 float heroToDestination, float guideToDestination, EscortGait heroGait)
        {
            if (guideToDestination <= ArriveMetres)
            {
                Waiting = true;
                return EscortGait.Stand;
            }
            if (Waiting)
            {
                if (toHero > ResumeMetres && heroToDestination > ResumeMetres) return EscortGait.Stand;
                Waiting = false;
            }
            else if (toHero > FallBehindMetres && !(heroInView && heroToPoint <= guideToPoint))
            {
                Waiting = true;
                return EscortGait.Stand;
            }
            return heroGait == EscortGait.Run || heroGait == EscortGait.Sprint ? EscortGait.Run : EscortGait.Walk;
        }
    }

    /// <summary>
    /// How far the hero's escorts may stray in a fight (Docs/party.md §6) <b>[interp: G3's follower keeps its fights near
    /// the leader through ZS_FollowPlayer's perception range and the chase limits; the exact leash was not decoded]</b>.
    /// A following member or a guide whose fight (chase or flight) took it more than <see cref="LeashMetres"/> from the hero
    /// gives the fight up and comes back (party-1 verify: a freed slave chased an orc scout 1.7 km away and never returned).
    /// </summary>
    public static class PartyLeash
    {
        /// <summary>A fighting escort farther than this from the hero breaks off, m.</summary>
        public const float LeashMetres = 30f;

        /// <summary>
        /// A fighting escort <paramref name="toHero"/> metres from the hero gives up its fight: following members and guides
        /// only (a waiting member defends its spot); unknown distances never break a fight off.
        /// </summary>
        public static bool BreaksOff(EscortMode mode, bool fighting, float toHero) =>
            fighting && (mode == EscortMode.Follow || mode == EscortMode.Guide) && toHero > LeashMetres && toHero < float.MaxValue;

        /// <summary>
        /// The leash with G3's own rule first (fix-37): an escort whose enemy attacks it (the enemy's fight targets it) fights
        /// back wherever it is (G3 OnGuidePlayer / OnFollowPlayer: AssessActivePerception or AssessAgressor -> ZS_Attack;
        /// G3 has no leash to the hero; the fight ends when the enemy is down or flees: chase time, ZS_Attack_WaitForFlee).
        /// Only a chase (the enemy fights someone else, or nobody) is broken off beyond <see cref="LeashMetres"/>. Breaking
        /// off a defence made a loop (r27: Mort 60 break-offs in a row, each undone by the next hit, ReactToDamage, while the
        /// goblins drew him 80 m off; Rufus 24 vs wolves).
        /// </summary>
        public static bool BreaksOff(EscortMode mode, bool fighting, float toHero, bool attacked) =>
            !attacked && BreaksOff(mode, fighting, toHero);

        /// <summary>No hit either way for this long ends an escort's fight, s <b>[interp]</b>.</summary>
        public const float StalemateSeconds = 15f;

        /// <summary>
        /// A following member or a guide whose fight has gone <paramref name="secondsWithoutHit"/> s without a hit landed or
        /// taken gives it up <b>[interp: G3 reaches its targets on its own way net; ours can stand an escort below a ledge or
        /// at a bush it cannot get round: r27 Hauke vs a sabertooth 2 m below for 4 min, the escort timed out 100 m short]</b>.
        /// </summary>
        public static bool GivesUpStalemate(EscortMode mode, float secondsWithoutHit) =>
            (mode == EscortMode.Follow || mode == EscortMode.Guide) && secondsWithoutHit >= StalemateSeconds;

        /// <summary>
        /// A following member or a guide whose chase ran out (the enemy fled or stays out of reach: ZS_Attack_WaitForFlee)
        /// gives the fight up and goes back to following or guiding, instead of waiting there for the enemy
        /// <b>[interp]</b> (editor-runner-18: Gorn fought a scavenger for over 3 min and never resumed guiding).
        /// </summary>
        public static bool GivesUpChase(EscortMode mode, bool chaseRanOut) =>
            chaseRanOut && (mode == EscortMode.Follow || mode == EscortMode.Guide);

        /// <summary>
        /// Whether a scout of the hero's side runs a look-out of its own. Only the hero's summoned creatures do, and they pick
        /// nothing but an NPC fighting the hero (slot 1). CP AssessActivePerception (0x100295f0) skips the hostile step for
        /// every NPC whose party leader is the player unless its member type is Summoned (then it attacks the slot-1
        /// attacker of the player, reason Enemy); besides, an NPC in AIMode PlayerParty (ZS_FollowPlayer,
        /// ZS_WaitForPlayer) senses no one but the player (CP sense test 0x10008bd0, old 0x1000d760). So companions,
        /// mobs, slaves and the guide never attack on sight: they fight what attacks them (AssessAgressor), what they hear
        /// fight the hero (AssessFightSound), the hero's opponent in a defeat, and the hero's focus on command.
        /// fix-41 dropped the guide's look-out; fix-106 the companions' (Cyrus, an Orc-aligned mercenary of Kap Dun, attacked
        /// the rebel Iomar on sight on the way to Ortega).
        /// </summary>
        public static bool LooksOut(PartyMemberType type, bool guiding) => type == PartyMemberType.Summoned && !guiding;

        /// <summary>Seconds after a break-off before the look-out may pick an enemy again.</summary>
        public const float LookoutCooldownSeconds = 5f;
        /// <summary>A member that broke off looks out again only once it is back this close to the hero, m.</summary>
        public const float LookoutResumeMetres = 10f;

        /// <summary>
        /// After a leash break-off the escort first returns to the hero: its look-out stays off for
        /// <see cref="LookoutCooldownSeconds"/> and until it is within <see cref="LookoutResumeMetres"/> of him, so it does not
        /// turn round at once and chase the same enemy again (G3 ZS_FollowPlayer goes to the leader before
        /// AssessActivePerception picks a target again) <b>[interp: thresholds]</b>.
        /// </summary>
        public static bool LookoutResumes(float secondsSinceBreakOff, float toHero) =>
            secondsSinceBreakOff >= LookoutCooldownSeconds && toHero <= LookoutResumeMetres;
    }

    /// <summary>Off-screen escorts (Near and Abstract NPCs; Docs/party.md §3) <b>[interp]</b>.</summary>
    public static class AbstractEscort
    {
        /// <summary>Speed of a Near follower catching up, m/s (the human sprint clip).</summary>
        public const float CatchUpSpeed = 6f;

        /// <summary>
        /// Where an off-screen follower is after <paramref name="realSeconds"/>: an Abstract one is put at its follow
        /// distance behind the hero at once; a Near one (visible far away) runs towards him at <see cref="CatchUpSpeed"/>.
        /// </summary>
        public static WorldPose Follow(WorldPose self, WorldPose leader, float followDistance, DetailLevel detail, float realSeconds)
        {
            float d = self.HorizontalDistanceTo(leader);
            if (d <= followDistance) return self;
            if (detail == DetailLevel.Abstract) return Behind(leader, self, followDistance);
            float step = Math.Min(d - followDistance, CatchUpSpeed * Math.Max(0f, realSeconds));
            float t = step / d;
            return new WorldPose(self.X + (leader.X - self.X) * t, self.Y + (leader.Y - self.Y) * t, self.Z + (leader.Z - self.Z) * t,
                                 self.YawTowards(leader));
        }

        /// <summary>The point <paramref name="distance"/> from the hero on the side the follower comes from, facing him.</summary>
        public static WorldPose Behind(WorldPose leader, WorldPose from, float distance)
        {
            float dx = from.X - leader.X, dz = from.Z - leader.Z;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-3f)
            {
                // Straight behind the hero's heading.
                double yaw = leader.Yaw * Math.PI / 180.0;
                dx = -(float)Math.Sin(yaw);
                dz = -(float)Math.Cos(yaw);
                len = 1;
            }
            var p = new WorldPose(leader.X + dx / len * distance, leader.Y, leader.Z + dz / len * distance, 0);
            return p.WithYaw(p.YawTowards(leader));
        }
    }
}
