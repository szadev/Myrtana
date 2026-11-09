using G3.Player;
using G3.Unity.Characters;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Camera of the <see cref="G3Player"/> (on the camera object, a child of the player).
    /// Third person (default, as in G3): follows straight behind the hero's head (G3: no shoulder offset, fix-90), looking along the
    /// player's yaw/pitch; sphere-cast collision pulls the camera in front of walls and terrain and eases back out;
    /// the session keys Plus/Minus (G3: wheel forward raises the distance, Num + / Num -) zoom; zooming in past the minimum
    /// switches to first person, out of first person back to third; Look (Num 2) looks behind while held, ResetCamera
    /// (Num 4) restores the mode's best distance. First person: at the player's eye height; the hero head is reduced to its
    /// shadow (<see cref="IG3BodyView"/>). FirstPerson (T / Num Del) toggles. Does nothing while the player flies
    /// (FlyCamera owns the camera then).
    /// fix-76 stability (Docs/hero.md "Camera stability", <see cref="CameraStability"/>): runs after every Update and
    /// LateUpdate that moves or poses the hero (execution order 1000); the head offset and the feet height are damped
    /// separately; collision pulls in at once and eases out after a hold, characters and loose physics bodies only block
    /// near the camera; mode switches, a dialog's end, short teleports and <see cref="G3Player.TurnTo"/> blend; a far
    /// teleport or a load cuts.
    /// </summary>
    [DefaultExecutionOrder(1000)] // fix-76: after the hero's movement (0), animation (100-200), head look (290), teleports
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Player Camera")]
    public sealed class G3PlayerCamera : MonoBehaviour
    {
        public G3Player player;
        public bool thirdPerson = true;

        [Header("G3 camera mode (PC_Camera gCCameraAI_PS; Docs/hero.md \"Camera\")")]
        [Tooltip("Use the G3 mode values: zoom range, distance kept while moving, field of view (else the fields below)")]
        public bool g3Modes = true;
        /// <summary>
        /// The G3 camera mode. Every player weapon state (normal, melee, ranged, magic) uses CamModeRanged in G3; the
        /// inventory/interaction mode exists for the HUD (<see cref="CameraModes"/>).
        /// </summary>
        [System.NonSerialized] public CameraMode mode = CameraMode.Ranged;
        [Tooltip("Above this horizontal speed (m/s) the hero counts as moving (G3 IsStanding)")] public float movingSpeed = 0.1f;

        /// <summary>
        /// True while a HUD page is open or the hero uses an object (G3 PS_HUD / PS_Interact: PSCamera::SetMode(5),
        /// CamModeInventory: best range 1.5 m, FOV 45); set by the game. SetMode resets the zoom to the mode's best range,
        /// on the way in and back.
        /// </summary>
        [System.NonSerialized] public System.Func<bool> hudOrInteraction;

        /// <summary>
        /// fix-253: true while the hero has a ranged weapon drawn (set by PlayerCombat each frame). The view then turns
        /// with the look only (<see cref="ThirdPersonAim.SteadyForward"/>: the unobstructed boom's look-at direction):
        /// obstacles and the drawn relief still move the camera, but no longer turn it, so the crosshair follows the mouse
        /// one to one. Eased in and out (<see cref="ThirdPersonAim.SteadyEaseSeconds"/>); without obstacles both
        /// directions are the same.
        /// </summary>
        [System.NonSerialized] public bool steadyAim;

        /// <summary>fix-253: logs player pitch, view pitch, boom and relief state per frame (or set env G3_TRACE_AIM=1).</summary>
        public static bool TraceAimPitch = System.Environment.GetEnvironmentVariable("G3_TRACE_AIM") == "1";

        /// <summary>
        /// fix-122 [owner]: the port option "Camera in windows". false (default, "as in play, no zoom"): pages and object
        /// uses keep the play camera exactly (<see cref="CameraModes.HoldsPlayCamera"/>); true: G3's CamModeInventory.
        /// Set by the game's options.
        /// </summary>
        public static bool OriginalWindowCamera { get; set; }

        /// <summary>fix-122: the play camera is held this frame (a page or a use runs under the default option).</summary>
        public bool HoldingPlayCamera { get; private set; }
        float _heldWanted = float.NaN;

        /// <summary>
        /// A trigger collider that is a character's body (set by the game: combat bodies). G3's camera cast is blocked by
        /// NPCs; they never count as thin obstacles.
        /// </summary>
        public static System.Func<Collider, bool> IsCharacter;

        [Tooltip("Look at TargetOffsetY above the head (G3 mode values) instead of along the boom")] public bool targetOffset = true;

        [Header("Third person")]
        [Tooltip("Look-at pivot above the feet, m")] public float pivotHeight = 1.7f;
        /// <summary>
        /// Added to the boom length while the hero is in a longer body (animal form): the zoom range counts from the
        /// body's rear instead of the controller's centre, so a wolf or boar does not fill the view [interp].
        /// </summary>
        [System.NonSerialized] public float bodyExtraDistance;
        [Tooltip("Sideways offset to the right, m (0 = centred); only without the G3 modes (G3: none)")] public float shoulderOffset = 0.35f;
        public float distance = 3f;
        public float minDistance = 1.2f, maxDistance = 8f;
        [Tooltip("Distance change per wheel notch, m")] public float zoomStep = 0.4f;
        public float minPitch = -60f, maxPitch = 70f;
        [Tooltip("Sphere radius of the shoulder leg and of the use-orbit probes, m")] public float collisionRadius = 0.2f;
        [Tooltip("Sphere radius of the back leg, m: G3 traces a ray and then keeps 17.5 cm off the surface along its normal " +
                 "(fix-94); a thin sweep keeps the near plane out of walls")]
        public float boomRadius = 0.1f;
        public LayerMask collisionMask = ~0;
        [Tooltip("Seconds for the camera to ease back out after an obstacle (critically damped)")] public float collisionRecover = 0.3f;
        [Tooltip("Obstacles narrower than this in plan view (posts, poles, thin trunks, small props), m, do not pull the camera in")]
        public float minObstacleWidth = 0.6f;
        [Tooltip("Below this camera distance the look-at lift is off (fix-94: the hero is hidden by his eye distance instead, " +
                 "CameraStability.HidesHero)")] public float hideBodyDistance = 0.6f;

        [Header("Debug")]
        [Tooltip("Log every frame in which the camera distance changes by more than jumpThreshold, with the cause")]
        public bool debugJumps;
        public float jumpThreshold = 0.3f;
        /// <summary>Turns <see cref="debugJumps"/> on for every player camera (set from an eval before or during Play).</summary>
        public static bool DebugJumpsAll;
        /// <summary>Jumps logged since the domain loaded (for an automated check).</summary>
        public static int JumpCount;

        [Header("Orbit offset (scripted orbit, look behind)")]
        public float orbitReturnTime = 0.35f;
        [Tooltip("Zoom with keys held (G3 Num + / Num -), wheel notches per second [interp]")] public float keyZoomNotchesPerSecond = 6f;
        bool _lookingBehind;

        [Header("Test input")]
        [Tooltip("While non-zero: fixed orbit offset (yaw, pitch degrees), e.g. for screenshots from the side")]
        public Vector2 scriptedOrbit;

        [Header("First person")]
        [Tooltip("Camera offset in front of the eye point, m (keeps the near plane out of the face)")]
        public float firstPersonForward = 0.1f;

        [Header("Combat mode (a focus target is locked)")]
        [Tooltip("Camera distance × this while a target is locked")] public float combatDistanceFactor = 0.8f;
        [Tooltip("Seconds to ease in and out of the combat distance")] public float combatEase = 0.4f;
        [Tooltip("Shake amplitude falls off by this factor per second")] public float shakeDamping = 8f;

        /// <summary>The locked combat target (set by the game while a weapon is drawn), or null.</summary>
        [System.NonSerialized] public Transform combatTarget;

        float _combatBlend, _shake;

        float _orbitYaw, _orbitPitch, _orbitVelYaw, _orbitVelPitch;
        // fix-73: the orbit of the Camera rotation keys (the port's option, KeyOrbitRules)
        float _keyYaw, _keyYawVel;

        /// <summary>Degrees the Camera rotation keys have orbited the camera round the hero (port option; 0 = behind him).</summary>
        public float KeyOrbitYaw => _keyYaw;
        float _currentDistance = -1, _distanceVel, _currentShoulder = -1, _shoulderVel, _pivotY;
        // fix-76: feet height and pivot height of the pivot (damped), collision hysteresis, moving latch, blends, look snaps
        float _pivotHeightNow = float.NaN, _pivotHeightVel, _stillFor;
        CameraStability.RootFilter _root;
        CameraStability.FreeLimit _free;
        CameraStability.HeadFilter _headFilter;
        ReliefCameraRules.State _relief;   // fix-240: lift / pull-in against the drawn terrain relief
        // fix-253: the unobstructed boom length (wished, eased; no pull-in) and the steady-aim weight
        float _framingDistance = -1f, _framingVel, _steadyW, _traceViewPitch = float.NaN, _tracePlayerPitch;
        float _moveRange;   // fix-256: CameraStability.MoveRangeWeight's linear state
        Vector3 _blendPos, _lastFeet;
        Quaternion _blendRot;
        float _blendFov = float.NaN, _blendT, _blendDur, _snapYaw, _snapYawVel, _snapPitch, _snapPitchVel;
        bool _placed, _enabledAgain;
        G3MotionPlayer _motion;
        float _motionSearch;

        /// <summary>Counts camera cuts (a far teleport, a load): the jitter probe does not difference across one.</summary>
        public int CutSerial { get; private set; }
        /// <summary>A transition blend (mode switch, dialog end, short teleport) is running.</summary>
        public bool Blending => _blendDur > 0f;
        readonly RaycastHit[] _hits = new RaycastHit[16];
        // debug
        float _lastLoggedDistance = -1;
        Vector3 _lastPivot;
        string _shoulderCause, _backCause;
        bool _viewApplied, _appliedFirstPerson, _appliedHidden, _heroHidden;

        /// <summary>Real first person (the head casts shadows only). fix-94: no longer when a third-person camera is pulled
        /// in by an obstacle: G3 never shows the hero without his head, it fades the whole hero (<see cref="HeroHidden"/>).</summary>
        public bool IsFirstPersonView => !thirdPerson;
        /// <summary>The third-person camera is so close to the hero's eyes that G3 has faded him out (CameraStability.HidesHero).</summary>
        public bool HeroHidden => thirdPerson && _heroHidden;
        /// <summary>Distance of the camera from the hero's eyes (the first-person point), m; -1 before it is placed.</summary>
        public float EyeDistance { get; private set; } = -1f;
        /// <summary>Boom length in use (third person), m; -1 before it is placed.</summary>
        public float CurrentDistance => _currentDistance;
        /// <summary>Parameters of the current G3 mode.</summary>
        public CameraModeParams ModeParams => CameraModes.Params(mode);
        /// <summary>The sideways offset in use: none with the G3 modes (PC_Camera CamOffsetX 0; fix-90), else <see cref="shoulderOffset"/>.</summary>
        float Shoulder => CameraModes.Shoulder(g3Modes, shoulderOffset);
        float MinZoom => g3Modes ? ModeParams.MinRange : minDistance;
        float MaxZoom => g3Modes ? ModeParams.MaxRange : maxDistance;
        Camera _cam;

        void OnEnable()
        {
            _viewApplied = false;
            _lastLoggedDistance = -1;
            _enabledAgain = _placed; // back from the dialog camera (or a framed shot): blend from where it left the camera
        }

        void LateUpdate()
        {
            if (player == null || player.IsFlying) return;
            ConsumeSnaps();
            UpdateMode();
            HandleInput();
            if (thirdPerson) PlaceThirdPerson();
            else PlaceFirstPerson();
            ApplyBodyView();
            _placed = true;
        }

        /// <summary>
        /// fix-76: teleports and look snaps since the last frame (<see cref="G3Player.TakeCameraSnap"/>): a far move (or a
        /// load, also one not made through Teleport) cuts like G3's EntityMovedBigDistance; a short one blends; a
        /// <see cref="G3Player.TurnTo"/> swings the camera round. Back from the dialog camera the camera blends from the
        /// last dialog shot.
        /// </summary>
        void ConsumeSnaps()
        {
            player.TakeCameraSnap(out var moved, out float yawFrom, out float pitchFrom);
            var feet = player.transform.position;
            bool jumped = _placed && (feet - _lastFeet).magnitude > CameraStability.CutDistance;
            _lastFeet = feet;
            if (!_placed) return;
            if (jumped || CameraStability.Cuts(moved.magnitude))
            {
                Cut();
                _enabledAgain = false;
                return;
            }
            if (_enabledAgain)
            {
                _enabledAgain = false;
                BeginBlend(CameraStability.DialogBlendSeconds);
                return; // the conversation's look snaps (facing the speaker) are part of that blend
            }
            if (moved.sqrMagnitude > 1e-4f) BeginBlend(CameraStability.SnapBlendSeconds);
            if (thirdPerson && (yawFrom != 0f || pitchFrom != 0f))
            {
                _snapYaw = Mathf.DeltaAngle(0f, _snapYaw + yawFrom);
                _snapPitch = Mathf.Clamp(_snapPitch + pitchFrom, -90f, 90f);
            }
        }

        /// <summary>
        /// fix-105: a load or new game — the orbit offset (scripted orbit, look behind, key turns), combat blend and shake go,
        /// then <see cref="Cut"/>: the camera starts behind the hero as after a teleport.
        /// </summary>
        public void ResetTransient()
        {
            _orbitYaw = _orbitPitch = _orbitVelYaw = _orbitVelPitch = 0f;
            _keyYaw = _keyYawVel = 0f;
            _lookingBehind = false;
            _combatBlend = _shake = 0f;
            Cut();
        }

        /// <summary>Drops every smoothing state: the next frame places the camera where it belongs (teleport, load).</summary>
        public void Cut()
        {
            _currentDistance = _currentShoulder = -1;
            _framingDistance = -1f;
            _traceViewPitch = float.NaN;
            _distanceVel = _shoulderVel = 0;
            _root = default;
            _pivotHeightNow = float.NaN;
            _pivotHeightVel = 0;
            _headFilter = default;
            _free = default;
            _relief = default;
            _stillFor = CameraStability.StandLatchSeconds;
            _blendDur = 0;
            _snapYaw = _snapYawVel = _snapPitch = _snapPitchVel = 0;
            _lastLoggedDistance = -1;
            _heldWanted = float.NaN;
            CutSerial++;
        }

        /// <summary>Blends from the camera's pose now to the placed one over <paramref name="seconds"/> (smoothstep).</summary>
        public void BeginBlend(float seconds)
        {
            if (!_placed || seconds <= 0f) return;
            _blendPos = transform.position;
            _blendRot = transform.rotation;
            if (_cam == null) _cam = GetComponent<Camera>();
            _blendFov = _cam != null ? _cam.fieldOfView : float.NaN;
            _blendT = 0f;
            _blendDur = seconds;
        }

        /// <summary>Sets the camera pose (and the FOV unless NaN), through a running transition blend.</summary>
        void ApplyPose(Vector3 at, Quaternion rot, float fov)
        {
            if (_blendDur > 0f)
            {
                _blendT += Mathf.Min(Time.unscaledDeltaTime, 0.1f); // a paused game (HUD page) still finishes the blend
                float w = CameraStability.BlendWeight(_blendT, _blendDur);
                at = Vector3.Lerp(_blendPos, at, w);
                rot = Quaternion.Slerp(_blendRot, rot, w);
                if (!float.IsNaN(fov) && !float.IsNaN(_blendFov)) fov = Mathf.Lerp(_blendFov, fov, w);
                if (w >= 1f) _blendDur = 0f;
            }
            transform.SetPositionAndRotation(at, rot);
            if (!float.IsNaN(fov)) SetFov(fov);
        }

        /// <summary>The look snap offsets (<see cref="G3Player.TurnTo"/>) swing back to 0.</summary>
        void DecaySnaps(float dt)
        {
            if (HoldingPlayCamera) return; // fix-122: a use's turns to its spot do not swing the held camera round
            if (_snapYaw != 0f)
            {
                _snapYaw = Mathf.SmoothDampAngle(_snapYaw, 0f, ref _snapYawVel, CameraStability.LookSnapSeconds, Mathf.Infinity, dt);
                if (Mathf.Abs(_snapYaw) < 0.05f) _snapYaw = _snapYawVel = 0f;
            }
            if (_snapPitch != 0f)
            {
                _snapPitch = Mathf.SmoothDamp(_snapPitch, 0f, ref _snapPitchVel, CameraStability.LookSnapSeconds, Mathf.Infinity, dt);
                if (Mathf.Abs(_snapPitch) < 0.05f) _snapPitch = _snapPitchVel = 0f;
            }
        }

        void HandleInput()
        {
            // G3 session keys (G3Input): FirstPerson T / Num Del, Plus (raise the distance) Num + / wheel forward, Minus
            // Num - / wheel back, Look (look behind while held) Num 2, ResetCamera Num 4. Rebinding applies at once.
            bool captured = G3Cursor.Captured;
            if (captured && G3Input.WasPressed(SessionKey.FirstPerson)) SetThirdPerson(!thirdPerson);

            // fix-101: the wheel zooms only in play — over a page, menu or dialog list it scrolls that UI (CameraZoomInput)
            float notches = CameraZoomInput.Notches(captured,
                G3Input.WheelNotches(SessionKey.Plus) - G3Input.WheelNotches(SessionKey.Minus),
                ((G3Input.IsHeldByButton(SessionKey.Plus) ? 1f : 0f) - (G3Input.IsHeldByButton(SessionKey.Minus) ? 1f : 0f))
                * keyZoomNotchesPerSecond * Time.unscaledDeltaTime);
            if (notches != 0)
            {
                if (thirdPerson)
                {
                    float target = distance + notches * zoomStep;
                    if (target < MinZoom - 0.01f) SetThirdPerson(false);
                    else distance = Mathf.Min(target, MaxZoom);
                }
                else if (notches > 0) { SetThirdPerson(true); distance = MinZoom; }
            }
            if (captured && G3Input.WasPressed(SessionKey.ResetCamera))
            {
                distance = g3Modes ? Mathf.Clamp(ModeParams.BestRange, MinZoom, MaxZoom) : 3f;
                _orbitYaw = _orbitPitch = _orbitVelYaw = _orbitVelPitch = 0;
                _keyYaw = _keyYawVel = 0;
            }

            bool behind = thirdPerson && captured && G3Input.IsDown(SessionKey.Look);
            if (_lookingBehind && !behind) _orbitYaw = _orbitPitch = _orbitVelYaw = _orbitVelPitch = 0; // snaps back on release
            _lookingBehind = behind;
            player.LookingBehind = behind;
            player.lookInputEnabled = true;
            UpdateKeyOrbit(behind);
            if (scriptedOrbit != Vector2.zero)
            {
                _orbitYaw = scriptedOrbit.x;
                _orbitPitch = scriptedOrbit.y;
            }
            else if (behind)
            {
                _orbitYaw = 180f; // G3 "Camera Look" (HUD_SessionKey_Look "Look behind"): the camera faces back while held
                _orbitPitch = 0f;
            }
            else
            {
                _orbitYaw = Mathf.SmoothDampAngle(_orbitYaw, 0, ref _orbitVelYaw, orbitReturnTime);
                _orbitPitch = Mathf.SmoothDamp(_orbitPitch, 0, ref _orbitVelPitch, orbitReturnTime);
            }
        }

        /// <summary>
        /// The port's option (fix-73): the Camera rotation keys (G3 Q / E) orbit the camera round the hero's pivot while held
        /// out of combat; the offset holds while he stands and swings back behind him once he moves, a weapon is drawn or a
        /// HUD page / use takes the camera (<see cref="KeyOrbitRules"/>). Looking behind or a camera reset drops it.
        /// </summary>
        void UpdateKeyOrbit(bool behind)
        {
            if (behind || scriptedOrbit != Vector2.zero || !thirdPerson) { _keyYaw = _keyYawVel = 0; return; }
            bool orbit = player.TurnKeysOrbit;
            var v = player.Velocity;
            bool moving = v.x * v.x + v.z * v.z > movingSpeed * movingSpeed || player.MoveKeys != Vector2.zero;
            _keyYaw = KeyOrbitRules.Step(_keyYaw, orbit ? player.TurnKeyInput() : 0f, !orbit || moving || mode == CameraMode.Inventory,
                                         Time.deltaTime, ref _keyYawVel);
        }

        /// <summary>G3 PSCamera::SetMode: Inventory while a page or a use runs, else Ranged; each switch resets the zoom.
        /// fix-122: only with the option "Camera in windows: Original"; by default the play mode stays and the camera is held.</summary>
        void UpdateMode()
        {
            bool hud = hudOrInteraction != null && hudOrInteraction();
            HoldingPlayCamera = CameraModes.HoldsPlayCamera(hud, OriginalWindowCamera);
            var wanted = CameraModes.OfPlayerState(hud, OriginalWindowCamera);
            if (wanted == mode) return;
            mode = wanted;
            if (g3Modes) distance = ModeParams.BestRange;
            BeginBlend(CameraStability.ModeBlendSeconds); // fix-76: FOV 60 <-> 45 and the look-at lift no longer snap
        }

        /// <summary>A short shake (heavy hit taken); <paramref name="strength"/> in metres of offset.</summary>
        public void Shake(float strength) => _shake = Mathf.Max(_shake, strength);

        public void SetThirdPerson(bool on)
        {
            if (thirdPerson == on) return;
            thirdPerson = on;
            _currentDistance = _lastLoggedDistance = -1;
            _free = default;
            _blendDur = 0; // a deliberate cut (G3 switches first person at once)
            _orbitYaw = _orbitPitch = 0;
            _keyYaw = _keyYawVel = 0;
            if (player != null) player.lookInputEnabled = true;
        }

        [Tooltip("The pivot follows the hero head's height (G3 GetHeadMat): drawn stances crouch, the camera with them")]
        public bool followHead = true;
        float _headOffset;
        Transform _head;
        float _headSearch;

        /// <summary>
        /// The head offset of the pivot now (<see cref="CameraPivotRules.HeadOffset"/>): the hero's head bone
        /// (<c>Hero_Head_Head_1</c>) height above the feet against the unarmed stand's. 0 without the hero's head (an animal
        /// form has its own pivot height, HeroFormView) or with <see cref="followHead"/> off.
        /// </summary>
        float HeadOffsetNow(float feetY)
        {
            if (!followHead) return 0f;
            if ((_head == null || !_head.gameObject.activeInHierarchy) && Time.unscaledTime >= _headSearch)
            {
                _headSearch = Time.unscaledTime + 1f;
                _head = null;
                foreach (var t in player.GetComponentsInChildren<Transform>(false))
                    if (t.name == HeadBone) { _head = t; break; }
            }
            return _head != null && _head.gameObject.activeInHierarchy ? CameraPivotRules.HeadOffset(_head.position.y - feetY) : 0f;
        }

        /// <summary>
        /// fix-76: the head is moved by more than the locomotion now — a one-shot owns the motion player (attack, stumble,
        /// get-up, full-body draw, object use), an upper-body overlay plays (draw while moving, aim, reload, cast) or the
        /// hero is in the air: the head offset is followed slowly (<see cref="CameraStability.HeadBusySeconds"/>).
        /// </summary>
        bool HeadBusy()
        {
            if (!player.IsGrounded) return true;
            if (_motion == null && Time.unscaledTime >= _motionSearch)
            {
                _motionSearch = Time.unscaledTime + 1f;
                _motion = player.GetComponentInChildren<G3MotionPlayer>();
            }
            return _motion != null && _motion.isActiveAndEnabled && (_motion.Claimed || _motion.OverlayKey != null);
        }

        const string HeadBone = "Hero_Head_Head_1";

        void PlaceFirstPerson()
        {
            var p = player.transform;
            var rot = Quaternion.Euler(player.Pitch, player.Yaw, 0);
            var eye = p.position + Vector3.up * (player.eyeHeight + G3ReliefFooting.HeroOffset) + Quaternion.Euler(0, player.Yaw, 0) * Vector3.forward * firstPersonForward;
            DecaySnaps(Time.deltaTime);
            if (_snapYaw != 0f || _snapPitch != 0f) rot = Quaternion.Euler(player.Pitch + _snapPitch, player.Yaw + _snapYaw, 0);
            _pivotY = p.position.y + pivotHeight;
            _root = default;
            ApplyPose(eye, rot, FovFor(CameraModes.FirstPersonFov));
        }

        void PlaceThirdPerson()
        {
            var p = player.transform.position;
            p.y += G3ReliefFooting.HeroOffset;   // the model stands on the drawn relief (Docs/remaster-world.md section 14)
            float dt = Time.deltaTime;
            // G3 bases the camera on the hero's head (fix-60: the crouched drawn stances lower it with the head). fix-76: the
            // head offset is low-passed, dead-banded and spring-eased (the run bob, swings and draw overlays stay out), slower
            // while a one-shot or an overlay moves the head; the feet height has its own damping (step-ups, stairs).
            // fix-122: a held play camera keeps its pivot height (the kneel of a loot or a use does not lower the view)
            if (!HoldingPlayCamera) _headOffset = CameraStability.FollowHead(ref _headFilter, HeadOffsetNow(p.y), HeadBusy(), dt);
            var hv = player.Velocity;
            float rootY = CameraStability.FollowRoot(ref _root, p.y, new Vector2(hv.x, hv.z).magnitude,
                                                     player.IsGrounded ? 0f : player.DesiredVelocity.y, dt);
            _pivotHeightNow = float.IsNaN(_pivotHeightNow) ? pivotHeight
                : Mathf.SmoothDamp(_pivotHeightNow, pivotHeight, ref _pivotHeightVel, CameraStability.ModeBlendSeconds, Mathf.Infinity, dt);
            _pivotY = rootY + _pivotHeightNow + _headOffset;
            var pivot = new Vector3(p.x, _pivotY, p.z);

            DecaySnaps(dt);
            if (!Physics.autoSyncTransforms) Physics.SyncTransforms(); // casts see the NPC bodies moved this frame, not at the last physics step
            float pitch = Mathf.Clamp(player.Pitch + _orbitPitch + _snapPitch, minPitch, maxPitch);
            UpdateUseOrbit(pivot, pitch, dt);
            var rot = Quaternion.Euler(pitch, player.Yaw + _orbitYaw + _useYaw + _keyYaw + _snapYaw, 0);

            // Pivot → shoulder → back, each leg sphere-cast against the world. Hits the cast starts inside of (the own
            // capsule contains the pivot), own colliders and thin obstacles are skipped (CameraBoom.Blocks).
            var right = rot * Vector3.right;
            float side = Shoulder;
            if (side > 0 && Cast(pivot, right, side, out float sd, out _shoulderCause))
                side = Mathf.Max(0, sd - 0.02f);
            _currentShoulder = CameraBoom.Follow(_currentDistance < 0 ? -1 : _currentShoulder, side, ref _shoulderVel, collisionRecover, dt);
            var shoulder = pivot + right * _currentShoulder;

            var back = rot * Vector3.back;
            _combatBlend = Mathf.MoveTowards(_combatBlend, combatTarget != null ? 1 : 0, dt / Mathf.Max(0.01f, combatEase));
            float wanted;
            if (g3Modes)
            {
                // G3 GetCurrentWishedDistance: the zoom, but at least MinRangeOnMove while the hero is not standing
                // (fix-76: standing only after 0.35 s below the moving speed, CameraStability.MovingLatch).
                var v = player.Velocity;
                bool moving = CameraStability.MovingLatch(v.x * v.x + v.z * v.z > movingSpeed * movingSpeed, ref _stillFor, dt);
                // fix-256: the MinRangeOnMove floor eases in and out (the latch flip snapped a close zoom in by 1 m)
                wanted = ModeParams.EasedWishedDistance(ModeParams.ClampZoom(distance), CameraStability.MoveRangeWeight(ref _moveRange, moving, dt));
            }
            else wanted = Mathf.Clamp(distance * Mathf.Lerp(1, combatDistanceFactor, _combatBlend), minDistance, maxDistance);
            wanted += Mathf.Max(0f, bodyExtraDistance);
            wanted = CameraModes.Hold(HoldingPlayCamera, wanted, ref _heldWanted); // fix-122: no zoom in or out while held
            float free = wanted;
            // fix-94: the back leg is a thin sweep (G3 traces a ray); the wall distance is kept along the hit's normal below
            bool blocked = Cast(shoulder, back, wanted, out float bd, out _backCause, boomRadius, out var wallPoint, out var wallNormal);
            if (blocked) free = Mathf.Clamp(CameraStability.RayHitDistance(bd, boomRadius, Vector3.Dot(back, wallNormal)), 0.05f, wanted);
            // fix-76: in at once; after an obstacle the boom waits, then eases out (CameraStability.FollowFree)
            free = CameraStability.FollowFree(ref _free, free, blocked, dt);

            // Pull in at once, ease back out (G3 modes: gCCameraAI_PS::UpdateDistance's smoothing).
            float before = _currentDistance;
            _currentDistance = g3Modes ? CameraModes.SmoothDistance(_currentDistance, wanted, free, dt)
                                       : CameraBoom.Follow(_currentDistance, free, ref _distanceVel, collisionRecover, dt);
            if (debugJumps || DebugJumpsAll) LogJump(before, wanted, free, pivot, dt);
            // fix-253: the boom as it would hang without obstacles (the steady aim's framing)
            _framingDistance = g3Modes ? CameraModes.SmoothDistance(_framingDistance, wanted, float.MaxValue, dt)
                                       : CameraBoom.Follow(_framingDistance, wanted, ref _framingVel, collisionRecover, dt);

            var shake = Vector3.zero;
            if (_shake > 1e-3f)
            {
                shake = rot * new Vector3(Mathf.PerlinNoise(Time.time * 25f, 0) - 0.5f, Mathf.PerlinNoise(0, Time.time * 25f) - 0.5f, 0) * (2 * _shake);
                _shake *= Mathf.Exp(-shakeDamping * dt);
            }
            var at = shoulder + back * _currentDistance;
            if (blocked) at = KeepOffWall(shoulder, at, back, wallPoint, wallNormal);
            at = OffRelief(shoulder, at, dt);
            at += shake;
            // Swimming or wading: the camera stays above the water surface (G3 CamModeSwim keeps the normal boom; the
            // water has no underwater view here) [interp].
            if (player.Water >= G3.Player.WaterMode.Wade && at.y < player.WaterSurface + SurfaceClearance)
                at.y = player.WaterSurface + SurfaceClearance;
            // G3 looks at a point TargetOffsetY above the head (50 cm near … 120 cm far in CamModeRanged): the hero sits
            // lower in the picture the farther the camera is [the head base: UpdatePlayerPosition's GetHeadMat].
            float lift = LookLift();
            var boomRot = rot;
            if (lift > 0f)
                rot = Quaternion.LookRotation(shoulder + Vector3.up * lift - at, Vector3.up);
            // fix-253: a drawn bow / crossbow turns the view with the look only — the look-at from the collided end above
            // turns it far more than the mouse when the boom end is lifted off the relief or pulled in (ThirdPersonAim)
            _steadyW = Mathf.MoveTowards(_steadyW, steadyAim ? 1f : 0f, dt / ThirdPersonAim.SteadyEaseSeconds);
            if (_steadyW > 0f)
            {
                var f = ThirdPersonAim.SteadyForward(player.Yaw + _orbitYaw + _useYaw + _keyYaw + _snapYaw, pitch, _framingDistance, SteadyLift());
                var steadyRot = Quaternion.LookRotation(new Vector3(f.X, f.Y, f.Z), Vector3.up);
                rot = _steadyW >= 1f ? steadyRot : Quaternion.Slerp(rot, steadyRot, _steadyW);
            }
            if (TraceAimPitch) TraceAim(rot, boomRot, lift, at);
            ApplyPose(at, rot, FovFor(ModeParams.Fov(_currentDistance)));
            var eye = p + Vector3.up * player.eyeHeight;
            EyeDistance = Vector3.Distance(transform.position, eye);
            _heroHidden = CameraStability.HidesHero(EyeDistance, _heroHidden);
        }

        const float SurfaceClearance = 0.25f;

        /// <summary>
        /// fix-240: the terrain relief is drawn above G3's collision (render-only, Docs/remaster-world.md 13-14), so the casts
        /// above keep the camera off a surface up to ~1 m (more on steep strata) under what is seen; close to raised ground the
        /// camera ended inside the relief. The drawn height field (the relief's ground maps, <see cref="G3TerrainRelief.TryGround"/>)
        /// lifts the end off it and pulls it in front of drawn ground between the hero and the camera
        /// (<see cref="ReliefCameraRules"/>). Nothing with the relief off.
        /// </summary>
        Vector3 OffRelief(Vector3 from, Vector3 at, float dt)
        {
            if (!G3TerrainRelief.Active) { _relief = default; return at; }
            var e = ReliefCameraRules.Follow(ReliefGround, new System.Numerics.Vector3(from.x, from.y, from.z),
                                             new System.Numerics.Vector3(at.x, at.y, at.z), ref _relief, dt);
            return new Vector3(e.X, e.Y, e.Z);
        }

        static readonly ReliefCameraRules.Ground ReliefGround = SampleRelief;

        static bool SampleRelief(float x, float y, float z, out float collisionY, out float visualY) =>
            G3TerrainRelief.TryGround(new Vector3(x, y, z), out collisionY, out visualY);

        /// <summary>
        /// fix-94, G3 gCCameraAI_PS::MaintainWallDistance: a camera that reached the surface its boom hit is moved out along
        /// that surface's normal until it is <see cref="CameraStability.WallDistance"/> off it, so against a slope behind
        /// the hero it rides up the slope instead of closing in on his head. When the way from the shoulder to the moved
        /// point is not clear (an overhang, a corner), the camera is pulled in along the boom as before (the old sweep
        /// radius taken off the hit).
        /// </summary>
        Vector3 KeepOffWall(Vector3 shoulder, Vector3 at, Vector3 back, Vector3 wallPoint, Vector3 wallNormal)
        {
            float push = CameraStability.WallPush(Vector3.Dot(at - wallPoint, wallNormal));
            if (push <= 0f) return at;
            var moved = at + wallNormal * push;
            var toMoved = moved - shoulder;
            float len = toMoved.magnitude;
            if (len < 1e-3f) return at;
            if (!Cast(shoulder, toMoved / len, len, out float hd, out _, boomRadius * 0.5f, out _, out _) || hd >= len - 0.01f) return moved;
            float pulled = Cast(shoulder, back, _currentDistance, out float sd, out _, collisionRadius, out _, out _)
                ? Mathf.Max(0.05f, sd - 0.05f) : _currentDistance;
            return shoulder + back * Mathf.Min(_currentDistance, pulled);
        }

        /// <summary>fix-253: the steady aim's lift: TargetOffsetY of the unobstructed boom (no step at the hide distance).</summary>
        float SteadyLift() =>
            g3Modes && targetOffset && !_lookingBehind && scriptedOrbit == Vector2.zero && _framingDistance > 0f
                ? ModeParams.TargetOffsetY(_framingDistance) : 0f;

        /// <summary>fix-253: one line per frame — player pitch, view pitch (up +) and their steps, boom, relief.</summary>
        void TraceAim(Quaternion rot, Quaternion boomRot, float lift, Vector3 at)
        {
            float view = -Mathf.DeltaAngle(0f, rot.eulerAngles.x), look = -player.Pitch;
            float dv = float.IsNaN(_traceViewPitch) ? 0f : view - _traceViewPitch, dl = float.IsNaN(_traceViewPitch) ? 0f : look - _tracePlayerPitch;
            _traceViewPitch = view;
            _tracePlayerPitch = look;
            Debug.Log($"[aimtrace] f={Time.frameCount} look={look:F2} dLook={dl:F2} view={view:F2} dView={dv:F2}" +
                      $"{(Mathf.Abs(dv - dl) > 0.5f ? " KICK" : "")} steady={_steadyW:F2} dist={_currentDistance:F2}" +
                      $" framing={_framingDistance:F2} lift={lift:F2} relief(lift={_relief.Lift:F2} reach={_relief.Reach:F2})" +
                      $" camY={at.y - _pivotY:F2}");
        }

        /// <summary>How far above the shoulder point the third-person camera looks (0 = along the boom).</summary>
        float LookLift()
        {
            float lift = g3Modes && targetOffset && !_lookingBehind && scriptedOrbit == Vector2.zero ? ModeParams.TargetOffsetY(_currentDistance) : 0f;
            return lift > 0f && _currentDistance > hideBodyDistance ? lift : 0f;
        }

        /// <summary>
        /// The player look (yaw, pitch) that puts the screen centre — the crosshair — on <paramref name="target"/>, for the
        /// camera as it hangs now (boom lengths, look-at lift, orbit offsets; <see cref="ThirdPersonAim"/>). What a player
        /// does with the mouse; the dev runner aims with it (fix-56). False before the camera is placed.
        /// </summary>
        public bool LookFor(Vector3 target, out float yaw, out float pitch)
        {
            yaw = player != null ? player.Yaw : 0f;
            pitch = player != null ? player.Pitch : 0f;
            if (player == null) return false;
            if (!thirdPerson || _currentDistance < 0f)
            {
                var d = target - transform.position;
                if (d.sqrMagnitude < 1e-4f) return false;
                yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
                return !thirdPerson;
            }
            var p = player.transform.position;
            var pivot = new System.Numerics.Vector3(p.x, _pivotY, p.z);
            float extraYaw = _orbitYaw + _useYaw + _keyYaw + _snapYaw;
            // fix-253: the steady aim's centre is the unobstructed boom's (the pulled-in position's parallax is left out)
            bool steady = _steadyW >= 0.5f && _framingDistance > 0f;
            bool ok = ThirdPersonAim.Solve(pivot, new System.Numerics.Vector3(target.x, target.y, target.z), _currentShoulder,
                                           steady ? _framingDistance : _currentDistance, steady ? SteadyLift() : LookLift(),
                                           minPitch, maxPitch, out float camYaw, out float camPitch);
            yaw = camYaw - extraYaw;
            pitch = camPitch - _orbitPitch - _snapPitch;
            return ok;
        }

        // [interp] fix-46: during an object use (Inventory mode) in a cramped spot the camera moves round the hero to the
        // freest side (CameraBoom.UseOrbit), checked every UseOrbitCheck s; back behind him when the use ends.
        float _useYaw, _useYawVel, _nextUseCheck;
        int _useIndex;
        readonly float[] _useFree = new float[CameraBoom.UseOrbitYaws.Length];
        const float UseOrbitCheck = 0.25f, UseOrbitEase = 0.35f;

        void UpdateUseOrbit(Vector3 pivot, float pitch, float dt)
        {
            bool use = g3Modes && mode == CameraMode.Inventory && !_lookingBehind && scriptedOrbit == Vector2.zero;
            if (!use) _useIndex = 0;
            else if (Time.time >= _nextUseCheck)
            {
                _nextUseCheck = Time.time + UseOrbitCheck;
                float wanted = ModeParams.WishedDistance(ModeParams.ClampZoom(distance), false);
                for (int i = 0; i < _useFree.Length; i++)
                {
                    var r = Quaternion.Euler(pitch, player.Yaw + CameraBoom.UseOrbitYaws[i], 0);
                    var right = r * Vector3.right;
                    float side = Shoulder > 0 && Cast(pivot, right, Shoulder, out float sd, out _) ? Mathf.Max(0, sd - 0.02f) : Shoulder;
                    _useFree[i] = Cast(pivot + right * side, r * Vector3.back, wanted, out float bd, out _) ? bd : wanted;
                }
                _useIndex = CameraBoom.UseOrbit(_useFree, wanted, _useIndex);
            }
            _useYaw = Mathf.SmoothDampAngle(_useYaw, CameraBoom.UseOrbitYaws[_useIndex], ref _useYawVel, UseOrbitEase, Mathf.Infinity, dt);
        }

        /// <summary>
        /// The port's field-of-view option (player feedback item 15): degrees added to every mode's FOV (0 = G3's values).
        /// Set by the game's options; dialog cameras keep their own FOV.
        /// </summary>
        public static float FovOffset { get; set; }

        /// <summary>G3 field of view (vertical, as Unity's): the mode's FOV by distance; first person 70°; plus
        /// <see cref="FovOffset"/>. NaN (the camera keeps its own) without the G3 modes.</summary>
        float FovFor(float fov) => g3Modes ? Mathf.Clamp(fov + FovOffset, 20f, 120f) : float.NaN;

        void SetFov(float fov)
        {
            if (_cam == null) _cam = GetComponent<Camera>();
            if (_cam != null && Mathf.Abs(_cam.fieldOfView - fov) > 0.01f) _cam.fieldOfView = fov;
        }

        /// <summary>Nearest blocking hit of a sphere sweep (see <see cref="CameraBoom.Blocks"/>); cause = debug text.</summary>
        bool Cast(Vector3 from, Vector3 dir, float length, out float hitDistance, out string cause) =>
            Cast(from, dir, length, out hitDistance, out cause, collisionRadius, out _, out _);

        /// <summary>Nearest blocking hit of a sphere sweep of <paramref name="radius"/>, with the hit point and surface normal.</summary>
        bool Cast(Vector3 from, Vector3 dir, float length, out float hitDistance, out string cause, float radius,
                  out Vector3 hitPoint, out Vector3 hitNormal)
        {
            hitDistance = length;
            hitPoint = from + dir * length;
            hitNormal = -dir;
            cause = null;
            bool debug = debugJumps || DebugJumpsAll;
            int n = Physics.SphereCastNonAlloc(from, radius, dir, _hits, length, collisionMask,
                                               IsCharacter != null ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);
            bool any = false;
            string ignored = null;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                var c = h.collider;
                if (c == null) continue;
                var size = c.bounds.size;
                bool own = c.transform.IsChildOf(player.transform);
                bool character = IsCharacter != null && IsCharacter(c);
                if (c.isTrigger && !character) continue;
                // fix-76: characters and loose physics bodies (ragdolls, dropped items) only block near the camera's end
                var body = c.attachedRigidbody;
                bool soft = character || (body != null && !body.isKinematic);
                if (!CameraStability.Blocks(h.distance, own, CameraBoom.PlanWidth(size.x, size.z), minObstacleWidth, soft, length))
                {
                    if (debug && h.distance > 0) ignored = $"{ignored}{(ignored == null ? "" : ",")}{Describe(c)}@{h.distance:0.00}";
                    continue;
                }
                if (h.distance >= hitDistance) continue;
                hitDistance = h.distance;
                hitPoint = h.point;
                hitNormal = h.normal;
                any = true;
                if (debug) cause = $"{Describe(c)}@{h.distance:0.00}";
            }
            if (debug && ignored != null) cause = $"{cause ?? "none"} (ignored {ignored})";
            return any;
        }

        static string Describe(Collider c)
        {
            var s = c.bounds.size;
            return $"'{c.name}'<{(c.transform.parent != null ? c.transform.parent.name : "")}> layer {LayerMask.LayerToName(c.gameObject.layer)} {c.GetType().Name} {s.x:0.0}x{s.y:0.0}x{s.z:0.0}";
        }

        void LogJump(float before, float wanted, float free, Vector3 pivot, float dt)
        {
            float prev = _lastLoggedDistance;
            _lastLoggedDistance = _currentDistance;
            var pivotDelta = pivot - _lastPivot;
            _lastPivot = pivot;
            if (!CameraBoom.IsJump(prev, _currentDistance, jumpThreshold)) return;
            JumpCount++;
            Debug.Log($"[cam-jitter] #{JumpCount} t={Time.time:0.00} dt={dt * 1000:0.0}ms dist {prev:0.00}->{_currentDistance:0.00} " +
                      $"(was {before:0.00}, wanted {wanted:0.00}, free {free:0.00}) shoulder {_currentShoulder:0.00} " +
                      $"pivotDelta {pivotDelta.magnitude:0.00} ({pivotDelta.x:0.00},{pivotDelta.y:0.00},{pivotDelta.z:0.00}) " +
                      $"back hit: {_backCause ?? "none"}; shoulder hit: {_shoulderCause ?? "none"}");
        }

        float _baseNear = -1f;

        /// <summary>fix-208: the first-person camera clips what presses against the lens (shoulder pads, an arm raised in a
        /// swing) with a farther near plane (<see cref="FirstPersonBodyRules.NearClip"/>); third person keeps its own.</summary>
        void ApplyNearPlane(bool firstPerson)
        {
            if (_cam == null) _cam = GetComponent<Camera>();
            if (_cam == null) return;
            if (_baseNear < 0f) _baseNear = _cam.nearClipPlane;
            float near = firstPerson ? Mathf.Max(_baseNear, FirstPersonBodyRules.NearClip) : _baseNear;
            if (Mathf.Abs(_cam.nearClipPlane - near) > 1e-4f) _cam.nearClipPlane = near;
        }

        void OnDisable()
        {
            if (_baseNear >= 0f && _cam != null) _cam.nearClipPlane = _baseNear; // the dialog camera gets the normal plane
        }

        void ApplyBodyView()
        {
            bool fp = IsFirstPersonView, hidden = HeroHidden;
            ApplyNearPlane(fp);
            if (_viewApplied && fp == _appliedFirstPerson && hidden == _appliedHidden) return;
            _viewApplied = true;
            _appliedFirstPerson = fp;
            _appliedHidden = hidden;
            foreach (var v in player.GetComponentsInChildren<IG3BodyView>(true))
            {
                v.SetFirstPerson(fp);
                v.SetHidden(hidden);
            }
        }
    }
}
