using G3.Audio;
using G3.Unity.Characters;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Player footsteps from g3.efm (eff_step_human_{gait}_{side}_{surface}_01). Cadence comes from the horizontal
    /// speed of the CharacterController (or the transform) while grounded — or, when the player has a
    /// <see cref="G3HeroAnimator"/>, from its foot-plant events (at the foot bone); the surface from the material under the
    /// feet (<see cref="SurfaceProbe"/>), else the region default. <see cref="Step"/> can also be called directly.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/Audio/G3 Footsteps")]
    public sealed class G3Footsteps : MonoBehaviour
    {
        public G3AudioLibrary library;
        [Tooltip("Speeds (m/s) that separate walk / run / sprint; match G3Player.walkSpeed / runSpeed.")]
        public float walkSpeed = 4.5f, runSpeed = 8f;
        [Range(0, 1)] public float volume = 0.8f;
        [Tooltip("Time steps from the speed. Ignored while a G3HeroAnimator below this object raises Footstep events.")]
        public bool autoCadence = true;
        public bool useHeroAnimator = true;
        [Tooltip("Feet are this far below the transform (0 for a CharacterController pivot at the feet).")]
        public float feetOffset = 0f;

        [Header("Current (read only)")]
        [SerializeField] FootstepGait gait;
        [SerializeField] string surface;
        [SerializeField] string surfaceSource;

        CharacterController _cc;
        AudioSource _src;
        G3WorldStreamer _streamer;
        readonly System.Random _rng = new System.Random();
        G3HeroAnimator _hero;
        Vector3 _lastPos;
        float _phase, _nextHeroLookup;
        bool _left;

        void Start()
        {
            _cc = GetComponent<CharacterController>();
            _streamer = FindAnyObjectByType<G3WorldStreamer>();
            var go = new GameObject("Footstep Audio");
            go.transform.SetParent(transform, false);
            _src = go.AddComponent<AudioSource>();
            G3AudioMixer.Route(_src, G3AudioBus.Effects);
            _src.playOnAwake = false;
            _src.spatialBlend = 1f;
            _src.minDistance = 1f;
            _src.maxDistance = 30f;
            _src.dopplerLevel = 0f;
            _lastPos = transform.position;
            _player = GetComponent<G3Player>();
            if (_player != null) _player.DivedIn += OnDivedIn;
        }

        void OnDestroy()
        {
            if (_hero != null) _hero.Footstep -= OnHeroFootstep;
            if (_player != null) _player.DivedIn -= OnDivedIn;
        }

        /// <summary>G3 ZS_DiveIn: the splash of falling into deep water. The hero's dive motion carries it on its effect track
        /// (G3MotionSounds), so it is played here only for a body without that motion: one splash per fall (it played twice).
        /// Landing and wading have no sounds of their own in G3 (no _Land_ step effects; wade waves are visual only).</summary>
        void OnDivedIn(float fallSeconds)
        {
            if (_hero == null && useHeroAnimator) HookHeroAnimator();
            if (_hero != null && _hero.isActiveAndEnabled && _hero.HasDiveInMotion) return;
            G3EffectSounds.Play(DiveInEffect, transform.position, transform);
        }

        const string DiveInEffect = "eff_ani_dropinwater_01";
        G3Player _player;

        void Update()
        {
            var pos = transform.position;
            float dt = Time.deltaTime;
            Vector3 v = _cc != null && _cc.enabled ? _cc.velocity : (dt > 0 ? (pos - _lastPos) / dt : Vector3.zero);
            _lastPos = pos;
            bool grounded = _cc == null || !_cc.enabled || _cc.isGrounded;
            gait = grounded ? FootstepRules.GaitFor(new Vector2(v.x, v.z).magnitude, walkSpeed, runSpeed) : FootstepGait.None;

            if (useHeroAnimator && _hero == null && Time.time >= _nextHeroLookup) HookHeroAnimator();
            if (!autoCadence || (_hero != null && _hero.isActiveAndEnabled)) return;
            if (gait == FootstepGait.None) { _phase = 0.5f; return; }   // first step comes half an interval after starting
            _phase += dt / FootstepRules.StepInterval(gait);
            if (_phase < 1f) return;
            _phase -= Mathf.Floor(_phase);
            Step(_left);
            _left = !_left;
        }

        void HookHeroAnimator()
        {
            _nextHeroLookup = Time.time + 1f;
            _hero = GetComponentInChildren<G3HeroAnimator>();
            if (_hero != null) _hero.Footstep += OnHeroFootstep;
        }

        void OnHeroFootstep(Vector3 foot)
        {
            if (!useHeroAnimator) return;
            StepAt(foot, _left);
            _left = !_left;
        }

        /// <summary>Plays one step at the player's feet (also usable from animation events).</summary>
        public void Step(bool left) => StepAt(transform.position + Vector3.down * feetOffset, left);

        /// <summary>Plays one step at a world position (a foot bone).</summary>
        public void StepAt(Vector3 feet, bool left)
        {
            if (library == null || _src == null) return;
            surface = SurfaceProbe.Probe(feet, transform, out surfaceSource) ?? FootstepRules.RegionDefault(_streamer != null ? _streamer.CurrentRegion : null);
            // Feet more than 1 cm in water: the floor material is water (G3 CheckChangedMovementStates). Water meshes
            // have no colliders, so the probe sees the ground under the water.
            if (G3Water.BodyDepth(feet) > G3.Player.SwimRules.WetFeetDepth)
            {
                surface = "water";
                surfaceSource = "water zone";
            }
            var g = gait == FootstepGait.None ? FootstepGait.Walk : gait;
            var fx = library.GetEffect(FootstepRules.EffectName(g, left, surface))
                     ?? library.GetEffect(FootstepRules.EffectName(g, left, "earth"));
            var clip = fx != null ? library.GetClip(fx.PickClip(_rng)) : null;
            if (clip == null) return;
            _src.minDistance = fx.minDistance;
            _src.maxDistance = Mathf.Max(fx.minDistance + 1f, fx.maxDistance);
            _src.transform.position = feet;
            _src.PlayOneShot(clip, fx.volume * volume);
        }
    }
}
