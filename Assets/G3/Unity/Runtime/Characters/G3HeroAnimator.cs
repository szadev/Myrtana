using System;
using G3.Player;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Animation selector of the player hero: reads the movement state of the <see cref="IG3Locomotion"/> above it,
    /// composes G3 motion names (Docs/formats-animation.md §5) and plays them through the <see cref="G3MotionPlayer"/>.
    /// Movement is code driven — the G3 clips are in place and the engine moves the entity with the speed encoded in the
    /// motion name — so <see cref="G3LocomotionSelector"/> weights the clips by how well their speed matches the velocity.
    /// States: Locomotion (stand/turn/walk/run/sprint, 4 directions) → Jump (Jump_Stand) → Fall (Fall_Loop) →
    /// Land (Fall_End), HardLand (Fall_Recover, a fall that hurts: <see cref="FallRules"/>) or Roll (Fall_Hit, Perk_Acrobatic;
    /// <see cref="LandingKindOf"/>); Slide (Slide_Begin loop) on too-steep ground.
    /// Standing still in Locomotion plays the stand idle (<see cref="G3HeroIdle"/>: Ambient_Loop with random Ambient_Ambient fidgets).
    /// In water (<see cref="IG3WaterLocomotion"/>) the locomotion uses the Wade or Swim stance; falling into deep water
    /// plays DiveIn (<c>Hero_Swim_…_Fall_Hit</c>, which carries the splash sound).
    /// Also emits <see cref="Footstep"/> when a foot is planted (G3 locomotion clips carry no step effects). See Docs/hero.md.
    /// </summary>
    [RequireComponent(typeof(G3MotionPlayer))]
    [DefaultExecutionOrder(100)] // after the controller's Update, before G3MotionPlayer (200)
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Hero Animator")]
    public sealed class G3HeroAnimator : MonoBehaviour, IG3WeaponStance, IG3ParadeStance
    {
        public enum State { Locomotion, Jump, Fall, Land, HardLand, Slide, DiveIn, Roll }

        [Header("Motion names")]
        public string rig = "Hero";
        public string stance = "Stand";
        public string leftItem = "None", rightItem = "None";
        public string pose = "P0";

        [Header("Blending")]
        [Tooltip("Smoothing of the velocity that drives the locomotion weights, s")] public float velocitySmoothing = 0.1f;
        [Tooltip("Smoothing of the turn rate, s")] public float turnSmoothing = 0.15f;
        // Locomotion and the stand idle fade as G3's movement module does (MotionBlendRules: 0.2 s from a running motion,
        // a cut after a one-shot ended; 0.5 s into the idle). Jump, fall, landing and slide fades are not researched.
        public float jumpFade = 0.1f, fallFade = 0.25f, landFade = 0.1f, slideFade = 0.25f;
        [Tooltip("Air time after which an unplanned drop counts as falling, s")] public float fallDelay = 0.35f;
        [Tooltip("A jump holds its last frame until this air time before it turns into falling, s (flat-ground jump ≈ 0.66 s)")]
        public float jumpFallAfter = 0.8f;
        [Tooltip("Above this speed a landing goes straight back to locomotion, m/s")] public float landMoveSpeed = 0.5f;

        [Header("Stand idle")]
        [Tooltip("The hero stands idle below this speed, m/s")] public float idleMaxSpeed = 0.05f;
        [Tooltip("… and below this turn rate, °/s (G3: 10 °/s; faster turns play the turn motions)")] public float idleMaxTurn = 10f;

        [Header("Footsteps")]
        public string leftFootBone = "Hero_Left_Foot_Foot_1";
        public string rightFootBone = "Hero_Right_Foot_Foot_1";
        [Tooltip("World speed of a foot relative to the body speed: above = swinging, below plantRatio = planted")]
        public float swingRatio = 1.2f, plantRatio = 0.35f;
        [Tooltip("Minimum time between two steps of the same foot, s (G3 sprint cycle: 0.56 s)")]
        public float minStepInterval = 0.3f;

        /// <summary>A foot was planted (world position of the foot bone). Also raised once on landing.</summary>
        public event Action<Vector3> Footstep;

        public State Current { get; private set; }
        public G3LocomotionSelector Locomotion => _loco;
        /// <summary>Idle motion playing while the hero stands still (breathing loop or fidget), else null.</summary>
        public string IdleMotion => _idle?.Motion;
        /// <summary>
        /// The landing after a fall of the given seconds (G3 OnMovementModeChanged: ZS_Land / ZS_HeavyLand / ZS_Acrobat).
        /// Set by the game (it knows Perk_Acrobatic and the knock-out); default <see cref="FallRules.DefaultKind"/>.
        /// </summary>
        public Func<float, LandingKind> LandingKindOf { get; set; }
        /// <summary>
        /// The block is up and the combat animator has handed the body back: the locomotion uses the Parade stance
        /// (<c>Hero_Parade_None_1H_P0_Move_…</c>), so the hero walks while blocking (G3 PS_Melee_Loop keeps moving).
        /// </summary>
        public bool ParadeLocomotion { get; set; }
        /// <summary>
        /// Sneaking with a melee weapon (or fists) drawn: the sneak locomotion uses the Parade stance
        /// (<c>Hero_Parade_None_1H_P0_Move_…</c>; the data has no armed melee Sneak set). Set by PlayerCombat.
        /// </summary>
        public bool SneakInParade { get; set; }

        G3MotionPlayer _player;
        IG3Locomotion _src;
        G3LocomotionSelector _loco;
        G3HeroIdle _idle;
        G3MotionKey _key;
        Transform _leftFoot, _rightFoot;
        FootState _left, _right;
        float _lastYaw, _turnRate;
        Vector2 _localVel;
        bool _jumped, _subscribed, _yielded, _divedIn;
        float _landedAirTime = -1, _landedFallTime;

        void Awake()
        {
            _player = GetComponent<G3MotionPlayer>();
            _idle = new G3HeroIdle(_player);
            _key = StanceKey(stance);
            _leftFoot = FindDeep(transform, leftFootBone);
            _rightFoot = FindDeep(transform, rightFootBone);
            _lastYaw = transform.eulerAngles.y;
        }

        void OnEnable()
        {
            ResolveSource();
            if (EnsureSelector()) Enter(State.Locomotion, 0);
        }

        bool EnsureSelector()
        {
            if (_loco != null) return true;
            if (_player == null || _player.library == null) return false;
            _loco = new G3LocomotionSelector(_player.library, _key, G3.Player.StancePose.MoveFallbackPose(_key.Stance, leftItem, pose));
            return true;
        }

        void OnDisable() => Subscribe(false);

        /// <summary>
        /// The controller above this model. Looked up lazily: at scene start the streamer may still hold the player
        /// (its behaviours disabled), where the lookup can come back empty.
        /// </summary>
        void ResolveSource()
        {
            if (_src == null)
                foreach (var mb in GetComponentsInParent<MonoBehaviour>(true))
                    if (mb is IG3Locomotion l) { _src = l; break; }
            Subscribe(true);
        }

        void Subscribe(bool on)
        {
            if (_src == null || on == _subscribed) return;
            _subscribed = on;
            if (on) { _src.Jumped += OnJumped; _src.Landed += OnLanded; }
            else { _src.Jumped -= OnJumped; _src.Landed -= OnLanded; }
            if (_src is IG3WaterLocomotion w)
            {
                if (on) w.DivedIn += OnDivedIn;
                else w.DivedIn -= OnDivedIn;
            }
        }

        void OnJumped() => _jumped = true;
        void OnDivedIn(float fallSeconds) => _divedIn = true;

        void OnLanded(float airTime, float fallSeconds)
        {
            _landedAirTime = airTime;
            _landedFallTime = fallSeconds;
            if (airTime > fallDelay) Footstep?.Invoke(Vector3.Lerp(Pos(_leftFoot), Pos(_rightFoot), 0.5f));
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            if (_src == null) ResolveSource();
            if (_loco == null)
            {
                if (!EnsureSelector()) return;
                Enter(State.Locomotion, 0);
            }
            if (_player.Claimed)
            {
                // A combat one-shot has the player; take over again from locomotion when it ends.
                _yielded = true;
                _jumped = false;
                _landedAirTime = -1;
                return;
            }
            if (_yielded)
            {
                _yielded = false;
                Enter(State.Locomotion, LocomotionFade);
            }

            PushHandSpeeds();
            float yaw = transform.eulerAngles.y;
            float rawTurn = Mathf.DeltaAngle(_lastYaw, yaw) / dt;
            _lastYaw = yaw;
            bool active = _src != null && _src.IsActive;
            var v = active ? _src.Velocity : Vector3.zero;
            var local = transform.InverseTransformDirection(new Vector3(v.x, 0, v.z));
            float kv = 1 - Mathf.Exp(-dt / Mathf.Max(1e-3f, velocitySmoothing));
            float kt = 1 - Mathf.Exp(-dt / Mathf.Max(1e-3f, turnSmoothing));
            _localVel = Vector2.Lerp(_localVel, new Vector2(local.x, local.z), kv);
            _turnRate = Mathf.Lerp(_turnRate, active ? rawTurn : 0, kt);
            float speed = new Vector2(local.x, local.z).magnitude;

            bool grounded = !active || _src.IsGrounded;
            bool sliding = active && _src.IsSliding;
            float air = active ? _src.AirTime : 0;
            var water = active && _src is IG3WaterLocomotion wl ? wl.Water : WaterMode.None;
            bool sneak = active && _src.Sneaking && grounded && !sliding && !_jumped && Current == State.Locomotion;
            UpdateStance(water == WaterMode.Swim ? SwimStance : water == WaterMode.Wade ? WadeStance : ParadeLocomotion ? ParadeStance :
                         sneak ? (SneakInParade ? ParadeStance : SneakStance) : StandStance);
            bool jumped = _jumped, divedIn = _divedIn;
            float landed = _landedAirTime;
            _jumped = _divedIn = false;
            _landedAirTime = -1;
            if (divedIn && Current != State.DiveIn) Enter(State.DiveIn, fallFade);
            else if (water == WaterMode.Swim && Current != State.Locomotion && Current != State.DiveIn) Enter(State.Locomotion, LocomotionFade);

            switch (Current)
            {
                case State.Locomotion:
                case State.Land:
                    if (jumped) Enter(State.Jump, jumpFade);
                    else if (sliding) Enter(State.Slide, slideFade);
                    else if (!grounded && air > fallDelay) Enter(State.Fall, fallFade);
                    else if (Current == State.Land && (speed > landMoveSpeed || _player.CurrentFinished)) Enter(State.Locomotion, LocomotionFade);
                    break;
                case State.Jump:
                    if (landed >= 0 || (grounded && _player.CurrentElapsed > 0.2f)) Land(landed >= 0 ? _landedFallTime : 0, speed);
                    else if (_player.CurrentFinished && air > jumpFallAfter) Enter(State.Fall, fallFade);
                    break;
                case State.Fall:
                    if (landed >= 0 || grounded) Land(landed >= 0 ? _landedFallTime : 0, speed);
                    else if (sliding) Enter(State.Slide, slideFade);
                    break;
                case State.HardLand:
                case State.Roll:
                    if (_player.CurrentFinished) Enter(State.Locomotion, LocomotionFade);
                    else if (jumped) Enter(State.Jump, jumpFade);
                    break;
                case State.Slide:
                    if (!sliding) Enter(grounded ? State.Locomotion : State.Fall, slideFade);
                    break;
                case State.DiveIn:
                    if (_player.CurrentFinished || (speed > landMoveSpeed && _player.CurrentElapsed > 0.5f)) Enter(State.Locomotion, LocomotionFade);
                    break;
            }

            if (Current == State.Locomotion)
            {
                if (UpdateIdle(active && grounded && !sliding, speed, rawTurn, dt)) return;
                _player.SetWeights(_loco.Evaluate(_localVel, Mathf.Abs(speed) < 0.3f ? _turnRate : 0));
                _player.SetSpeed(_loco.PlaybackRate);
            }
        }

        /// <summary>
        /// Combat stance between one-shots: locomotion with the weapon's hand fields (<c>Hero_Stand_None_1H_P0_Move_…</c>,
        /// <c>Hero_Stand_Bow_Arrow_P0_Move_…</c>) when the library has that set, else unarmed. The weapon itself is shown
        /// by the game's weapon view.
        /// </summary>
        public void SetWeaponStance(string weaponField, string weaponTemplate, string ammoTemplate = null)
        {
            if (string.IsNullOrEmpty(weaponField)) weaponField = null; // runner-15: "" (no weapon) made the torch set key "Hero_Stand_Torch__…"
            _stanceField = weaponField;
            _stanceTemplate = weaponTemplate;
            _stanceAmmo = ammoTemplate;
            string left = "None", right = "None";
            bool found = false;
            // An item in the left hand (the torch): Hero_Stand_Torch_None_… / Hero_Stand_Torch_1H_… when the library has the set.
            if (_player.library != null && G3CombatMotions.TakesOffHand(weaponField, OffHand))
            {
                var armed = _key.With("Move", "Run");
                armed.Stance = StandStance;
                armed.Pose = pose;
                armed.LeftItem = OffHand;
                armed.RightItem = weaponField ?? "None";
                if (_player.library.Find(armed) != null) { left = OffHand; right = armed.RightItem; found = true; }
            }
            for (string field = found ? null : weaponField; field != null && _player.library != null; field = G3CombatMotions.Fallback(field))
            {
                var (l, r) = G3CombatMotions.Hands(field, ammoTemplate != null);
                var armed = _key.With("Move", "Run");
                armed.Stance = StandStance; // the standing set decides (the Parade set has no Run Fwd)
                armed.Pose = pose;
                armed.LeftItem = l;
                armed.RightItem = r;
                if (_player.library.Find(armed) != null) { left = l; right = r; break; }
            }
            if (left == leftItem && right == rightItem && _loco != null) return;
            leftItem = left;
            rightItem = right;
            _key = StanceKey(stance);
            _loco = null;
            if (!EnsureSelector()) return;
            if (Current == State.Locomotion && !_player.Claimed) Enter(State.Locomotion, LocomotionFade);
        }

        /// <summary>
        /// Locomotion in the Sneak, Wade or Swim stance (<c>Hero_Sneak_None_None_P0_Move_…</c>, <c>Hero_Wade_…</c>,
        /// <c>Hero_Swim_…</c>) when the library has that set for the hands; sneaking with a melee weapon walks in the Parade
        /// set (<see cref="SneakInParade"/>), a missing set falls back to standing; jumps, falls and landings always use the
        /// standing stance.
        /// </summary>
        void UpdateStance(string want)
        {
            if (want == stance) return;
            // Sneaking with a melee set the rig lacks in the Parade stance: the Sneak set of the hands, else standing.
            if (want == ParadeStance && SneakInParade && !ParadeLocomotion && !HasWalk(want)) want = SneakStance;
            if (want != StandStance && !HasWalk(want)) want = StandStance;
            if (want == stance) return;
            stance = want;
            _key = StanceKey(stance);
            _loco = null;
            if (EnsureSelector() && Current == State.Locomotion && !_player.Claimed) Enter(State.Locomotion, LocomotionFade);
        }

        string _pushedHands;

        /// <summary>
        /// Gives the controller the root speeds of the standing set of the hands in use and of the unarmed set (G3 moves
        /// by the motion: a drawn sword sprints at its Sprint motion's 500 cm/s, <see cref="G3.Player.HeroGaitSpeeds"/>).
        /// </summary>
        void PushHandSpeeds()
        {
            if (!(_src is G3Player gp) || _player.library == null) return;
            string hands = leftItem + "|" + rightItem + "|" + pose;
            if (hands == _pushedHands) return;
            _pushedHands = hands;
            gp.SetHandSpeeds(StandSpeeds(leftItem, rightItem), StandSpeeds("None", "None"));
        }

        G3.Player.HeroGaitSpeeds StandSpeeds(string left, string right)
        {
            var key = new G3MotionKey(rig, "Move", "Stand", "Fwd", StandStance, left, right, StancePose.For(StandStance, left, pose));
            var sel = new G3LocomotionSelector(_player.library, key, G3.Player.StancePose.MoveFallbackPose(StandStance, left, pose));
            return G3.Player.HeroGaitSpeeds.From(sel.Speed);
        }

        bool HasWalk(string st) => _player.library.Find(StanceKey(st).With("Move", "Walk")) != null;

        /// <summary>
        /// Locomotion key of a stance with the current hands. Shield sets use pose P1 (<see cref="StancePose"/>): with P0
        /// the parade fell back to the standing walk once the block was up (no P0 parade set), and standing showed the 1H
        /// fencing pose with the shield flat across the back.
        /// </summary>
        G3MotionKey StanceKey(string st) => new G3MotionKey(rig, "Move", "Stand", "Fwd", st, leftItem, rightItem, StancePose.For(st, leftItem, pose));

        const string StandStance = "Stand", SneakStance = "Sneak", WadeStance = "Wade", SwimStance = "Swim", ParadeStance = "Parade";

        /// <summary>The current key in the standing stance (jumps, falls, landings and slides exist only standing).</summary>
        G3MotionKey StandKey
        {
            get
            {
                var k = _key;
                k.Stance = StandStance;
                k.Pose = pose;
                // Hero_Stand_Torch_None has no jumps or falls: those play with free hands (the torch stays shown).
                if (k.LeftItem != "None" && k.LeftItem == OffHand && _player.library != null && _player.library.Find(k.With("Jump", "Stand")) == null)
                    k.LeftItem = "None";
                return k;
            }
        }

        string _stanceField, _stanceTemplate, _stanceAmmo;

        /// <summary>The item field in the left hand beside the weapon ("Torch"; null = none): locomotion uses its set.</summary>
        public string OffHand { get; private set; }

        /// <summary>The locomotion set in use (stance, hands, pose), for logs: "Hero_Stand_Torch_None_P0_Move_Stand_N_Fwd_00".</summary>
        public string LocomotionKey => _key.ToString();

        /// <summary>Puts an item into the left-hand field (the hero's torch) or clears it, and re-selects the locomotion set.</summary>
        public void SetOffHand(string field)
        {
            if (string.IsNullOrEmpty(field)) field = null;
            // Same field: nothing to do unless the set was not taken yet (asked before the library was there).
            if (field == OffHand && (field == null || leftItem == field)) return;
            OffHand = field;
            SetWeaponStance(_stanceField, _stanceTemplate, _stanceAmmo);
        }

        /// <summary>
        /// Plays the stand idle (<see cref="G3HeroIdle"/>) while the hero stands still with its controls on; any movement or
        /// turn, a jump, a combat claim or switched-off controls (dialog, pages) end it at once.
        /// </summary>
        bool UpdateIdle(bool canIdle, float speed, float rawTurn, float dt)
        {
            bool still = canIdle && speed < idleMaxSpeed && Mathf.Abs(rawTurn) < idleMaxTurn &&
                         (_idle.Playing || (_localVel.magnitude < idleMaxSpeed && Mathf.Abs(_turnRate) < idleMaxTurn));
            if (still && _idle.Update(_key, dt)) return true;
            if (_idle.Playing)
            {
                _idle.Stop();
                _player.PlayBlend(_loco.SetKey, _loco.Keys, LocomotionFade);
            }
            return false;
        }

        void Land(float fallSeconds, float speed)
        {
            var kind = LandingKindOf != null ? LandingKindOf(fallSeconds) : FallRules.DefaultKind(fallSeconds);
            if (kind == LandingKind.Heavy) Enter(State.HardLand, landFade);
            else if (kind == LandingKind.Acrobatic) Enter(State.Roll, landFade);
            else if (speed > landMoveSpeed) Enter(State.Locomotion, LocomotionFade);
            else Enter(State.Land, landFade);
        }

        /// <summary>G3's fade into locomotion (gCCharacterMovement): the standard 0.2 s, none after a one-shot ended.</summary>
        float LocomotionFade => _player.FadeFor(MotionStarter.Locomotion, null);

        void Enter(State s, float fade)
        {
            Current = s;
            _idle.Stop();
            switch (s)
            {
                case State.Locomotion: _player.PlayBlend(_loco.SetKey, _loco.Keys, fade); break;
                case State.Jump: _player.Play(StandKey.With("Jump", "Stand"), fade, true); break;
                case State.Fall: _player.Play(StandKey.With("Fall", "Loop"), fade); break;
                case State.Land: _player.Play(StandKey.With("Fall", "End"), fade, true); break;
                case State.HardLand: _player.Play(StandKey.With("Fall", "Recover"), fade, true); break;
                // ZS_Acrobat: the combat move "Fall" = Hero_Stand_…_Fall_Hit_N_Fwd_00 (forward roll; its 430 cm are
                // travelled by HeroFallDamage.Tick, the clip itself plays in place).
                case State.Roll:
                    if (!_player.Play(StandKey.With("Fall", "Hit"), fade, true)) Enter(State.Land, fade);
                    break;
                case State.Slide: _player.Play(StandKey.With("Slide", "Begin"), fade); break;
                case State.DiveIn:
                    // Hero_Swim_None_None_P0_Fall_Hit_N_Fwd_00 (2 s, effect track eff_ani_dropinwater_01 at 0 s).
                    var dive = _key;
                    dive.Stance = SwimStance;
                    if (!_player.Play(dive.With("Fall", "Hit"), fade, true)) Enter(State.Locomotion, fade);
                    break;
            }
        }

        /// <summary>
        /// The library has the dive motion (<c>Hero_Swim_…_Fall_Hit</c>), whose effect track plays the splash
        /// (<c>eff_ani_dropinwater_01</c>, G3 ZS_DiveIn): other splash sounds must then stay silent (one splash per fall).
        /// </summary>
        public bool HasDiveInMotion
        {
            get
            {
                if (_player == null || _player.library == null) return false;
                var dive = _key;
                dive.Stance = SwimStance;
                return _player.library.Find(dive.With("Fall", "Hit")) != null;
            }
        }

        struct FootState { public Vector3 last; public bool valid, swinging; public float lastStep; }

        void LateUpdate()
        {
            // Animation has been evaluated. A foot is planted when its world speed drops to a fraction of the body speed
            // after a swing (works for every direction and gait, and for blends).
            float dt = Time.deltaTime;
            // Hitches (dt > 0.1 s) are skipped: the controller clamps its step, so foot and body speeds would not compare.
            bool track = _src != null && _src.IsActive && _src.IsGrounded && !_src.IsSliding && Current == State.Locomotion && !_player.Claimed && dt > 0 && dt <= 0.1f
                         && stance != SwimStance;
            var v = track ? _src.Velocity : Vector3.zero;
            float body = new Vector2(v.x, v.z).magnitude;
            track &= body > 0.3f;
            Foot(_leftFoot, ref _left, track, body, dt);
            Foot(_rightFoot, ref _right, track, body, dt);
        }

        void Foot(Transform foot, ref FootState s, bool track, float body, float dt)
        {
            if (foot == null) return;
            var p = foot.position;
            if (!track || !s.valid) { s.last = p; s.valid = track; s.swinging = false; return; }
            var d = p - s.last;
            s.last = p;
            float ratio = new Vector2(d.x, d.z).magnitude / dt / body;
            if (ratio > swingRatio) s.swinging = true;
            else if (s.swinging && ratio < plantRatio)
            {
                s.swinging = false;
                if (Time.time - s.lastStep < minStepInterval) return;
                s.lastStep = Time.time;
                Footstep?.Invoke(p);
            }
        }

        Vector3 Pos(Transform t) => t != null ? t.position : transform.position;

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
