using System;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Whether the straight line between two points is free (the Unity side raycasts against the world). Poses are
    /// feet positions; the implementation adds eye heights.
    /// </summary>
    public interface ILineOfSight
    {
        bool IsClear(WorldPose from, WorldPose to);
    }

    /// <summary>
    /// A line of sight that knows the bodies' sizes (fix-204): from the observer's eyes, <paramref name="eyeHeight"/> m above
    /// its feet, to any of the target's sample points (head, chest) below its top, <paramref name="targetTop"/> m above its feet.
    /// </summary>
    public interface ISightLines : ILineOfSight
    {
        bool IsClear(WorldPose from, float eyeHeight, WorldPose to, float targetTop);
    }

    /// <summary>Line-of-sight helpers (fix-204).</summary>
    public static class SightLines
    {
        /// <summary>A human's eyes above his feet, m (G3 humanoids; only they needed sight there).</summary>
        public const float HumanEyeHeight = 1.7f;
        /// <summary>A human's top above his feet, m.</summary>
        public const float HumanTop = 1.8f;

        /// <summary>The sized query when the implementation supports it, else the humanoid default.</summary>
        public static bool IsClear(ILineOfSight lineOfSight, WorldPose from, float eyeHeight, WorldPose to, float targetTop)
        {
            var los = lineOfSight ?? OpenLineOfSight.Instance;
            if (los is ISightLines sized)
                return sized.IsClear(from, eyeHeight > 0 ? eyeHeight : HumanEyeHeight, to, targetTop > 0 ? targetTop : HumanTop);
            return los.IsClear(from, to);
        }
    }

    /// <summary>Every line is free (tests, and the core until the Unity side supplies raycasts).</summary>
    public sealed class OpenLineOfSight : ILineOfSight
    {
        public static readonly OpenLineOfSight Instance = new OpenLineOfSight();
        public bool IsClear(WorldPose from, WorldPose to) => true;
    }

    /// <summary>Someone who might be noticed, as the caller sees it this frame.</summary>
    public struct SenseTarget
    {
        public ActorId Id;
        public WorldPose Pose;
        /// <summary>Walking or running (standing still makes no sound).</summary>
        public bool Moving;
        /// <summary>The player sneaks (silent; animals do not notice beyond 1000).</summary>
        public bool Sneaking;
        /// <summary>In parade (blocking) stance: silent like sneaking.</summary>
        public bool Parade;
        /// <summary>Aiming a ranged weapon with the second sneak perk: animals do not notice beyond 1000.</summary>
        public bool HiddenFromAnimals;
        /// <summary>Never perceived: invisible, ghost, in mist, swimming or diving.</summary>
        public bool Undetectable;
        /// <summary>The indoor AI zone the target stands in; null = outdoors.</summary>
        public string IndoorZone;
        /// <summary>Top of the body above the feet, m (0 = a human's); the sight rays aim below it (fix-204).</summary>
        public float Height;
    }

    /// <summary>The NPC that looks and listens.</summary>
    public struct SenseObserver
    {
        public ActorId Id;
        public WorldPose Pose;
        /// <summary>Humans and orcs need to see (field of view and line of sight) or hear; other creatures just sense.</summary>
        public bool Humanoid;
        /// <summary>A misted observer perceives nothing.</summary>
        public bool Blind;
        public string IndoorZone;
        /// <summary>Eyes above the feet, m (0 = a human's); a wolf looks from knee height (fix-204).</summary>
        public float EyeHeight;
    }

    public enum Sense { None, Seen, Heard, Sensed }

    /// <summary>
    /// G3's sense check as pure geometry (perception.json senses; Docs/perception-crime.md §2.1). Distances from the
    /// rules are centimetres; poses are metres, yaw in degrees (0 = +Z, as <see cref="WorldPose.YawTowards"/>).
    /// </summary>
    public static class Senses
    {
        /// <summary>Distance in centimetres.</summary>
        public static float Distance(WorldPose a, WorldPose b) => a.DistanceTo(b) * PerceptionRules.CentimetresPerUnit;

        public static float HeightDifference(WorldPose a, WorldPose b) => Math.Abs(a.Y - b.Y) * PerceptionRules.CentimetresPerUnit;

        /// <summary>
        /// The target lies within the observer's horizontal field of view (<paramref name="fieldOfView"/> = full angle in
        /// degrees). A target at the same spot counts as seen.
        /// </summary>
        public static bool InFieldOfView(WorldPose observer, WorldPose target, float fieldOfView)
        {
            float dx = target.X - observer.X, dz = target.Z - observer.Z;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length < 1e-4f) return true;
            double yaw = observer.Yaw * Math.PI / 180.0;
            double cos = (Math.Sin(yaw) * dx + Math.Cos(yaw) * dz) / length;
            return cos >= Math.Cos(fieldOfView * 0.5 * Math.PI / 180.0) - 1e-6;
        }

        /// <summary>
        /// Whether <paramref name="observer"/> notices <paramref name="target"/> within <paramref name="range"/> (cm; 3D distance,
        /// the height difference at most <see cref="PerceptionRules.MaxHeightDifference"/>).
        /// Humanoids see a target in their field of view with a free line of sight (or in the same indoor zone), and
        /// hear a moving, non-sneaking, non-blocking one within the hearing range. G3's other creatures sense everything in
        /// range, except a hidden player beyond the sneak range.
        /// <para>Port (fix-204, owner: wolves noticed the hero through rock walls and from below cliffs): a creature senses
        /// without sight only within <see cref="PerceptionRules.CreatureSenseRange"/> (smell, hearing); farther it needs a free
        /// line of sight. Hearing through a wall or the ground carries only <see cref="PerceptionRules.OccludedHearingFactor"/>
        /// of the hearing range. G3 values: CreatureSenseRange ≥ every slot range, OccludedHearingFactor 1.</para>
        /// </summary>
        public static Sense Perceive(in SenseObserver observer, in SenseTarget target, float range, PerceptionRules rules,
                                     ILineOfSight lineOfSight)
        {
            if (observer.Blind || target.Undetectable) return Sense.None;
            if (HeightDifference(observer.Pose, target.Pose) > rules.MaxHeightDifference) return Sense.None;
            float distance = Distance(observer.Pose, target.Pose);
            if (distance > range) return Sense.None;

            if (!observer.Humanoid)
            {
                bool hidden = target.Sneaking || target.Parade || target.HiddenFromAnimals;
                if (hidden && distance > rules.SneakVsAnimalsRange) return Sense.None;
                if (distance <= rules.CreatureSenseRange) return Sense.Sensed;
                return Clear(observer, target, lineOfSight) ? Sense.Sensed : Sense.None;
            }

            bool sameZone = SameIndoorZone(observer.IndoorZone, target.IndoorZone);
            bool? clear = null;
            if (InFieldOfView(observer.Pose, target.Pose, rules.FieldOfView))
            {
                if (sameZone) return Sense.Seen;
                clear = Clear(observer, target, lineOfSight);
                if (clear == true) return Sense.Seen;
            }
            if (distance > rules.HearingRange || !target.Moving || target.Sneaking || target.Parade) return Sense.None;
            if (sameZone || distance <= rules.HearingRange * rules.OccludedHearingFactor) return Sense.Heard;
            return (clear ?? Clear(observer, target, lineOfSight)) ? Sense.Heard : Sense.None;
        }

        /// <summary>The sight line from the observer's eyes to the target's body (sized when the implementation can).</summary>
        public static bool Clear(in SenseObserver observer, in SenseTarget target, ILineOfSight lineOfSight) =>
            SightLines.IsClear(lineOfSight, observer.Pose, observer.EyeHeight, target.Pose, target.Height);

        /// <summary>
        /// G3 OnSleep (Routine_Callback OnSleep.c): a human sleeper wakes when the player, whom it is not friendly to
        /// (checked by the caller), moves within 1000 with a free line of sight, neither sneaking nor in the parade stance,
        /// and: a sleeper in an indoor zone only for a player in that zone (no other limit); outdoors (or without a zone)
        /// only within 500. Then ZS_ReactToFootsteps ("YouDisturbedMySlumber", stand up, ZS_ObserveSuspect).
        /// </summary>
        public static bool WakesSleeper(in SenseObserver sleeper, in SenseTarget target, PerceptionRules rules, ILineOfSight lineOfSight = null)
        {
            if (target.Undetectable || !target.Moving || target.Sneaking || target.Parade) return false;
            if (HeightDifference(sleeper.Pose, target.Pose) > rules.MaxHeightDifference) return false;
            float distance = Distance(sleeper.Pose, target.Pose);
            if (distance > rules.WakeRangeIndoor) return false;
            if (sleeper.IndoorZone != null) { if (!SameIndoorZone(sleeper.IndoorZone, target.IndoorZone)) return false; }
            else if (distance > rules.WakeRangeOutdoor) return false;
            return Clear(sleeper, target, lineOfSight);
        }

        /// <summary>
        /// G3 OnAnimalSleep (ZS_AnimalSleep's callback; CP's ZS_Ambient_Sleep has none): a sleeping animal wakes for the
        /// player within 1000, within 500 while he sneaks or blocks; with Perk_Sneak_2 never while he sneaks, blocks or is in a
        /// ranged state, else within 1000. No sight, no movement test. The caller checks the attitude (not Friendly, not
        /// Neutral) and attacks (AssessTarget Angry).
        /// <para>Port (fix-204): not across more than <see cref="PerceptionRules.MaxHeightDifference"/>, and beyond the
        /// occluded hearing share of the range only with a free line of sight (a wolf asleep below a cliff).</para>
        /// </summary>
        public static bool WakesAnimal(WorldPose sleeper, in SenseTarget player, bool sneakPerk2, bool rangedState, PerceptionRules rules,
                                       ILineOfSight lineOfSight = null, float eyeHeight = 0)
        {
            if (player.Undetectable) return false;
            bool hidden = player.Sneaking || player.Parade;
            float range = rules.WakeRangeIndoor;
            if (sneakPerk2)
            {
                if (hidden || rangedState) return false;
            }
            else if (hidden) range = rules.WakeRangeOutdoor;
            if (HeightDifference(sleeper, player.Pose) > rules.MaxHeightDifference) return false;
            float distance = Distance(sleeper, player.Pose);
            if (distance > range) return false;
            if (distance <= range * rules.OccludedHearingFactor) return true;
            return SightLines.IsClear(lineOfSight, sleeper, eyeHeight, player.Pose, player.Height);
        }

        public static bool SameIndoorZone(string a, string b) => a != null && string.Equals(a, b, StringComparison.Ordinal);
    }
}
