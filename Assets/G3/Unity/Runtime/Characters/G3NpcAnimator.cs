using System;
using G3.Player;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Animation selector of an NPC (Docs/npc-simulation.md): walks with the locomotion blend of its rig
    /// (<see cref="G3LocomotionSelector"/>, unarmed standing stance) and uses routine points with the motions of
    /// <see cref="G3InteractionMotions"/>: enter (sit down, take the tool) → loop → leave (stand up). Whoever moves the NPC
    /// sets <see cref="Interaction"/> (null = none); velocity and turn rate are measured from the transform, since the
    /// G3 clips are in place and the mover drives the position.
    /// The items in the hands follow the use (<see cref="LeftItem"/>, <see cref="RightItem"/>, Docs/formats-animation.md §8.7):
    /// the tools of the interaction from the start of the enter motion to the end of the leave motion; at relaxing points
    /// (<see cref="G3InteractionMotions.Pose.Enjoy"/>) a drink, food or pipe that is taken out, used now and then and put away.
    /// </summary>
    [RequireComponent(typeof(G3MotionPlayer))]
    [DefaultExecutionOrder(100)] // after the movers, before G3MotionPlayer (200)
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 NPC Animator")]
    public sealed class G3NpcAnimator : MonoBehaviour, IG3WeaponStance
    {
        enum State { Locomotion, Enter, Use, PutAway, Leave, ItemUse }
        enum Enjoy { None, TakeOut, Hold, Raise, Lower }

        // Fades as G3's (MotionBlendRules): interaction motions as sAIPlayAniStart starts them (0.2 s from a running
        // motion, 0.1 s into a _Begin_ motion, a cut after a one-shot ended), locomotion as the movement module (0.2 s).
        [Tooltip("Smoothing of the measured velocity, s")] public float velocitySmoothing = 0.15f;
        [Tooltip("Below this speed (m/s) an NPC may start its interaction")] public float stillSpeed = 0.15f;
        [Tooltip("Seconds between two sips / bites / puffs of a relaxing NPC (random in range)")]
        public Vector2 enjoyInterval = new Vector2(6f, 16f);

        G3MotionPlayer _player;
        G3LocomotionSelector _loco;
        string _rig;
        State _state;
        string _interaction, _active;
        G3InteractionMotions.Clips _clips;
        Vector3 _lastPos;
        float _lastYaw, _turnRate;
        Vector2 _localVel;
        G3InteractionMotions.Pose _pose;
        G3InteractionMotions.EnjoyClips _enjoyClips;
        Enjoy _enjoy;
        string _enjoyItem;
        float _nextEnjoyUse;
        string _weaponLeft = "None", _weaponRight = "None", _weaponTemplate, _ammoTemplate;
        bool _rangedWeapon;
        bool _yielded;

        /// <summary>The interaction to perform at the current spot (gEUseType name: "Anvil", "Bench" …); null = just stand/walk.</summary>
        public string Interaction
        {
            get => _interaction;
            set => _interaction = string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>Side the interaction is done from ("Fwd", "Left", "Right" — e.g. which side of a bed); set with the interaction.</summary>
        public string Direction { get; set; } = "Fwd";

        /// <summary>The NPC is in its interaction loop (or entering it).</summary>
        public bool IsInteracting => _state == State.Enter || _state == State.Use;

        /// <summary>
        /// A one-shot use of an inventory item while standing (G3 <c>_AI_UseInventoryItem</c>, e.g. an info's "Use It_Booze"):
        /// take it out (it appears half-way), raise it (drink, eat, read), lower it, put it away (it vanishes half-way), then
        /// back to locomotion. <paramref name="field"/> is the hand field of the motions ("Potion", "Meat", "Letter" …; the
        /// item's use type). An NPC still at its station (a talk just started: G3 FullStop leaves the interaction first) leaves
        /// it and then uses the item (fix-7: Hjalte's "Use It_Booze" was refused in the station's Use state). False when the
        /// NPC is busy (combat, another use) or its rig has no such motions.
        /// </summary>
        public bool UseItemOnce(string template, string field)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(field) || !EnsureSelector() || _player.Claimed) return false;
            if (_state == State.ItemUse) return false;
            if (_state != State.Locomotion)
            {
                if (ResolveItemUse(field).Raise == null) return false;
                _interaction = null; // off the station (G3 _AI_UseInventoryItem holds the item standing)
                if (_yielded)
                {
                    // a talk gesture already took the interaction's motions: it is over, no leave motion to wait for
                    _yielded = false;
                    _active = null;
                    EnterLocomotion();
                }
                else
                {
                    _pendingUse = (template, field); // started by Update once the leave motion has ended
                    return true;
                }
            }
            return StartItemUse(template, field);
        }

        (string template, string field)? _pendingUse;

        G3InteractionMotions.EnjoyClips ResolveItemUse(string field) =>
            G3InteractionMotions.ResolveEnjoy(_rig, new G3InteractionMotions.Pose("Stand", "None", "None", "Ambient"), field, Exists);

        bool StartItemUse(string template, string field)
        {
            _pendingUse = null;
            var clips = ResolveItemUse(field);
            if (clips.Raise == null)
            {
                LastItemUse = $"{template} {field}: no motion";
                return false;
            }
            _useClips = clips;
            _useItem = template;
            // fix-164: a talk gesture that just gave the player back (StopGesture) left _yielded set; the next Update took it
            // for the end of a combat one-shot and went to locomotion, cutting the use at TakeOut (Mojok's schnapps).
            _yielded = false;
            _active = null;
            _state = State.ItemUse;
            LastItemUse = $"{template} {field}:";
            if (clips.TakeOut != null && PlayAni(clips.TakeOut)) UseStep(Enjoy.TakeOut);
            else
            {
                SetHands(LeftItem, template);
                PlayAni(clips.Raise);
                UseStep(Enjoy.Raise);
            }
            return true;
        }

        /// <summary>
        /// The last <see cref="UseItemOnce"/> and the steps it played, e.g. "It_Booze Potion: TakeOut Raise Lower PutAway done"
        /// (dev scenarios, fix-153); null before any.
        /// </summary>
        public string LastItemUse { get; private set; }

        void UseStep(Enjoy step)
        {
            _useStep = step;
            LastItemUse += " " + (step == Enjoy.None ? "PutAway" : step.ToString());
        }

        /// <summary>A <see cref="UseItemOnce"/> is playing (or waits for the NPC to leave its station).</summary>
        public bool IsUsingItem => _state == State.ItemUse || _pendingUse != null;

        G3InteractionMotions.EnjoyClips _useClips;
        string _useItem;
        Enjoy _useStep;

        void UpdateItemUse()
        {
            if (_useStep == Enjoy.TakeOut || _useStep == Enjoy.None)
            {
                if (_useStep == Enjoy.TakeOut && _player.CurrentPhase >= 0.5f) SetHands(LeftItem, _useItem);
                if (_useStep == Enjoy.None && _player.CurrentPhase >= 0.5f) SetHands(LeftItem, null); // putting away
                if (!_player.CurrentFinished) return;
                if (_useStep == Enjoy.None) { LastItemUse += " done"; EnterLocomotion(); return; }
                PlayAni(_useClips.Raise);
                UseStep(Enjoy.Raise);
                return;
            }
            if (!_player.CurrentFinished) return;
            if (_useStep == Enjoy.Raise && _useClips.Lower != null && PlayAni(_useClips.Lower))
            {
                UseStep(Enjoy.Lower);
                return;
            }
            if (_useClips.PutAway != null && PlayAni(_useClips.PutAway))
            {
                UseStep(Enjoy.None); // put-away phase
                return;
            }
            SetHands(LeftItem, null);
            LastItemUse += " done";
            EnterLocomotion();
        }

        /// <summary>Sitting in an interaction (ground, stool, bench, throne: a <c>Sit*</c> stance), or sitting down.</summary>
        public bool IsSeated => (_state == State.Enter || _state == State.Use) && _pose?.Stance != null &&
                                _pose.Stance.StartsWith("Sit", System.StringComparison.Ordinal);

        /// <summary>Free to walk: not sitting, working or standing up (a mover clears <see cref="Interaction"/> and waits for this).</summary>
        public bool CanWalk => _state == State.Locomotion;

        /// <summary>Item template in the left / right hand ("It_Hammer_Smith" …), null = empty.</summary>
        public string LeftItem { get; private set; }
        public string RightItem { get; private set; }

        /// <summary>
        /// Tools kept in the hands while walking (G3: the items of a work task's <c>_AI_HoldItems</c> stay in the hands while
        /// the NPC goes to the next station of the same task, e.g. the smith's sword and hammer between forge and anvil);
        /// null = the hands are empty (or hold the weapon) while walking.
        /// </summary>
        public void CarryTools(string left, string right)
        {
            if (left == _carryLeft && right == _carryRight) return;
            _carryLeft = left;
            _carryRight = right;
            if (_state == State.Locomotion && _player != null && !_player.Claimed) SetWeaponHands();
        }

        string _carryLeft, _carryRight;

        /// <summary>Raised when <see cref="LeftItem"/> or <see cref="RightItem"/> changed.</summary>
        public event Action HandsChanged;

        /// <summary>Walk speed of the rig (m/s), from the forward walk clip; 1.6 when the rig has none.</summary>
        public float WalkSpeed => _loco != null && _loco.Speed("Walk", "Fwd") > 0 ? _loco.Speed("Walk", "Fwd") : 1.6f;

        /// <summary>Forward root speed (m/s) of a gait clip of the rig ("Walk", "Run", "Sprint"); <paramref name="fallback"/> without the clip.</summary>
        public float GaitSpeed(string gait, float fallback)
        {
            float s = EnsureSelector() ? _loco.Speed(gait, "Fwd") : 0;
            return s > 0 ? s : fallback;
        }

        void Awake() => _player = GetComponent<G3MotionPlayer>();

        /// <summary>Every NPC animation selector steps from one batched Update (perf-3, <see cref="G3FrameBatch"/>; order 100).</summary>
        static readonly G3FrameBatch.Batch<G3NpcAnimator> s_batch =
            new G3FrameBatch.Batch<G3NpcAnimator>(G3FrameBatch.Slot.Update100, "G3NpcAnimator", a => a.FrameUpdate());

        void OnDisable() => s_batch.Remove(this);

        void ResetClock() => _clock.Reset(Time.timeAsDouble, Time.deltaTime, Time.frameCount, s_batch.RanThisFrame);

        void OnEnable()
        {
            ResetClock();
            s_batch.Add(this);
            _missedInteraction = null;
            _lastPos = transform.position;
            _lastYaw = transform.eulerAngles.y;
            _localVel = Vector2.zero;
            _turnRate = 0;
            _active = null;
            _weaponTemplate = _ammoTemplate = null;
            _rangedWeapon = false;
            _yielded = false;
            _pendingUse = null;
            if (_weaponLeft != "None" || _weaponRight != "None") { _weaponLeft = _weaponRight = "None"; _loco = null; } // pooled view of an armed NPC
            _carryLeft = _carryRight = null;
            SetHands(null, null);
            if (EnsureSelector()) EnterLocomotion(cut: true);
        }

        /// <summary>
        /// Jump to a new place without walking (a view appears, a teleport): no velocity spike, and an NPC that appears at
        /// its station is shown in its interaction loop at once instead of sitting down in front of the player.
        /// </summary>
        public void Teleported()
        {
            ResetClock();
            _lastPos = transform.position;
            _lastYaw = transform.eulerAngles.y;
            _localVel = Vector2.zero;
            _turnRate = 0;
            if (!EnsureSelector()) return;
            if (_interaction != null) StartInteraction(skipEnter: true);
            else EnterLocomotion(cut: true);
        }

        bool EnsureSelector()
        {
            if (_loco != null) return true;
            if (_player == null || _player.library == null) return false;
            _rig = _player.library.rig;
            // the stance is a named argument: positionally it landed in the phase field and the wade set was never chosen
            string stance = G3.Player.SwimRules.NpcLocomotionStance(_wading, HasStance("Wade"));
            _loco = new G3LocomotionSelector(_player.library, new G3MotionKey(_rig, "Move", "Stand", stance: stance, leftItem: _weaponLeft, rightItem: _weaponRight));
            return true;
        }

        bool _wading;

        /// <summary>
        /// The NPC wades (G3 movement mode WADE: body depth ≥ SwimDepth − 50 cm, <c>G3.Player.SwimRules.NpcWades</c>): its
        /// locomotion uses the rig's <c>&lt;Rig&gt;_Wade_…_Move_*</c> set when the library has one for the hands it holds,
        /// else the standing set. Wading has no AI state of its own in G3 — routines and interactions go on.
        /// </summary>
        public bool Wading
        {
            get => _wading;
            set
            {
                if (_wading == value) return;
                _wading = value;
                if (_loco == null || _player == null || _player.library == null) return;
                if (!HasStance("Wade")) return; // no wade motions for this rig: the standing set stays
                _loco = null;
                if (!EnsureSelector()) return;
                if (_state == State.Locomotion && !_player.Claimed) EnterLocomotion();
            }
        }

        bool HasStance(string stance) =>
            _player != null && _player.library != null && _rig != null &&
            (_player.library.Find(new G3MotionKey(_rig, "Move", "Run", stance: stance, leftItem: _weaponLeft, rightItem: _weaponRight)) != null ||
             _player.library.Find(new G3MotionKey(_rig, "Move", "Walk", stance: stance, leftItem: _weaponLeft, rightItem: _weaponRight)) != null);

        /// <summary>Update LOD of this NPC (<see cref="G3MotionPlayer.UpdateInterval"/>; the selector steps with its player).</summary>
        public int UpdateInterval
        {
            get => _player != null ? _player.UpdateInterval : 1;
            set { if (_player != null) _player.UpdateInterval = value; }
        }

        /// <summary>This frame is a step frame of the NPC's update LOD.</summary>
        public bool UpdateDue => _player == null || _player.UpdateDue;

        StepClock _clock;

        void FrameUpdate()
        {
            if (!UpdateDue) return;
            float dt = _clock.Take(G3FrameBatch.Now, G3FrameBatch.DeltaTime, G3FrameBatch.Frame);
            if (dt <= 0 || !EnsureSelector()) return;
            MeasureMotion(dt);
            float speed = _localVel.magnitude;
            if (_player.Claimed)
            {
                // A combat one-shot has the player: the interaction is over; locomotion takes over when it ends.
                _yielded = true;
                return;
            }
            if (_yielded)
            {
                _yielded = false;
                _active = null;
                EnterLocomotion();
            }

            switch (_state)
            {
                case State.Locomotion:
                    if (_interaction != null && speed < stillSpeed) StartInteraction();
                    break;
                case State.Enter:
                    if (_interaction != _active) Leave();
                    else if (_player.CurrentFinished) PlayLoop();
                    break;
                case State.Use:
                    if (_interaction != _active) PutAwayOrLeave();
                    else if (_enjoy != Enjoy.None) UpdateEnjoy();
                    else if (_clips.HoldsEnter) { } // fix-217: the enter motion stays at its last frame
                    else if (_player.CurrentFinished) _player.Play(NextLoop(), MotionBlendRules.StandardFade, restart: true); // one-shot "Ambient" loops (G3 sAIPlayAniItlLoop: standard fade)
                    break;
                case State.PutAway:
                    if (_player.CurrentPhase >= 0.5f) SetHands(LeftItem, null);
                    if (_player.CurrentFinished) Leave();
                    break;
                case State.Leave:
                    if (_player.CurrentFinished) EnterLocomotion();
                    break;
                case State.ItemUse:
                    UpdateItemUse();
                    break;
            }

            if (_state == State.Locomotion && _pendingUse is { } use)
            {
                if (_interaction != null) _pendingUse = null; // sent back to the station meanwhile (the talk ended): dropped
                else StartItemUse(use.template, use.field);
            }

            if (_state == State.Locomotion)
            {
                _player.SetWeights(_loco.Evaluate(_localVel, speed < 0.3f ? _turnRate : 0));
                _player.SetSpeed(_loco.PlaybackRate);
            }
        }

        void MeasureMotion(float dt)
        {
            var delta = transform.position - _lastPos;
            _lastPos = transform.position;
            float yaw = transform.eulerAngles.y;
            float rawTurn = Mathf.DeltaAngle(_lastYaw, yaw) / dt;
            _lastYaw = yaw;
            var local = transform.InverseTransformDirection(new Vector3(delta.x, 0, delta.z) / dt);
            float k = 1 - Mathf.Exp(-dt / Mathf.Max(1e-3f, velocitySmoothing));
            _localVel = Vector2.Lerp(_localVel, new Vector2(local.x, local.z), k);
            _turnRate = Mathf.Lerp(_turnRate, rawTurn, k);
        }

        void StartInteraction(bool skipEnter = false)
        {
            if (!G3InteractionMotions.TryGet(_interaction, out var pose)) return;
            // perf-3: an interaction this rig has no loop for was resolved again on every step (~80 B of key strings each,
            // 8 KB per frame at the Nordmar camp); the same interaction from the same side gives the same answer.
            if (ReferenceEquals(_missedInteraction, _interaction) && _missedDirection == Direction && ReferenceEquals(_missedLibrary, _player.library)) return;
            _clips = G3InteractionMotions.Resolve(_rig, pose, Exists, Direction);
            if (_clips.Loop == null)
            {
                _missedInteraction = _interaction;
                _missedDirection = Direction;
                _missedLibrary = _player.library;
                return; // this rig has no such motion: keep standing
            }
            _missedInteraction = null;
            _pose = pose;
            _active = _interaction;
            ChooseEnjoyItem();
            // G3 puts the tools into the hands (_AI_HoldItems) before the use motion starts.
            SetHands(pose.LeftProp, skipEnter && _enjoyItem != null ? _enjoyItem : pose.RightProp);
            if (!skipEnter && _clips.Enter != null && PlayAni(_clips.Enter)) _state = State.Enter;
            else PlayLoop(itemInHand: skipEnter);
        }

        bool Exists(string key) => _player.library.Find(key) != null;

        string _missedInteraction, _missedDirection; // the last interaction without a loop motion for this rig (perf-3)
        G3MotionLibrary _missedLibrary;

        void ChooseEnjoyItem()
        {
            _enjoy = Enjoy.None;
            _enjoyItem = null;
            if (!_pose.Enjoy) return;
            string item = G3HandItems.PickEnjoyItem(_rig, UnityEngine.Random.value);
            _enjoyClips = G3InteractionMotions.ResolveEnjoy(_rig, _pose, G3HandItems.FieldOf(item), Exists);
            if (_enjoyClips.Hold != null) _enjoyItem = item; // the rig cannot hold it in this stance: no item
        }

        void PlayLoop(bool itemInHand = false)
        {
            _state = State.Use;
            if (_clips.HoldsEnter)
            {
                // fix-217: no loop: the enter motion held at its end (after it played, or shown at once after a teleport)
                if (_player.CurrentKey != _clips.Enter && PlayAni(_clips.Enter)) _player.FinishCurrent();
                return;
            }
            if (_enjoyItem == null)
            {
                PlayAni(NextLoop(fidgets: false));
                return;
            }
            if (!itemInHand && _enjoyClips.TakeOut != null && PlayAni(_enjoyClips.TakeOut))
            {
                _enjoy = Enjoy.TakeOut;
                return;
            }
            SetHands(LeftItem, _enjoyItem);
            HoldEnjoyItem();
        }

        /// <summary>
        /// The next cycle of the use loop, as G3's stand idle picks it (Docs/formats-animation.md §5.1): a fidget (phase
        /// "Ambient" of the same action) with chance 1/7 when the rig has one, else the loop; each a uniformly random index
        /// among the variants _00, _01 … that exist.
        /// </summary>
        string NextLoop(bool fidgets = true)
        {
            var library = _player.library;
            if (fidgets && UnityEngine.Random.Range(0, 7) == 6)
            {
                string fidget = G3MotionLibrary.WithPhase(_clips.Loop, "Ambient");
                if (fidget != null && fidget != _clips.Loop && library.Find(fidget) != null)
                    return library.RandomVariant(fidget, n => UnityEngine.Random.Range(0, n));
            }
            return library.RandomVariant(_clips.Loop, n => UnityEngine.Random.Range(0, n));
        }

        /// <summary>Relaxing with an item: take it out (it appears half-way), hold it, raise it to the mouth and lower it now and then.</summary>
        void UpdateEnjoy()
        {
            switch (_enjoy)
            {
                case Enjoy.TakeOut:
                    if (_player.CurrentPhase >= 0.5f) SetHands(LeftItem, _enjoyItem);
                    if (_player.CurrentFinished) HoldEnjoyItem();
                    break;
                case Enjoy.Hold:
                    if (Time.time >= _nextEnjoyUse && _enjoyClips.Raise != null && PlayAni(_enjoyClips.Raise))
                        _enjoy = Enjoy.Raise;
                    else if (_player.CurrentFinished) PlayAni(_enjoyClips.Hold);
                    break;
                case Enjoy.Raise:
                    if (!_player.CurrentFinished) break;
                    if (_enjoyClips.Lower != null && PlayAni(_enjoyClips.Lower)) _enjoy = Enjoy.Lower;
                    else HoldEnjoyItem();
                    break;
                case Enjoy.Lower:
                    if (_player.CurrentFinished) HoldEnjoyItem();
                    break;
            }
        }

        void HoldEnjoyItem()
        {
            _enjoy = Enjoy.Hold;
            _nextEnjoyUse = Time.time + UnityEngine.Random.Range(enjoyInterval.x, enjoyInterval.y);
            PlayAni(_enjoyClips.Hold);
        }

        /// <summary>The use ends: a relaxing item is put away first (it vanishes half-way), then the NPC leaves the spot.</summary>
        void PutAwayOrLeave()
        {
            bool holding = _enjoy != Enjoy.None && RightItem != null;
            _enjoy = Enjoy.None;
            if (holding && _enjoyClips.PutAway != null && PlayAni(_enjoyClips.PutAway))
            {
                _active = null;
                _state = State.PutAway;
                return;
            }
            Leave();
        }

        void Leave()
        {
            _active = null;
            _enjoy = Enjoy.None;
            if (_enjoyItem != null) SetHands(LeftItem, null);
            if (_clips.Leave != null && PlayAni(_clips.Leave)) _state = State.Leave;
            else EnterLocomotion();
        }

        /// <summary>Starts an interaction motion with G3's command fade (sAIPlayAniStart).</summary>
        bool PlayAni(string key) => _player.Play(key, _player.FadeFor(MotionStarter.Command, key), restart: true);

        void EnterLocomotion(bool cut = false)
        {
            float fade = cut ? 0f : _player.FadeFor(MotionStarter.Locomotion, null); // G3 movement module: 0.2 s, a cut after a one-shot
            _state = State.Locomotion;
            _enjoy = Enjoy.None;
            _enjoyItem = null;
            SetWeaponHands();
            _player.PlayBlend(_loco.SetKey, _loco.Keys, fade);
        }

        /// <summary>
        /// Combat stance between one-shots: the weapon template in its hand (a bow or crossbow in the left, its nocked
        /// ammunition in the right) and locomotion with its hand fields (<c>Orc_Stand_None_2H_P0_Move_…</c>,
        /// <c>Orc_Stand_CrossBow_Bolt_P0_Move_…</c>) when the rig has that set, else unarmed locomotion. Null = unarmed.
        /// </summary>
        public void SetWeaponStance(string weaponField, string weaponTemplate, string ammoTemplate = null)
        {
            string left = "None", right = "None";
            for (string field = weaponField; field != null && _player != null && _player.library != null && _rig != null; field = G3CombatMotions.Fallback(field))
            {
                var (l, r) = G3CombatMotions.Hands(field, ammoTemplate != null);
                if (_player.library.Find(new G3MotionKey(_rig, "Move", "Run", leftItem: l, rightItem: r)) != null) { left = l; right = r; break; }
            }
            _weaponTemplate = weaponTemplate;
            _ammoTemplate = ammoTemplate;
            _rangedWeapon = G3CombatMotions.IsRanged(weaponField);
            if (left != _weaponLeft || right != _weaponRight)
            {
                _weaponLeft = left;
                _weaponRight = right;
                _loco = null;
                if (!EnsureSelector()) return;
            }
            if (_state == State.Locomotion && !_player.Claimed) EnterLocomotion();
            else SetWeaponHands();
        }

        void SetWeaponHands()
        {
            if (_weaponTemplate == null && (_carryLeft != null || _carryRight != null)) SetHands(_carryLeft, _carryRight);
            else if (_rangedWeapon) SetHands(_weaponTemplate, _ammoTemplate);
            else SetHands(null, _weaponTemplate);
        }

        void SetHands(string left, string right)
        {
            if (left == LeftItem && right == RightItem) return;
            LeftItem = left;
            RightItem = right;
            HandsChanged?.Invoke();
        }
    }
}
