using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace G3.Unity
{
    /// <summary>What <see cref="RenderTuningOptions.Probes"/> does to the world's mesh renderers.</summary>
    public enum WorldProbeMode
    {
        /// <summary>Leave the renderers as built.</summary>
        Keep,
        /// <summary>LightProbeUsage.Off: the shader uses the ambient probe (the world has no light probes, so the image is the same).</summary>
        Off,
        /// <summary>LightProbeUsage.BlendProbes (the old default; for A/B measurements after a world rebuild).</summary>
        Blend,
    }

    /// <summary>
    /// Runtime render switches of the streamed world, set from the player command line (Docs/performance.md,
    /// "CPU cost of the GPU Resident Drawer"). Engine-free so the parsing is unit-tested (Tools/G3Tests).
    /// <list type="bullet">
    /// <item><c>-g3probes keep|off|blend</c>: light-probe usage of world mesh renderers (default off).</item>
    /// <item><c>-g3treefade 0|1</c>: 0 = trees switch LOD instantly (LODFadeMode.None) instead of the dithered cross-fade.</item>
    /// <item><c>-g3pointshadows N</c>: shadowed point/spot lights at a time (G3LightShadowBudget), -1 = component default.</item>
    /// <item><c>-g3cascades N</c>: sun shadow cascades 1-4 (URP asset, default: asset = 4). Each cascade is one more
    /// GPU Resident Drawer culling view over every instance (perf-7).</item>
    /// <item><c>-g3shadowsmall P</c>: GRD shadow small-mesh culling, instances covering less than P % (0-50) of a
    /// cascade are dropped from it (default: asset = 0, off).</item>
    /// <item><c>-g3smallmesh P</c>: GRD camera small-mesh culling, P % (0-20) of the screen (default: asset = 0, off).</item>
    /// <item><c>-g3lightdist M</c>: world point lights farther than M metres (camera to the light's range sphere) are
    /// switched off (G3LightShadowBudget, <see cref="LightLod"/>); 0 = never, default: component default.</item>
    /// <item><c>-g3npclod 0|1</c>: NPC update LOD (<see cref="NpcUpdateLod"/>, default 1 = on): NPC views beyond
    /// <see cref="NpcUpdateLod.FullDistance"/> or out of view update their routine and animation less often (~30 / 15 / 5 Hz,
    /// perf-3); <c>2</c> = only the views no camera renders (the Very high object detail's mode, <see cref="NpcLodHidden"/>).</item>
    /// <item><c>-g3swaprate N</c>: renderers changed per frame by bulk renderer waves (<see cref="PacedWork{TKey}"/>:
    /// remaster switches, tree replacement, story sectors), 0 = all in one frame (the old behaviour), default: G3RendererChanges.</item>
    /// <item><c>-g3perfprofile [frames]</c>: G3PerfProbe writes a binary profiler log of that many frames (default 120) per view.</item>
    /// <item><c>-g3mt MASK</c> (mt-1, <see cref="MtBits"/>): which main-thread work runs as Burst jobs (decimal or 0x hex);
    /// 0 = the old managed paths; default <see cref="MtBits.Default"/>. Also the environment variable <c>G3_MT</c>; switchable
    /// at runtime (G3Mt.Mask).</item>
    /// </list>
    /// </summary>
    public sealed class RenderTuningOptions
    {
        public const int DefaultProfileFrames = 120;

        public WorldProbeMode Probes = WorldProbeMode.Off;
        public bool TreeCrossFade = true;
        public int PointShadows = -1;
        public int ProfileFrames;
        /// <summary>Sun shadow cascades 1-4, 0 = leave the URP asset alone.</summary>
        public int Cascades;
        /// <summary>Shadow small-mesh screen percentage, negative = leave the URP asset alone.</summary>
        public float ShadowSmallMesh = -1f;
        /// <summary>Camera small-mesh screen percentage, negative = leave the URP asset alone.</summary>
        public float SmallMesh = -1f;
        /// <summary>World light cull distance in metres, 0 = off, negative = component default.</summary>
        public float LightDistance = -1f;
        /// <summary>NPC update LOD (<see cref="NpcUpdateLod"/>) on.</summary>
        public bool NpcLod = true;
        /// <summary>
        /// perf-3: with <see cref="NpcLod"/> off the views no camera or shadow view renders still step less often (their
        /// tier, and so their perception, stays the full one); off only with <c>-g3npclod 0</c>.
        /// </summary>
        public bool NpcLodHidden = true;
        /// <summary><c>-g3npclod</c> was given (it then wins over the menu's object detail).</summary>
        public bool NpcLodGiven;
        /// <summary>Renderers per frame of the paced renderer waves, 0 = no pacing, negative = the default.</summary>
        public int SwapRate = -1;
        /// <summary><c>-g3mt</c> mask (<see cref="MtBits"/>), negative = not given.</summary>
        public int MtMask = -1;

        /// <summary>
        /// The effective switches: the menu's values (Performance options, Myrtana PerformanceOptions) with every switch the
        /// command line gives on top, so A/B runs with <c>-g3…</c> still measure what they ask for.
        /// </summary>
        public static RenderTuningOptions Merge(RenderTuningOptions menu, RenderTuningOptions commandLine)
        {
            var o = commandLine.Copy();
            if (menu == null) return o;
            if (o.PointShadows < 0) o.PointShadows = menu.PointShadows;
            if (o.Cascades <= 0) o.Cascades = menu.Cascades;
            if (o.ShadowSmallMesh < 0) o.ShadowSmallMesh = menu.ShadowSmallMesh;
            if (o.SmallMesh < 0) o.SmallMesh = menu.SmallMesh;
            if (o.LightDistance < 0) o.LightDistance = menu.LightDistance;
            if (!o.NpcLodGiven)
            {
                o.NpcLod = menu.NpcLod;
                o.NpcLodHidden = menu.NpcLodHidden;
            }
            return o;
        }

        public RenderTuningOptions Copy() => (RenderTuningOptions)MemberwiseClone();

        public static RenderTuningOptions Parse(IReadOnlyList<string> args)
        {
            var o = new RenderTuningOptions();
            if (args == null) return o;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Count ? args[i + 1] : null;
                if (Is(a, "-g3probes") && next != null)
                {
                    switch (next.ToLowerInvariant())
                    {
                        case "keep": o.Probes = WorldProbeMode.Keep; i++; break;
                        case "off": o.Probes = WorldProbeMode.Off; i++; break;
                        case "blend": o.Probes = WorldProbeMode.Blend; i++; break;
                    }
                }
                else if (Is(a, "-g3treefade") && next != null && TryInt(next, out int fade))
                {
                    o.TreeCrossFade = fade != 0;
                    i++;
                }
                else if (Is(a, "-g3pointshadows") && next != null && TryInt(next, out int n))
                {
                    o.PointShadows = Math.Max(-1, n);
                    i++;
                }
                else if (Is(a, "-g3cascades") && next != null && TryInt(next, out int c))
                {
                    o.Cascades = Math.Min(4, Math.Max(0, c));
                    i++;
                }
                else if (Is(a, "-g3shadowsmall") && next != null && TryFloat(next, out float sp))
                {
                    o.ShadowSmallMesh = Math.Min(50f, Math.Max(0f, sp));
                    i++;
                }
                else if (Is(a, "-g3smallmesh") && next != null && TryFloat(next, out float mp))
                {
                    o.SmallMesh = Math.Min(20f, Math.Max(0f, mp));
                    i++;
                }
                else if (Is(a, "-g3lightdist") && next != null && TryFloat(next, out float ld))
                {
                    o.LightDistance = Math.Min(1000f, Math.Max(0f, ld));
                    i++;
                }
                else if (Is(a, "-g3npclod") && next != null && TryInt(next, out int nl))
                {
                    o.NpcLod = nl == 1;
                    o.NpcLodHidden = nl != 0;
                    o.NpcLodGiven = true;
                    i++;
                }
                else if (Is(a, "-g3swaprate") && next != null && TryInt(next, out int sr))
                {
                    o.SwapRate = Math.Max(0, sr);
                    i++;
                }
                else if (Is(a, "-g3mt") && next != null && MtBits.TryParse(next, out int mt))
                {
                    o.MtMask = mt;
                    i++;
                }
                else if (Is(a, "-g3perfprofile"))
                {
                    o.ProfileFrames = DefaultProfileFrames;
                    if (next != null && TryInt(next, out int f)) { o.ProfileFrames = Math.Max(1, f); i++; }
                }
            }
            return o;
        }

        /// <summary>Short form for result lines, e.g. "probes=off fade=dither pshadows=def".</summary>
        public string Describe()
        {
            string p = Probes == WorldProbeMode.Keep ? "keep" : Probes == WorldProbeMode.Off ? "off" : "blend";
            string s = PointShadows < 0 ? "def" : PointShadows.ToString(CultureInfo.InvariantCulture);
            string d = $"probes={p} fade={(TreeCrossFade ? "dither" : "instant")} pshadows={s}";
            if (Cascades > 0) d += " cascades=" + Cascades.ToString(CultureInfo.InvariantCulture);
            if (ShadowSmallMesh >= 0) d += " shadowsmall=" + ShadowSmallMesh.ToString("0.##", CultureInfo.InvariantCulture);
            if (SmallMesh >= 0) d += " smallmesh=" + SmallMesh.ToString("0.##", CultureInfo.InvariantCulture);
            if (LightDistance >= 0) d += " lightdist=" + LightDistance.ToString("0.##", CultureInfo.InvariantCulture);
            if (!NpcLod) d += NpcLodHidden ? " npclod=hidden" : " npclod=0";
            if (SwapRate >= 0) d += " swaprate=" + SwapRate.ToString(CultureInfo.InvariantCulture);
            if (MtMask >= 0) d += " mt=" + MtMask.ToString(CultureInfo.InvariantCulture);
            return d;
        }

        static bool Is(string a, string name) => string.Equals(a, name, StringComparison.OrdinalIgnoreCase);

        static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        static bool TryFloat(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v);
    }

    /// <summary>
    /// Distance LOD of the world's point lights (G3LightShadowBudget). G3 lights its objects with at most four static
    /// lights each (eCIlluminated_PS caches 4; the shaders take XFX_VS_POINTLIGHT0..3) and draws only objects within
    /// its object culling distances, so a light far from the camera adds nothing visible; URP Forward+ would still loop
    /// over every enabled light in view (≈ 360 in Faring alone since the mesh-less .lrentdat lights were added).
    /// <b>[interp]</b>: the cut-off distance is ours; lights fade over <see cref="FadeBand"/> instead of popping.
    /// </summary>
    public static class LightLod
    {
        /// <summary>Default cull distance, metres from the camera to the light's range sphere.</summary>
        public const float DefaultDistance = 60f;
        /// <summary>Fade band, metres: full intensity up to (distance − band), off from distance on.</summary>
        public const float FadeBand = 10f;

        /// <summary>
        /// Intensity factor 0..1 of a light whose range sphere is <paramref name="toSphere"/> metres from the camera
        /// (negative or 0: the camera is inside it). <paramref name="cullDistance"/> ≤ 0 = never culled.
        /// </summary>
        public static float Factor(float toSphere, float cullDistance)
        {
            if (cullDistance <= 0f || toSphere <= cullDistance - FadeBand) return 1f;
            if (toSphere >= cullDistance) return 0f;
            float band = Math.Min(FadeBand, cullDistance);
            return (cullDistance - toSphere) / band;
        }
    }

    /// <summary>
    /// perf-4: which point lights get the few shadow slots of <c>G3LightShadowBudget</c> and how a shadow comes and goes.
    /// A shadowed point light is six more culling views over the world instances, ~750 more shadow-caster draws and the
    /// additional-light shadow keyword on every opaque pixel: at the Geldern camp (editor, 1440p, Ultra) 2 shadowed lights
    /// cost ~2 ms main thread and ~2 ms GPU, the 160 unshadowed lights around it ~0.8 ms GPU together. The data flags
    /// nearly every world light as shadow casting, also the dim blue-grey night fills (colour ≈ 0.25-0.3, range 15-25 m),
    /// which took one of the two slots wherever one stood near a fire. <b>[interp]</b>: thresholds are ours.
    /// </summary>
    public static class PointShadowRules
    {
        /// <summary>Default shadow reach: metres from the camera to the light's range sphere (was 20 m).</summary>
        public const float DefaultDistance = 12f;

        /// <summary>
        /// Least peak brightness (intensity × brightest colour channel) of a light worth a shadow: torches and fires 4
        /// (2 × <c>FireLightRules.IntensityGain</c>), teal cave lights 1.75; the blue-grey fills 0.5-0.8 (their shadow is a
        /// barely darker patch in the night ambient).
        /// </summary>
        public const float MinBrightness = 1.2f;

        /// <summary>Seconds a shadow takes to fade in or out (shadow strength), instead of popping at the slot change.</summary>
        public const float FadeSeconds = 0.4f;

        /// <summary>Metres added to a light's range for the view test (the selection runs a few times a second).</summary>
        public const float ViewMargin = 2f;

        /// <summary>Whether a light of this intensity and colour (0..1 channels) is bright enough to get a shadow.</summary>
        public static bool Bright(float intensity, float r, float g, float b) =>
            intensity * Math.Max(r, Math.Max(g, b)) >= MinBrightness;

        /// <summary>Shadow fade 0..1 after <paramref name="dt"/> seconds towards on (1) or off (0).</summary>
        public static float Fade(float current, bool on, float dt)
        {
            float step = FadeSeconds > 0f ? dt / FadeSeconds : 1f;
            return on ? Math.Min(1f, current + step) : Math.Max(0f, current - step);
        }

        /// <summary>
        /// A light may switch its shadow on only while fewer than <paramref name="max"/> lights have one (lights still fading
        /// out included): at a swap the new shadow waits for the old one, so the cost never goes above the budget.
        /// </summary>
        public static bool MayStart(int withShadows, int max) => withShadows < max;
    }

    /// <summary>Formatting of profiler marker averages for the G3PerfProbe result lines.</summary>
    public static class MarkerSummary
    {
        /// <summary>
        /// The <paramref name="count"/> largest entries of at least <paramref name="minMs"/>, largest first (ties by name),
        /// as "name=1.23 name=0.50".
        /// </summary>
        public static string Top(IEnumerable<KeyValuePair<string, double>> avgMs, int count, double minMs)
        {
            var list = new List<KeyValuePair<string, double>>();
            if (avgMs != null)
                foreach (var kv in avgMs)
                    if (kv.Key != null && kv.Value >= minMs) list.Add(kv);
            list.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count && i < count; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(list[i].Key).Append('=').Append(list[i].Value.ToString("F2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
