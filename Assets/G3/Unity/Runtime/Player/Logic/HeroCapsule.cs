using System;

namespace G3.Player
{
    /// <summary>
    /// fix-148: the hero's movement capsule, sized as G3's (PC_Hero <c>eCCollisionShape_PS</c>, shape group 6 — the
    /// character controller — : capsule r 33.2 cm, from 3 cm below the feet to 196.9 cm; <c>G3Cli items tpl PC_Hero</c>).
    /// The CharacterController keeps <see cref="Skin"/> (its contact offset) between its capsule and the world, so the
    /// radius the world feels is <see cref="Radius"/> + <see cref="Skin"/>.
    /// Our earlier capsule (r 0.35 + 0.04 skin) was 12 cm wider than G3's (owner, Demo_2026-10-19: "по некоторым ступеням
    /// не удается идти": the steep open stairs of Gotha's great tower; the step probe part of that fix is in
    /// <see cref="SlopeRules.NearLanding"/>).
    /// </summary>
    public static class HeroCapsule
    {
        /// <summary>CharacterController height, m (top 1.95 m + skin; G3 196.9 cm).</summary>
        public const float Height = 1.95f;
        /// <summary>CharacterController radius, m.</summary>
        public const float Radius = 0.29f;
        /// <summary>CharacterController skin width (contact offset), m.</summary>
        public const float Skin = 0.04f;
        /// <summary>G3's hero movement capsule radius, m (PC_Hero shape group 6).</summary>
        public const float G3Radius = 0.332f;
        /// <summary>G3's hero movement capsule top above the feet, m.</summary>
        public const float G3Top = 1.969f;

        /// <summary>The radius the world feels (capsule + skin), m.</summary>
        public static float EffectiveRadius => Radius + Skin;
        /// <summary>The height the world feels above the feet, m.</summary>
        public static float EffectiveTop => Height + Skin;

        /// <summary>Whether he walks upright under something <paramref name="clearance"/> m above the floor.</summary>
        public static bool FitsUnder(float clearance) => EffectiveTop <= clearance;
    }
}
