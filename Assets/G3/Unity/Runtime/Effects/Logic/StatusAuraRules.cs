namespace G3.Effects
{
    /// <summary>Body size class G3's scripts pick status effects by (IsHumanoid / IsAmbientCreature / IsLarge).</summary>
    public enum AuraBody { HumanoidOrSmall, Medium, Large }

    /// <summary>
    /// The G3 effects of the burning and frozen states (Script_Game CP 1.75, read for the rules only; Docs/formats-effects.md §8):
    /// * Burning: AssessHit starts the runtime effect <c>eff_magic_firespell_target_01</c> when CanBurn passes (flames on
    ///   both legs, both arms, spine and head bones + a small fire light); OnTick stops it when the burning time is over or
    ///   the body is more than 75 cm under water, and on knock-out / death.
    /// * Frozen: ZS_Freeze starts <c>eff_magic_icespell_target_01</c> (humanoids and ambient creatures), <c>_03</c> (large
    ///   creatures) or <c>_02</c> (everyone else) and holds it; ZS_Freeze_Loop stops it when the time is up and plays the
    ///   matching burst <c>eff_magic_icespell_burst_0N</c>.
    /// * Poisoned: no effect while it lasts (MagicPoison and AssessHit only set the status; the expiry only stops effects).
    /// </summary>
    public static class StatusAuraRules
    {
        public const string BurningEffect = "eff_magic_firespell_target_01";

        public static string FrozenEffect(AuraBody body) => "eff_magic_icespell_target_0" + Index(body);

        public static string ThawBurst(AuraBody body) => "eff_magic_icespell_burst_0" + Index(body);

        /// <summary>G3 order: humanoid or ambient creature → 1, else large → 3, else 2.</summary>
        public static AuraBody BodyOf(bool humanoid, bool ambientCreature, bool large) =>
            humanoid || ambientCreature ? AuraBody.HumanoidOrSmall : large ? AuraBody.Large : AuraBody.Medium;

        static int Index(AuraBody body) => body == AuraBody.HumanoidOrSmall ? 1 : body == AuraBody.Large ? 3 : 2;
    }

    /// <summary>
    /// Effects of item entities (gCEffect_PS + eCPointLight_PS on the item template; Docs/formats-effects.md §8): G3 shows
    /// them whenever the item exists as an entity — in a hand, on a carry slot, nocked or flying, lying in the world.
    /// Units: offsets in cm of the item frame (= the hand slot frame for held items).
    /// </summary>
    public static class ItemEffectRules
    {
        /// <summary>eEDynamicLightEffect [inferred order]: 0 Steady, 1 Flicker, 2 Pulse, 3 Blink, 4 Strobe.</summary>
        public enum LightEffect { Steady = 0, Flicker = 1, Pulse = 2, Blink = 3, Strobe = 4 }

        /// <summary>
        /// Light intensity factor at time <paramref name="t"/> s for an effect with phase/period (s): flicker/blink use a
        /// smooth pseudo-random wobble in [1 − depth, 1] [interp: G3's curve is not decoded], pulse a sine.
        /// </summary>
        public static float Factor(LightEffect fx, float t, float phase, float period, float depth = 0.25f)
        {
            if (period <= 0) period = 1f;
            float x = (t + phase) / period;
            switch (fx)
            {
                case LightEffect.Pulse: return 1f - depth * 0.5f * (1f - (float)System.Math.Cos(x * 2 * System.Math.PI));
                case LightEffect.Flicker:
                case LightEffect.Blink:
                case LightEffect.Strobe:
                {
                    double n = System.Math.Sin(x * 12.9898 * 2) * 0.5 + System.Math.Sin(x * 7.233 * 2 + 1.3) * 0.3 + System.Math.Sin(x * 31.1 + 0.7) * 0.2;
                    return 1f - depth * (float)(0.5 + 0.5 * n);
                }
                default: return 1f;
            }
        }
    }

    /// <summary>
    /// The world's fire lights (eCStaticPointLight_PS on torches, camp fires, candles, braziers, fireplaces; fire-fx).
    /// The data has no light effect for static lights; their colour is the fire palette — rgb(0.992, 0.769, 0.529) on
    /// torches/camp fires/bowls/fireplaces, rgb(1, 0.796, 0.533) on candles — while glow-worms (0.545, 0.875, 0.792), magic
    /// (0.271, 0.788, 2.16) and night/ambient lights (0.235, 0.235, 0.31) differ (G3Cli fx worldlights).
    /// </summary>
    public static class FireLightRules
    {
        /// <summary>Warm orange-yellow colour of a fire light: red ≈ 1, green 0.6–0.9, blue 0.3–0.7 of red.</summary>
        public static bool IsFireColor(float r, float g, float b)
            => r > 0.9f && r <= 1.05f && g >= 0.6f * r && g <= 0.9f * r && b >= 0.3f * r && b <= 0.7f * r && b < g;

        /// <summary>
        /// Intensity factor on the imported world-light intensity (2) for fire lights [interp]. G3 lit in gamma space with a
        /// soft falloff to the range; URP lights in linear space with 1/d². With range 5.5–7.5 m the G3 torch light is
        /// ≈ 0.7 (linear) at 2 m and ≈ 0.45 at 3 m, URP intensity 2 gives 0.48 / 0.18: the walls and the ground around a
        /// torch got a third to a half of G3's light. 2 (= intensity 4) matches at 2–4 m.
        /// </summary>
        public const float IntensityGain = 2f;

        /// <summary>Depth of the fire flicker (intensity between 1 − depth and 1) [interp: static lights carry no effect].</summary>
        public const float FlickerDepth = 0.18f;

        /// <summary>Flicker period (s) as the hero torch's eCPointLight_PS (EffectPeriod 2).</summary>
        public const float FlickerPeriod = 2f;

        /// <summary>Flicker factor at time <paramref name="t"/> for a light with its own phase (s) — the same wobble as the item lights.</summary>
        public static float Flicker(float t, float phase)
            => ItemEffectRules.Factor(ItemEffectRules.LightEffect.Flicker, t, phase, FlickerPeriod, FlickerDepth);
    }
}
