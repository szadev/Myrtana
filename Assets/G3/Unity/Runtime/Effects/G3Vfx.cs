using System;
using System.Runtime.CompilerServices;
using G3.Effects;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Spawn parameters of one G3 emitter (eCParticle_PS) in Unity units: metres, seconds, degrees. Everything that happens
    /// over a particle's life (colour/size/velocity scales, drag, acceleration, spin, frames) is configured on the
    /// ParticleSystem modules by the importer; this is what <see cref="G3Vfx"/> needs to emit like G3. Docs/formats-effects.md §5.
    /// </summary>
    [Serializable]
    public sealed class G3EmitterSpec
    {
        public int maxParticles = 10;
        public float rate = 10;
        public bool respawn = true;
        [Tooltip("Non-respawning emitters: seconds to emit maxParticles (0 = one burst).")] public float emitSeconds;
        public Vector2 lifetime = new Vector2(1, 1);
        public Vector2 initialDelay;
        [Tooltip("Seconds simulated at start (RelativeWarmupTime × max lifetime).")] public float warmup;
        [Tooltip("0 box, 1 sphere shell")] public int shape;
        public Vector3 boxMin, boxMax;
        public Vector2 sphereRadius;
        public Vector3 offset;
        public Vector3 velocityMin, velocityMax;
        [Tooltip("0 per-axis ranges; 1 away from the emitter; 2 towards it (speed = |random velocity|).")] public int directionFrom;
        [Tooltip("UseRotationFrom Entity: offsets and velocities turn with the effect.")] public bool rotateWithEntity = true;
        public bool uniformSize = true;
        public Vector3 sizeMin = Vector3.one * 0.1f, sizeMax = Vector3.one * 0.1f;
        public bool spin;
        public Vector2 startSpinDegrees, spinDegreesPerSecond;
        [Range(0, 1)] public float spinClockwise = 0.5f;
        public bool worldSpace = true;
        [Tooltip("fix-179: G3 revolution of a world-space emitter, done by G3Vfx (EmitterRules.RevolvesInWorld): each particle turns about its own centre (the emitter at spawn + offset) on the world axes.")]
        public bool revolve;
        [Tooltip("Revolution rate range about world x, y, z (radians/s: G3 RevolutionsPerSecond as stored).")] public Vector3 revolutionMin, revolutionMax;
        [Tooltip("Revolution centre offset range (m) from the emitter position at spawn.")] public Vector3 revolutionCenterMin, revolutionCenterMax;
        [Tooltip("RevolutionScale over the particle's life (x, y, z); empty = 1.")] public AnimationCurve revolutionScaleX, revolutionScaleY, revolutionScaleZ;
    }

    /// <summary>
    /// One imported G3 effect template (VFX_*): an emitter driven like eCParticle_PS::EmitParticles (custom emission through
    /// <see cref="ParticleSystem.Emit(ParticleSystem.EmitParams, int)"/>, so start boxes, spheres, per-axis velocity ranges and
    /// non-uniform sizes match G3) and/or a point light. Instances are pooled and ticked by <see cref="G3EffectSystem"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class G3Vfx : MonoBehaviour
    {
        public string templateName;
        public bool hasEmitter;
        public G3EmitterSpec emitter = new G3EmitterSpec();
        public ParticleSystem system;
        public Light pointLight;
        public float lightIntensity = 1;
        [Tooltip("eEDynamicLightEffect [inferred order]: 0 steady, 1 flicker, 2 pulse, 3 blink, 4 strobe")] public int lightEffect;
        public float lightPeriod = 1, lightPhase;

        [NonSerialized] public G3Vfx prefab;      // pool key
        [NonSerialized] public int handle;
        [NonSerialized] public Transform follow;  // attached: TargetEntity / TargetBone
        [NonSerialized] public bool followSet;
        [NonSerialized] public Vector3 followOffset;
        [NonSerialized] public float maxSeconds;  // safety stop for fire-and-forget looping effects (0 = none)
        [NonSerialized] public Vector3 position;  // world position as last placed by G3EffectSystem (no transform read per frame)

        /// <summary>A screen part (<see cref="ScreenSpriteRules.IsScreenTemplate"/>): its sprites are kept here and drawn by
        /// <see cref="G3ScreenOverlay"/> over the view, not by the world ParticleSystem.</summary>
        [NonSerialized] public bool screen;

        /// <summary>The torch halo (<see cref="EmitterRules.IsTorchGlow"/>): hidden unless <see cref="G3EffectPlayer.TorchGlow"/>.</summary>
        [NonSerialized] public bool torchGlow;
        /// <summary>perf-4: a heat-haze part (its particles draw with the G3/HeatHaze material): emits only within
        /// <see cref="EmitterRules.HeatHazeDistance"/> of the camera.</summary>
        [NonSerialized] public bool heatHaze;
        /// <summary>perf-3: time not yet ticked (a far part ticks every 2nd frame, <see cref="G3EffectSystem"/>) and its frame parity.</summary>
        [NonSerialized] internal float tickDebt;
        [NonSerialized] internal int tickPhase;

        /// <summary>One sprite of a screen part: where it is (world, or local to the effect), how old, how big.</summary>
        public struct ScreenSprite
        {
            public Vector3 position, velocity;
            public bool local;
            public float age, life, size;
            /// <summary>Random 0..1: the texture frame of an atlas.</summary>
            public float seed;
            /// <summary>Roll in the view plane (degrees) and its rate (degrees/s): SpinParticles as for world sprites.</summary>
            public float spin, spinRate;
        }

        readonly System.Collections.Generic.List<ScreenSprite> _screenSprites = new System.Collections.Generic.List<ScreenSprite>();
        public System.Collections.Generic.List<ScreenSprite> ScreenSprites => _screenSprites;

        bool _emitting, _stopped;
        float _age, _delay, _accumulator;
        float _emitEndAge = -1f; // age when emission ended (stop, or a burst emitted all); -1 = still emitting
        int _emitted;
        System.Random _rng;
        Func<float> _rand;
        float _lightSeed;
        bool _lightOn;          // pointLight.enabled as last written (perf-8: no native write per part per frame)
        float _lightIntensity = -1f;

        // fix-179: G3 revolution of world-space particles: centre and rate per particle, by the particle's random seed
        readonly System.Collections.Generic.Dictionary<uint, (Vector3 center, Vector3 rate)> _revolution =
            new System.Collections.Generic.Dictionary<uint, (Vector3, Vector3)>();
        ParticleSystem.Particle[] _revolveBuffer;

        public bool Emitting => _emitting;
        public float Age => _age;

        // fix-196: the look asked for by the effect this part plays in (EffectPartLook; Default = as imported)
        EffectPartLook _look = EffectPartLook.Default;
        bool _baseKept, _baseWorld;
        ParticleSystemSimulationSpace _baseSpace;
        Color _baseColor;
        LightShadows _baseShadows;

        /// <summary>The look of this part (<see cref="EffectPartLook"/>).</summary>
        public EffectPartLook Look => _look;

        /// <summary>
        /// fix-196: draws this part with <paramref name="look"/> (sizes, start alpha, own-frame particles, light gain and
        /// shadows) until another look is applied; <see cref="EffectPartLook.Default"/> restores the import. Call before
        /// <see cref="Begin"/>: switching the simulation space does not move live particles.
        /// </summary>
        public void ApplyLook(EffectPartLook look)
        {
            if (look.Equals(_look)) return;
            if (!_baseKept)
            {
                _baseKept = true;
                _baseWorld = emitter.worldSpace;
                if (system != null) { var m = system.main; _baseSpace = m.simulationSpace; _baseColor = m.startColor.color; }
                if (pointLight != null) _baseShadows = pointLight.shadows;
            }
            _look = look;
            emitter.worldSpace = _baseWorld && !look.Local;
            if (system != null)
            {
                var m = system.main;
                m.simulationSpace = look.Local ? ParticleSystemSimulationSpace.Local : _baseSpace;
                var c = _baseColor;
                c.a *= Mathf.Max(0f, look.Alpha);
                m.startColor = c;
            }
            if (pointLight != null)
                pointLight.shadows = look.IsDefault ? _baseShadows : look.Shadows ? LightShadows.Soft : LightShadows.None;
            _lightIntensity = -1f;
        }

        /// <summary>Starts emitting from scratch (pooled instance reused).</summary>
        public void Begin()
        {
            _rng ??= new System.Random(RuntimeHelpers.GetHashCode(this) ^ Environment.TickCount);
            _rand ??= () => (float)_rng.NextDouble();
            _age = 0;
            _accumulator = 0;
            _emitted = 0;
            _stopped = false;
            _emitting = hasEmitter;
            _emitEndAge = -1f;
            SetAging(false);
            _delay = hasEmitter ? Mathf.Lerp(emitter.initialDelay.x, emitter.initialDelay.y, _rand()) : 0;
            _lightSeed = _rand() * 100f;
            _screenSprites.Clear();
            _revolution.Clear();
            if (system != null && screen) system.Clear(true);
            else if (system != null)
            {
                system.Clear(true);
                system.Play(true);
                if (emitter.warmup > 0 && hasEmitter) Warm(emitter.warmup);
            }
            if (pointLight != null) SetLight(true);
        }

        void SetLight(bool on)
        {
            if (_lightOn == on && pointLight.enabled == on) return;
            _lightOn = on;
            pointLight.enabled = on;
        }

        /// <summary>No new particles; the instance is finished once the last one died (AutoKill).</summary>
        public void StopEmitting()
        {
            EmissionEnded();
            _emitting = false;
            _stopped = true;
            if (pointLight != null) SetLight(false);
        }

        /// <summary>
        /// fix-58: the emission is over, so the particles left must age out even out of view. The imported systems loop with
        /// <see cref="ParticleSystemCullingMode.Automatic"/>, which pauses a looping system off screen: the last particles of a
        /// stopped part behind the camera never died, the part never finished and its slot in the effect budget was never
        /// given back (runner-31: "384 live effect parts: skipping new effects" after a long session, sparks and blood gone).
        /// </summary>
        void EmissionEnded()
        {
            if (_emitEndAge < 0f) _emitEndAge = _age;
            SetAging(true);
        }

        bool _aging;

        /// <summary>Always simulate while the last particles age out; the imported culling mode while emitting.</summary>
        void SetAging(bool on)
        {
            if (system == null || screen || _aging == on) return;
            _aging = on;
            var main = system.main;
            main.cullingMode = on ? ParticleSystemCullingMode.AlwaysSimulate : ParticleSystemCullingMode.Automatic;
        }

        /// <summary>The longest particle life of this part, s (0 without an emitter).</summary>
        float MaxLife => hasEmitter ? Mathf.Max(emitter.lifetime.x, emitter.lifetime.y) : 0f;

        /// <summary>
        /// True when nothing is visible any more and nothing will be emitted. Backstop (fix-58): once the emission ended, a
        /// part is finished after its longest particle life (+0.5 s) whatever the particle system still reports.
        /// </summary>
        public bool Finished => hasEmitter
            ? !_emitting && (G3.Effects.EmitterRules.AgedOut(_emitEndAge, _age, MaxLife) ||
                             (screen ? _screenSprites.Count == 0 : system == null || system.particleCount == 0))
            : _stopped;

        /// <summary>Every particle gone at once (pool release, an effect ended with its screen parts).</summary>
        public void ClearParticles()
        {
            _screenSprites.Clear();
            _revolution.Clear();
            if (system != null) system.Clear(true);
        }

        /// <summary>Whether this part's particles draw with the heat-haze shader (perf-4; read once per pooled instance).</summary>
        public bool DrawsHeatHaze()
        {
            var r = system != null ? system.GetComponent<ParticleSystemRenderer>() : null;
            var m = r != null ? r.sharedMaterial : null;
            return m != null && m.shader != null && m.shader.name == HeatHazeShaderName;
        }

        /// <summary>Shader of the heat-haze material (G3EffectImporter.HeatHazeShader).</summary>
        public const string HeatHazeShaderName = "G3/HeatHaze";

        int LiveCount => screen ? _screenSprites.Count : system.particleCount;

        /// <summary>Per-frame emission; <paramref name="emit"/> false = culled (far away): no new particles, the light off.</summary>
        public void Tick(float dt, bool emit)
        {
            _age += dt;
            if (screen) AgeScreenSprites(dt);
            if (pointLight != null)
            {
                bool on = emit && !_stopped && (_emitting || !hasEmitter);
                if (on != _lightOn) SetLight(on);
                if (on)
                {
                    float intensity = lightIntensity * _look.LightGain * LightFactor(_age + lightPhase * lightPeriod);
                    if (intensity != _lightIntensity) { _lightIntensity = intensity; pointLight.intensity = intensity; } // steady lights: one write
                }
            }
            if (emitter.revolve && emit && !screen && system != null && _revolution.Count > 0) Revolve(dt);
            if (!_emitting || system == null) return;
            if (_age < _delay) return;
            var s = emitter;
            int n;
            if (s.respawn)
            {
                _accumulator += s.rate * dt;
                n = (int)_accumulator;
                _accumulator -= n;
                if (n > 0) n = Mathf.Min(n, s.maxParticles - LiveCount); // native read only when something would be emitted
            }
            else
            {
                if (s.emitSeconds <= 0) n = s.maxParticles;
                else
                {
                    _accumulator += s.rate * dt;
                    n = (int)_accumulator;
                    _accumulator -= n;
                }
                n = Mathf.Min(n, s.maxParticles - _emitted);
                _emitted += Mathf.Max(0, n);
                if (_emitted >= s.maxParticles) { _emitting = false; EmissionEnded(); }
            }
            if (emit) for (int i = 0; i < n; i++) EmitOne(0f);
        }

        void Warm(float seconds)
        {
            // Pre-age a respawning emitter: emit what would be alive and let Shuriken simulate it forward.
            int n = Mathf.Min(emitter.maxParticles, Mathf.CeilToInt(emitter.rate * Mathf.Min(seconds, emitter.lifetime.y)));
            for (int i = 0; i < n; i++) EmitOne(0f);
            system.Simulate(Mathf.Min(seconds, emitter.lifetime.y), true, false, false);
            system.Play(true);
        }

        void EmitOne(float preAge)
        {
            var s = emitter;
            var r = _rand;
            Vector3 p;
            if (s.shape == 1)
            {
                ParticleSpawn.SpherePoint(r, s.sphereRadius.x, s.sphereRadius.y, out var x, out var y, out var z);
                p = new Vector3(x, y, z);
            }
            else p = new Vector3(Lerp(s.boxMin.x, s.boxMax.x, r()), Lerp(s.boxMin.y, s.boxMax.y, r()), Lerp(s.boxMin.z, s.boxMax.z, r()));
            p += s.offset;
            float vx = Lerp(s.velocityMin.x, s.velocityMax.x, r()), vy = Lerp(s.velocityMin.y, s.velocityMax.y, r()), vz = Lerp(s.velocityMin.z, s.velocityMax.z, r());
            ParticleSpawn.DirectedVelocity(s.directionFrom, p.x, p.y, p.z, ref vx, ref vy, ref vz);
            var v = new Vector3(vx, vy, vz);

            var ep = new ParticleSystem.EmitParams();
            if (s.worldSpace)
            {
                var t = transform;
                ep.position = s.rotateWithEntity ? t.TransformPoint(p) : t.position + p;
                ep.velocity = s.rotateWithEntity ? t.TransformDirection(v) : v;
            }
            else
            {
                ep.position = p;
                ep.velocity = v;
            }
            float life = Lerp(s.lifetime.x, s.lifetime.y, r());
            if (screen)
            {
                // uniform size (every screen template); the overlay faces the camera like the G3 sprite
                var t = transform;
                bool local = !s.worldSpace;
                _screenSprites.Add(new ScreenSprite
                {
                    local = local,
                    position = local ? p : s.rotateWithEntity ? t.TransformPoint(p) : t.position + p,
                    velocity = local ? v : s.rotateWithEntity ? t.TransformDirection(v) : v,
                    age = preAge, life = Mathf.Max(0.01f, life), size = Lerp(s.sizeMin.x, s.sizeMax.x, r()) * _look.Size, seed = r(),
                    spin = s.spin ? Lerp(s.startSpinDegrees.x, s.startSpinDegrees.y, r()) : 0f,
                    spinRate = s.spin ? (r() < s.spinClockwise ? 1f : -1f) * Lerp(s.spinDegreesPerSecond.x, s.spinDegreesPerSecond.y, r()) : 0f,
                });
                return;
            }
            ep.applyShapeToPosition = false;
            ep.startLifetime = Mathf.Max(0.01f, life);
            float k = _look.Size;
            if (s.uniformSize) ep.startSize = Lerp(s.sizeMin.x, s.sizeMax.x, r()) * k;
            else ep.startSize3D = new Vector3(Lerp(s.sizeMin.x, s.sizeMax.x, r()), Lerp(s.sizeMin.y, s.sizeMax.y, r()), Lerp(s.sizeMin.z, s.sizeMax.z, r())) * k;
            if (s.spin)
            {
                float sign = r() < s.spinClockwise ? 1f : -1f;
                ep.rotation = Lerp(s.startSpinDegrees.x, s.startSpinDegrees.y, r());
                ep.angularVelocity = sign * Lerp(s.spinDegreesPerSecond.x, s.spinDegreesPerSecond.y, r());
            }
            ep.randomSeed = (uint)_rng.Next();
            if (s.revolve && s.worldSpace)
            {
                // G3 EmitParticles: centre = RevolutionCenterOffset + the emitter's location (Independent), rate random per particle
                var c = new Vector3(Lerp(s.revolutionCenterMin.x, s.revolutionCenterMax.x, r()), Lerp(s.revolutionCenterMin.y, s.revolutionCenterMax.y, r()),
                                    Lerp(s.revolutionCenterMin.z, s.revolutionCenterMax.z, r()));
                var rate = new Vector3(Lerp(s.revolutionMin.x, s.revolutionMax.x, r()), Lerp(s.revolutionMin.y, s.revolutionMax.y, r()),
                                       Lerp(s.revolutionMin.z, s.revolutionMax.z, r()));
                _revolution[ep.randomSeed] = (transform.position + c, rate);
            }
            system.Emit(ep, 1);
        }

        /// <summary>
        /// fix-179: G3 eCParticle_PS::DoProcessV2 revolution of world-space particles (EmitterRules.RevolvesInWorld): each
        /// particle's world position turns about its own centre by RevolutionsPerSecond (radians) × RevolutionScale × dt.
        /// Entries of particles that died are dropped when the table grows past the live count.
        /// </summary>
        void Revolve(float dt)
        {
            if (dt <= 0f) return;
            int cap = system.main.maxParticles;
            if (_revolveBuffer == null || _revolveBuffer.Length < cap) _revolveBuffer = new ParticleSystem.Particle[cap];
            int n = system.GetParticles(_revolveBuffer);
            var s = emitter;
            for (int i = 0; i < n; i++)
            {
                ref var p = ref _revolveBuffer[i];
                if (!_revolution.TryGetValue(p.randomSeed, out var rv)) continue;
                float t = p.startLifetime > 0 ? 1f - p.remainingLifetime / p.startLifetime : 0f;
                float ax = EmitterRules.RevolutionRadians(rv.rate.x, Scale(s.revolutionScaleX, t), dt);
                float ay = EmitterRules.RevolutionRadians(rv.rate.y, Scale(s.revolutionScaleY, t), dt);
                float az = EmitterRules.RevolutionRadians(rv.rate.z, Scale(s.revolutionScaleZ, t), dt);
                var pos = p.position;
                EmitterRules.Revolve(ref pos.x, ref pos.y, ref pos.z, rv.center.x, rv.center.y, rv.center.z, ax, ay, az);
                p.position = pos;
            }
            system.SetParticles(_revolveBuffer, n);
            if (_revolution.Count > 2 * n + 16)
            {
                var live = new System.Collections.Generic.HashSet<uint>();
                for (int i = 0; i < n; i++) live.Add(_revolveBuffer[i].randomSeed);
                var dead = new System.Collections.Generic.List<uint>();
                foreach (var k in _revolution.Keys) if (!live.Contains(k)) dead.Add(k);
                foreach (var k in dead) _revolution.Remove(k);
            }
        }

        static float Scale(AnimationCurve c, float t) => c != null && c.length > 0 ? c.Evaluate(t) : 1f;

        void AgeScreenSprites(float dt)
        {
            for (int i = _screenSprites.Count - 1; i >= 0; i--)
            {
                var sp = _screenSprites[i];
                sp.age += dt;
                if (sp.age >= sp.life) { _screenSprites.RemoveAt(i); continue; }
                sp.position += sp.velocity * dt;
                sp.spin += sp.spinRate * dt;
                _screenSprites[i] = sp;
            }
        }

        // eEDynamicLightEffect as the item lights (ItemEffectRules.Factor): every fire light in the data (torch, fire arrow,
        // VFX_Magic_LS_SmallFire/BigFire) uses value 3, which the [inferred] order names Blink — a hard on/off blink made the
        // burning-body light strobe; it is the fire wobble like Flicker.
        float LightFactor(float t) => ItemEffectRules.Factor((ItemEffectRules.LightEffect)lightEffect, t, _lightSeed, lightPeriod);

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
