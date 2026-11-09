using System;
using System.Collections.Generic;

namespace G3.Effects
{
    /// <summary>Unity's UnityEngine.Rendering.BlendMode values (kept engine-free).</summary>
    public enum BlendFactor { Zero = 0, One = 1, DstColor = 2, SrcColor = 3, OneMinusDstColor = 4, SrcAlpha = 5, OneMinusSrcColor = 6, DstAlpha = 7, OneMinusDstAlpha = 8, SrcAlphaSaturate = 9, OneMinusSrcAlpha = 10 }

    /// <summary>How a G3 draw style (eETextureDrawStyle, UE2 EParticleDrawStyle) is blended.</summary>
    public readonly struct BlendSetup
    {
        public readonly BlendFactor Src, Dst;
        public readonly bool Visible, Transparent;
        /// <summary>Colour the fog fades the particle to: false = the fog colour, true = black (additive styles).</summary>
        public readonly bool FogToBlack;
        public BlendSetup(BlendFactor src, BlendFactor dst, bool visible, bool transparent, bool fogToBlack)
        { Src = src; Dst = dst; Visible = visible; Transparent = transparent; FogToBlack = fogToBlack; }
        public override string ToString() => Visible ? $"{Src}/{Dst}" : "invisible";
    }

    /// <summary>
    /// Pure rules of the G3 particle emitter (eCParticle_PS, Engine.dll; Docs/formats-effects.md §3) shared by the importer and
    /// the runtime: emission rate, frames, scale repeats, fade factors, blending, budgets. Units: G3 (cm, s, revolutions).
    /// </summary>
    public static class EmitterRules
    {
        /// <summary>Hard cap of live particles per emitter instance (G3 data: max 500; one emitter has 3000 = rain-like).</summary>
        public const int MaxParticlesCap = 600;

        /// <summary>Particles per second: ParticlesPerSecond, or MaxParticles / max lifetime when it is 0 (UE2 automatic rate).</summary>
        public static float EmissionRate(int maxParticles, float particlesPerSecond, float lifetimeMax)
        {
            if (particlesPerSecond > 0) return particlesPerSecond;
            return lifetimeMax > 0 ? maxParticles / lifetimeMax : maxParticles;
        }

        /// <summary>
        /// A non-respawning emitter emits MaxParticles in total and stops. Below this many seconds the emission is a burst at
        /// the start (e.g. ParticlesPerSecond 10000 with 50 particles).
        /// </summary>
        public const float BurstSeconds = 1f / 30f;

        /// <summary>Seconds a non-respawning emitter emits: total / rate (0 = burst).</summary>
        public static float OneShotEmitSeconds(int maxParticles, float rate)
        {
            if (rate <= 0) return 0;
            float s = maxParticles / rate;
            return s < BurstSeconds ? 0 : s;
        }

        /// <summary>CalcFrame's frame grid: NumU/NumV count the cuts, so a texture has (U+1) × (V+1) frames.</summary>
        public static int FrameCount(int numU, int numV) => (Math.Max(0, numU) + 1) * (Math.Max(0, numV) + 1);

        /// <summary>Repeating scales (…ScaleRepeats): t' = frac(t · (repeats + 1)); t = 1 stays at the curve end.</summary>
        public static float RepeatTime(float t, float repeats)
        {
            if (repeats <= 0) return t;
            float x = t * (repeats + 1);
            if (x >= repeats + 1) return 1f;
            return x - (float)Math.Floor(x);
        }

        /// <summary>
        /// CalcFrame without a SubdivisionScale: start and end are clamped to the grid; equal → start; else
        /// start + (end − start + 1)·t, at most end (fractional; the integer part is the frame).
        /// </summary>
        public static float FrameAt(float t, int numU, int numV, int start, int end)
        {
            int count = FrameCount(numU, numV);
            if (start < 0) start = 0;
            if (end >= count) end = count - 1;
            if (end == start) return start;
            float f = start + (end - start + 1) * t;
            return f > end ? end : f;
        }

        /// <summary>Clamped [start, end] of the frame grid (end 0 or beyond the grid = last frame).</summary>
        public static void FrameRange(int numU, int numV, int start, int end, out int first, out int last)
        {
            int count = FrameCount(numU, numV);
            first = Math.Max(0, Math.Min(start, count - 1));
            last = end <= 0 || end >= count ? count - 1 : end;
            if (last < first) last = first;
        }

        /// <summary>
        /// CalcColorFactor for one channel: fade in from <paramref name="inFactor"/> to 1 over [0, fadeInEnd] s, fade out from 1 to
        /// <paramref name="outFactor"/> over [fadeOutStart, lifetime] s; the two multiply. A factor of 1 means no fade.
        /// </summary>
        public static float FadeFactor(float age, float lifetime, bool fadeIn, float fadeInEnd, float inFactor, bool fadeOut, float fadeOutStart, float outFactor)
        {
            float a = 1f, b = 1f;
            if (fadeIn && fadeInEnd > age)
            {
                float q = fadeInEnd > 0 ? Clamp01(age / fadeInEnd) : 1f;
                a = 1f - (1f - inFactor) * (1f - q);
            }
            if (fadeOut && age >= fadeOutStart && lifetime > fadeOutStart)
            {
                float p = Clamp01((age - fadeOutStart) / (lifetime - fadeOutStart));
                b = 1f - (1f - outFactor) * p;
            }
            return a * b;
        }

        /// <summary>Draw style → blending [inferred from the UE2 meanings of the enum names; Docs/formats-effects.md §3.4].</summary>
        public static BlendSetup Blend(int drawStyle)
        {
            switch (drawStyle)
            {
                case 0: return new BlendSetup(BlendFactor.One, BlendFactor.Zero, true, false, false);                    // Regular (opaque, alpha test)
                case 1: return new BlendSetup(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, true, true, false);    // AlphaBlend
                case 2: return new BlendSetup(BlendFactor.DstColor, BlendFactor.SrcColor, true, true, false);            // Modulated (2× multiply)
                case 3: return new BlendSetup(BlendFactor.One, BlendFactor.OneMinusSrcColor, true, true, true);          // Translucent
                case 4: return new BlendSetup(BlendFactor.One, BlendFactor.OneMinusSrcAlpha, true, true, false);         // AlphaModulate (premultiplied)
                case 5: return new BlendSetup(BlendFactor.Zero, BlendFactor.OneMinusSrcColor, true, true, true);         // Darken
                case 6: return new BlendSetup(BlendFactor.SrcAlpha, BlendFactor.One, true, true, true);                  // Brighten (additive)
                default: return new BlendSetup(BlendFactor.One, BlendFactor.Zero, false, false, false);                  // Invisible
            }
        }

        /// <summary>Slack after the longest particle life before a part whose emission ended counts as finished, s.</summary>
        public const float AgeOutSlackSeconds = 0.5f;

        /// <summary>
        /// fix-58: a part whose emission ended at <paramref name="emitEndAge"/> (s of its age; &lt; 0 = still emitting) has no
        /// particle left at <paramref name="age"/> once its longest particle life (+<see cref="AgeOutSlackSeconds"/>) is over,
        /// whatever the particle system still reports (a paused, culled system keeps its particles).
        /// </summary>
        public static bool AgedOut(float emitEndAge, float age, float maxLifetime) =>
            emitEndAge >= 0f && age - emitEndAge > Math.Max(0f, maxLifetime) + AgeOutSlackSeconds;

        public static int CapParticles(int max) => Math.Max(1, Math.Min(max <= 0 ? 1 : max, MaxParticlesCap));

        /// <summary>
        /// A scale curve with repeats as keys over 0..1 (repeated copies), reduced to at most <paramref name="maxKeys"/> keys
        /// by resampling (Unity gradients take 8). Keys are (time, values[]); <paramref name="eval"/> evaluates the source.
        /// </summary>
        public static List<float> SampleTimes(IReadOnlyList<float> keyTimes, float repeats, int maxKeys)
        {
            var times = new List<float>();
            int copies = repeats > 0 ? (int)Math.Round(repeats) + 1 : 1;
            for (int c = 0; c < copies; c++)
                foreach (var t in keyTimes)
                {
                    float x = (c + Clamp01(t)) / copies;
                    if (times.Count == 0 || x > times[times.Count - 1] + 1e-4f) times.Add(x);
                }
            if (times.Count == 0) { times.Add(0); times.Add(1); }
            if (times.Count > maxKeys)
            {
                times.Clear();
                for (int i = 0; i < maxKeys; i++) times.Add(i / (float)(maxKeys - 1));
            }
            return times;
        }

        /// <summary>Piecewise-linear evaluation of keys (times ascending); clamps outside the key range.</summary>
        public static float Evaluate(IReadOnlyList<float> times, IReadOnlyList<float> values, float t)
        {
            int n = times.Count;
            if (n == 0) return 1f;
            if (t <= times[0]) return values[0];
            if (t >= times[n - 1]) return values[n - 1];
            for (int i = 1; i < n; i++)
                if (t <= times[i])
                {
                    float span = times[i] - times[i - 1];
                    float x = span > 0 ? (t - times[i - 1]) / span : 1f;
                    return values[i - 1] + (values[i] - values[i - 1]) * x;
                }
            return values[n - 1];
        }

        /// <summary>UE2 velocity damping (Velocity −= Velocity · VelocityLoss · dt) as one Unity drag value: the mean |loss| of the axes.</summary>
        public static float Drag(float lossX, float lossY, float lossZ) => (Math.Abs(lossX) + Math.Abs(lossY) + Math.Abs(lossZ)) / 3f;

        /// <summary>
        /// Speed limit (cm/s) from MaxAbsoluteVelocity, or <see cref="float.PositiveInfinity"/> for none. 96 templates
        /// (waterfalls, splashes, fire breath, meteor fire …) store (0,0,0) while they clearly move: 0 means unlimited
        /// [inferred] — clamping them froze the particles in place.
        /// </summary>
        public static float VelocityLimit(float maxX, float maxY, float maxZ)
        {
            float m = Math.Max(Math.Abs(maxX), Math.Max(Math.Abs(maxY), Math.Abs(maxZ)));
            return m > 0 ? m : float.PositiveInfinity;
        }

        /// <summary>
        /// Axis (0 x, 1 y, 2 z) of StartSpin / SpinsPerSecond that rolls a camera-facing sprite: the data keeps the roll in z
        /// (e.g. StartSpin 0,0,−π .. 0,0,π, SpinsPerSecond 0,0,−0.01 .. 0,0,0.01; a few set all three axes alike), so z, or x
        /// when both z ranges are 0 [inferred].
        /// </summary>
        public static int SpinAxis(float startZMin, float startZMax, float rateZMin, float rateZMax)
            => startZMin == 0 && startZMax == 0 && rateZMin == 0 && rateZMax == 0 ? 0 : 2;

        /// <summary>
        /// Colour gain of additive draw styles (Brighten, Translucent) [interp]. G3 added sprites in gamma space; URP adds them
        /// in linear space, where stacked flame sprites sum to far less (two 0.5 sprites: 1.0 in G3, ≈ 0.69 here) and never
        /// reach a white core or the bloom threshold. 1.5 brings a dense torch/fire core back to ≈ G3 brightness; a single
        /// sprite over black gets brighter by the same factor (1.5^(1/2.2) ≈ 1.2 in display terms).
        /// </summary>
        public const float AdditiveGain = 1.5f;

        /// <summary>
        /// Unity intensity per unit of eCPointLight_PS Intensity for effect and item lights [interp] (was 0.8). G3 lit in gamma
        /// space with a soft falloff to the range; URP's 1/d² leaves the hero's torch (Intensity 3, range 20 m) at 0.15 at
        /// 4 m with 0.8, where G3 still lights the ground clearly; 1.6 gives ≈ 0.3 at 4 m and keeps the near field sane after
        /// tonemapping.
        /// </summary>
        public const float EffectLightScale = 1.6f;

        public static float EffectLightIntensity(float g3Intensity) => Math.Max(0.1f, g3Intensity * EffectLightScale);

        public static float ColorGain(int drawStyle) => drawStyle == 6 || drawStyle == 3 ? AdditiveGain : 1f;

        /// <summary>
        /// Live particles of an emitter at steady state, i.e. how many of its sprites stack: respawning emitters keep
        /// rate × mean life (at most the max), one-shot emitters put out all of them.
        /// </summary>
        public static float SteadyCount(int maxParticles, float rate, bool respawn, float lifetimeMean) =>
            respawn ? Math.Min(maxParticles, Math.Max(0f, rate) * Math.Max(0f, lifetimeMean)) : maxParticles;

        /// <summary>
        /// fix-124 (owner: an orange haze ball around the hero's torch in caves): start alpha of an additive emitter whose stacked
        /// sprites stay faint. G3 added Brighten/Translucent sprites in gamma space: n sprites of alpha a over a dark
        /// background sum to n·a·colour in sRGB = (n·a·colour)^2.2 linear. Here the texture and colour are linear already but
        /// the alpha is not, and <see cref="AdditiveGain"/> multiplies on top, so a faint glow (torch BigGlow
        /// VFX_Env_Fire_Torch_Glow_01: 6-8 m, alpha 0.1, ~2.5 live) came out ~8× brighter than G3 — the "smoke ball". Matching
        /// the stack: a' = (n·a)^2.2 / (n·gain), i.e. a factor (n·a)^1.2 / gain, never above 1. Dense stacks (flames: n·a ≥ 0.71)
        /// keep their alpha and the fire-fx gain; non-additive styles are unchanged.
        /// </summary>
        public static float AdditiveAlphaScale(int drawStyle, float count, float peakAlpha)
        {
            float gain = ColorGain(drawStyle);
            if (gain <= 1f) return 1f;
            float stack = Math.Max(0f, count) * Clamp01(peakAlpha);
            if (stack <= 0f) return 1f;
            return Math.Min(1f, (float)Math.Pow(stack, 1.2) / gain);
        }

        /// <summary>
        /// fix-182 (owner, night Nordmar with the remaster on: "а можно вообще убрать эту ауру вокруг факела?"): the torch's
        /// halo template. eff_item_torch_02 (the torch held by the hero and NPCs) spawns VFX_Env_Fire_Torch_Glow_01: BigGlow
        /// texture, 6-8 m Brighten sprites, ~2.5 live, no light of its own (the torch's light is the item's). It is the only
        /// VFX_Env_* template that uses VFX_Magic_BigGlow_01 (G3Cli fx props eCParticle_PS Texture BigGlow; the other 40 are
        /// spells, events and magic weapons), and the remaster's auto-exposure lifts it at night. Wall torches
        /// (eff_item_torch_01), bowls, camp fires and fireplaces have no such part. Shown only with the port option
        /// "Torch glow" (<c>G3EffectPlayer.TorchGlow</c>; on = G3).
        /// </summary>
        public const string TorchGlowTemplate = "VFX_Env_Fire_Torch_Glow_01";

        /// <summary>
        /// perf-4: heat-haze parts (the G3/HeatHaze screen distortion above torches, camp fires and braziers) farther than this
        /// from the camera (m) emit nothing. Their sprites are 0.3-1 m and shift the picture by a few pixels; at 35 m that is a
        /// shimmer of one or two pixels, while they were half of all particles emitted around a camp at night (Geldern:
        /// 4980 of 9980 per second, 2640 of them beyond 30 m) [interp: G3's own effect culling is not known].
        /// </summary>
        public const float HeatHazeDistance = 35f;

        /// <summary>Emission cut-off (m) of a part: <paramref name="cullDistance"/>, at most <see cref="HeatHazeDistance"/> for heat haze.</summary>
        public static float PartCullDistance(bool heatHaze, float cullDistance) =>
            heatHaze && cullDistance > HeatHazeDistance ? HeatHazeDistance : cullDistance;

        /// <summary>Whether <paramref name="template"/> is the torch halo (<see cref="TorchGlowTemplate"/>).</summary>
        public static bool IsTorchGlow(string template) =>
            string.Equals(template, TorchGlowTemplate, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Render queue offset after Transparent (3000): additive styles draw after the alpha-blended sprites (their own smoke,
        /// rain) so a smoke puff sorted in front of the flame cannot grey it out; heat haze stays last (+10).
        /// </summary>
        public static int QueueOffset(int drawStyle) => drawStyle == 6 || drawStyle == 3 ? 1 : 0;

        public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// fix-179: angle (radians) a revolving particle turns in <paramref name="dt"/> s. G3 eCParticle_PS::DoProcessV2
        /// (0x10155c8e-0x10155d67) builds bCEulerAngles(rate.y·f.y·dt, rate.x·f.x·dt, rate.z·f.z·dt) from the particle's
        /// RevolutionsPerSecond and the RevolutionScale factor, and SharedBase bCMatrix3::SetToRotation feeds them to sin/cos
        /// unconverted: the value is radians per second, not turns (×2π made the burning-body flames, torch flames, heal
        /// swirls … revolve 6.3× too fast). [verified]
        /// </summary>
        public static float RevolutionRadians(float revolutionsPerSecond, float factor, float dt) => revolutionsPerSecond * factor * dt;

        /// <summary>
        /// fix-179: a revolving emitter whose particles live in the world (CoordinateSystem Independent) is revolved by the
        /// runtime, not by Unity's orbital velocity. G3 EmitParticles (0x101568be-0x101568f1) gives each particle its own centre
        /// = RevolutionCenterOffset + the emitter's location at spawn (Independent only) and DoProcessV2 turns the particle's
        /// world position about it on the world axes; Unity's orbital turns every particle about the emitter's current
        /// position on the emitter's own axes (bone-attached flames whirled round the limb's axes into a wheel of fire).
        /// Relative emitters (centre and rotation in the emitter frame) keep Unity's local orbital. [verified]
        /// </summary>
        public static bool RevolvesInWorld(bool useRevolution, bool independentSpace) => useRevolution && independentSpace;

        /// <summary>
        /// fix-179: the point <paramref name="px"/>.. turned about the centre <paramref name="cx"/>.. by the angles (radians)
        /// about the x, y and z axes — y = G3's yaw — applied like Unity's Quaternion.Euler (z, then x, then y) [interp:
        /// multi-axis order; the data revolves about one axis]. Left-handed, y up: +90° about y takes +z to +x.
        /// </summary>
        public static void Revolve(ref float px, ref float py, ref float pz, float cx, float cy, float cz, float ax, float ay, float az)
        {
            float x = px - cx, y = py - cy, z = pz - cz;
            if (az != 0) { float c = (float)Math.Cos(az), s = (float)Math.Sin(az); float nx = x * c - y * s; y = x * s + y * c; x = nx; }
            if (ax != 0) { float c = (float)Math.Cos(ax), s = (float)Math.Sin(ax); float ny = y * c - z * s; z = y * s + z * c; y = ny; }
            if (ay != 0) { float c = (float)Math.Cos(ay), s = (float)Math.Sin(ay); float nx = x * c + z * s; z = -x * s + z * c; x = nx; }
            px = cx + x; py = cy + y; pz = cz + z;
        }

        /// <summary>
        /// Particle collision (22 emitters: blood, debris, sparks, ground fires/stones, leaves) → Unity collision module
        /// response. Only collision with the static world is ported (CollideWithStatic; the one dynamic-only emitter stays off).
        /// UseMaxCollisions with MaxCollisions ≤ 1 (all but footstep/slide dust, metal-on-ground chips and stones) = the
        /// particle stops where it hits [inferred, data: the ground effects (fire rain, meteor, summon stones) shoot their
        /// sprites down at 5–80 m/s with MaxCollisions 1 and live 2–8 s, i.e. they are meant to land and stay; blood drops
        /// (lifetime 3 s, no fade) land within ~0.3 s]. Otherwise a bounce: UE2 mirrors the velocity and scales it per axis by
        /// DampingFactor, so the tangential speed keeps the x/z mean (Unity dampen = 1 − that) and the normal part keeps y
        /// (Unity bounce = y / tangential mean, at most 2, since Unity applies dampen to both). Factors are clamped to 0..1 (leaves: 5).
        /// The bounce count itself is not tracked (Unity has none); bounces below 1 settle on their own.
        /// </summary>
        public static bool Collision(bool useCollision, bool collideWithStatic, bool useMaxCollisions, float maxCollisions,
                                     float dampX, float dampY, float dampZ, out float bounce, out float dampen)
        {
            bounce = 0; dampen = 1;
            if (!useCollision || !collideWithStatic) return false;
            if (useMaxCollisions && maxCollisions <= 1) return true;
            float h = Clamp01((Math.Abs(dampX) + Math.Abs(dampZ)) * 0.5f), v = Clamp01(Math.Abs(dampY));
            dampen = 1 - h;
            bounce = h > 0.01f ? Math.Min(2f, v / h) : 0f; // Unity allows 0..2
            return true;
        }

        /// <summary>
        /// A particle that stops where it hits (<see cref="Collision"/>: bounce 0, dampen 1) and is drawn stretched along its
        /// velocity dies at the hit (Unity collision lifetime loss 1, as UE2 kills a particle at its MaxCollisions). A
        /// stopped stretched sprite has no direction: under the gravity that keeps pushing it into the ground its velocity
        /// flips between ~0 and a tiny downward value every frame and the sprite flickers [fix-132, owner: the arena floor
        /// covered with flashing blood; VFX_Col_Fight_Blood_01 = Stretch, life 3 s, no fade, 60 drops per hit]. G3 leaves no
        /// blood on the ground (the drops are its only blood). Billboards (fire rain, meteor, summon stones) still land and stay.
        /// </summary>
        public static bool DiesAtHit(bool stretched, bool collides, float bounce, float dampen) =>
            stretched && collides && bounce <= 0f && dampen >= 1f;
    }

    /// <summary>
    /// Spawn sampling of one particle as in eCParticle_PS::EmitParticles (UE2 rules): position in the start box or sphere shell
    /// plus the offset, velocity from the per-axis ranges (or along the start position for the direction modes), size and spin.
    /// All values in G3 units; the caller converts. Engine-free and deterministic for a given random source.
    /// </summary>
    public static class ParticleSpawn
    {
        /// <summary>Random between a and b (either order).</summary>
        public static float Range(Func<float> rand01, float a, float b) => a + (b - a) * rand01();

        /// <summary>Uniform point in a sphere shell [rMin, rMax] around the origin.</summary>
        public static void SpherePoint(Func<float> rand01, float rMin, float rMax, out float x, out float y, out float z)
        {
            // direction: uniform on the sphere
            float u = rand01() * 2f - 1f, phi = rand01() * 6.2831853f;
            float s = (float)Math.Sqrt(Math.Max(0, 1 - u * u));
            float r = Range(rand01, rMin, rMax);
            x = s * (float)Math.Cos(phi) * r; y = u * r; z = s * (float)Math.Sin(phi) * r;
        }

        /// <summary>
        /// Velocity for the direction modes: speed = |random StartVelocity|, direction = start position − owner (1) or owner −
        /// start position (2); mode 0 returns the random vector unchanged.
        /// </summary>
        public static void DirectedVelocity(int mode, float px, float py, float pz, ref float vx, ref float vy, ref float vz)
        {
            if (mode == 0) return;
            float speed = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
            float len = (float)Math.Sqrt(px * px + py * py + pz * pz);
            if (len < 1e-5f) { vx = 0; vy = speed; vz = 0; return; }
            float k = (mode == 1 ? 1f : -1f) * speed / len;
            vx = px * k; vy = py * k; vz = pz * k;
        }
    }
}
