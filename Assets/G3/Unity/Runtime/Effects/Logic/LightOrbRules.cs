using System;

namespace G3.Effects
{
    /// <summary>
    /// How one part of a started effect is drawn, on top of its imported template (G3EffectPlayer.Start with a look): sprite
    /// size and start alpha factors, particles kept in the part's own frame (they move with it), a gain on its point light
    /// and whether that light casts shadows. <see cref="Default"/> = the template as imported. Pooled parts get the default
    /// back when released, so a template shared by several effects is only changed where the look is asked for.
    /// </summary>
    public readonly struct EffectPartLook : IEquatable<EffectPartLook>
    {
        public readonly float Size, Alpha, LightGain;
        public readonly bool Local, Shadows;

        public EffectPartLook(float size = 1f, float alpha = 1f, float lightGain = 1f, bool local = false, bool shadows = false)
        {
            Size = size; Alpha = alpha; LightGain = lightGain; Local = local; Shadows = shadows;
        }

        public static EffectPartLook Default => new EffectPartLook(1f, 1f, 1f, false, false);
        public bool IsDefault => Equals(Default);

        public bool Equals(EffectPartLook o) => Size == o.Size && Alpha == o.Alpha && LightGain == o.LightGain && Local == o.Local && Shadows == o.Shadows;
        public override bool Equals(object obj) => obj is EffectPartLook o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Size, Alpha, LightGain, Local, Shadows);
        public override string ToString() => $"size x{Size:0.###} alpha x{Alpha:0.###} light x{LightGain:0.##}{(Local ? " local" : "")}{(Shadows ? " shadows" : "")}";
    }

    /// <summary>
    /// fix-196 (owner, build 23a, cave: "Light of Innos" is a huge blinding white-violet blob over the hero's upper body and
    /// half the screen while the cave stays dark): Spl_Light's light ball <see cref="Effect"/> as a small bright orb above
    /// the hero's head with a point light that lights the area.
    /// <para>g3.efm spawns three templates (G3Cli fx effect / fx parse):
    /// <see cref="GlowTemplate"/> BigGlow halo, Brighten, 2-8 m, no depth test, cyan → white (a 0.2) → magenta, ~3.5 live;
    /// <see cref="LightTemplate"/> point light rgb (0.94, 0.98, 1), range 10 m, intensity 2.5, CastShadows;
    /// <see cref="SphereTemplate"/> 50 Brighten sprites of 1-1.5 m (size 0 → 1, alpha 0 → 0.5 → 0, 5 s, 10/s) on one point,
    /// world space. G3 summed them in an 8-bit gamma frame that clamps at white; in the HDR frame the 50 stacked sprites
    /// reach ~19× white at the core (bloom spreads it over half the view), the world-space sprites trail metres behind a
    /// running hero (between him and the camera), and a blob over more than ~5 % of a dark view enters the remaster's
    /// auto-exposure metering (WorldLightNew: 40-95 % of the histogram) and darkens the cave further.</para>
    /// <para>[owner]/[interp] look: the orb's core at <see cref="CorePeak"/> × white, sprites at <see cref="SphereSize"/>
    /// of their size, the halo at <see cref="GlowSize"/> and <see cref="GlowAlpha"/>, both in the orb's own frame (no trail),
    /// placed <see cref="OffsetUp"/> m straight up from the caster's root (above the head, out of the camera
    /// line), and the light at <see cref="LightGain"/> × the effect-light intensity (range and colour as G3). The look is per
    /// effect: the same templates in other effects (the staff glow, frost wave …) keep their import.</para>
    /// </summary>
    public static class LightOrbRules
    {
        public const string Effect = "eff_magic_light_source_01";
        public const string GlowTemplate = "VFX_Magic_Light_Glow_01";
        public const string SphereTemplate = "VFX_Magic_Light_Sphere_01";
        public const string LightTemplate = "VFX_Magic_LS_Light_01";

        /// <summary>Orb position in the caster's frame (m from the root at the feet: right, up, forward) [owner: centred above the head, higher].</summary>
        public const float OffsetRight = 0f, OffsetUp = 2.8f, OffsetForward = 0f;

        /// <summary>Sprite size factors (sphere 1-1.5 m → ~0.45-0.7 m at the end of life; halo 2-8 m → 0.5-2 m).</summary>
        public const float SphereSize = 0.45f, GlowSize = 0.25f;
        /// <summary>HDR brightness (× white) of the stacked sphere sprites at the orb's centre.</summary>
        public const float CorePeak = 5f;
        /// <summary>Start alpha factor of the halo (over its fix-124 import factor).</summary>
        public const float GlowAlpha = 0.5f;
        /// <summary>
        /// Light intensity factor over the imported effect light (2.5 × EmitterRules.EffectLightScale = 4): URP's 1/d² from
        /// 2.3 m up left the floor a few metres away dark where G3's soft falloff lit the whole 10 m; 2.25 → 9 (in-editor
        /// cave shots fix-196: 1.75 / core 3 still read dim).
        /// </summary>
        public const float LightGain = 2.25f;

        // g3.efm VFX_Magic_Light_Sphere_01: max 50, 10/s, life 5 s, alpha keys 0 → 0.5 (t 0.47) → 0 (mean 0.25), Brighten.
        const int SphereMax = 50;
        const float SphereRate = 10f, SphereLife = 5f, SphereMeanAlpha = 0.25f;
        const int Brighten = 6;

        /// <summary>
        /// Alpha factor that brings a stack of <paramref name="count"/> additive sprites (mean alpha <paramref name="meanAlpha"/>,
        /// colour gain <paramref name="gain"/>) to <paramref name="peak"/> × white where they overlap; never above 1.
        /// </summary>
        public static float StackAlphaScale(float count, float meanAlpha, float gain, float peak)
        {
            float sum = Math.Max(0f, count) * Math.Max(0f, meanAlpha) * Math.Max(0f, gain);
            return sum <= 0f ? 1f : Math.Min(1f, Math.Max(0f, peak) / sum);
        }

        /// <summary>Alpha factor of <see cref="SphereTemplate"/> for <see cref="CorePeak"/> (≈ 0.27).</summary>
        public static float SphereAlpha => StackAlphaScale(EmitterRules.SteadyCount(SphereMax, SphereRate, true, SphereLife), SphereMeanAlpha,
                                                            EmitterRules.ColorGain(Brighten), CorePeak);

        /// <summary>The look of one template of <see cref="Effect"/>; <paramref name="shadows"/>: the light may cast shadows.</summary>
        public static EffectPartLook For(string template, bool shadows = false)
        {
            if (Is(template, SphereTemplate)) return new EffectPartLook(SphereSize, SphereAlpha, 1f, local: true);
            if (Is(template, GlowTemplate)) return new EffectPartLook(GlowSize, GlowAlpha, 1f, local: true);
            if (Is(template, LightTemplate)) return new EffectPartLook(1f, 1f, LightGain, local: true, shadows: shadows);
            return EffectPartLook.Default;
        }

        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
