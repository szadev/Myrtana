using System;
using G3.Player;
using G3.Unity.Characters;
using UnityEngine;
using UnityEngine.InputSystem;

namespace G3.Unity
{
    public enum G3MoveMode { Walk, Run, Sprint, Sneak }

    /// <summary>
    /// Player controller for imported Gothic 3 regions (CharacterController, new Input System): input, look (yaw/pitch),
    /// movement, jumping, sliding on slopes steeper than the walkable limit, fly mode. Pivot = feet.
    /// Rendering is elsewhere: <see cref="G3PlayerCamera"/> places the camera (third person by default, first person
    /// toggle), the hero model (child, <see cref="G3HeroAnimator"/>) animates from the <see cref="IG3Locomotion"/> state
    /// exposed here. G3 movement: running is the default gait; the speeds are the root speeds of the hero motions
    /// (Docs/hero.md). Controls are the G3 session keys of <see cref="G3Input"/> (CP 1.75 defaults, rebindable in the
    /// Controls menu): WASD / arrows move, mouse X/Y and Q/E turn, Shift (hold) sprint, Caps Lock walk toggle, Left Alt /
    /// Num 0 jump, Ctrl sneak. Noclip (console noclip / fly, fix-131): <see cref="SetNoclip"/>. Dev keys: click = capture mouse, Esc = release, F fly mode (FlyCamera), [ / ] time of day
    /// −/+ 1 h (G3Environment), F1 help.
    /// Tests can drive it without a keyboard via <see cref="scriptedMove"/>, <see cref="scriptedWalk"/>,
    /// <see cref="scriptedSprint"/>, <see cref="scriptedJump"/> and <see cref="scriptedTurn"/>.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Player")]
    public sealed class G3Player : MonoBehaviour, IG3Locomotion, IG3WaterLocomotion
    {
        [Header("Movement (m/s)")]
        public float walkSpeed = 1.6f;
        public float runSpeed = 4f;
        public float strafeRunSpeed = 3f;
        public float backRunSpeed = 4f;
        public float sprintSpeed = 6f;
        [Tooltip("Sneaking speed, m/s (the Hero_Sneak_* walk clips are 150 cm/s)")] public float sneakSpeed = 1.5f;
        public float jumpHeight = 1.1f;
        public float gravity = 20f;
        [Tooltip("Air control 0..1")] public float airControl = 0.3f;

        [Header("Sliding on slopes steeper than CharacterController.slopeLimit")]
        [Tooltip("× gravity × sin(slope)")] public float slideAcceleration = 0.7f;
        [Tooltip("G3 hero SlideSpeedMax 800 cm/s")] public float maxSlideSpeed = SlopeRules.SlideSpeedMax;
        [Tooltip("Sideways steering while sliding, m/s")] public float slideSteer = 1f;

        [Header("Water (G3 hero gCCharacterMovement_PS, Docs/hero.md: Water and swimming)")]
        [Tooltip("SwimDepth: a swimmer's feet hang this far below the surface, m (hero 155 cm)")] public float swimDepth = SwimRules.HeroSwimDepth;
        [Tooltip("Time to settle at the swim depth, s")] public float swimSettle = 0.25f;

        [Header("Look")]
        public Camera playerCamera;
        public float eyeHeight = 1.83f;   // G3 hero head bone sits at 1.78–1.94 m
        public float lookSensitivity = 0.12f;
        [Tooltip("Turning with the RotateCam keys (G3 default Q/E), degrees per second [interp]")] public float keyTurnSpeed = 150f;
        public float minPitch = -85f, maxPitch = 85f;
        /// <summary>Cleared by the camera while it orbits freely (the mouse then turns the camera, not the hero).</summary>
        [NonSerialized] public bool lookInputEnabled = true;
        /// <summary>False while the body is busy (a combat move): walking and jumping input is ignored, gravity still applies.</summary>
        [NonSerialized] public bool moveInputEnabled = true;
        /// <summary>
        /// The block is held (G3 Parade stance): walking uses the parade motion speeds (<see cref="ParadeMovement"/>:
        /// 3.5 m/s forward/back, 2 / 4 m/s sideways), no sprint, no jump, no sneak.
        /// </summary>
        [NonSerialized] public bool paradeWalk;
        /// <summary>
        /// The keys walk (no run, no sprint): set while aiming a bow or crossbow (G3 PS_Ranged_Aim: SetMovementMode walk,
        /// <see cref="HeroMoveRules"/>), so the archer-pose Walk motions keep the bow on the crosshair.
        /// </summary>
        [NonSerialized] public bool forceWalk;
        /// <summary>
        /// The state keeps its gait (G3: a melee move or a spell cast does not run the gait update, <see cref="HeroMoveRules.UpdatesGait"/>):
        /// walk, run or sprint stay as they were when it began.
        /// </summary>
        [NonSerialized] public bool holdGait;
        /// <summary>Stamina points for the sprint gate (G3 FUN_10002130: start at 21, keep at 1); int.MaxValue = no gate.</summary>
        [NonSerialized] public int sprintStamina = int.MaxValue;

        [Header("Time of day")]
        public float timeStepHours = 1f;

        [Header("Test input (consumed every frame while non-zero)")]
        public Vector2 scriptedMove;
        public bool scriptedWalk;
        public bool scriptedSprint;
        public bool scriptedJump;
        [Tooltip("Toggles sneaking once (tests)")] public bool scriptedSneakToggle;
        [Tooltip("Yaw degrees per second")] public float scriptedTurn;
        [Tooltip("As if the Camera rotation keys were held (G3 Q/E): -1 left, +1 right (tests, fix-73)")] public float scriptedKeyTurn;
        /// <summary>
        /// Only the scripted input moves and turns the hero (keys and mouse are ignored): a scripted walk such as the
        /// approach to a used object (G3 PS_Interact → _AI_Goto_Ext, fix-75), animated like any walk.
        /// </summary>
        [NonSerialized] public bool scriptedOnly;

        /// <summary>
        /// The port's option (fix-73, Docs/controls-options.md section 7): the Camera rotation keys (G3 Q / E) orbit the
        /// third-person camera round the hero instead of turning him (<see cref="KeyOrbitRules"/>). Off = G3. Set by the game's options.
        /// </summary>
        public static bool TurnKeysOrbitCamera { get; set; }

        /// <summary>
        /// fix-205: asked when Sneak goes down; true holds the toggle back until the key is released (the quick-loot chord
        /// shares Ctrl with Sneak and the hero faces something to loot). Set by the focus (Myrtana PlayerInteraction).
        /// </summary>
        public System.Func<bool> DeferSneakToggle { get; set; }

        readonly ChordedToggle _sneakKey = new ChordedToggle();

        /// <summary>fix-205: the held Sneak key served the quick-loot chord (Ctrl + click): its release does not toggle sneaking.</summary>
        public void ConsumeSneakKey() => _sneakKey.Consume();

        /// <summary>The hero's player state (set by the game's combat each frame): out of <see cref="HeroMoveState.Normal"/> the keys turn the hero.</summary>
        [NonSerialized] public HeroMoveState moveState;

        public bool IsFlying { get; private set; }
        /// <summary>
        /// fix-131: noclip (dev console <c>noclip</c> / <c>fly</c>, also in the demo): the hero flies through geometry, his
        /// controller off (no collision, gravity, ground check, world catch or fall damage; <see cref="SetNoclip"/>).
        /// </summary>
        public bool IsNoclip { get; private set; }
        /// <summary>Noclip base speed, m/s (Shift × <see cref="NoclipRules.FastFactor"/>).</summary>
        [NonSerialized] public float noclipSpeed = NoclipRules.Speed;
        /// <summary>Standing on ground, or swimming (a swimmer is carried by the water: no falling, no air time).</summary>
        public bool IsGrounded => _cc != null && _cc.enabled && (_cc.isGrounded || Water == WaterMode.Swim);
        public bool IsSliding { get; private set; }
        public bool IsActive => isActiveAndEnabled && !IsFlying && _cc != null && _cc.enabled;
        public float AirTime { get; private set; }
        /// <summary>
        /// Seconds in the falling state (G3 movement mode FALL): air time, without the rising part of a jump. Fall damage
        /// and the hard landing use it (<see cref="FallRules"/>); 0 on the ground.
        /// </summary>
        public float FallTime { get; private set; }
        /// <summary>Actual world velocity of the last step (after collisions).</summary>
        public Vector3 Velocity { get; private set; }
        /// <summary>Velocity the controller tries to move with (before collisions).</summary>
        public Vector3 DesiredVelocity => _velocity;
        public G3MoveMode MoveMode { get; private set; } = G3MoveMode.Run;
        /// <summary>Sneaking (G3: the Down/Sneak key, Ctrl, toggles it): slow and silent; a jump ends it.</summary>
        public bool Sneaking { get; private set; }
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        /// <summary>
        /// When set, the body faces this yaw instead of the look yaw while walking stays relative to the look yaw
        /// (G3's alignment to a combat target: the hero strafes around it, the camera keeps following the mouse).
        /// </summary>
        [NonSerialized] public float? bodyYaw;
        /// <summary>The movement keys of this frame (x right, y forward), also while walking is locked.</summary>
        public Vector2 MoveKeys { get; private set; }
        /// <summary>Slope of the ground face under the feet, degrees.</summary>
        public float GroundAngle { get; private set; }

        public event Action Jumped;
        /// <summary>Touched the ground after more than 0.15 s in the air: (air time, <see cref="FallTime"/>) in seconds.</summary>
        public event Action<float, float> Landed;

        /// <summary>How the hero stands in water (<see cref="SwimRules.Classify"/>); None on land.</summary>
        public WaterMode Water { get; private set; }
        public bool Swimming => Water == WaterMode.Swim;
        /// <summary>Depth of the feet under the water surface, m (0 or less, or NaN: dry).</summary>
        public float WaterDepth { get; private set; } = float.NaN;
        /// <summary>Height of the water surface over the hero (negative infinity without water).</summary>
        public float WaterSurface { get; private set; } = float.NegativeInfinity;
        /// <summary>(old mode, new mode), also after a teleport or a loaded save puts the hero into or out of water.</summary>
        public event Action<WaterMode, WaterMode> WaterChanged;
        /// <summary>Fell into deep water (G3 ZS_DiveIn): seconds of the fall.</summary>
        public event Action<float> DivedIn;

        CharacterController _cc;
        Vector3 _velocity;
        float _yaw, _pitch;
        bool _walkToggle;
        WalkKeyFlag _walkKey;
        HeroGait _gait = HeroGait.Run;
        HeroGaitSpeeds? _handSpeeds, _unarmedSpeeds;

        /// <summary>
        /// The standing locomotion set of the hands in use and the unarmed one (G3HeroAnimator): the controller's speed
        /// fields are the unarmed motion speeds, scaled per gait and direction by <see cref="HeroGaitSpeeds.Factor"/>
        /// (drawn melee/fists/spell sprint 5 m/s forward, 4 m/s sideways; crossbow walks 2 m/s …). Null = unarmed.
        /// </summary>
        public void SetHandSpeeds(HeroGaitSpeeds? hands, HeroGaitSpeeds? unarmed)
        {
            _handSpeeds = hands;
            _unarmedSpeeds = unarmed;
        }

        float Factor(G3MoveMode mode, HeroMoveDir dir)
        {
            if (!_handSpeeds.HasValue || !_unarmedSpeeds.HasValue) return 1f;
            var gait = mode == G3MoveMode.Sprint ? HeroGait.Sprint : mode == G3MoveMode.Walk ? HeroGait.Walk : mode == G3MoveMode.Sneak ? HeroGait.Sneak : HeroGait.Run;
            return HeroGaitSpeeds.Factor(_handSpeeds.Value, _unarmedSpeeds.Value, gait, dir);
        }
        bool _showHelp;
        readonly MouseLookFilter _look = new MouseLookFilter();
        FlyCamera _fly;

        /// <summary>The camera looks behind the hero (session key Look held; set by <see cref="G3PlayerCamera"/>).</summary>
        [NonSerialized] public bool LookingBehind;

        /// <summary>
        /// fix-105 (owner: a clean hero on load / new game, G3 rebuilds the player from the save): every input and gait flag
        /// the game's systems set while playing goes back to its rest value — scripted input, input switches, parade walk,
        /// forced walk, held gait, sprint stamina, move state, body yaw, look-behind. The systems set them again each frame
        /// as the new game needs.
        /// </summary>
        public void ResetTransientState()
        {
            scriptedMove = Vector2.zero;
            scriptedWalk = scriptedSprint = scriptedJump = false;
            scriptedOnly = false;
            lookInputEnabled = moveInputEnabled = true;
            paradeWalk = forceWalk = holdGait = false;
            sprintStamina = int.MaxValue;
            moveState = G3.Player.HeroMoveState.Normal;
            bodyYaw = null;
            LookingBehind = false;
        }
        G3PlayerCamera _rig;

        /// <summary>The Camera rotation keys orbit the camera now instead of turning the hero (<see cref="KeyOrbitRules.Orbits"/>).</summary>
        public bool TurnKeysOrbit =>
            KeyOrbitRules.Orbits(TurnKeysOrbitCamera, moveState, _rig != null && _rig.isActiveAndEnabled && _rig.thirdPerson, LookingBehind);

        /// <summary>The Camera rotation keys this frame (−1 left … +1 right): the bound keys while the game has the cursor and the body, plus <see cref="scriptedKeyTurn"/>.</summary>
        public float TurnKeyInput()
        {
            float keys = G3Cursor.Captured && moveInputEnabled && !scriptedOnly ? G3Input.KeyTurn() : 0f;
            return Mathf.Clamp(keys + scriptedKeyTurn, -1f, 1f);
        }
        GUIStyle _style;
        float _slideSpeed, _fallSpeed;
        SlopeRules.SlideState _slide;
        Vector3 _groundNormal = Vector3.up;
        // Contacts of the last CharacterController.Move: the flattest one under the feet, and steep faces met at any
        // height (those at foot height block walking into them unless they are a step or a landscape fold; any of them
        // can hold the hero, then the step/fold probe runs).
        Vector3 _contactNormal;
        bool _hasFloorContact;
        readonly Vector3[] _steepPoint = new Vector3[6], _steepNormal = new Vector3[6];
        int _steepCount;
        readonly Collider[] _overlap = new Collider[8];
        readonly System.Collections.Generic.Dictionary<Collider, bool> _landscape = new System.Collections.Generic.Dictionary<Collider, bool>();
        float _foldLogAt = -10f;

        /// <summary>Times the hero stepped up a ledge (three controller moves) since start (fix-49; scenario terrain_ledges).</summary>
        public int StepUps { get; private set; }
        /// <summary>Times the hero walked past a landscape fold since start (fix-49; scenario terrain_ledges).</summary>
        public int FoldPasses { get; private set; }

        /// <summary>
        /// Character bodies that block the hero (fix-86, <see cref="BodyBlockRules"/>): footprint radius (m, &gt; 0) of
        /// the body <paramref name="collider"/> belongs to, 0 when it is none or does not block (lying, dead, the hero's own).
        /// Set by the game; null = nobody blocks. Bodies are trigger capsules, so the controller alone walks through them.
        /// </summary>
        public static Func<Collider, float> BodyFootprint;

        /// <summary>
        /// fix-219: whether the runtime NavMesh around a point is still being built (cells streaming in, collected or in
        /// the running update). Set by the game; null = always ready. The ceiling-top guard reads "no NavMesh here" as
        /// "inside the rock" only once the NavMesh there is built: right after a teleport to the Gotha great tower the
        /// ground floor (90.15) had no NavMesh yet, two checks 0.4 s apart judged it a cave ceiling and moved the hero 5 m
        /// down onto the landscape under the tower whose cell NavMesh was already built (scenario fix148_stairs_beam, build-25a:
        /// with the owner's game running on the same PC the editor's build lagged the checks).
        /// </summary>
        public static Func<Vector3, bool> NavMeshPending;

        /// <summary>Times a character body clipped the hero's move since start (fix-86; scenario fix86_npc_block).</summary>
        public int BodyBlocks { get; private set; }
        /// <summary>The last body that clipped the hero's move (its collider; fix-86 probes).</summary>
        public Collider LastBlockingBody { get; private set; }

        static readonly Collider[] _bodyHits = new Collider[64];
        static readonly System.Collections.Generic.List<BodyCircle> _bodies = new System.Collections.Generic.List<BodyCircle>(8);
        static readonly System.Collections.Generic.List<Collider> _bodyColliders = new System.Collections.Generic.List<Collider>(8);

        /// <summary>The horizontal part of <paramref name="move"/> stopped at character bodies and slid round them.</summary>
        Vector3 ClipAtBodies(Vector3 move)
        {
            var footprint = BodyFootprint;
            if (footprint == null || (move.x == 0f && move.z == 0f)) return move;
            var p = transform.position;
            float own = _cc.radius;
            float reach = new Vector2(move.x, move.z).magnitude + own + 2.5f; // the longest body footprint (big animals)
            var centre = p + Vector3.up * (_cc.height * 0.5f);
            int n = Physics.OverlapSphereNonAlloc(centre, reach, _bodyHits, ~0, QueryTriggerInteraction.Collide);
            _bodies.Clear();
            _bodyColliders.Clear();
            for (int i = 0; i < n; i++)
            {
                var c = _bodyHits[i];
                if (c == null || !c.isTrigger || c.transform.IsChildOf(transform)) continue;
                float r = footprint(c);
                if (r <= 0f) continue;
                // Only bodies at the hero's height (not one on a floor above or below him).
                var b = c.bounds;
                if (b.max.y < p.y + 0.1f || b.min.y > p.y + _cc.height - 0.1f) continue;
                _bodies.Add(new BodyCircle(b.center.x, b.center.z, r)); // capsule centre (a long animal's lies ahead of its root)
                _bodyColliders.Add(c);
            }
            if (_bodies.Count == 0) return move;
            float dx = move.x, dz = move.z;
            if (!BodyBlockRules.Clip(p.x, p.z, own, ref dx, ref dz, _bodies)) return move;
            BodyBlocks++;
            // The nearest body is reported (probes).
            float best = float.MaxValue;
            for (int i = 0; i < _bodies.Count; i++)
            {
                float d = new Vector2(_bodies[i].X - p.x, _bodies[i].Z - p.z).sqrMagnitude;
                if (d < best) { best = d; LastBlockingBody = _bodyColliders[i]; }
            }
            return new Vector3(dx, move.y, dz);
        }

        void Awake()
        {
            useGUILayout = false; // OnGUI uses no GUILayout: skips the per-frame IMGUI layout pass (perf-7)
            _cc = GetComponent<CharacterController>();
            ConfigureController(_cc);
            if (playerCamera == null) playerCamera = GetComponentInChildren<Camera>();
            if (playerCamera == null) playerCamera = Camera.main;
            if (playerCamera != null)
            {
                _fly = playerCamera.GetComponent<FlyCamera>();
                if (_fly != null) _fly.enabled = false;
                if (playerCamera.transform.parent != transform) playerCamera.transform.SetParent(transform, true);
                _yaw = playerCamera.transform.eulerAngles.y;
                float px = playerCamera.transform.eulerAngles.x;
                _pitch = px > 180 ? px - 360 : px;
                // Scenes set up before the camera rig existed get one here.
                var rig = playerCamera.GetComponent<G3PlayerCamera>();
                if (rig == null) rig = playerCamera.gameObject.AddComponent<G3PlayerCamera>();
                if (rig.player == null) rig.player = this;
                _rig = rig;
            }
            else _yaw = transform.eulerAngles.y;
        }

        /// <summary>G3 hero (gCCharacterMovement_PS): floor up to 49° is walkable, steps up to 65 cm (Docs/hero.md); the
        /// capsule as wide as G3's (<see cref="HeroCapsule"/>, fix-148: open stairs with 70 cm treads).</summary>
        public static void ConfigureController(CharacterController cc)
        {
            cc.height = HeroCapsule.Height;
            cc.radius = HeroCapsule.Radius;
            cc.center = new Vector3(0, HeroCapsule.Height * 0.5f, 0);
            cc.slopeLimit = SlopeRules.WalkableAngleMax;
            cc.stepOffset = SlopeRules.StepHeight;
            cc.skinWidth = HeroCapsule.Skin;
            cc.minMoveDistance = 0f;
        }

        void Update()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            var mouse = Mouse.current;

            // Cursor capture of the bare demo; a game's cursor policy (G3Cursor.Apply) owns the cursor otherwise
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && !IsFlying) G3Cursor.DemoCapture(true);
            if (kb != null && kb.escapeKey.wasPressedThisFrame) G3Cursor.DemoCapture(false);

            if (kb != null && DevKeys)   // release (demo) builds: no fly mode, time keys or help overlay (Docs/build.md)
            {
                if (kb.f1Key.wasPressedThisFrame && !kb.ctrlKey.isPressed) _showHelp = !_showHelp; // Ctrl+F1 toggles the HUD
                if (kb.fKey.wasPressedThisFrame && !kb.ctrlKey.isPressed) SetFlying(!IsFlying); // Ctrl+F: the FPS overlay (fix-172)
                if (kb.leftBracketKey.wasPressedThisFrame) AddTime(-timeStepHours);
                if (kb.rightBracketKey.wasPressedThisFrame) AddTime(timeStepHours);
            }

            if (IsFlying) return;

            // Look: the mouse halves of RotateCam/Turn (Controls options: sensitivity, invert, smoothing) and their keys.
            _yaw += scriptedTurn * Time.deltaTime;
            bool captured = G3Cursor.Captured;
            if (lookInputEnabled && captured && !scriptedOnly)
            {
                var (dx, dy) = _look.Apply(G3Input.MouseTurn(), G3Input.MousePitch(), Time.deltaTime, G3Input.Options, LookingBehind);
                _yaw += dx * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch - dy * lookSensitivity, minPitch, maxPitch);
                // gamepad-1: the camera stick turns at a rate (degrees per second), next to the mouse
                var pad = G3Input.PadLookRates(LookingBehind);
                if (pad != Vector2.zero)
                {
                    _yaw += pad.x * Time.deltaTime;
                    _pitch = Mathf.Clamp(_pitch - pad.y * Time.deltaTime, minPitch, maxPitch);
                }
            }
            else _look.Reset();
            // G3: the Camera rotation keys turn the hero; the port's option orbits the camera with them (G3PlayerCamera).
            if (!TurnKeysOrbit) _yaw += TurnKeyInput() * keyTurnSpeed * Time.deltaTime;
            if (captured && moveInputEnabled && !scriptedOnly)
            {
                _pitch = Mathf.Clamp(_pitch - G3Input.KeyPitch() * keyTurnSpeed * 0.5f * Time.deltaTime, minPitch, maxPitch);
            }
            if (IsNoclip) { NoclipStep(kb, Time.unscaledDeltaTime); return; }

            // Move input (G3: running is the default; the "Walk / Sprint" key pressed while moving sprints, pressed while
            // standing walks, until released (OnPlayerWalk); WalkToggle = permanent walk)
            var input = scriptedMove;
            if (!scriptedOnly && G3Input.WasPressed(SessionKey.WalkToggle)) _walkToggle = !_walkToggle;
            if (!G3Input.IsDown(SessionKey.Walk)) _walkKey = WalkKeyFlag.None;
            else if (G3Input.WasPressed(SessionKey.Walk) || _walkKey == WalkKeyFlag.None)
                _walkKey = HeroMoveRules.WalkKeyPress(new Vector2(Velocity.x, Velocity.z).sqrMagnitude > 0.01f);
            bool walk = scriptedWalk ^ _walkToggle || _walkKey == WalkKeyFlag.Walk, sprint = scriptedSprint || _walkKey == WalkKeyFlag.Sprint, jump = scriptedJump;
            if (scriptedOnly) { walk = scriptedWalk; sprint = scriptedSprint; }
            scriptedJump = false;
            bool keysMove = !scriptedOnly;
            if (keysMove && G3Input.KeysDown(SessionKey.Forward)) input.y += 1;
            if (keysMove && G3Input.KeysDown(SessionKey.Backward)) input.y -= 1;
            if (keysMove && G3Input.KeysDown(SessionKey.StrafeRight)) input.x += 1;
            if (keysMove && G3Input.KeysDown(SessionKey.StrafeLeft)) input.x -= 1;
            // gamepad-1: the move stick gives the direction; a small deflection walks (G3 has walk and run, no speeds between)
            var padMove = keysMove ? G3Input.PadMove() : Vector2.zero;
            if (padMove != Vector2.zero)
            {
                var (px, py) = PadStick.MoveDirection(padMove.x, padMove.y);
                input += new Vector2(px, py);
                if (PadStick.Walks(padMove.magnitude) && !sprint && input == new Vector2(px, py)) walk = true;
            }
            jump |= keysMove && G3Input.WasPressed(SessionKey.Up);
            bool sneakToggle = scriptedSneakToggle;
            if (keysMove && G3Input.WasPressed(SessionKey.Down))
                sneakToggle |= _sneakKey.Press(DeferSneakToggle != null && DeferSneakToggle());
            else if (_sneakKey.Pending && (!keysMove || !G3Input.IsDown(SessionKey.Down)))
                sneakToggle |= keysMove ? _sneakKey.Release() : false;
            if (!keysMove) _sneakKey.Cancel();
            scriptedSneakToggle = false;
            input = Vector2.ClampMagnitude(input, 1f);
            MoveKeys = input;
            if (!moveInputEnabled) { input = Vector2.zero; jump = false; sprint = false; sneakToggle = false; }
            if (paradeWalk) { jump = false; sprint = false; sneakToggle = false; SetSneaking(false); }
            if (sneakToggle) SetSneaking(!Sneaking);
            if (jump) SetSneaking(false);
            // G3 FUN_10002130, the same for every loop state with or without a weapon (HeroMoveRules.KeysGait); aiming walks.
            if (moveInputEnabled && !holdGait)
                _gait = HeroMoveRules.KeysGait(Sneaking, walk, sprint, input.y > 0f, _gait == HeroGait.Sprint, sprintStamina);
            if (Sneaking) _gait = HeroGait.Sneak;
            else if (_gait == HeroGait.Sneak) _gait = HeroGait.Run;
            var gait = forceWalk && _gait != HeroGait.Sneak ? HeroGait.Walk : _gait;
            var mode = gait == HeroGait.Sneak ? G3MoveMode.Sneak : gait == HeroGait.Walk ? G3MoveMode.Walk : gait == HeroGait.Sprint ? G3MoveMode.Sprint : G3MoveMode.Run;
            Step(input, mode, jump, Time.deltaTime);
        }

        public void SetSneaking(bool on) => Sneaking = on && !IsFlying && !IsNoclip;

        /// <summary>
        /// fix-131: noclip on or off. On: the controller is off (no collision, gravity, ground check, world catch, landing
        /// or fall damage: they all run in <see cref="Step"/>), the keys move the hero himself (<see cref="NoclipStep"/>),
        /// the camera stays his. Off: he is put on a floor below on the same side (<see cref="NoclipLand"/>), his air and
        /// fall time cleared, and the ground guard judges the place at once. Returns where he ended (off) or "".
        /// </summary>
        public string SetNoclip(bool on)
        {
            if (on == IsNoclip || _cc == null) return "";
            if (on)
            {
                if (IsFlying) SetFlying(false);
                IsNoclip = true;
                SetSneaking(false);
                IsSliding = false;
                _slide = default;
                _velocity = Vector3.zero;
                Velocity = Vector3.zero;
                AirTime = 0;
                FallTime = 0;
                _fallSpeed = 0;
                _cc.enabled = false;
                return "";
            }
            IsNoclip = false;
            string how = NoclipLand();
            _cc.enabled = true;
            _velocity = Vector3.zero;
            Velocity = Vector3.zero;
            AirTime = 0;
            FallTime = 0;
            _fallSpeed = 0;
            ValidatePlacement("noclip off");
            return how;
        }

        /// <summary>fix-131: one noclip frame: WASD along the look (pitch included), Space up, Ctrl down, Shift faster.</summary>
        void NoclipStep(Keyboard kb, float dt)
        {
            transform.rotation = Quaternion.Euler(0, _yaw, 0);
            dt = Mathf.Clamp(dt, 0f, 0.25f);
            Vector2 input = scriptedMove;
            bool up = false, down = false, fast = false;
            if (!scriptedOnly && moveInputEnabled)
            {
                if (G3Input.KeysDown(SessionKey.Forward)) input.y += 1;
                if (G3Input.KeysDown(SessionKey.Backward)) input.y -= 1;
                if (G3Input.KeysDown(SessionKey.StrafeRight)) input.x += 1;
                if (G3Input.KeysDown(SessionKey.StrafeLeft)) input.x -= 1;
                input += G3Input.PadMove();
                if (kb != null)
                {
                    up = kb.spaceKey.isPressed;
                    down = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
                    fast = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                }
            }
            input = Vector2.ClampMagnitude(input, 1f);
            MoveKeys = input;
            var (x, y, z) = NoclipRules.Velocity(input.y, input.x, up, down, fast, _yaw, _pitch, noclipSpeed);
            transform.position += new Vector3(x, y, z) * dt;
        }

        /// <summary>
        /// fix-131: leaving noclip (<see cref="NoclipRules.Choose"/>): the first free floor straight below with NavMesh, else
        /// the nearest free floor round the feet (ground guard: same level, then down; never up through a ceiling), else the
        /// first free floor below, else his last valid place, else he stays (the world catch takes a fall into nothing).
        /// The rays start at the feet and go down only, so a surface above him (a ceiling, the land over a cave) is never picked.
        /// </summary>
        string NoclipLand()
        {
            var feet = transform.position;
            Vector3 navFloor = default, freeFloor = default;
            bool nav = false, free = false;
            var from = feet + Vector3.up * 0.05f;
            float left = NoclipRules.LandProbe;
            for (int i = 0; i < NoclipRules.LandMaxHits && left > 0f && !nav; i++)
            {
                if (!RayWorld(from, Vector3.down, left, out var hit)) break;
                left -= hit.distance + 0.15f;
                from = hit.point + Vector3.down * 0.15f;
                if (hit.normal.y < 0.6f || !CapsuleFree(hit.point, out _)) continue;
                if (!free) { free = true; freeFloor = hit.point; }
                if (UnityEngine.AI.NavMesh.SamplePosition(hit.point, out var nh, 0.6f, UnityEngine.AI.NavMesh.AllAreas) &&
                    Mathf.Abs(nh.position.y - hit.point.y) < 0.6f)
                {
                    nav = true;
                    navFloor = hit.point;
                }
            }
            Vector3 near = default;
            string nearHow = null;
            bool hasNear = !nav && TryNearestFloor(feet, out near, out nearHow);
            bool safe = _lastSafe.HasValue && CapsuleFree(_lastSafe.Value, out _);
            Vector3 to;
            string how;
            switch (NoclipRules.Choose(nav, hasNear, free, safe))
            {
                case NoclipRules.Landing.NavFloorBelow: to = navFloor; how = $"the floor {feet.y - navFloor.y:0.0} m below"; break;
                case NoclipRules.Landing.NearestFloor: to = near; how = nearHow; break;
                case NoclipRules.Landing.FreeFloorBelow: to = freeFloor; how = $"the free floor {feet.y - freeFloor.y:0.0} m below (no NavMesh)"; break;
                case NoclipRules.Landing.LastSafe: to = _lastSafe.Value; how = "no floor below: the last safe position"; break;
                default:
                    Debug.Log($"[G3Player] fix-131 noclip off at {feet}: no floor below and no safe position; he stays (the world catch takes a fall)");
                    return $"no floor below {feet.x:0.0} {feet.y:0.0} {feet.z:0.0}; stays";
            }
            TeleportCore(to);
            _lastSafe = to;
            Debug.Log($"[G3Player] fix-131 noclip off: {feet} -> {to} ({how})");
            return $"{how}, at {to.x:0.0} {to.y:0.0} {to.z:0.0}";
        }

        /// <summary>Local target velocity (x = right, z = forward) for a keys/stick input in the given gait.</summary>
        public Vector3 LocalWish(Vector2 input, G3MoveMode mode)
        {
            if (paradeWalk)
            {
                var (px, pz) = ParadeMovement.Wish(input.x, input.y, mode != G3MoveMode.Walk);
                return new Vector3(px, 0, pz);
            }
            float fwd, side, back;
            switch (mode)
            {
                case G3MoveMode.Walk: fwd = side = back = walkSpeed; break;
                case G3MoveMode.Sneak: fwd = side = back = sneakSpeed; break;
                case G3MoveMode.Sprint: fwd = sprintSpeed; side = strafeRunSpeed; back = backRunSpeed; break;
                default: fwd = runSpeed; side = strafeRunSpeed; back = backRunSpeed; break;
            }
            // The drawn hands' motion speeds (G3 moves by the motion: Hero_Stand_None_1H_P0_Move_Sprint_N_Fwd = 500 cm/s).
            fwd *= Factor(mode, HeroMoveDir.Fwd);
            side *= Factor(mode, HeroMoveDir.Side);
            back *= Factor(mode, HeroMoveDir.Back);
            return new Vector3(input.x * side, 0, input.y * (input.y >= 0 ? fwd : back));
        }

        /// <summary>
        /// One movement tick (public so tests can step the controller deterministically). A long frame (an editor or
        /// streaming hitch) is split into sub-steps of at most <see cref="HeroMoveRules.MaxStepSeconds"/> instead of being
        /// cut to 0.1 s: the cut dropped distance, so a sprint measured 3.6-5.5 m/s instead of 6 (fix-48, editor-runner-29).
        /// </summary>
        public void Step(Vector2 input, G3MoveMode mode, bool jump, float dt)
        {
            if (dt <= 0 || !_cc.enabled) return;
            int n = HeroMoveRules.SubSteps(dt);
            float h = Mathf.Min(dt, HeroMoveRules.MaxFrameSeconds) / n;
            for (int i = 0; i < n && _cc.enabled; i++) StepOnce(input, mode, jump && i == 0, h);
        }

        void StepOnce(Vector2 input, G3MoveMode mode, bool jump, float dt)
        {
            MoveMode = mode;
            transform.rotation = Quaternion.Euler(0, bodyYaw ?? _yaw, 0);
            UpdateWater();
            if (Water == WaterMode.Swim)
            {
                SwimStep(input, mode, dt);
                return;
            }
            if (Water == WaterMode.Wade) SetSneaking(false);
            var wish = Quaternion.Euler(0, _yaw, 0) * LocalWish(input, mode);
            wish = BlockSteepContacts(wish);

            bool grounded = _cc.isGrounded;
            UpdateSlide(grounded, dt);
            // Grounded only on faces steeper than the limit: no footing. Gravity keeps acting (the controller slides
            // down along the face) instead of being reset, so walking cannot hold the hero on a wall.
            bool footing = grounded && GroundAngle <= _cc.slopeLimit;
            if (IsSliding && jump && _slide.CanJump)
            {
                // G3 OnSlide → PS_Slide_Jump: after 0.6 s of sliding the jump key jumps off the slope.
                IsSliding = _slide.Sliding = false;
                _velocity = Vector3.ProjectOnPlane(Vector3.down, _groundNormal).normalized * _slideSpeed;
                _velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);
                Jumped?.Invoke();
            }
            else if (IsSliding)
            {
                var downhill = Vector3.ProjectOnPlane(Vector3.down, _groundNormal).normalized;
                _slideSpeed = SlopeRules.SlideSpeed(_slideSpeed, GroundAngle, gravity, slideAcceleration, maxSlideSpeed, dt);
                var steer = Vector3.ProjectOnPlane(wish, downhill);
                steer.y = 0;
                _velocity = downhill * _slideSpeed + Vector3.ClampMagnitude(steer, slideSteer);
                _velocity.y -= 2f; // stay in contact
            }
            else if (footing)
            {
                _velocity.x = wish.x;
                _velocity.z = wish.z;
                if (_velocity.y < 0) _velocity.y = -2f; // keep contact on slopes / stairs going down
                if (jump)
                {
                    _velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);
                    Jumped?.Invoke();
                }
            }
            else
            {
                float k = 1f - Mathf.Exp(-airControl * 10f * dt);
                _velocity.x = Mathf.Lerp(_velocity.x, wish.x, k);
                _velocity.z = Mathf.Lerp(_velocity.z, wish.z, k);
            }
            if (!IsSliding) _velocity.y -= gravity * dt;
            // Wedged on steep faces the controller cannot descend while gravity keeps adding up: cap it at the G3
            // slide speed so one big downward move cannot push the capsule through a seam.
            if (grounded && !footing && _velocity.y < -maxSlideSpeed) _velocity.y = -maxSlideSpeed;
            if (_velocity.y < -50f) _velocity.y = -50f;

            var before = transform.position;
            _hasFloorContact = false;
            _contactNormal = Vector3.up;
            _steepCount = 0;
            var flags = _cc.Move(ClipAtBodies(_velocity * dt));
            // fix-130 (owner: "climbing the crates throws me onto the roof"): a move that lifted him through geometry (the
            // controller's own step-up / overlap recovery against a low two-sided roof) is undone.
            if (GroundGuard.NeedsPathCheck(before.y, transform.position.y) && CrossedGeometry(before, transform.position, out var through))
                RefuseRise(before, through, "move");
            Velocity = (transform.position - before) / dt;
            // Held by a steep face while walking: G3 judges only the floor ahead (fix-49), so a ledge within step height
            // is stepped up and a thin landscape fold is walked past.
            if (footing && !IsSliding && Water != WaterMode.Swim && _steepCount > 0)
            {
                var moved = transform.position - before;
                float wishSpeed = new Vector2(wish.x, wish.z).magnitude;
                if (SlopeRules.Stalled(wishSpeed, new Vector2(moved.x, moved.z).magnitude, dt) && PassSteep(wish, dt))
                {
                    Velocity = new Vector3(wish.x, 0f, wish.z);
                    if (_velocity.y > 0f) _velocity.y = 0f;
                }
            }
            if ((flags & CollisionFlags.Above) != 0 && _velocity.y > 0) _velocity.y = 0;
            if (_velocity.y <= 0) RecoverIfSunk(before);

            // Air time / landing
            if (_cc.isGrounded)
            {
                if (AirTime > 0.15f) Landed?.Invoke(AirTime, FallTime);
                AirTime = 0;
                FallTime = 0;
                _fallSpeed = 0;
            }
            else
            {
                AirTime += dt;
                // G3 switches from JUMP to FALL once the jump stops rising; a step off a ledge falls at once [interp].
                if (_velocity.y <= 0) FallTime += dt;
                _fallSpeed = Mathf.Max(_fallSpeed, -Velocity.y);
                CatchIfOutOfWorld(dt);
            }
            GuardGround(footing && !IsSliding && Water == WaterMode.None && _cc.isGrounded, dt);
        }

        Vector3? _lastSafe;
        float _catchCheck;

        /// <summary>
        /// A hero falling with no ground below (under the terrain) is put back on the world (<see cref="WorldCatch"/>): on
        /// his last valid standing place (<see cref="GroundGuard.ChooseCatch"/>), else on the surface he fell through.
        /// fix-123 (owner log: "put back ... (surface above)" every 1.2 s, 2 m up and 13 m down each time, from y 81 to
        /// -29): the casts took the hero's own capsule, whose top lies 2 m above his feet, for that surface. They skip
        /// the hero's own colliders now.
        /// </summary>
        void CatchIfOutOfWorld(float dt)
        {
            if (FallTime < WorldCatch.MinFallSeconds && transform.position.y >= WorldCatch.WorldFloorY) return;
            _catchCheck -= dt;
            if (_catchCheck > 0) return;
            _catchCheck = 0.25f;
            var feet = transform.position;
            bool below = RayWorld(feet + Vector3.up * 0.1f, Vector3.down, WorldCatch.GroundProbe + 0.1f, out _);
            if (!WorldCatch.ShouldCatch(FallTime, below, feet.y)) return;
            // The surface just above him (he fell through it; not the topmost one: a rock or a roof far above).
            float startY = Mathf.Max(feet.y, 0f) + 2000f;
            float? surface = WorldCatch.LowestSurfaceAbove(feet.y, startY, from =>
                RayWorld(new Vector3(feet.x, from, feet.z), Vector3.down, from - feet.y, out var h) ? h.point.y : (float?)null);
            float? safeDistance = _lastSafe.HasValue
                ? new Vector2(_lastSafe.Value.x - feet.x, _lastSafe.Value.z - feet.z).magnitude : (float?)null;
            var choice = GroundGuard.ChooseCatch(safeDistance, surface.HasValue);
            Vector3 to = choice == GroundGuard.CatchTarget.LastSafe ? _lastSafe.Value
                : choice == GroundGuard.CatchTarget.SurfaceAbove ? new Vector3(feet.x, surface.Value, feet.z)
                : feet;
            string how = choice == GroundGuard.CatchTarget.LastSafe ? "last safe position"
                : choice == GroundGuard.CatchTarget.SurfaceAbove ? "surface above" : "nothing found, he stays";
            WorldCatches++;
            Debug.Log($"[G3Player] out of the world at {feet} after {FallTime:0.0} s of falling: put back at {to} ({how}), no fall damage");
            if (choice != GroundGuard.CatchTarget.None) TeleportCore(to);
        }

        /// <summary>How often the hero was caught out of the world (dev probes).</summary>
        public int WorldCatches { get; private set; }
        /// <summary>How often a lift onto a ceiling was refused or the hero was moved off a ceiling top / out of geometry (fix-123, dev probes).</summary>
        public int CeilingGuards { get; private set; }
        /// <summary>His last valid standing place (fix-123: saved with the pose; null: none since a teleport).</summary>
        public Vector3? LastSafe => _lastSafe;

        /// <summary>
        /// fix-123 safety valve (dev op / console <c>unstuck</c>): the hero goes to the floor straight below him when he
        /// stands on a ceiling top, else to the nearest free floor on his level or below, else to his last valid place.
        /// Returns what was done.
        /// </summary>
        public string Unstuck()
        {
            var feet = transform.position;
            bool navHere = UnityEngine.AI.NavMesh.SamplePosition(feet, out var nh, 1f, UnityEngine.AI.NavMesh.AllAreas) && Mathf.Abs(nh.position.y - feet.y) < 0.7f;
            if (!navHere && NavFloorBelow(feet, out var floor))
            {
                GuardMove(feet, floor, $"unstuck: the floor {feet.y - floor.y:0.0} m below");
                return $"unstuck: down {feet.y - floor.y:0.0} m to {floor}";
            }
            if (!CapsuleFree(feet, out var inside) && TryNearestFloor(feet, out var free, out string how))
            {
                GuardMove(feet, free, $"unstuck: inside {inside.name}, {how}");
                return $"unstuck: out of {inside.name} to {free} ({how})";
            }
            if (!navHere && _lastSafe.HasValue && CapsuleFree(_lastSafe.Value, out _))
            {
                var to = _lastSafe.Value;
                GuardMove(feet, to, "unstuck: the last safe position");
                return $"unstuck: back to the last safe position {to}";
            }
            return $"unstuck: the hero stands free at {feet} (NavMesh {(navHere ? "here" : "none")}); nothing to do";
        }

        /// <summary>A loaded save's last valid standing place (fix-123).</summary>
        public void RestoreLastSafe(Vector3 feet) => _lastSafe = feet;

        readonly RaycastHit[] _rayHits = new RaycastHit[24];
        readonly Collider[] _overlapHits = new Collider[16];

        /// <summary>The nearest world hit along a ray, never the hero's own colliders (his capsule, weapons).</summary>
        bool RayWorld(Vector3 origin, Vector3 dir, float dist, out RaycastHit hit)
        {
            hit = default;
            int n = Physics.RaycastNonAlloc(origin, dir, _rayHits, dist, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = _rayHits[i].collider;
                if (c == null || c.transform.IsChildOf(transform)) continue;
                if (_rayHits[i].distance < best) { best = _rayHits[i].distance; hit = _rayHits[i]; }
            }
            return best < float.MaxValue;
        }

        /// <summary>Whether a ray hits world geometry, back faces included (a one-sided landscape seen from below).</summary>
        bool RayWorldBackfaces(Vector3 origin, Vector3 dir, float dist)
        {
            bool back = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try { return RayWorld(origin, dir, dist, out _); }
            finally { Physics.queriesHitBackfaces = back; }
        }

        /// <summary>Room for the hero's capsule standing at <paramref name="feet"/> (above the ground contact; his own colliders aside).</summary>
        bool CapsuleFree(Vector3 feet, out Collider blocker)
        {
            blocker = null;
            float r = _cc.radius * 0.9f;
            var bottom = feet + Vector3.up * (r + 0.1f);
            var top = feet + Vector3.up * Mathf.Max(r + 0.1f, _cc.height - r - 0.02f);
            int n = Physics.OverlapCapsuleNonAlloc(bottom, top, r, _overlapHits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _overlapHits[i];
                if (c == null || c.transform.IsChildOf(transform)) continue;
                blocker = c;
                return false;
            }
            return true;
        }

        readonly RaycastHit[] _pathHits = new RaycastHit[16];
        float[] _pathHeights;
        int _riseRefusals;
        float _riseRefusedAt = -10f;

        /// <summary>
        /// fix-130: whether going from <paramref name="from"/> to <paramref name="to"/> (feet) passes through static geometry:
        /// the segments between the capsule's old and new middle and head (<see cref="GroundGuard.PathHeights"/>) hit
        /// something that is not the hero, a character or a moving body (back faces count: a one-sided ceiling too).
        /// A swept controller move never does; a lift through a roof always does.
        /// </summary>
        bool CrossedGeometry(Vector3 from, Vector3 to, out Collider blocker)
        {
            blocker = null;
            bool back = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                _pathHeights ??= GroundGuard.PathHeights(_cc.height, _cc.radius);
                foreach (float h in _pathHeights)
                {
                    var a = from + Vector3.up * h;
                    var d = to + Vector3.up * h - a;
                    float len = d.magnitude;
                    if (len < 1e-4f) continue;
                    int n = Physics.RaycastNonAlloc(a, d / len, _pathHits, len, ~0, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        var c = _pathHits[i].collider;
                        if (c == null || c.transform.IsChildOf(transform) || c is CharacterController || c.attachedRigidbody != null) continue;
                        blocker = c;
                        return true;
                    }
                }
                return false;
            }
            finally { Physics.queriesHitBackfaces = back; }
        }

        /// <summary>
        /// fix-130: a move lifted the hero through <paramref name="through"/>: he goes back to <paramref name="before"/>
        /// and stops rising. Refused again and again (the controller keeps pushing him out of an overlap upwards), he goes
        /// to the nearest free floor on his level or below (never up).
        /// </summary>
        void RefuseRise(Vector3 before, Collider through, string why)
        {
            var at = transform.position;
            CeilingGuards++;
            _riseRefusals = Time.time - _riseRefusedAt < 0.5f ? _riseRefusals + 1 : 1;
            _riseRefusedAt = Time.time;
            Place(before);
            if (_velocity.y > 0) _velocity.y = 0;
            if (Time.time - _guardLogAt > 2f)
            {
                _guardLogAt = Time.time;
                Debug.Log($"[G3Player] fix-130: {why} {before} -> {at} undone: it went through {through.name}");
            }
            if (_riseRefusals >= GroundGuard.MaxRiseRefusals && TryNearestFloor(before, out var free, out string how))
            {
                _riseRefusals = 0;
                GuardMove(before, free, $"{why}: pushed up through {through.name} again and again; {how}");
            }
        }

        float _guardAt;
        int _ceilingHits;
        bool _navWaitLogged;
        string _validateWhy;
        float _guardLogAt = -10f;

        /// <summary>
        /// The next ground check judges at once (a loaded save): the hero inside geometry or on a ceiling top goes to the
        /// nearest valid floor (the same level first, then down) or his last valid place; falling with nothing below is
        /// the world catch's (<see cref="CatchIfOutOfWorld"/>).
        /// </summary>
        public void ValidatePlacement(string why)
        {
            _validateWhy = why ?? "placement";
            _guardAt = 0f;
        }

        /// <summary>
        /// fix-123: every 0.4 s on the ground. A place with NavMesh (or in the open) is his last valid place; standing on a
        /// cave ceiling's top (<see cref="GroundGuard.OnCeiling"/>, twice in a row) puts him on the floor below it. After
        /// <see cref="ValidatePlacement"/> the first check also resolves standing inside geometry.
        /// </summary>
        void GuardGround(bool standing, float dt)
        {
            _guardAt -= dt;
            if (_guardAt > 0f) return;
            _guardAt = 0.4f;
            string why = _validateWhy;
            _validateWhy = null;
            var feet = transform.position;
            if (why != null && !CapsuleFree(feet, out var inside))
            {
                if (TryNearestFloor(feet, out var free, out string how))
                {
                    GuardMove(feet, free, $"{why}: inside {inside.name}, {how}");
                    return;
                }
                if (_lastSafe.HasValue && CapsuleFree(_lastSafe.Value, out _))
                {
                    GuardMove(feet, _lastSafe.Value, $"{why}: inside {inside.name}, no free floor near; the last safe position");
                    return;
                }
                Debug.LogWarning($"[G3Player] fix-123 {why}: the hero stands inside {inside.name} at {feet} and no free floor is near (F10: tp pos=x,z)");
            }
            if (!standing)
            {
                if (why != null) _validateWhy = why; // judged once he stands (the cells round a loaded save may still stream in)
                return;
            }
            bool navHere = UnityEngine.AI.NavMesh.SamplePosition(feet, out var nh, 1f, UnityEngine.AI.NavMesh.AllAreas) && Mathf.Abs(nh.position.y - feet.y) < 0.7f;
            if (!navHere && NavMeshPending != null && NavMeshPending(feet))
            {
                // fix-219: no NavMesh yet is no verdict; judged again once it is built (a pending placement check, too)
                if (why != null) _validateWhy = why;
                _ceilingHits = 0;
                if (!_navWaitLogged) Debug.Log($"[G3Player] fix-219 ground guard at {feet}: no NavMesh yet (being built), judged once it is");
                _navWaitLogged = true;
                return;
            }
            _navWaitLogged = false;
            // fix-134: the hill over a cave is the landscape seen from below (its back faces; the Montera cave at (119.4, -35.4):
            // landscape 125.1 over the roof top 94.6): back faces count, as for the NPC cave judge (CaveGround.Judge)
            bool enclosed = !navHere && RayWorldBackfaces(feet + Vector3.up * (_cc.height + 0.1f), Vector3.up, GroundGuard.EnclosedProbe);
            Vector3 floor = default;
            float? drop = enclosed && NavFloorBelow(feet, out floor) ? feet.y - floor.y : (float?)null;
            if (!GroundGuard.OnCeiling(navHere, enclosed, drop))
            {
                _lastSafe = feet;
                _ceilingHits = 0;
                return;
            }
            if (why == null && ++_ceilingHits < 2) return;
            _ceilingHits = 0;
            GuardMove(feet, floor, $"{why ?? "standing"}: on a ceiling top inside the rock (no NavMesh here, the floor {drop:0.0} m below)");
        }

        void GuardMove(Vector3 from, Vector3 to, string why)
        {
            CeilingGuards++;
            TeleportCore(to);
            _lastSafe = to;
            Debug.Log($"[G3Player] fix-123 {why}: moved {from} -> {to}");
        }

        /// <summary>
        /// The floor straight below the surface he stands on (a cave floor under its ceiling), with NavMesh and room for
        /// him. The ray starts under the surface, so its lower side (facing down, away from the ray) is not hit.
        /// </summary>
        bool NavFloorBelow(Vector3 feet, out Vector3 floor)
        {
            floor = default;
            var from = feet + Vector3.down * 0.15f;
            for (int i = 0; i < 4; i++)
            {
                if (!RayWorld(from, Vector3.down, GroundGuard.MaxCeilingDrop, out var hit) || feet.y - hit.point.y > GroundGuard.MaxCeilingDrop) return false;
                if (hit.normal.y >= 0.6f && UnityEngine.AI.NavMesh.SamplePosition(hit.point, out var nh, 0.6f, UnityEngine.AI.NavMesh.AllAreas) &&
                    Mathf.Abs(nh.position.y - hit.point.y) < 0.6f && CapsuleFree(hit.point, out _))
                {
                    floor = hit.point;
                    return true;
                }
                from = hit.point + Vector3.down * 0.15f;
            }
            return false;
        }

        static readonly float[] GuardRings = { 0.6f, 1.2f, 2f, 3f, 4.5f, 6f };
        readonly System.Collections.Generic.List<(float dx, float dy, float dz)> _spotOffsets = new System.Collections.Generic.List<(float dx, float dy, float dz)>();
        readonly System.Collections.Generic.List<Vector3> _spots = new System.Collections.Generic.List<Vector3>();

        /// <summary>
        /// The nearest free floor round the feet (<see cref="GroundGuard.Best"/>): the same level first (rays from a step
        /// above the feet down, never from higher, so a ceiling over him is never picked), then the floor below; room for
        /// the capsule checked.
        /// </summary>
        bool TryNearestFloor(Vector3 feet, out Vector3 spot, out string how)
        {
            spot = feet;
            how = null;
            _spotOffsets.Clear();
            _spots.Clear();
            foreach (float r in GuardRings)
                for (int k = 0; k < 12; k++)
                {
                    float a = (k + (r > 1f ? 0.5f : 0f)) * Mathf.PI * 2f / 12f;
                    var c = feet + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    if (!RayWorld(c + Vector3.up * GroundGuard.MaxRise, Vector3.down, GroundGuard.MaxRise + GroundGuard.MaxDrop, out var hit)) continue;
                    if (hit.normal.y < 0.6f || !CapsuleFree(hit.point, out _)) continue;
                    _spots.Add(hit.point);
                    _spotOffsets.Add((hit.point.x - feet.x, hit.point.y - feet.y, hit.point.z - feet.z));
                }
            if (NavFloorBelow(feet, out var below))
            {
                _spots.Add(below);
                _spotOffsets.Add((0f, below.y - feet.y, 0f));
            }
            int best = GroundGuard.Best(_spotOffsets);
            if (best < 0) return false;
            spot = _spots[best];
            var o = _spotOffsets[best];
            how = GroundGuard.Tier(o.dy) == GroundGuard.SpotTier.SameLevel
                ? $"the free floor {new Vector2(o.dx, o.dz).magnitude:0.0} m away on the same level"
                : $"the free floor {-o.dy:0.0} m below";
            return true;
        }

        /// <summary>
        /// Water mode for this frame from the imported water zones (<see cref="G3Water"/>; pure rules in
        /// <see cref="SwimRules"/>): body depth = surface - feet, column = surface - floor under the hero.
        /// </summary>
        void UpdateWater()
        {
            var feet = transform.position;
            var old = Water;
            bool silent = _waterSilent;
            _waterSilent = false;
            if (!G3Water.TrySurface(feet, out float surface))
            {
                WaterSurface = float.NegativeInfinity;
                WaterDepth = float.NaN;
                SetWater(old, WaterMode.None);
                return;
            }
            WaterSurface = surface;
            WaterDepth = surface - feet.y;
            float column = WaterDepth > 0 || old == WaterMode.Swim ? surface - G3Water.FloorUnder(feet, surface, transform) : WaterDepth;
            bool falling = !_cc.isGrounded && AirTime > 0.1f && old != WaterMode.Swim;
            float wade = SwimRules.WadeDepth(swimDepth, _cc.stepOffset);
            var mode = SwimRules.Classify(old, falling, WaterDepth, column, swimDepth, wade);
            if (mode == WaterMode.Swim && old != WaterMode.Swim && falling && !silent)
            {
                DivedIn?.Invoke(FallTime);
                G3WaterEffects.Play(G3WaterEffects.DropInWater, new Vector3(feet.x, surface, feet.z), transform);
            }
            SetWater(old, mode);
        }

        void SetWater(WaterMode old, WaterMode mode)
        {
            if (mode == old) return;
            Water = mode;
            if (mode == WaterMode.Swim)
            {
                SetSneaking(false);
                IsSliding = _slide.Sliding = false;
                AirTime = 0;
                FallTime = 0;
                _fallSpeed = 0;
            }
            // Waves around the body while wading / swimming (G3 SetEffectsByMode).
            if (old == WaterMode.Wade) G3WaterEffects.Stop(G3WaterEffects.WadeWaves, transform);
            if (old == WaterMode.Swim) G3WaterEffects.Stop(G3WaterEffects.SwimWaves, transform);
            var at = new Vector3(transform.position.x, WaterSurface, transform.position.z);
            if (mode == WaterMode.Wade) G3WaterEffects.Start(G3WaterEffects.WadeWaves, at, transform);
            if (mode == WaterMode.Swim) G3WaterEffects.Start(G3WaterEffects.SwimWaves, at, transform);
            WaterChanged?.Invoke(old, mode);
        }

        bool _waterSilent;

        /// <summary>
        /// Swimming (G3 SWIM): forward/back only at the Swim motion speeds (walk 1.5, run 4.0 m/s; no sprint, no
        /// sideways swimming, no jump), the feet held <see cref="swimDepth"/> below the surface (G3 PutToGround).
        /// Collisions still apply: a rising floor makes the hero stand and wade out.
        /// </summary>
        void SwimStep(Vector2 input, G3MoveMode mode, float dt)
        {
            MoveMode = mode == G3MoveMode.Walk ? G3MoveMode.Walk : G3MoveMode.Run;
            var (sx, sz) = SwimRules.SwimWish(input.x, input.y, MoveMode == G3MoveMode.Walk);
            var wish = Quaternion.Euler(0, _yaw, 0) * new Vector3(sx, 0, sz);
            float targetY = WaterSurface - swimDepth;
            _velocity.x = wish.x;
            _velocity.z = wish.z;
            _velocity.y = Mathf.Clamp((targetY - transform.position.y) / Mathf.Max(0.05f, swimSettle), -3f, 3f);
            var before = transform.position;
            _hasFloorContact = false;
            _steepCount = 0;
            _cc.Move(ClipAtBodies(_velocity * dt));
            Velocity = (transform.position - before) / dt;
            AirTime = 0;
            FallTime = 0;
            _fallSpeed = 0;
        }

        /// <summary>
        /// Slide state with hysteresis: starts after 0.15 s on ground steeper than slopeLimit + 3°, ends after 0.2 s on
        /// ground flatter than slopeLimit − 3° or in the air. The angle is the face normal under the feet.
        /// </summary>
        void UpdateSlide(bool grounded, float dt)
        {
            GroundAngle = 0;
            _groundNormal = Vector3.up;
            if (_hasFloorContact)
            {
                // The flattest face the capsule rested on in the last move (a ray straight down misses the face the
                // hero stands on when that face is a steep rock touching the side of the capsule's bottom sphere).
                _groundNormal = _contactNormal;
                GroundAngle = Vector3.Angle(_contactNormal, Vector3.up);
            }
            // Starts inside the own capsule, which a ray cast ignores. On a slope of angle a the capsule rests on its
            // bottom sphere, so the ground below the feet lies r·(1/cos a − 1) deeper: 1.5 m covers slopes up to ~75°.
            // With a contact, the ray only counts when it finds a flatter face right under the feet (on a stair edge
            // the contact normal is tilted, the tread under the feet is flat; 0.25 m covers walkable slopes).
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out var hit, 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                float a = Vector3.Angle(hit.normal, Vector3.up);
                if (!_hasFloorContact || (transform.position.y - hit.point.y < 0.25f && a < GroundAngle))
                {
                    _groundNormal = hit.normal;
                    GroundAngle = a;
                }
            }
            bool was = _slide.Sliding;
            IsSliding = _slide.Update(grounded, GroundAngle, AirTime, _cc.slopeLimit, dt);
            if (IsSliding && !was) _slideSpeed = Mathf.Max(1f, new Vector2(_velocity.x, _velocity.z).magnitude * 0.5f);
        }

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (hit.collider == null || hit.collider.isTrigger) return;
            var n = hit.normal;
            float feet = transform.position.y;
            // Under the feet: below the centre of the capsule's bottom sphere, facing up.
            // Faces steeper than 80° (edges, walls) are no floor: the slide direction would point almost straight down.
            if (n.y > 0.17f && hit.point.y < feet + _cc.radius * 0.95f)
            {
                if (!_hasFloorContact || n.y > _contactNormal.y) _contactNormal = n;
                _hasFloorContact = true;
            }
            // A face steeper than the limit (rock faces, walls, stair risers, landscape folds).
            if (n.y > -0.05f && Vector3.Angle(n, Vector3.up) > _cc.slopeLimit && _steepCount < _steepPoint.Length)
            {
                _steepPoint[_steepCount] = hit.point;
                _steepNormal[_steepCount] = n;
                _steepCount++;
            }
        }

        /// <summary>
        /// G3 only lets the hero move where the floor ahead is walkable (gCCharacterMovement_PS probes the position
        /// 0.15 s ahead and stops on non-walkable floor). Here: the part of the move that pushes into a steep face met
        /// at foot height in the last move is removed unless that face is a step or a landscape fold
        /// (<see cref="ProbeStep"/>). Without this the controller's step offset walks up rough rock faces in small steps.
        /// </summary>
        Vector3 BlockSteepContacts(Vector3 wish)
        {
            if (_steepCount == 0 || (wish.x == 0 && wish.z == 0)) return wish;
            float footTop = transform.position.y + _cc.stepOffset + 0.05f;
            for (int i = 0; i < _steepCount; i++)
            {
                var n = _steepNormal[i];
                if (_steepPoint[i].y >= footTop) continue; // met above foot height: the controller itself stops there
                if (wish.x * n.x + wish.z * n.z >= 0) continue; // not moving into it
                if (ProbeStep(_steepPoint[i], n, wish, out _) != SlopeRules.StepKind.Blocked) continue;
                var (x, z) = SlopeRules.BlockInto(wish.x, wish.z, n.x, n.z);
                wish.x = x;
                wish.z = z;
            }
            return wish;
        }

        /// <summary>
        /// Safety net: walkable floor crossing the capsule's axis above the feet means the capsule sank into it (mesh
        /// colliders are one-sided; a move that starts overlapping a face passes through it). Puts the feet back on it.
        /// </summary>
        void RecoverIfSunk(Vector3 before)
        {
            var feet = transform.position;
            // fix-134: never his own colliders (logged "no lift onto G3Player 1,39 m above the feet" while climbing crates)
            if (!RayWorld(feet + Vector3.up * 1.5f, Vector3.down, 1.5f, out var hit)) return;
            float angle = Vector3.Angle(hit.normal, Vector3.up);
            if (hit.point.y < feet.y + GroundGuard.SunkMinDepth || angle > _cc.slopeLimit) return;
            // fix-123 (owner: "carried into the cave ceiling"): the collision is two-sided, so the top of a low cave
            // ceiling over the hero is a walkable floor for this ray too. He only sank into a floor his feet were on (or
            // above) before the move, and the place on it must have room for him.
            bool sank = GroundGuard.ShouldLiftSunk(hit.point.y, feet.y, before.y, angle, _cc.slopeLimit);
            // fix-130: and he can get there from where he was before the move without passing through anything
            if (sank && CrossedGeometry(before, hit.point + Vector3.up * 0.05f, out var through))
            {
                CeilingGuards++;
                if (Time.time - _guardLogAt > 2f)
                {
                    _guardLogAt = Time.time;
                    Debug.Log($"[G3Player] fix-130: no lift onto {hit.collider.name} {hit.point.y - feet.y:0.00} m above the feet at {feet} ({through.name} lies between)");
                }
                return;
            }
            if (!sank || !CapsuleFree(hit.point, out _))
            {
                CeilingGuards++;
                if (Time.time - _guardLogAt > 2f)
                {
                    _guardLogAt = Time.time;
                    Debug.Log($"[G3Player] fix-123: no lift onto {hit.collider.name} {hit.point.y - feet.y:0.00} m above the feet at {feet} (" +
                              (sank ? "no room there" : $"a ceiling over him: the feet were {hit.point.y - before.y:0.00} m below it before the move") + ")");
                }
                return;
            }
            _cc.enabled = false;
            transform.position = hit.point + Vector3.up * 0.05f;
            _cc.enabled = true;
            if (_velocity.y < 0) _velocity.y = 0;
        }

        /// <summary>Heights (m above the feet) of the horizontal rays that look for steep faces between the hero and a landing.</summary>
        static readonly float[] ObstacleRayHeights = { 0.15f, 0.4f, 0.6f, 0.9f, 1.15f, 1.4f, 1.65f, 1.9f };

        readonly SlopeRules.FaceHit[] _faceHits = new SlopeRules.FaceHit[16];
        /// <summary>fix-148: the last step probe chose the near landing (<see cref="SlopeRules.NearLanding"/>).</summary>
        bool _landingNear;

        /// <summary>
        /// G3's floor test past a steep face met at <paramref name="point"/> while moving along <paramref name="move"/>
        /// (pure decision: <see cref="SlopeRules.DecideStep"/>): vertical rays for the floor at the landing points
        /// (<see cref="SlopeRules.LandingDistances"/>), horizontal rays at <see cref="ObstacleRayHeights"/> for steep faces
        /// on the way (landscape or not), and room for the capsule at the landing.
        /// </summary>
        SlopeRules.StepKind ProbeStep(Vector3 point, Vector3 faceNormal, Vector3 move, out Vector3 landing)
        {
            landing = default;
            var dir = new Vector3(move.x, 0f, move.z);
            if (dir.sqrMagnitude < 1e-6f) return SlopeRules.StepKind.Blocked;
            dir.Normalize();
            var feet = transform.position;
            float step = _cc.stepOffset;
            float contact = Vector3.Dot(new Vector3(point.x - feet.x, 0f, point.z - feet.z), dir);
            var landings = SlopeRules.LandingDistances(contact, _cc.radius);
            float reach = 0f;
            foreach (float d in landings) reach = Mathf.Max(reach, d);
            int faces = 0;
            foreach (float h in ObstacleRayHeights)
            {
                // fix-214: never his own colliders (carried weapons, hand props: none today, but they must not count)
                if (!RayWorld(feet + Vector3.up * h, dir, reach, out var hit)) continue;
                if (Vector3.Angle(hit.normal, Vector3.up) <= _cc.slopeLimit) continue; // the slope itself
                _faceHits[faces++] = new SlopeRules.FaceHit(h, hit.distance, IsLandscape(hit.collider));
            }
            var kind = SlopeRules.StepKind.Blocked;
            float top = feet.y + _cc.height - 0.05f;
            for (int li = 0; li < landings.Length; li++)
            {
                float d = landings[li];
                var at = feet + dir * d;
                // fix-148: only the faces on the way to this landing (not the next treads of a stair)
                var (wallTop, foldTop) = SlopeRules.TopsBefore(_faceHits, faces, d);
                var probe = new SlopeRules.StepProbe
                {
                    WallTop = wallTop, FoldTop = foldTop, FloorAngle = 90f,
                    SlopeRise = SlopeRules.PlaneRise(_groundNormal.x, _groundNormal.y, _groundNormal.z, dir.x, dir.z, d, _cc.slopeLimit),
                };
                if (RayWorld(new Vector3(at.x, top, at.z), Vector3.down, top - feet.y + step + 0.1f, out var floor))
                {
                    probe.FloorFound = true;
                    probe.FloorRise = floor.point.y - feet.y;
                    probe.FloorAngle = Vector3.Angle(floor.normal, Vector3.up);
                    probe.Fits = Fits(floor.point);
                    if (foldTop >= 0f && !IsLandscape(floor.collider)) probe.UnderLandscape = UnderLandscape(floor.point);
                    // fix-214: a hit above step height (door jamb, arch) is judged by sweeping the body over the step
                    if (probe.Fits && wallTop > step + Mathf.Max(probe.SlopeRise, 0f))
                        probe.BodyClear = BodyClearOver(feet, faceNormal, d, probe.FloorRise, step);
                }
                kind = SlopeRules.DecideStep(probe, step, _cc.slopeLimit);
                // the near landing (centre just past the face) is for steps only: a fold is passed onto the floor behind it
                if (li == SlopeRules.NearLandingIndex && kind == SlopeRules.StepKind.PassFold) kind = SlopeRules.StepKind.Blocked;
                if (kind == SlopeRules.StepKind.Blocked) continue;
                landing = floor.point;
                _landingNear = li == SlopeRules.NearLandingIndex;
                return kind;
            }
            return kind;
        }

        readonly RaycastHit[] _sweepHits = new RaycastHit[16];

        /// <summary>
        /// fix-214 (owner: the hero could not walk through the stone-arch doorway of the Nordmar monastery library with a
        /// torch and a bow on his back): the step probe's horizontal rays run along his centre line in the move direction,
        /// so a jamb or an arch met at an angle counted as a wall above step height and the threshold in front of it as
        /// "Blocked" (the controller's own step offset was then refused, too). Here the capsule (a little thinner), lifted
        /// onto the threshold, sweeps straight across the face over <paramref name="distance"/> m: nothing touched
        /// (his own colliders, triggers and characters aside) means the high hit lies beside his way.
        /// </summary>
        bool BodyClearOver(Vector3 feet, Vector3 faceNormal, float distance, float floorRise, float step)
        {
            var across = new Vector3(-faceNormal.x, 0f, -faceNormal.z);
            if (across.sqrMagnitude < 1e-6f) return false;
            across.Normalize();
            float lift = Mathf.Min(step, Mathf.Max(floorRise, LowFaceTop(feet, across, distance, step))) + 0.03f;
            float r = _cc.radius - 0.02f;
            var bottom = feet + Vector3.up * (lift + r);
            var top = feet + Vector3.up * Mathf.Max(lift + r, lift + HeroCapsule.Height - r - 0.05f);
            int n = Physics.CapsuleCastNonAlloc(bottom, top, r, across, _sweepHits, Mathf.Max(0.05f, distance), ~0,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _sweepHits[i].collider;
                if (c == null || c.transform.IsChildOf(transform) || c is CharacterController) continue;
                return false;
            }
            return true;
        }

        /// <summary>The lowest probe-ray height (up to step height) at which nothing steep lies across the way: just over the threshold's lip.</summary>
        float LowFaceTop(Vector3 feet, Vector3 across, float distance, float step)
        {
            foreach (float h in ObstacleRayHeights)
            {
                if (h > step) break;
                if (!RayWorld(feet + Vector3.up * h, across, distance, out var hit) || Vector3.Angle(hit.normal, Vector3.up) <= _cc.slopeLimit)
                    return h;
            }
            return step;
        }

        /// <summary>How far above a fold's landing the landscape is looked for (m): a hill over a cave (fix-99).</summary>
        const float LandscapeCoverReach = 60f;

        /// <summary>
        /// A landscape surface lies above <paramref name="floor"/>, a floor that is not landscape itself (back faces count:
        /// the landscape may be one-sided): the landing is inside a hill (fix-99: past a "fold" of the hillside the floor ray, which starts at head height, found
        /// the cave behind it and put the hero into the cave through its back wall).
        /// </summary>
        bool UnderLandscape(Vector3 floor)
        {
            bool back = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                // from above the highest fold (a fin may lean over its own landing), up into the hill
                float from = SlopeRules.FoldHeightMax + 0.5f;
                int n = Physics.RaycastNonAlloc(floor + Vector3.up * from, Vector3.up, _coverHits, LandscapeCoverReach, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                    if (IsLandscape(_coverHits[i].collider)) return true;
                return false;
            }
            finally { Physics.queriesHitBackfaces = back; }
        }

        readonly RaycastHit[] _coverHits = new RaycastHit[16];

        /// <summary>Whether the hero's capsule (a little thinner, lifted off a walkable slope) has room standing at <paramref name="feet"/>.</summary>
        bool Fits(Vector3 feet)
        {
            float r = _cc.radius;
            int n = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * (r + 0.15f), feet + Vector3.up * (_cc.height - r), r - 0.05f,
                _overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (_overlap[i] != _cc && !_overlap[i].transform.IsChildOf(transform)) return false; // fix-214: own gear aside
            return true;
        }

        /// <summary>
        /// The landscape's collider: its object, collision mesh or render mesh is named after the landscape cell
        /// ("…_Landscape_Cell_…", the same test G3CollisionBuilder uses for terrain).
        /// </summary>
        bool IsLandscape(Collider c)
        {
            if (c == null) return false;
            if (_landscape.TryGetValue(c, out bool yes)) return yes;
            static bool Named(string n) => n != null && n.IndexOf("Landscape", StringComparison.OrdinalIgnoreCase) >= 0;
            yes = Named(c.gameObject.name) || c is MeshCollider mc && mc.sharedMesh != null && Named(mc.sharedMesh.name) ||
                  c.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null && Named(mf.sharedMesh.name);
            if (_landscape.Count > 4096) _landscape.Clear();
            _landscape[c] = yes;
            return yes;
        }

        /// <summary>
        /// The hero is held by a steep face he walks into (fix-49): a step within step height is climbed with three
        /// controller moves (up, on, down: walls and ceilings still block each of them); a landscape fold is passed by
        /// putting him on the floor behind it. Returns whether he moved; otherwise he stays where he was.
        /// </summary>
        bool PassSteep(Vector3 wish, float dt)
        {
            for (int i = 0; i < _steepCount; i++)
            {
                var n = _steepNormal[i];
                if (wish.x * n.x + wish.z * n.z >= 0) continue; // not moving into it
                var kind = ProbeStep(_steepPoint[i], n, wish, out var landing);
                if (kind == SlopeRules.StepKind.Step) return StepUp(wish, landing, dt);
                if (kind != SlopeRules.StepKind.PassFold) continue;
                var from = transform.position;
                Place(landing + Vector3.up * 0.03f);
                _cc.Move(Vector3.down * 0.1f);
                FoldPasses++;
                if (Time.time - _foldLogAt > 2f)
                {
                    _foldLogAt = Time.time;
                    Debug.Log($"[G3Player] walked past a landscape fold {from} -> {transform.position} (G3 floor rule, fix-49)");
                }
                return true;
            }
            return false;
        }

        /// <summary>Up by step height, on towards the landing, down onto the floor; undone unless he ends on the ground further on.</summary>
        bool StepUp(Vector3 wish, Vector3 landing, float dt)
        {
            var start = transform.position;
            var dir = new Vector3(wish.x, 0f, wish.z).normalized;
            float toLanding = new Vector2(landing.x - start.x, landing.z - start.z).magnitude;
            // fix-148: onto the near landing (steep open stairs) all the way, so his centre ends over the step
            float onward = SlopeRules.StepOnward(new Vector2(wish.x, wish.z).magnitude * dt, toLanding, _landingNear);
            float lift = Mathf.Max(_cc.stepOffset + 0.05f, landing.y - start.y + 0.1f); // a step on a slope: plus its rise
            _cc.Move(Vector3.up * lift);
            float up = transform.position.y - start.y;
            _cc.Move(ClipAtBodies(dir * onward));
            var on = transform.position - start;
            _cc.Move(Vector3.down * (up + onward * Mathf.Tan(_cc.slopeLimit * Mathf.Deg2Rad) + 0.05f));
            float gained = transform.position.y - start.y;
            if (!SlopeRules.StepUpLanded(up, landing.y - start.y, new Vector2(on.x, on.z).magnitude, _cc.isGrounded, gained, lift))
            {
                Place(start);
                return false;
            }
            // fix-130: never up through geometry (a roof over a crate stack)
            if (GroundGuard.NeedsPathCheck(start.y, transform.position.y) && CrossedGeometry(start, transform.position, out var through))
            {
                Place(start);
                CeilingGuards++;
                if (Time.time - _guardLogAt > 2f)
                {
                    _guardLogAt = Time.time;
                    Debug.Log($"[G3Player] fix-130: step-up {start} -> {transform.position} undone ({through.name} lies between)");
                }
                return false;
            }
            StepUps++;
            return true;
        }

        void Place(Vector3 feet)
        {
            _cc.enabled = false;
            transform.position = feet;
            _cc.enabled = true;
        }

        /// <summary>Fly mode: hands the camera to FlyCamera; leaving it drops the player below the camera.</summary>
        public void SetFlying(bool fly)
        {
            if (fly == IsFlying || playerCamera == null) return;
            if (fly && IsNoclip) SetNoclip(false); // fix-131: the free camera and the noclip hero exclude each other
            IsFlying = fly;
            var ct = playerCamera.transform;
            if (fly)
            {
                ct.SetParent(null, true);
                if (_fly == null) _fly = playerCamera.gameObject.AddComponent<FlyCamera>();
                _fly.enabled = true;
                _cc.enabled = false;
                IsSliding = false;
                Velocity = Vector3.zero;
                G3Cursor.DemoCapture(false);
            }
            else
            {
                if (_fly != null) _fly.enabled = false;
                var p = ct.position;
                Teleport(Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var hit, 500f, ~0, QueryTriggerInteraction.Ignore)
                    ? hit.point : p - Vector3.up * eyeHeight);
                _yaw = ct.eulerAngles.y;
                ct.SetParent(transform, true);
                _cc.enabled = true;
                _velocity = Vector3.zero;
            }
        }

        /// <summary>Places the feet at <paramref name="feet"/> (disables the controller for the move).</summary>
        public void Teleport(Vector3 feet)
        {
            TeleportCore(feet);
            _lastSafe = null; // fix-123: his last valid place is from before the teleport; the next valid ground sets it
        }

        /// <summary><see cref="Teleport"/> that keeps his last valid place (the world catch, the ground guard).</summary>
        void TeleportCore(Vector3 feet)
        {
            // fix-76: the camera blends a short move (an object use's spot) and cuts a far one (G3 EntityMovedBigDistance)
            var target = feet + Vector3.up * 0.05f;
            _cameraMove += target - transform.position;
            TeleportSerial++;
            bool was = _cc.enabled;
            _cc.enabled = false;
            transform.position = feet + Vector3.up * 0.05f;
            _cc.enabled = was;
            _velocity = Vector3.zero;
            Velocity = Vector3.zero;
            AirTime = 0;
            FallTime = 0;
            _steepCount = 0;
            _hasFloorContact = false;
            _slide = default;
            IsSliding = false;
            // Loading a save or a teleport into water: the next step classifies again, without a dive-in splash.
            _waterSilent = true;
            if (Water != WaterMode.None)
            {
                G3WaterEffects.Stop(Water == WaterMode.Swim ? G3WaterEffects.SwimWaves : G3WaterEffects.WadeWaves, transform);
                var old = Water;
                Water = WaterMode.None;
                WaterChanged?.Invoke(old, WaterMode.None);
            }
        }

        public void SetLook(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            transform.rotation = Quaternion.Euler(0, _yaw, 0);
        }

        /// <summary>
        /// fix-76: turns the hero's look like <see cref="SetLook"/> (an object use faces its spot), but the third-person
        /// camera swings round to the new look instead of cutting (<see cref="TakeCameraSnap"/>). Aiming and scripted looks
        /// keep <see cref="SetLook"/> (the camera follows at once, as with the mouse).
        /// </summary>
        public void TurnTo(float yaw, float pitch)
        {
            float oldYaw = _yaw, oldPitch = _pitch;
            SetLook(yaw, pitch);
            _cameraYaw += Mathf.DeltaAngle(_yaw, oldYaw);
            _cameraPitch += oldPitch - _pitch;
        }

        /// <summary>Counts <see cref="Teleport"/> calls (a load, a teleport, an object use's spot).</summary>
        public int TeleportSerial { get; private set; }

        Vector3 _cameraMove;
        float _cameraYaw, _cameraPitch;

        /// <summary>
        /// fix-76: what moved the hero or his look outside walking and the mouse since the camera last asked: the summed
        /// teleport offset (m) and the yaw/pitch the camera should swing in from (<see cref="TurnTo"/>; degrees, old − new).
        /// Clears them.
        /// </summary>
        public void TakeCameraSnap(out Vector3 moved, out float yawFrom, out float pitchFrom)
        {
            moved = _cameraMove;
            yawFrom = _cameraYaw;
            pitchFrom = _cameraPitch;
            _cameraMove = Vector3.zero;
            _cameraYaw = _cameraPitch = 0f;
        }

        static void AddTime(float hours)
        {
            var env = FindAnyObjectByType<G3Environment>();
            if (env == null) return;
            env.timeOfDay = Mathf.Repeat(env.timeOfDay + hours, 24f);
            env.Apply();
        }

        G3Environment _helpEnv;

        /// <summary>
        /// The dev keys (F fly, [ ] time of day, F1 help and its corner hint): editor and development builds only, so a
        /// release player shows the game alone. Set by tools that need them in a release build.
        /// </summary>
        public static bool DevKeys
        {
            get => s_devKeys ??= Debug.isDebugBuild;   // lazily: not from a static initializer (serialization thread)
            set => s_devKeys = value;
        }
        static bool? s_devKeys;

        void OnGUI()
        {
            if (!DevKeys) return;
            _style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true };
            if (!_showHelp)
            {
                GUI.Label(new Rect(10, 10, 300, 22), "F1 — help");
                return;
            }
            if (_helpEnv == null) _helpEnv = FindAnyObjectByType<G3Environment>();
            var env = _helpEnv;
            string time = env != null ? $"{(int)env.timeOfDay:00}:{(int)(env.timeOfDay % 1 * 60):00}" : "n/a";
            var p = transform.position;
            string text =
                "<b>Gothic 3 world — player</b>\n" +
                "Click: capture mouse    Esc: release\n" +
                "W A S D / arrows: move    Q / E: turn    Shift: sprint    Caps Lock: walk    Left Alt: jump    Ctrl: sneak\n" +
                "Space / middle mouse: draw    T: first person    wheel / Num + -: zoom    Num 2: look behind    Num 4: reset camera\n" +
                "F: fly mode (right mouse look, WASD, Q/E, wheel = speed)\n" +
                "[ / ]: time of day −/+ 1 h\n" +
                "F1: hide help\n\n" +
                $"mode: {(IsFlying ? "fly" : IsNoclip ? "noclip" : MoveMode.ToString())}{(IsSliding ? " (sliding)" : "")}{(Water != WaterMode.None ? $" ({Water}, {WaterDepth:F2} m)" : "")}   grounded: {IsGrounded}   " +
                $"speed: {new Vector2(Velocity.x, Velocity.z).magnitude:F1} m/s\n" +
                $"position: {p.x:F1} {p.y:F1} {p.z:F1}   time: {time}";
            GUI.Box(new Rect(10, 10, 540, 220), text, _style);
        }
    }
}
