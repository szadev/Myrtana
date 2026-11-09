using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Dialog
{
    /// <summary>
    /// One bone of a G3 look-at chain (the actor's <c>.xact</c> look-at constraints, Docs/dialog-presentation.md §2):
    /// how far it may turn towards the goal — yaw is the twist about the bone's own axis (the neck runs along it), pitch
    /// the tilt towards that axis (up = positive) — and how fast it follows (<see cref="Speed"/>).
    /// </summary>
    public readonly struct HeadLookBone
    {
        public HeadLookBone(string suffix, float speed, float minYaw, float maxYaw, float minPitch, float maxPitch)
        {
            Suffix = suffix; Speed = speed; MinYaw = minYaw; MaxYaw = maxYaw; MinPitch = minPitch; MaxPitch = maxPitch;
        }

        /// <summary>Bone name after the rig prefix: <c>Head_Neck_1</c> … <c>Head_Head_1</c>.</summary>
        public string Suffix { get; }
        public float Speed { get; }
        /// <summary>Limits, radians.</summary>
        public float MinYaw { get; }
        public float MaxYaw { get; }
        public float MinPitch { get; }
        public float MaxPitch { get; }
    }

    /// <summary>
    /// G3's head look-at (EMotionFX look-at controller as G3 drives it; Docs/dialog-presentation.md §2): every bone of the
    /// chain (neck first) aims at the goal on its own, clamped to an ellipse around the middle of its limits; it follows
    /// at <c>min(1, 6·speed·dt)</c> per frame and, released, fades back to the animation at <c>2·speed</c> per second.
    /// The goal is the partner's <c>&lt;Rig&gt;_Head_Head_1</c> bone.
    /// </summary>
    public static class HeadLook
    {
        public const float FollowRate = 6f, FadeRate = 2f;
        public const string TargetBoneSuffix = "Head_Head_1";

        /// <summary>
        /// Look-at chains of the humanoid skeletons (<c>G3_&lt;Rig&gt;_Skeleton.xact</c>, CP 1.75 data: speed, yaw = x,
        /// pitch = y limits in radians). Rigs without a chain do not turn their heads.
        /// </summary>
        static readonly Dictionary<string, HeadLookBone[]> Chains = new Dictionary<string, HeadLookBone[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hero"] = Chain(1.0f, 0.9f, 0.8f, 0.7f, 0.017f, 0.087f, 0, 0),
            ["Slave"] = Chain(1.0f, 0.9f, 0.8f, 0.7f, 0.087f, 0.087f, 0, 0),
            ["Fat"] = Chain(0.2f, 0.3f, 0.4f, 0.6f, 0.087f, 0.087f, 0, 0),
            ["Orc"] = new[]
            {
                new HeadLookBone("Head_Neck_1", 1.0f, -0.087f, 0.087f, -1.745f, 0.873f),
                new HeadLookBone("Head_Neck_2", 0.9f, -0.087f, 0.087f, -1.745f, 1.047f),
                new HeadLookBone("Head_Neck_3", 0.8f, -0.087f, 0.087f, -1.745f, 1.222f),
                new HeadLookBone("Head_Head_1", 0.7f, -0.873f, 0.873f, -1.745f, 1.396f),
            },
        };

        // Hero, Slave and Fat share the shape: neck yaw ±n1 / ±n (no pitch), head yaw ±0.873, pitch ±0.349.
        static HeadLookBone[] Chain(float s1, float s2, float s3, float sHead, float neck1, float neck, float neckPitchMin, float neckPitchMax) => new[]
        {
            new HeadLookBone("Head_Neck_1", s1, -neck1, neck1, neckPitchMin, neckPitchMax),
            new HeadLookBone("Head_Neck_2", s2, -neck, neck, neckPitchMin, neckPitchMax),
            new HeadLookBone("Head_Neck_3", s3, -neck, neck, neckPitchMin, neckPitchMax),
            new HeadLookBone("Head_Head_1", sHead, -0.873f, 0.873f, -0.349f, 0.349f),
        };

        /// <summary>The look-at chain of a rig (neck first), or null.</summary>
        public static IReadOnlyList<HeadLookBone> ChainOf(string rig) =>
            rig != null && Chains.TryGetValue(rig, out var chain) ? chain : null;

        /// <summary>
        /// Clamps an aim (yaw, pitch; radians) into the bone's limits: an ellipse centred between min and max with the
        /// half ranges as radii (G3 keeps a goal beyond the limits at the edge; it does not let go). An axis without range
        /// stays at its centre.
        /// </summary>
        public static void Clamp(in HeadLookBone bone, ref float yaw, ref float pitch)
        {
            float cy = (bone.MinYaw + bone.MaxYaw) * 0.5f, hy = (bone.MaxYaw - bone.MinYaw) * 0.5f;
            float cp = (bone.MinPitch + bone.MaxPitch) * 0.5f, hp = (bone.MaxPitch - bone.MinPitch) * 0.5f;
            if (hy <= 1e-6f) yaw = cy;
            if (hp <= 1e-6f) pitch = cp;
            if (hy <= 1e-6f && hp <= 1e-6f) return;
            float u = hy > 1e-6f ? (yaw - cy) / hy : 0, v = hp > 1e-6f ? (pitch - cp) / hp : 0;
            float r2 = u * u + v * v;
            if (r2 <= 1) return;
            float k = 1 / (float)Math.Sqrt(r2);
            if (hy > 1e-6f) yaw = cy + u * k * hy;
            if (hp > 1e-6f) pitch = cp + v * k * hp;
        }

        /// <summary>Fraction of the way to the goal a bone moves this frame.</summary>
        public static float FollowStep(float speed, float dt) => Math.Min(1f, Math.Max(0f, FollowRate * speed * dt));

        /// <summary>The look-at weight after a frame of fading out (1 = aiming, 0 = the animation alone).</summary>
        public static float FadeOut(float weight, float speed, float dt) => Math.Max(0f, weight - FadeRate * speed * dt);

        /// <summary>
        /// G3 ZS_Talk: an NPC sitting (ground, stool, bench, throne) that sees the hero stays seated and only turns its
        /// head to him; everyone else stands up and turns the body (no head look-at). The hero never turns his head.
        /// </summary>
        public static bool StaysSeated(bool sitting, bool heroInView) => sitting && heroInView;

        /// <summary>CP 1.75 ZS_Talk: bisons, pigs and cows do not turn to face the hero (species of the NPC).</summary>
        public static bool TurnsToFace(string species) =>
            !string.Equals(species, "Bison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(species, "Pig", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(species, "Cow", StringComparison.OrdinalIgnoreCase);
    }
}
