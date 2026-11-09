using System;
using System.Collections.Generic;
using G3.Player;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>Phase of a combat move as G3 names it in the motions: Raise → Hit (the swing window) → Recover.</summary>
    public enum G3SwingPhase { None, Raise, Hit, Recover }

    /// <summary>
    /// A selector that shows the stance between combat one-shots: it takes the weapon's hand fields into its locomotion
    /// set (<c>Hero_Stand_None_1H_P0_Move_…</c>, <c>Hero_Stand_Bow_Arrow_P0_Move_…</c>; <see cref="G3CombatMotions.Hands"/>)
    /// and shows the weapon template in its hand. Null field = unarmed. <paramref name="ammoTemplate"/>: the nocked
    /// arrow or bolt in the right hand of a ranged weapon (null = none).
    /// </summary>
    public interface IG3WeaponStance
    {
        void SetWeaponStance(string weaponField, string weaponTemplate, string ammoTemplate = null);
    }

    /// <summary>
    /// A locomotion selector that can walk in the Parade stance (the hero: G3 lets him move while blocking). When the
    /// raised parade reaches its held pose, the combat animator releases the motion player and sets this; any other
    /// combat request clears it.
    /// </summary>
    public interface IG3ParadeStance
    {
        bool ParadeLocomotion { get; set; }
        /// <summary>
        /// Sneaking uses the Parade stance instead of Sneak (G3: a human with a melee weapon drawn sneaks in the Parade
        /// ani-state; set by the game from its rules, Docs/combat.md §1.6).
        /// </summary>
        bool SneakInParade { get; set; }
    }

    /// <summary>
    /// The combat motion selector on a <see cref="G3MotionPlayer"/> (Docs/combat.md §9): draw and sheathe, attacks with
    /// their Raise/Hit/Recover phases and combo poses, the parade, hit reactions, knock-down and getting up, and death.
    /// Rig-agnostic and rule-free: callers (the Myrtana combat views) say which G3 action to show; this class composes the
    /// motion keys (<see cref="G3CombatMotions"/>), sequences the phases and claims the motion player while a one-shot runs,
    /// so the locomotion and interaction selectors wait. The Hit phase is the swing window (<see cref="SwingCount"/>).
    /// Bows and crossbows (left hand) reload, aim and shoot on the upper-body overlay (<see cref="G3MotionPlayer.PlayOverlay"/>),
    /// so the legs keep walking; their steps do not claim the player.
    /// </summary>
    [RequireComponent(typeof(G3MotionPlayer))]
    [DefaultExecutionOrder(150)] // after the locomotion selectors (100), before G3MotionPlayer (200)
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Combat Animator")]
    public sealed class G3CombatAnimator : MonoBehaviour
    {
        enum Mode { Free, Draw, Sheathe, Move, Recover, Parade, LowerParade, Reaction, Down, GetUp, Dead, HoldLeft }

        enum RangedMode { None, Reload, Aim, Shoot, Cast, CastRelease }

        struct Step
        {
            public string key;
            public G3SwingPhase phase;
            public bool hold; // stays until the next request (parade, lying, dead)
            public bool nockAfter; // ranged: the ammunition is in the hand when this step ends
            public string action, stage; // ranged: re-resolved with the aim direction
            public bool nocked;
        }

        // Body motions cross-fade as G3's motion commands do (MotionBlendRules: 0.2 s from a running motion, 0.1 s into a
        // _Begin_ motion, a cut after a one-shot ended). The bow/crossbow overlay keeps its own fade (not researched).
        public float overlayFade = 0.15f;
        [Tooltip("Aim pitch (deg) beyond which the Up/Down aim motions are used")] public float aimPitchSplit = 25f;

        readonly List<Step> _steps = new List<Step>();
        readonly List<Step> _overlaySteps = new List<Step>();
        RangedMode _ranged;
        int _overlayStep;
        string _aimDir = "Fwd";
        string _pendingAmmo;
        float _rangedSpeed = 1f, _drawStart = -1;
        G3MotionPlayer _player;
        IG3WeaponStance _stance;
        IG3ParadeStance _paradeStance;
        string _rig;
        Mode _mode;
        int _step;
        string _downStance, _downDir = "Fwd";
        readonly WeaponHand _hand = new WeaponHand();

        /// <summary>Hand field of the weapon in use ("1H", "2H", "Staff", "Fist"; "Bow", "CrossBow" in the left hand).</summary>
        public string Weapon => _hand.MotionField;
        /// <summary>Item template of the weapon in use (null for fists and monsters).</summary>
        public string WeaponTemplate => _hand.MotionTemplate;
        /// <summary>In the combat stance (drawn, or drawing).</summary>
        public bool Drawn => _hand.Drawn;
        /// <summary>The weapon is in the hand (switches between the two motions of a draw or sheathe, G3 HoldStacks).</summary>
        public bool WeaponInHand => _hand.InHand;
        /// <summary>The single source of truth for the hand (Drawn, WeaponInHand, Weapon derive from it; read-only for callers).</summary>
        public WeaponHand Hand => _hand;
        public event Action WeaponInHandChanged;
        /// <summary>Sound cues of any combat animator: a draw or sheathe started (G3 starts the draw/undraw sound with the
        /// command), the aim started, the shot left (combat sounds, Docs/combat.md §13).</summary>
        public static event Action<G3CombatAnimator, G3CombatCue> Cue;
        /// <summary>A body motion of a move started: its key, phase and the cross-fade it got, s (dev log).</summary>
        public event Action<string, G3SwingPhase, float> StepStarted;
        /// <summary>A move, reaction, draw, sheathe or get-up ended (G3 action name of the move).</summary>
        public event Action<string> MoveEnded;
        /// <summary>Playback speed of a step (G3 action, phase): the rules' motion speed factors; null = authored speed.</summary>
        public Func<string, G3SwingPhase, float> SpeedOf { get; set; }

        // --- ranged
        /// <summary>The weapon in use is a bow or crossbow.</summary>
        public bool IsRanged => G3CombatMotions.IsRanged(Weapon);
        /// <summary>An arrow or bolt is in the right hand (nocked).</summary>
        public bool Nocked { get; private set; }
        /// <summary>Item template of the nocked ammunition.</summary>
        public string AmmoTemplate { get; private set; }
        public bool InReload => _ranged == RangedMode.Reload;
        /// <summary>Raising, drawing or holding the aim.</summary>
        public bool InAim => _ranged == RangedMode.Aim;
        /// <summary>Holding the aim (the raise and draw motions are over): a shot leaves now.</summary>
        public bool AimHeld => _ranged == RangedMode.Aim && _overlayStep < _overlaySteps.Count && _overlaySteps[_overlayStep].hold;
        public bool InShot => _ranged == RangedMode.Shoot;
        /// <summary>Seconds the bow has been drawn (from the start of <c>Aim_PowerRaise</c>, G3 PS_Ranged_PowerAim); 0 when not aiming.</summary>
        public float DrawSeconds => _ranged == RangedMode.Aim && _drawStart >= 0 ? Time.time - _drawStart : 0;
        /// <summary>Aim pitch in degrees (up positive), set by the owner; picks the Up/Fwd/Down aim motions.</summary>
        public float AimPitch { get; set; }
        /// <summary>A reload ("Reload"), a shot ("Shoot") or a cast ("Cast") finished.</summary>
        public event Action<string> RangedEnded;

        // --- magic
        /// <summary>The spell hand is drawn (right-hand field "Cast").</summary>
        public bool IsCastStance => G3CombatMotions.IsCast(Weapon);
        /// <summary>Loading or holding a spell (the Raise motion, held), or releasing it (Hit → Recover).</summary>
        public bool InCast => _ranged == RangedMode.Cast || _ranged == RangedMode.CastRelease;
        /// <summary>The spell is released and its motion is not over yet.</summary>
        public bool InCastRelease => _ranged == RangedMode.CastRelease;
        /// <summary>A bow, crossbow or cast motion runs on the upper-body overlay (aim, shot, reload, cast).</summary>
        public bool InRangedOverlay => _ranged != RangedMode.None;
        /// <summary>The cast motion reached its Hit phase: the spell leaves the hand now.</summary>
        public event Action CastReleased;

        /// <summary>
        /// Item field in the left hand beside a melee weapon ("Torch": the hero's torch, Hero_Stand_Torch_1H_… motions);
        /// null = none. Motions without it fall back to the plain set.
        /// </summary>
        public string OffHand { get; set; }

        public string Action { get; private set; }
        public G3SwingPhase Phase => _mode != Mode.Free && _step < _steps.Count ? _steps[_step].phase : G3SwingPhase.None;
        /// <summary>Counts the Hit phases started: a new value opens a new swing window.</summary>
        public int SwingCount { get; private set; }
        public bool IsSwinging => (_mode == Mode.Move) && Phase == G3SwingPhase.Hit;
        /// <summary>A one-shot or a held combat pose has the motion player.</summary>
        public bool Busy => _mode != Mode.Free && _mode != Mode.Recover;
        public bool InMove => _mode == Mode.Move;
        public bool InParade => _mode == Mode.Parade;
        /// <summary>
        /// The block is up: the parade is held, or a parried hit's stumble plays and the parade comes back after it (G3
        /// _AI_ParadeStumble keeps the ani state Parade; Myrtana.Rules.ParadeHold). The hit check reads this.
        /// </summary>
        public bool HoldsParade => _mode == Mode.Parade && InParadePose || _mode == Mode.Reaction && _resumeParade;
        /// <summary>
        /// A hit reaction's Hit plays (G3: a ZS_*Stumble task; its Recover is a tail, <see cref="InRecoverTail"/>, or for a
        /// parried stumble the held parade).
        /// </summary>
        public bool InReaction => _mode == Mode.Reaction;
        /// <summary>The draw motions run (G3 PS_Melee's entry: _AI_HoldInventoryItems); the owner's moves wait for its end.</summary>
        public bool InDraw => _mode == Mode.Draw;

        /// <summary>
        /// A hit reaction's Hit or a draw is over (G3: back in PS_Melee_Loop): the owner's input may act again at once. Fired
        /// as the animator's last action of the frame, so a handler may start the next motion.
        /// </summary>
        public event Action<string> Freed;

        /// <summary>
        /// G3 IsInParadeMode = AniState Parade and IsInParadePose (0x1000d980): during the raise the primary pose is the raise
        /// motion's start pose until half of it, its end pose after (TrackCurrentPose); it must be the held parade's pose (the
        /// Parade Ambient Loop: P1 with a shield, else P0). The held loop and a parried stumble's Recover count at once.
        /// </summary>
        bool InParadePose
        {
            get
            {
                if (_step >= _steps.Count || _steps[_step].hold || _steps[_step].stage != "Begin") return true;
                int need = _paradePose;
                string motion = MotionOf(_steps[_step].key);
                int pose = _player.CurrentKey == _steps[_step].key && _player.CurrentPhase >= 0.5f ? PoseWord(motion, 12) : PoseWord(motion, 4);
                return need < 0 || pose < 0 || pose == need;
            }
        }

        int _paradePose = -1;

        string MotionOf(string key) => key != null && _player.library != null ? _player.library.Find(key)?.motion : null;

        /// <summary>The end pose (0…3) in the name of the step playing now; -1 when none (fix-244: a pierce's Raise end).</summary>
        public int StepEndPose => _mode == Mode.Free || _step >= _steps.Count ? -1 : PoseWord(MotionOf(_steps[_step].key), 12);

        /// <summary>The primary pose of word <paramref name="index"/> of a motion name (4: start pose, 12: end pose); -1 when none.</summary>
        static int PoseWord(string motion, int index)
        {
            var words = motion?.Split('_');
            return words == null || words.Length <= index ? -1 : PrimaryPose(words[index]);
        }
        public bool IsDown => _mode == Mode.Down || _mode == Mode.GetUp;
        public bool IsDead => _mode == Mode.Dead;
        /// <summary>Dead and in the final held pose (the LieDead loop): the dying motion is over (fix-80 corpse rest).</summary>
        public bool LiesDead => _mode == Mode.Dead && _step < _steps.Count && _steps[_step].hold;
        /// <summary>
        /// fix-180: dead, and the held dead pose is what the motion player shows (faded in, nothing else mixed in): the body
        /// may be laid on the ground. A rig without a dead pose (a frozen last frame) counts as shown.
        /// </summary>
        public bool DeadPoseShown => LiesDead && (_steps[_step].key == null || _player.CurrentKey == _steps[_step].key && _player.CurrentAlone);

        /// <summary>
        /// fix-180: shows the held dead pose again at once, without a fade (the player was taken over, a fade never ran:
        /// a body killed frozen or in a time bubble); false when not lying dead or the rig has no dead pose.
        /// </summary>
        public bool ReshowDeadPose()
        {
            if (!LiesDead || _steps[_step].key == null) return false;
            _player.TimeScale = 1f;
            _player.StopOverlay(0f);
            _player.Claim(this);
            _player.Play(_steps[_step].key, 0f, restart: true);
            return true;
        }

        /// <summary>Mode, overlay and hand for logs ("Move/Aim, hand 1H None").</summary>
        public string StateText => $"{_mode}{(_ranged != RangedMode.None ? "/" + _ranged : "")}, hand {_hand.Field ?? "empty"} {_hand.Transition}";
        /// <summary>
        /// The current step's motion-name number in metres (cm / 100): for a combat motion the distance G3 carries the body
        /// over the whole motion (Docs/combat.md §5.6, <c>Myrtana.Rules.CombatLunge</c>); 0 when free or between motions.
        /// </summary>
        public float StepDistance => _mode == Mode.Free || _step >= _steps.Count || _steps[_step].key == null || _player.CurrentKey != _steps[_step].key ? 0 : _player.CurrentNameNumber;
        /// <summary>The current step's phase word in the motion name ("Raise", "Hit", "Recover", "Begin", "Loop"); null when free.</summary>
        public string StepStage => _mode == Mode.Free || _step >= _steps.Count ? null : _steps[_step].stage;
        /// <summary>Counts the steps started (each motion of a move, reaction, fall …): a new value is a new motion.</summary>
        public int StepSerial { get; private set; }
        /// <summary>Normalised time (0..1) of the current step's motion (e.g. how far into the Hit a move is); 0 when free.</summary>
        public float StepProgress => _mode == Mode.Free || _player == null ? 0 : _player.CurrentPhase;

        void Awake()
        {
            _player = GetComponent<G3MotionPlayer>();
            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                if (_stance == null && mb is IG3WeaponStance s) _stance = s;
                if (_paradeStance == null && mb is IG3ParadeStance p) _paradeStance = p;
            }
        }

        /// <summary>Motion name prefix of the rig ("Hero", "Wolf"; G3's skeleton name for creature sounds).</summary>
        public string Rig => _rig ??= _player.library != null ? _player.library.rig : null;

        // ------------------------------------------------------------------------------------------- requests

        /// <summary>Takes the weapon (hand field, template) and plays the draw motion; false when already drawn.</summary>
        public bool Draw(string weaponField, string template)
        {
            // Only into empty hands and not while a draw or sheathe runs: a different weapon is put away first (WeaponHand.Next).
            if (IsDown || IsDead || !_hand.Settled || _hand.InHand) return false;
            Begin(Mode.Draw, "Draw");
            _hand.BeginDraw(string.IsNullOrEmpty(weaponField) ? G3CombatMotions.Fist : weaponField, template);
            Nocked = false;
            Cue?.Invoke(this, G3CombatCue.WeaponDrawn); // G3 _AI_HoldInventoryItems: the sound starts with the command
            StartHold(true, HandActions(), OffHand);
            return true;
        }

        /// <summary>
        /// The draw or sheathe actions to try, best first: a bow in the left hand (<c>HoldLeft</c>), a melee weapon in the
        /// right (<c>HoldRight</c>), a dual wielder's two blades or a one-handed weapon with a shield together (<c>Hold</c>,
        /// <see cref="G3CombatMotions.DualDraw"/>; G3 HoldStacks of both hands), falling back to the right-hand motion.
        /// </summary>
        string[] HandActions()
        {
            if (IsRanged) return HoldLeftOnly;
            bool oneHanded = string.Equals(Weapon, G3CombatMotions.OneHanded, StringComparison.OrdinalIgnoreCase);
            if (oneHanded && (G3CombatMotions.IsDualOffHand(OffHand) || string.Equals(OffHand, G3CombatMotions.Shield, StringComparison.OrdinalIgnoreCase)))
                return BothThenRight;
            return HoldRightOnly;
        }

        static readonly string[] HoldLeftOnly = { "HoldLeft" }, HoldRightOnly = { "HoldRight" }, BothThenRight = { G3CombatMotions.DualDraw, "HoldRight" };

        /// <summary>
        /// The left hand alone (G3 dual wielding, OnPlayerUseQuickSlot → FUN_10002550: the off-hand weapon's key while the
        /// main one-handed weapon is drawn): <paramref name="draw"/> takes the second blade into the left hand
        /// (<c>Hero_Stand_1H_1H_P0_HoldLeft_Begin</c>), else puts it back (<c>…_HoldLeft_End</c>). <see cref="LeftHandSwitched"/>
        /// fires between the two motions (at once without them). False when not standing with a one-handed weapon in the hand.
        /// </summary>
        public bool HoldLeft(bool draw)
        {
            if (IsDown || IsDead || Busy || !_hand.Settled || !_hand.InHand || IsRanged ||
                !string.Equals(Weapon, G3CombatMotions.OneHanded, StringComparison.OrdinalIgnoreCase)) return false;
            Begin(Mode.HoldLeft, G3CombatMotions.LeftDraw);
            _leftDraw = draw;
            _leftSwitched = false;
            Cue?.Invoke(this, draw ? G3CombatCue.LeftDrawn : G3CombatCue.LeftSheathed); // the sound starts with the command
            // The 1H_1H set (both hands), whichever way the left blade goes.
            StartHold(draw, LeftOnly, G3CombatMotions.DualOffHand);
            return true;
        }

        static readonly string[] LeftOnly = { G3CombatMotions.LeftDraw };

        /// <summary>The left blade reached or left the hand (between the two motions of <see cref="HoldLeft"/>): true = now in the left hand.</summary>
        public event Action<bool> LeftHandSwitched;

        bool _leftDraw, _leftSwitched;

        void SwitchLeft()
        {
            if (_leftSwitched) return;
            _leftSwitched = true;
            LeftHandSwitched?.Invoke(_leftDraw);
        }

        /// <summary>Puts the weapon away (sheathe motion, then the unarmed stance).</summary>
        public bool Sheathe()
        {
            if (!Drawn || IsDown || IsDead) return false;
            Begin(Mode.Sheathe, "Sheathe"); // a running draw is cut short: the hand keeps what it has
            if (!_hand.BeginSheathe())
            {
                Abort(); // the draw ended before the weapon reached the hand: nothing to put away
                return true;
            }
            SetNocked(false, null); // the arrow goes back into the quiver
            Cue?.Invoke(this, G3CombatCue.WeaponSheathed); // the sound starts with the command
            StartHold(false, HandActions(), OffHand);
            return true;
        }

        /// <summary>
        /// An attack or another move with phases (G3 action: "Attack", "PowerAttack", "QuickAttackL", "JumpBack",
        /// "FinishingAttack" …). <paramref name="combo"/> selects the combo pose (P0…P3 where the rig has them).
        /// <paramref name="raise"/>: the move starts with its Raise motion (G3: only PowerAttack, Pierce, Hack, Finishing do;
        /// Attack, QuickAttack and the whirls go straight into the Hit). <paramref name="recoverInMove"/> false: the move ends
        /// (<see cref="MoveEnded"/>) when the Hit ends and the Recover plays on as a tail (<see cref="InRecoverTail"/>) that
        /// any next request or <see cref="CutRecover"/> cuts (G3 sAICombatMoveStartRecover). The Recover is the one of the
        /// pose the Hit ends in (the Hit name's end pose; G3 GetPrimaryPoseExt), else of the asked pose, else P1.
        /// <paramref name="waitAfterHit"/> &gt; 0 (a tail move): the move still counts as running for that many seconds
        /// after its Hit while the Recover already plays (G3 _AI_QuickAttack: sAIWaitInstr 100 ms), then it ends.
        /// </summary>
        public bool Attack(string action, int combo = 0, bool raise = true, bool recoverInMove = true, float waitAfterHit = 0f)
        {
            if (IsDown || IsDead || string.IsNullOrEmpty(action)) return false;
            string pose = "P" + Mathf.Clamp(combo, 0, 3);
            // Finishing, whirl, twirl and pierce attacks start from the parade stance in G3; others from standing.
            bool parade = action == "FinishingAttack" || action == "WhirlAttack" || action == "TwirlAttack" || action == "PierceAttack";
            foreach (var stance in parade ? new[] { "Parade", "Stand" } : new[] { "Stand" })
            {
                Begin(Mode.Move, action);
                if (raise) Add(stance, action, "Raise", G3SwingPhase.Raise, pose);
                Add(stance, action, "Hit", G3SwingPhase.Hit, pose);
                if (!HasStep(G3SwingPhase.Hit)) continue;
                HitEndPose = EndPoseOf(_steps[_steps.Count - 1].key);
                _tailRecover = !recoverInMove;
                _waitAfterHit = _tailRecover ? Mathf.Max(0f, waitAfterHit) : 0f;
                if (HitEndPose >= 0 && "P" + HitEndPose != pose) Add(stance, action, "Recover", G3SwingPhase.Recover, "P" + HitEndPose);
                if (!HasStep(G3SwingPhase.Recover)) Add(stance, action, "Recover", G3SwingPhase.Recover, pose);
                // Some recovers exist only in the pose the hit ends in (hero 2H FinishingAttack: Hit P0 → Recover P1).
                if (!HasStep(G3SwingPhase.Recover) && pose != "P1") Add(stance, action, "Recover", G3SwingPhase.Recover, "P1");
                return Run();
            }
            return Abort();
        }

        /// <summary>The end pose (0…3) in the name of the motion behind <paramref name="key"/> (<c>…_00_%_00_P1_118_R</c> → 1); -1 when none.</summary>
        int EndPoseOf(string key)
        {
            var entry = key != null && _player.library != null ? _player.library.Find(key) : null;
            var words = entry?.motion?.Split('_');
            return words == null || words.Length < 13 ? -1 : PrimaryPose(words[12]);
        }

        /// <summary>
        /// G3's primary pose of a pose word (Game.dll TrackCurrentPose/GetPrimaryPoseExt): P0…P3; a transition Pxy → y
        /// (P31 → 1); P40…P70 → 0 (the quick attacks' P50/P60/P70 ends); -1 = no pose (Myrtana.Rules.MeleeComboRules.PoseOf).
        /// </summary>
        static int PrimaryPose(string w)
        {
            if (w == null || w.Length < 2 || w.Length > 3 || w[0] != 'P') return -1;
            for (int i = 1; i < w.Length; i++)
                if (w[i] < '0' || w[i] > '9') return -1;
            int last = w[w.Length - 1] - '0';
            if (w.Length == 2) return last <= 3 ? last : -1;
            return w[1] >= '4' ? 0 : last <= 3 ? last : -1;
        }

        /// <summary>The pose (0…3) the last attack's Hit ends in (its motion name), -1 when unknown: the next chained attack's combo pose.</summary>
        public int HitEndPose { get; private set; } = -1;

        /// <summary>A move ended with its Hit and its Recover still plays (cosmetic: the next move or walking cuts it).</summary>
        public bool InRecoverTail => _mode == Mode.Recover;

        /// <summary>Ends a Recover tail at once (the body walks off); the locomotion selector takes over.</summary>
        public void CutRecover()
        {
            if (_mode != Mode.Recover) return;
            _steps.Clear();
            _mode = Mode.Free;
            Action = null;
            _player.Release(this);
        }

        /// <summary>Raises the parade and holds it; false when the rig has no parade (monsters).</summary>
        public bool RaiseParade()
        {
            if (IsDown || IsDead) return false;
            if (_mode == Mode.Parade) return true;
            Begin(Mode.Parade, "Parade");
            Add("Stand", "Parade", "Begin", G3SwingPhase.None);
            Add("Parade", "Ambient", "Loop", G3SwingPhase.None, hold: true);
            if (_steps.Count == 0 || !_steps[_steps.Count - 1].hold) return Abort();
            _paradePose = PoseWord(MotionOf(_steps[_steps.Count - 1].key), 4); // the held parade's pose (IsInParadePose)
            return Run();
        }

        public void LowerParade()
        {
            if (_mode == Mode.Reaction && _resumeParade)
            {
                _resumeParade = false; // released during a parried stumble: it ends standing
                return;
            }
            if (_mode != Mode.Parade) return;
            Begin(Mode.LowerParade, "Parade");
            Add("Parade", "Stand", "Begin", G3SwingPhase.None);
            if (!Run()) End();
        }

        /// <summary>
        /// A hit reaction: the first of <paramref name="actions"/> the rig has ("ParadeStumbleL", "QuickStumble",
        /// "StumbleR", "PierceStumble" …), Hit then Recover; <paramref name="back"/> picks the Back variants (G3: the hitter in
        /// front, Myrtana.Rules.HitReactionRules.PlaysBackVariant; Fwd when the rig has no Back motion).
        /// </summary>
        public bool React(bool back, params string[] actions) => React(back, false, actions);

        /// <summary>
        /// A hit reaction (see above); <paramref name="keepParade"/>: the hit was parried in the block, which comes back
        /// (held, without the raise motion) when the stumble ends, unless <see cref="LowerParade"/> came meanwhile.
        /// </summary>
        public bool React(bool back, bool keepParade, params string[] actions)
        {
            if (IsDown || IsDead) return false;
            string dir = back ? "Back" : "Fwd";
            bool inParade = HoldsParade;
            string stance = inParade ? "Parade" : "Stand";
            foreach (var action in actions)
            {
                Begin(Mode.Reaction, action);
                AddReaction(stance, action, dir);
                if (stance == "Parade" && _steps.Count == 0) AddReaction("Stand", action, dir);
                if (_steps.Count == 0) continue;
                _resumeParade = keepParade && inParade;
                return Run();
            }
            return Abort();
        }

        /// <summary>
        /// A reaction's Hit and the Recover of the pose the Hit ends in (G3 sAICombatMoveStartRecover; P0 when that pose has
        /// none). The reaction is over with its Hit, the Recover is a tail (<see cref="ReactionHitOver"/>).
        /// </summary>
        void AddReaction(string stance, string action, string dir)
        {
            Add(stance, action, "Hit", G3SwingPhase.None, direction: dir);
            if (_steps.Count == 0) return;
            int end = EndPoseOf(_steps[0].key);
            if (end > 0) Add(stance, action, "Recover", G3SwingPhase.None, "P" + end, dir);
            if (_steps.Count == 1) Add(stance, action, "Recover", G3SwingPhase.None, direction: dir);
            HitEndPose = end < 0 ? 0 : end; // G3 TrackCurrentPose: a move started in the Recover tail goes on from this pose
        }

        /// <summary>
        /// The reaction's Hit is over (G3: the _AI_*Stumble command returned, AssessAgressor → PS_Melee): its Recover plays on
        /// as a tail that any request or walking cuts; a parried stumble whose button is still held goes on in the parade
        /// (the Recover, then the held Parade Ambient Loop; the ani state stayed Parade).
        /// </summary>
        void ReactionHitOver()
        {
            string ended = Action;
            bool parade = false;
            if (_resumeParade)
            {
                _resumeParade = false;
                int before = _steps.Count;
                Add("Parade", "Ambient", "Loop", G3SwingPhase.None, hold: true);
                parade = _steps.Count > before;
                if (parade) _paradePose = -1; // the Recover and the loop count as the parade (it never went down)
            }
            if (parade)
            {
                _mode = Mode.Parade;
                Action = "Parade";
            }
            else _mode = Mode.Recover; // walking cuts it (G3 hands the Recover back to the movement), as an attack's tail
            MoveEnded?.Invoke(ended);
            Freed?.Invoke(ended);
        }

        /// <summary>Falls into a down state ("LieKnockDown", "LieKnockOut", "LiePiercedKO", "SitKnockDown") and stays there.</summary>
        public bool FallDown(string downStance, bool back)
        {
            if (IsDead) return false;
            string dir = back ? "Back" : "Fwd";
            _downStance = downStance;
            _downDir = dir;
            Begin(Mode.Down, downStance);
            if (downStance == "SitKnockDown")
            {
                Add("LieKnockDown", "SitKnockDown", "Begin", G3SwingPhase.None, direction: dir);
                Add(downStance, "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
                if (_steps.Count == 0) { _downStance = downStance = "LieKnockDown"; }
            }
            if (_steps.Count == 0)
            {
                Add("Stand", downStance, "Begin", G3SwingPhase.None, direction: dir);
                Add(downStance, "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
            }
            if (_steps.Count == 0) return Abort();
            return Run();
        }

        /// <summary>Gets up from the down state (its Stand_Begin motion), then gives the player back.</summary>
        public void GetUp()
        {
            if (_mode != Mode.Down) return;
            Begin(Mode.GetUp, "GetUp");
            Add(_downStance, "Stand", "Begin", G3SwingPhase.None, direction: _downDir);
            if (!Run()) End();
        }

        /// <summary>Leaves the down state at once, without a motion (the dev console's safety valve); the hand is kept.</summary>
        public void StandUpNow()
        {
            if (!IsDown) return;
            End();
        }

        /// <summary>
        /// Gets up from sitting knocked down with an attack (<c>SitKnockDown_…_GetUpAttack</c>; Hit = swing window) or with
        /// the parade (<c>GetUpParade</c>: Hit, Recover); false when not sitting or the rig has no such motion.
        /// <paramref name="raise"/> and <paramref name="recoverInMove"/> shape the attack as in <see cref="Attack"/> (G3
        /// _AI_GetUpAttack: Hit only, the Recover of the pose the Hit ends in plays on as a tail).
        /// </summary>
        public bool GetUpWith(bool attack, bool raise = false, bool recoverInMove = false)
        {
            if (_mode != Mode.Down || _downStance != "SitKnockDown") return false;
            string action = attack ? "GetUpAttack" : "GetUpParade";
            string dir = _downDir;
            Begin(Mode.Move, action);
            bool ok;
            if (attack)
            {
                if (raise) Add(_downStance, action, "Raise", G3SwingPhase.Raise, direction: dir);
                Add(_downStance, action, "Hit", G3SwingPhase.Hit, direction: dir);
                ok = HasStep(G3SwingPhase.Hit);
                if (ok)
                {
                    HitEndPose = EndPoseOf(_steps[_steps.Count - 1].key);
                    _tailRecover = !recoverInMove;
                    Add(_downStance, action, "Recover", G3SwingPhase.Recover, HitEndPose >= 0 ? "P" + HitEndPose : "P1", dir);
                }
            }
            else
            {
                Add(_downStance, action, "Hit", G3SwingPhase.None, direction: dir);
                Add(_downStance, action, "Recover", G3SwingPhase.None, direction: dir);
                ok = _steps.Count >= 2;
            }
            if (ok) return Run();
            // No such motion: stay sitting.
            Begin(Mode.Down, _downStance);
            Add(_downStance, "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
            Run(cut: true);
            return false;
        }

        /// <summary>Dies (from the down state when lying) and stays dead (<paramref name="deadStance"/>: LieDead, LiePiercedDead).</summary>
        public void Die(bool back, string deadStance = "LieDead")
        {
            if (IsDead) return;
            bool lying = _mode == Mode.Down;
            string dir = lying ? _downDir : back ? "Back" : "Fwd";
            string from = _downStance;
            Begin(Mode.Dead, "Die");
            if (lying) Add(from, "Die", "Begin", G3SwingPhase.None, direction: dir);
            else Add("Stand", deadStance, "Begin", G3SwingPhase.None, direction: dir);
            Add(deadStance, "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
            if (_steps.Count == 0 || !_steps[_steps.Count - 1].hold) Add("LieDead", "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
            if (_steps.Count == 0) _steps.Add(new Step { key = null, hold = true }); // no dead pose: freeze in place
            Run();
        }

        /// <summary>
        /// Lies in the final pose at once, without the fall (G3 OnExitProcessingRange/ForceNextPose: a body met again is
        /// shown in its ground pose, fix-80): <paramref name="dead"/> = the dead stance's loop (LieDead, LiePiercedDead), else
        /// the down stance's (LieKnockDown, LieKnockOut, LiePiercedKO, SitKnockDown) — a loaded game or a view streaming in
        /// never replays the death or knock-down motion.
        /// </summary>
        public void LieAtOnce(bool dead, string stance, bool back)
        {
            if (dead ? IsDead : IsDown) return;
            string dir = back ? "Back" : "Fwd";
            if (!dead)
            {
                _downStance = stance;
                _downDir = dir;
            }
            Begin(dead ? Mode.Dead : Mode.Down, dead ? "Die" : stance);
            Add(stance, "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
            if (_steps.Count == 0 && dead) Add("LieDead", "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
            if (_steps.Count == 0 && !dead && stance != "LieKnockDown")
            {
                _downStance = "LieKnockDown";
                Add(_downStance, "Ambient", "Loop", G3SwingPhase.None, direction: dir, hold: true);
            }
            if (_steps.Count == 0) _steps.Add(new Step { key = null, hold = true }); // no lying pose in this rig: freeze in place
            Run(cut: true);
        }

        /// <summary>The weapon falls out of the hand (knock-out): unarmed until it is drawn again.</summary>
        public void DropWeapon()
        {
            StopRanged();
            Nocked = false;
            AmmoTemplate = null;
            ApplyHand(_hand.Clear());
        }

        /// <summary>
        /// The weapon (or the spell hand) leaves the hand at once, without the sheathe motion: G3 _AI_UseInventoryItem holds
        /// the used item instead (HoldStacks, fix-68). <paramref name="field"/>/<paramref name="template"/> tell what was in
        /// the hand, for <see cref="HoldAtOnce"/>. False (nothing changed) while a move, reaction, parade, draw or sheathe
        /// runs, or with empty hands.
        /// </summary>
        public bool StowAtOnce(out string field, out string template)
        {
            field = _hand.Field;
            template = _hand.Template;
            if (IsDown || IsDead || Busy || (_ranged != RangedMode.None && _ranged != RangedMode.Reload) || !_hand.Settled || !_hand.InHand) return false;
            StopRanged(); // a reload is cut
            Nocked = false; // the arrow goes back into the quiver
            ApplyHand(_hand.Clear());
            return true;
        }

        /// <summary>
        /// Takes <paramref name="field"/>/<paramref name="template"/> back into the empty hands at once, without the draw
        /// motion (G3 _AI_HoldInventoryItems with the stacks held before the use, then PS_Melee/PS_Ranged/PS_Magic; fix-68).
        /// </summary>
        public bool HoldAtOnce(string field, string template)
        {
            if (IsDown || IsDead || !_hand.Settled || _hand.InHand) return false; // knocked out meanwhile: the weapon stays out of the hand
            ApplyHand(_hand.Hold(field, template));
            return true;
        }

        /// <summary>Back to the unarmed standing state at once (view reused, game loaded, revived).</summary>
        public void ResetAll()
        {
            StopRanged();
            EndHoldOverlay(false);
            Nocked = false;
            AmmoTemplate = null;
            _steps.Clear();
            _mode = Mode.Free;
            Action = null;
            ApplyHand(_hand.Clear());
            _player.Release(this);
        }

        // ------------------------------------------------------------------------------------------- ranged (overlay)

        /// <summary>
        /// Takes an arrow or bolt (<paramref name="ammoTemplate"/>) and nocks it: bow <c>Bow_None Reload_Begin</c> →
        /// <c>Bow_Arrow Reload_End</c>; crossbow <c>Cock_Begin</c> → <c>Reload_Begin</c> → <c>CrossBow_Bolt Reload_End</c>.
        /// </summary>
        public bool Reload(string ammoTemplate, float speed = 1f)
        {
            if (_mode == Mode.Recover && IsRanged && Drawn && WeaponInHand && !Nocked && _ranged == RangedMode.None) CutRecover(); // fix-120
            if (!IsRanged || !Drawn || !WeaponInHand || Nocked || _mode != Mode.Free || _ranged != RangedMode.None) return false;
            BeginRanged(RangedMode.Reload);
            if (Weapon == G3CombatMotions.CrossBow) AddOverlay("Cock", "Begin", nocked: false);
            AddOverlay("Reload", "Begin", nocked: false, nockAfter: true);
            AddOverlay("Reload", "End", nocked: true);
            _pendingAmmo = ammoTemplate;
            _rangedSpeed = speed;
            if (!RunOverlay()) SetNocked(true, ammoTemplate); // no motions: nocked at once
            return true;
        }

        /// <summary>
        /// fix-184: the arrow or bolt on the string becomes <paramref name="ammoTemplate"/> at once (the player chose other
        /// ammunition while one was nocked, before drawing). False when nothing is nocked or the weapon is drawn back / shooting.
        /// </summary>
        public bool SwapNocked(string ammoTemplate)
        {
            if (!IsRanged || !Nocked || _mode != Mode.Free || (_ranged != RangedMode.None && _ranged != RangedMode.Reload)) return false;
            SetNocked(true, ammoTemplate);
            return true;
        }

        /// <summary>Raises the weapon and draws (bow: <c>Aim_Raise</c> → <c>Aim_PowerRaise</c> → <c>Aim_Aim</c> held).</summary>
        public bool Aim()
        {
            if (_mode == Mode.Recover && IsRanged && Nocked && _ranged != RangedMode.Aim) CutRecover(); // fix-120
            if (!IsRanged || !Nocked || _mode != Mode.Free || _ranged == RangedMode.Aim) return false;
            if (_ranged == RangedMode.Reload) _ranged = RangedMode.None; // nocked already: the reload's last motion gives way
            if (_ranged != RangedMode.None) return false;
            BeginRanged(RangedMode.Aim);
            Cue?.Invoke(this, G3CombatCue.AimRaised);
            AddOverlay("Aim", "Raise", nocked: true);
            if (Weapon == G3CombatMotions.Bow) AddOverlay("Aim", "PowerRaise", nocked: true);
            AddOverlay("Aim", "Aim", nocked: true, hold: true);
            _rangedSpeed = 1f;
            if (!RunOverlay())
            {
                _overlaySteps.Add(new Step { hold = true }); // no motions: aim at once
                _ranged = RangedMode.Aim;
                _drawStart = Time.time;
            }
            return true;
        }

        /// <summary>Lowers the aim without shooting (the ammunition stays nocked).</summary>
        public void LowerAim()
        {
            if (_ranged != RangedMode.Aim) return;
            StopRanged();
        }

        /// <summary>Releases the shot: the ammunition leaves the hand, then <c>Aim_Recover</c>.</summary>
        public bool Shoot()
        {
            if (_ranged != RangedMode.Aim || !Nocked) return false;
            SetNocked(false, null);
            BeginRanged(RangedMode.Shoot);
            Cue?.Invoke(this, G3CombatCue.ShotReleased);
            AddOverlay("Aim", "Recover", nocked: false);
            _rangedSpeed = 1f;
            if (!RunOverlay()) EndRanged();
            return true;
        }

        /// <summary>
        /// Loads a spell on the upper-body overlay: the first of <paramref name="actions"/> the rig has ("Summon", "Heal",
        /// "PowerCast", "Cast" …) plays its <c>_Raise</c> motion and holds its last pose until <see cref="ReleaseCast"/>.
        /// </summary>
        public bool BeginCast(params string[] actions)
        {
            if (_mode == Mode.Recover && IsCastStance && Drawn && WeaponInHand && !InCast) CutRecover(); // fix-120: a Recover tail (a stumble's) is cut by the next request
            if (!IsCastStance || !Drawn || !WeaponInHand || _mode != Mode.Free || InCast) return false;
            if (_ranged != RangedMode.None) StopRanged();
            BeginRanged(RangedMode.Cast);
            _castAction = FirstCastAction(actions);
            if (_castAction != null) AddOverlay(_castAction, "Raise", nocked: false, hold: true);
            for (int i = 0; i < _overlaySteps.Count; i++) { var st = _overlaySteps[i]; st.action = null; _overlaySteps[i] = st; } // no aim directions
            _rangedSpeed = CastSpeed;
            if (!RunOverlay())
            {
                _overlaySteps.Add(new Step { hold = true }); // no motions: held at once
                _ranged = RangedMode.Cast;
            }
            return true;
        }

        /// <summary>Releases the loaded spell: <c>_Hit</c> (raises <see cref="CastReleased"/>) → <c>_Recover</c>, then RangedEnded("Cast").</summary>
        public bool ReleaseCast()
        {
            if (_ranged != RangedMode.Cast) return false;
            BeginRanged(RangedMode.CastRelease);
            if (_castAction != null)
            {
                AddOverlay(_castAction, "Hit", nocked: false);
                AddOverlay(_castAction, "Recover", nocked: false);
            }
            _rangedSpeed = CastSpeed;
            if (!_overlaySteps.Exists(st => st.stage == "Hit"))
            {
                CastReleased?.Invoke();
                if (_ranged != RangedMode.CastRelease) return true; // ended by a listener (a teleport, fix-114)
            }
            if (_overlaySteps.Count == 0) EndRanged(); // no motions: released and over at once
            else PlayOverlayStep(overlayFade);
            return true;
        }

        /// <summary>
        /// Lets the loaded spell go as soon as its <c>_Raise</c> motion has played through (G3 _AI_Cast: the execute motion
        /// follows the begin motion by itself, fix-114): at once when it already has, else queued (<see cref="CastReleaseQueued"/>).
        /// </summary>
        public bool ReleaseCastAfterRaise()
        {
            if (_ranged != RangedMode.Cast) return false;
            if (RaiseOver) return ReleaseCast();
            _castReleaseQueued = true;
            return true;
        }

        /// <summary>A release waits for the end of the cast's raise motion (<see cref="ReleaseCastAfterRaise"/>).</summary>
        public bool CastReleaseQueued => _ranged == RangedMode.Cast && _castReleaseQueued;

        bool _castReleaseQueued;

        /// <summary>The cast's raise motion played through (or there is none): the hand is up, the spell ready to leave.</summary>
        bool RaiseOver => _overlayStep >= _overlaySteps.Count || _overlaySteps[_overlayStep].key == null || _player.OverlayPlayedOnce;

        /// <summary>Drops a loaded spell without casting it.</summary>
        public void AbortCast()
        {
            if (InCast) StopRanged();
        }

        /// <summary>Playback speed of the cast motions (summons ×1.5 …), set by the owner before <see cref="BeginCast"/>.</summary>
        public float CastSpeed { get; set; } = 1f;

        string _castAction;

        string FirstCastAction(string[] actions)
        {
            foreach (var action in actions)
                if (!string.IsNullOrEmpty(action) && ResolveRanged(action, "Raise", false, "Fwd") != null) return action;
            return null;
        }

        void BeginRanged(RangedMode mode)
        {
            _castReleaseQueued = false;
            _overlaySteps.Clear();
            _overlayStep = 0;
            _ranged = mode;
            _drawStart = -1;
            _aimDir = AimDirection();
        }

        void AddOverlay(string action, string stage, bool nocked, bool hold = false, bool nockAfter = false)
        {
            string key = ResolveRanged(action, stage, nocked, _aimDir);
            if (key != null) _overlaySteps.Add(new Step { key = key, hold = hold, nockAfter = nockAfter, action = action, stage = stage, nocked = nocked });
            else if (nockAfter && _overlaySteps.Count > 0)
            {
                var last = _overlaySteps[_overlaySteps.Count - 1];
                last.nockAfter = true;
                _overlaySteps[_overlaySteps.Count - 1] = last;
            }
        }

        bool RunOverlay()
        {
            if (_overlaySteps.Count == 0)
            {
                _ranged = RangedMode.None;
                return false;
            }
            PlayOverlayStep(overlayFade);
            return true;
        }

        void PlayOverlayStep(float fade)
        {
            var step = _overlaySteps[_overlayStep];
            if (_ranged == RangedMode.Aim && _drawStart < 0 && (step.stage == "PowerRaise" || step.hold)) _drawStart = Time.time;
            if (_ranged == RangedMode.CastRelease && step.stage == "Hit")
            {
                CastReleased?.Invoke();
                // A listener ended the cast on its release (a teleport puts the spell away at once, fix-114): the Hit must
                // not start on an upper body nobody steps on any more (it froze the arms up after the jump).
                if (_ranged != RangedMode.CastRelease) return;
            }
            if (step.key == null) return;
            _player.PlayOverlay(step.key, fade, restart: true);
            _player.SetOverlaySpeed(_rangedSpeed);
        }

        void UpdateRanged()
        {
            if (_ranged == RangedMode.None || _overlayStep >= _overlaySteps.Count) return;
            var step = _overlaySteps[_overlayStep];
            if (_ranged == RangedMode.Cast && _castReleaseQueued && RaiseOver)
            {
                ReleaseCast(); // the queued release (fix-114): the raise is over, the Hit lets the spell go
                return;
            }
            if (step.hold)
            {
                // Holding the aim: follow the pitch with the Up/Fwd/Down aim motions.
                string dir = AimDirection();
                if (dir != _aimDir && step.action != null)
                {
                    _aimDir = dir;
                    string key = ResolveRanged(step.action, step.stage, step.nocked, dir);
                    if (key != null && key != step.key)
                    {
                        step.key = key;
                        _overlaySteps[_overlayStep] = step;
                        _player.PlayOverlay(key, overlayFade);
                    }
                }
                return;
            }
            if (step.key != null && _player.OverlayKey != step.key && !_player.OverlayFinished)
            {
                // fix-197: another owner took (or a rig rebuild dropped) the upper body: the step can never finish and the
                // reload, raise, shot or cast would keep the hero out of the loop state for good (no weapon key, no
                // sheathe). A reload or shot counts as done (nothing more is used up); a raise is lowered (the arrow stays
                // nocked); a cast is cut (SpellCasterBody sees it: refund before the release, over after it).
                Debug.Log($"[combat] {name}: {_ranged} lost its overlay {step.key} to {_player.OverlayKey ?? "nothing"}: cut short");
                var lost = _ranged;
                if (lost == RangedMode.Reload || lost == RangedMode.Shoot) EndRanged(fadeOverlay: false);
                else
                {
                    StopRanged(fadeOverlay: false); // the overlay now playing is the other owner's
                    if (lost == RangedMode.Aim) RangedEnded?.Invoke("Aim");
                }
                return;
            }
            if (step.key != null && !_player.OverlayFinished) return;
            if (step.nockAfter) SetNocked(true, _pendingAmmo);
            if (_overlayStep + 1 < _overlaySteps.Count)
            {
                _overlayStep++;
                PlayOverlayStep(0.05f);
                return;
            }
            EndRanged();
        }

        void EndRanged(bool fadeOverlay = true)
        {
            var ended = _ranged;
            if (ended == RangedMode.Reload && !Nocked) SetNocked(true, _pendingAmmo);
            StopRanged(fadeOverlay);
            RangedEnded?.Invoke(ended == RangedMode.Reload ? "Reload" : ended == RangedMode.CastRelease ? "Cast" : "Shoot");
        }

        void StopRanged(bool fadeOverlay = true)
        {
            _castReleaseQueued = false;
            if (_ranged == RangedMode.None) return;
            if (_ranged == RangedMode.Reload && !Nocked) _pendingAmmo = null; // interrupted before the arrow was in the hand
            _ranged = RangedMode.None;
            _overlaySteps.Clear();
            if (fadeOverlay) _player.StopOverlay(overlayFade);
        }

        string AimDirection() => AimPitch > aimPitchSplit ? "Up" : AimPitch < -aimPitchSplit ? "Down" : "Fwd";

        string ResolveRanged(string action, string stage, bool nocked, string direction)
        {
            var library = _player.library;
            if (library == null || Rig == null) return null;
            foreach (var variant in new[] { G3CombatMotions.Overlay, G3CombatMotions.Normal })
                foreach (var key in G3CombatMotions.Candidates(Rig, "Stand", Weapon, "P0", action, stage, direction, nocked, variant))
                    if (library.Find(key) != null) return key;
            return null;
        }

        void SetNocked(bool nocked, string ammoTemplate)
        {
            if (Nocked == nocked && AmmoTemplate == ammoTemplate) return;
            Nocked = nocked;
            AmmoTemplate = nocked ? ammoTemplate : null;
            if (WeaponInHand)
            {
                _stance?.SetWeaponStance(Weapon, WeaponTemplate, AmmoTemplate);
                WeaponInHandChanged?.Invoke();
            }
        }

        // ------------------------------------------------------------------------------------------- sequencing

        bool _tailRecover;
        bool _resumeParade; // a parried stumble plays: the parade comes back after it
        float _waitAfterHit;
        float _endAt = -1f;
        /// <summary>The running step's age (real s) and its motion's phase last frame: the safety end (<see cref="CombatStepWatchdog"/>).</summary>
        float _stepSeconds, _stepPhase;

        /// <summary>Every combat animator steps from one batched Update (perf-3, <see cref="G3FrameBatch"/>; order 150 as before).</summary>
        static readonly G3FrameBatch.Batch<G3CombatAnimator> s_batch =
            new G3FrameBatch.Batch<G3CombatAnimator>(G3FrameBatch.Slot.Update150, "G3CombatAnimator", a => a.FrameUpdate());

        void OnEnable() => s_batch.Add(this);
        void OnDisable() => s_batch.Remove(this);

        void FrameUpdate()
        {
            TrackSpeed();
            UpdateRanged();
            if (IsHoldMode(_mode))
            {
                UpdateHold();
                return;
            }
            if (_mode == Mode.Free || _step >= _steps.Count) return;
            if (_mode == Mode.Move && _endAt >= 0f && Time.time >= _endAt)
            {
                // The wait after the Hit is over (G3 _AI_QuickAttack): the move ends, its Recover plays on as a tail.
                _endAt = -1f;
                _mode = Mode.Recover;
                MoveEnded?.Invoke(Action); // a queued move may start right here (Begin resets the tail)
                return;
            }
            var step = _steps[_step];
            if (!step.hold && step.key != null && _player.CurrentKey != step.key)
            {
                // Another owner (a talk gesture, an object use) or a disabled player replaced the step's motion: this
                // sequence can never end by itself and would keep the body Busy for good. It counts as cut short (a draw
                // or sheathe keeps the hand as it is), so the next request starts cleanly.
                Debug.Log($"[combat] {name}: {_mode} {Action} lost its motion {step.key} to {_player.CurrentKey ?? "nothing"}: cut short");
                string cut = _mode == Mode.Move ? Action : null;
                Abort();
                // A cut move is over too: its owner must not wait for it for good (the hero's input stayed busy and
                // only queued every later attack or parade, fix-43).
                if (cut != null) MoveEnded?.Invoke(cut);
                return;
            }
            if (_mode == Mode.Parade && step.hold && _paradeStance != null && !_paradeStance.ParadeLocomotion)
            {
                // The block is up: the locomotion selector takes over in the Parade stance (walking while blocking).
                _paradeStance.ParadeLocomotion = true;
                _player.Release(this);
            }
            if (step.hold || step.key == null || !StepFinished(step)) return;
            if (_step + 1 < _steps.Count)
            {
                _step++;
                PlayStep(_steps[_step]);
                StepSerial++;
                ApplySpeed();
                if (_steps[_step].phase == G3SwingPhase.Hit) SwingCount++;
                if (_mode == Mode.Move && _tailRecover && _steps[_step].phase == G3SwingPhase.Recover && _waitAfterHit > 0f)
                    _endAt = Time.time + _waitAfterHit; // still in the move: it ends after the wait (see above)
                else if (_mode == Mode.Move && _tailRecover && _steps[_step].phase == G3SwingPhase.Recover)
                {
                    // G3: the move is over with its Hit; the Recover plays on until something else starts.
                    string ended = Action;
                    _mode = Mode.Recover;
                    MoveEnded?.Invoke(ended); // a queued move may start right here (Begin resets the tail)
                }
                else if (_mode == Mode.Reaction && _steps[_step].stage == "Recover")
                    ReactionHitOver(); // G3 _AI_*Stumble: Hit only, the Recover is a tail (fix-45)
                return;
            }
            if (_mode == Mode.Recover)
            {
                CutRecover(); // the tail's end: its move was reported already
                return;
            }
            End();
        }

        /// <summary>
        /// The step's motion reached its end, or the safety end cut it (a looping motion's first cycle, or
        /// <see cref="CombatStepWatchdog.MaxStepSeconds"/>; audit S6, logged).
        /// </summary>
        bool StepFinished(Step step)
        {
            if (_player.CurrentFinished) return true;
            _stepSeconds += Time.deltaTime * _player.TimeScale; // frozen (time scale 0) or slowed bodies: motion time (fix-48)
            float phase = _player.CurrentPhase;
            var verdict = CombatStepWatchdog.Check(step.hold, _player.CurrentLoops, _stepPhase, phase, _stepSeconds);
            _stepPhase = phase;
            if (verdict == CombatStepWatchdog.Verdict.Running) return false;
            Debug.LogWarning($"[combat] {name}: {_mode} {Action} step {step.key} never ends ({verdict}, {_stepSeconds:F1} s): safety end");
            return true;
        }

        void End()
        {
            if (_mode == Mode.Reaction && _resumeParade && ResumeParade()) return;
            _endAt = -1f;
            bool freed = _mode == Mode.Reaction || _mode == Mode.Draw;
            if (_mode == Mode.Draw || _mode == Mode.Sheathe) ApplyHand(_hand.Finish());
            if (_mode == Mode.HoldLeft) SwitchLeft();
            EndHoldOverlay(true);
            string action = Action;
            _mode = Mode.Free;
            _steps.Clear();
            Action = null;
            _player.Release(this);
            MoveEnded?.Invoke(action);
            if (freed && _mode == Mode.Free) Freed?.Invoke(action);
        }

        void ApplySpeed()
        {
            if (_step < _steps.Count && _steps[_step].key != null)
                _player.SetSpeed(SpeedOf != null && Action != null ? SpeedOf(Action, _steps[_step].phase) : 1f);
        }

        /// <summary>A parried stumble is over: the held parade again, straight into its loop (G3: the ani state stayed Parade).</summary>
        bool ResumeParade()
        {
            string ended = Action;
            Begin(Mode.Parade, "Parade");
            Add("Parade", "Ambient", "Loop", G3SwingPhase.None, hold: true);
            if (_steps.Count == 0)
            {
                _mode = Mode.Reaction; // no loop in this rig: end the stumble as usual
                Action = ended;
                return false;
            }
            _paradePose = -1; // straight into the loop: the parade never went down
            Run();
            MoveEnded?.Invoke(ended);
            if (_mode == Mode.Parade) Freed?.Invoke(ended);
            return true;
        }

        void Begin(Mode mode, string action)
        {
            _resumeParade = false;
            if (_paradeStance != null) _paradeStance.ParadeLocomotion = false;
            if (mode != Mode.Draw) StopRanged(); // a hit, a fall, a melee move or putting the weapon away ends aiming and reloading
            InterruptHand();
            EndHoldOverlay(false);
            _steps.Clear();
            _step = 0;
            _mode = mode;
            _tailRecover = false;
            _waitAfterHit = 0f;
            _endAt = -1f;
            Action = action;
        }

        bool Abort()
        {
            _resumeParade = false;
            if (_paradeStance != null) _paradeStance.ParadeLocomotion = false;
            InterruptHand();
            EndHoldOverlay(false);
            _steps.Clear();
            _mode = Mode.Free;
            Action = null;
            _player.Release(this);
            return false;
        }

        bool Run(bool cut = false)
        {
            if (_steps.Count == 0)
            {
                Abort();
                return false;
            }
            _player.Claim(this);
            PlayStep(_steps[0], cut);
            StepSerial++;
            ApplySpeed();
            if (_steps[0].phase == G3SwingPhase.Hit) SwingCount++;
            return true;
        }

        /// <summary>
        /// Starts a step's motion with G3's command cross-fade (sAICombatMoveStart / sAICombatMoveStartRecover): from a
        /// running motion min(extro, intro) = 0.2 s, into a <c>_Begin_</c> motion 0.1 s, after a one-shot ended none (the
        /// next phase of a move: Raise → Hit → Recover). <paramref name="cut"/>: no fade at all.
        /// </summary>
        void PlayStep(Step step, bool cut = false)
        {
            _stepSeconds = 0f;
            _stepPhase = 0f;
            if (step.key == null) return;
            float fade = cut ? 0f : _player.FadeFor(MotionStarter.Command, step.key);
            _player.Play(step.key, fade, restart: true);
            StepStarted?.Invoke(step.key, step.phase, fade);
        }

        void Add(string stance, string action, string phase, G3SwingPhase swing, string pose = "P0", string direction = "Fwd", bool hold = false)
        {
            string key = Resolve(stance, action, phase, pose, direction);
            if (key != null) _steps.Add(new Step { key = key, phase = swing, hold = hold, stage = phase });
        }

        bool HasStep(G3SwingPhase phase) => _steps.Exists(s => s.phase == phase);

        string Resolve(string stance, string action, string phase, string pose, string direction)
        {
            var library = _player.library;
            if (library == null || Rig == null) return null;
            foreach (var key in G3CombatMotions.Candidates(Rig, stance, Weapon, pose, action, phase, direction, Nocked, offHand: OffHand))
                if (library.Find(key) != null) return key;
            return null;
        }

        // ------------------------------------------------------------------------------------------- draw / sheathe

        // G3 _AI_HoldInventoryItems_Base (G3.Player.HoldMotionRules): two motions around HoldStacks. Draw: Begin forwards
        // (reach for the weapon), the hand changes, End forwards (bring it to the front). Sheathe: End backwards, the hand
        // changes, Begin backwards. Each motion is the overlay variant (legs keep walking) when the body moves as it
        // starts, else the full-body one (the body stands: HoldsBody).
        HoldPart[] _holdParts;
        string[] _holdActions;
        string _holdOffHand, _holdKey, _holdWeapon;
        int _holdIndex = -1;
        bool _holdOverlay, _holdOverlayUsed;
        Vector3 _lastPos;
        bool _hasLastPos;

        static bool IsHoldMode(Mode m) => m == Mode.Draw || m == Mode.Sheathe || m == Mode.HoldLeft;

        /// <summary>
        /// Planar speed of the body, m/s (measured from its movement; picks the overlay or full-body draw motions, G3
        /// FUN_100031a0: the velocity is not zero).
        /// </summary>
        public float PlanarSpeed { get; private set; }

        /// <summary>A full-body draw or sheathe motion plays: the body stands (G3 moves a body by its motion; walking waits).</summary>
        public bool HoldsBody => IsHoldMode(_mode) && _holdKey != null && !_holdOverlay;

        /// <summary>The draw or sheathe motion playing (key, reversed, overlay) for logs; null when none.</summary>
        public string HoldText => IsHoldMode(_mode) && _holdKey != null
            ? $"{_holdKey}{(_holdParts[_holdIndex].Reverse ? " reversed" : "")}{(_holdOverlay ? " (overlay)" : "")}" : null;

        Transform _transform;

        void TrackSpeed()
        {
            var p = (_transform ??= transform).position;
            float dt = G3FrameBatch.DeltaTime;
            if (_hasLastPos && dt > 0f) PlanarSpeed = new Vector2(p.x - _lastPos.x, p.z - _lastPos.z).magnitude / dt;
            _lastPos = p;
            _hasLastPos = true;
        }

        void StartHold(bool draw, string[] actions, string offHand)
        {
            _holdParts = HoldMotionRules.Parts(draw);
            _holdActions = actions;
            _holdOffHand = offHand;
            // G3 _AI_HoldInventoryItems_Base reads the hands' use types before HoldStacks and, when both new stacks are
            // empty (a put-away), builds the second motion with them: the sheathe's Begin-reversed is the weapon's set, not
            // the empty hand's (fix-46: r28 drawtrace played Hero_Stand_None_Fist_P0_HoldRight_Begin for a 1H sheathe).
            // A draw's set is the new weapon's from the start.
            _holdWeapon = Weapon;
            _holdIndex = -1;
            _holdKey = null;
            _holdOverlay = _holdOverlayUsed = false;
            NextHoldPart();
        }

        /// <summary>Starts the next motion of the draw or sheathe (a missing one is skipped, its hand change still happens); ends the command after the last.</summary>
        void NextHoldPart()
        {
            while (++_holdIndex < _holdParts.Length)
            {
                var part = _holdParts[_holdIndex];
                bool wantOverlay = HoldMotionRules.UseOverlay(PlanarSpeed);
                string key = wantOverlay ? ResolveHold(part.Word, G3CombatMotions.Overlay) : null;
                bool overlay = key != null;
                key ??= ResolveHold(part.Word, G3CombatMotions.Normal);
                if (key == null)
                {
                    if (part.SwitchAfter) SwitchHold();
                    continue;
                }
                _holdKey = key;
                float fade;
                if (overlay)
                {
                    // The legs keep the locomotion: the body is not claimed.
                    fade = HoldMotionRules.OverlayIntro(part.Phase, part.Reverse);
                    _player.Release(this);
                    _player.PlayOverlay(key, fade, restart: true, reverse: part.Reverse);
                    _player.SetOverlaySpeed(1f);
                    _holdOverlayUsed = true;
                }
                else
                {
                    if (_holdOverlay) _player.StopOverlay(overlayFade); // the body stopped between the two motions
                    _player.Claim(this);
                    fade = _player.FadeFor(MotionStarter.Command, key);
                    _player.Play(key, fade, restart: true, reverse: part.Reverse);
                    _player.SetSpeed(1f);
                }
                _holdOverlay = overlay;
                StepSerial++;
                StepStarted?.Invoke(key, G3SwingPhase.None, fade);
                return;
            }
            _holdKey = null;
            End();
        }

        void UpdateHold()
        {
            if (_holdKey == null) return;
            bool finished;
            if (_holdOverlay)
            {
                if (_player.OverlayKey != _holdKey)
                {
                    Debug.Log($"[combat] {name}: {_mode} lost its overlay {_holdKey} to {_player.OverlayKey ?? "nothing"}: cut short");
                    Abort();
                    return;
                }
                finished = _player.OverlayFinished;
            }
            else
            {
                if (_player.CurrentKey != _holdKey)
                {
                    // Another owner (a talk gesture, an object use) replaced the motion: the command counts as cut short
                    // (the hand keeps what it holds), so the next request starts cleanly.
                    Debug.Log($"[combat] {name}: {_mode} {Action} lost its motion {_holdKey} to {_player.CurrentKey ?? "nothing"}: cut short");
                    Abort();
                    return;
                }
                finished = _player.CurrentFinished;
            }
            if (!finished) return;
            if (_holdParts[_holdIndex].SwitchAfter) SwitchHold();
            NextHoldPart();
        }

        /// <summary>G3 HoldStacks: the items change hands (between the two motions).</summary>
        void SwitchHold()
        {
            if (_mode == Mode.HoldLeft) SwitchLeft();
            else ApplyHand(_hand.SwitchHands());
        }

        /// <summary>Fades out the overlay a draw or sheathe used (<paramref name="ended"/>: its own end, G3 extro 0.1 s).</summary>
        void EndHoldOverlay(bool ended)
        {
            if (!IsHoldMode(_mode) || !_holdOverlayUsed) return;
            _holdOverlayUsed = false;
            if (_holdOverlay || !ended) _player.StopOverlay(ended ? HoldMotionRules.OverlayEdgeFade : overlayFade);
            _holdOverlay = false;
            _holdKey = null;
        }

        string ResolveHold(string phase, string variant)
        {
            var library = _player.library;
            if (library == null || Rig == null) return null;
            foreach (var action in _holdActions)
                foreach (var key in G3CombatMotions.Candidates(Rig, "Stand", _holdWeapon ?? Weapon, "P0", action, phase, "Fwd", Nocked, variant, _holdOffHand))
                    if (library.Find(key) != null) return key;
            return null;
        }


        /// <summary>A draw or sheathe cut short by another motion: the hand keeps what it holds (<see cref="WeaponHand.Interrupt"/>).</summary>
        void InterruptHand()
        {
            if (_mode == Mode.Draw || _mode == Mode.Sheathe) _hand.Interrupt();
        }

        /// <summary>The hand changed (<see cref="WeaponHand"/>): the stance and hand props follow, listeners hear it.</summary>
        void ApplyHand(bool changed)
        {
            if (!changed) return;
            bool inHand = _hand.InHand;
            _stance?.SetWeaponStance(inHand ? _hand.Field : null, inHand ? _hand.Template : null, inHand ? AmmoTemplate : null);
            WeaponInHandChanged?.Invoke();
        }
    }

    /// <summary>A moment of a combat animator that makes a sound (<see cref="G3CombatAnimator.Cue"/>).</summary>
    public enum G3CombatCue { WeaponDrawn, WeaponSheathed, AimRaised, ShotReleased, LeftDrawn, LeftSheathed }
}
