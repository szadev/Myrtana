using System;
using System.Collections.Generic;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// The player's melee adapter (Docs/combat.md §1.1, §9): Input System buttons → <see cref="MeleeInputInterpreter"/> →
    /// moves started through <see cref="CombatService.StartMove"/> (stamina) on the hero's <see cref="CombatFighter"/>,
    /// whose ends go back to the interpreter. Also the per-second player stamina tick, the combat focus (G3 soft focus,
    /// re-picked every frame; attacks, casts and the held block align the body to it while the camera stays free, §9.2),
    /// the knock-out (controls locked until the hero gets up) and game over on death, and the jump's stamina (10 SP).
    /// Keys (G3 session keys, <see cref="G3Input"/>): WeaponMode (Space / middle mouse) draws the last weapon kind
    /// (G3 LastWeaponConfig: melee, bow or spell) or holsters; a quick slot (1…0) holding a weapon, bow or spell readies it
    /// (<see cref="TryReady"/>, G3 PS_Normal → PS_Melee/PS_Ranged/PS_Magic); Use1 (left mouse) = primary (attack; with a bow: hold
    /// to draw, release to shoot; with a spell: hold to load and power up, release to cast), Use2 (right mouse) = secondary
    /// (parade; with a bow: lower the aim; with a spell: drop the loaded spell). Magic (PS_Magic, Docs/combat.md §12):
    /// <see cref="SpellCasterBody"/>; the player's pre-check fizzles without mana or focus. Ranged (PS_Ranged, Docs/combat.md §2): drawing nocks the ammunition (the equipped stack, else
    /// the auto-select order), the draw time gives the HitMultiplier, the shot flies from the bow towards the point under
    /// the screen centre, one arrow or bolt is used up; the last one holsters the weapon. Heavy hits on the hero shake the
    /// camera. Tests drive the same public methods.
    /// </summary>
    [DefaultExecutionOrder(-50)] // before G3Player (move lock, body yaw of the alignment)
    [DisallowMultipleComponent]
    public sealed partial class PlayerCombat : MonoBehaviour, ISessionView, IQuickSlotReadiness, ISessionControlHolder
    {
        enum Readied { Melee, Ranged, Spell }

        [Tooltip("Camera shake (m) on a stumble; knock-downs shake twice as much [interp]")] public float hitShake = 0.08f;

        G3Player _player;
        G3PlayerCamera _camera;
        CharacterController _cc;
        HeroControlLock _controls;
        CombatPresenter _presenter;
        CombatFighter _fighter;
        ItemVisualCatalog _catalog;
        GameOverView _gameOver;
        MeleeInputInterpreter _input = new MeleeInputInterpreter { Trace = CombatInputLog.Line };
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        Transform _slot, _leftSlot;
        GameObject _prop, _leftProp;
        readonly Carried _carriedMelee = new Carried(), _carriedRanged = new Carried(), _carriedShield = new Carried(), _carriedOffHand = new Carried();
        // G3 dual wielding (fix-32): drawing the main one-handed weapon draws the off-hand one too (OnPlayerSelectWeapon takes
        // BackLeft and BackRight); the off-hand weapon's own quick-slot key puts the left blade away (false) or takes it again.
        bool _leftWanted = true;
        G3HeroAnimator _hero;
        int _combo;
        int _comboStage; // the combo stage of the last started move (combatlog COMBO lines)
        /// <summary>The combo stage of the last started move (1 = a fresh start), and that move (a quick attack's side).</summary>
        public int ComboStage => _comboStage;
        public CombatAction LastStartedMove { get; private set; }
        /// <summary>Counts the melee moves started (dev scenarios: a new value = a new stage).</summary>
        public int MovesStarted { get; private set; }
        float _secondCarry;
        bool _knockedOut, _dead, _aimWanted;
        // G3 OnPlayerAction keeps one queued action per task (PS_Ranged): a press during or right after the draw queues the
        // aim (_aimQueued), a release before the aim started turns it into a shot (_shotQueued), fired once the aim is up.
        bool _aimQueued, _shotQueued;
        // fix-209: a stumble cut the aim while the shoot button was held (HitReactionRules.KeepsAimIntent); cleared when the
        // bow is raised again or the queue is cleared
        bool _aimCutByHit;
        // G3 alignment (Docs/combat.md §9.2): while aligned the body turns to _alignTarget (null: holds its yaw).
        bool _aligned;
        CombatFighter _alignTarget;
        readonly List<FocusCandidate> _candidates = new List<FocusCandidate>();
        readonly List<CombatFighter> _candidateFighters = new List<CombatFighter>();
        /// <summary>
        /// The hero's PSNpc CurrentTarget as GetFocusEntityPriority reads it (fix-90): PS_Melee_Loop and PS_Magic_Loop set it
        /// to the focus every tick, the PS_Ranged states never do, so while a bow is drawn it keeps the target from before.
        /// </summary>
        CombatFighter _heroTarget;
        readonly FocusLineOfSight _sight = new FocusLineOfSight();
        Readied _last;
        // Mouse button event times (input clock): the melee input is timed by the presses and releases themselves, not by
        // the frame that reads them (InputEventTime).
        InputAction _lmbStamp, _rmbStamp;
        G3.Player.KeyInput _use1Input, _use2Input; // the inputs the stamps listen to (the first Use1/Use2 binding)
        double _lmbDownAt, _lmbUpAt, _rmbDownAt, _rmbUpAt;
        string _use1Pad, _use2Pad;
        string _nockedAmmo, _spell;
        SpellCasterBody _magic;
        Func<bool> _pendingDraw;
        /// <summary>
        /// fix-101: a weapon or quick-slot key that came while the body was not in a G3 loop state (a move, the draw, the
        /// reload, the shot, the aim's raise, a cast) waits here for it (G3's one-slot action queue, read by the PS_*_Loop
        /// states only; <see cref="G3.Player.WeaponSwitchInput"/>). Before, such a key was refused and lost.
        /// </summary>
        readonly G3.Player.WeaponSwitchQueue<SwitchKey> _switchKey = new G3.Player.WeaponSwitchQueue<SwitchKey>();

        sealed class SwitchKey
        {
            public string Name;
            public Func<bool> Act;
        }
        HeroFallDamage _falls;
        IG3ParadeStance _paradeStance;
        HeroFormView _forms;

        GameSession Session => _presenter != null ? _presenter.Session : null;
        public CombatFighter Fighter => _fighter;

        /// <summary>
        /// The mouse buttons belong to the stance: a weapon, bow, spell or the fists are drawn, or a draw is under way (a press
        /// then still counts, see <see cref="ReadKeys"/>). Otherwise the hero is in G3's PS_Normal and the left button uses the
        /// focus (<see cref="Myrtana.Rules.HeroUseInput"/>).
        /// </summary>
        public bool StanceActive => _fighter != null && _fighter.Animator != null && (_fighter.Animator.Drawn || _pendingDraw != null);

        /// <summary>
        /// fix-226 [owner]: asked on a Use1 press in a drawn stance (<c>PlayerInteraction.StanceChord</c>): true when the press
        /// took everything from a body in front with the quick-loot modifier held; that press and its release then start no
        /// attack, shot or cast.
        /// </summary>
        public System.Func<bool> TakeAllChord { get; set; }

        bool _chordPress;
        /// <summary>The combat focus (G3 soft focus, picked anew every frame; Docs/combat.md §9.2), or null.</summary>
        public CombatFighter Focus { get; private set; }
        /// <summary>The focus is an opponent (fights the hero, or hostile/angry/predator towards him): the HUD shows it hostile.</summary>
        public bool FocusIsOpponent => Focus != null && Session != null && IsEnemy(Focus);
        public bool IsDead => _dead;
        /// <summary>The game-over overlay (G3 Kill → GO_GameOver, "Конец"/"The End") is up (dev probe).</summary>
        public bool GameOverShown => _gameOver != null && _gameOver.Shown;
        /// <summary>Sneaking would use the Parade stance: a melee weapon or the fists are drawn (G3 PS_Melee_Sneak; fix-28).</summary>
        public bool SneakInParade { get; private set; }
        /// <summary>The hero sneaks in the Parade stance now (vanilla rules: this blocks like a raised parade).</summary>
        public bool SneakingInParade => SneakInParade && _player != null && _player.Sneaking;
        /// <summary>The hero sneaks (G3Player toggle; the dev scenario op <c>sneak</c>).</summary>
        public bool Sneaking => _player != null && _player.Sneaking;
        /// <summary>Turns sneaking on or off as the sneak key would (no effect while blocking, swimming or in the air).</summary>
        public void SetSneaking(bool on)
        {
            if (_player == null || on && (_player.paradeWalk || _player.Swimming)) return;
            _player.SetSneaking(on);
        }
        /// <summary>The spell item in the spell hand (or the last one readied).</summary>
        public string ReadiedSpell => _spell;
        public SpellCasterBody Magic => _magic;

        public void Init(CombatPresenter presenter, HeroControlLock controls, ItemVisualCatalog catalog, GameOverView gameOver)
        {
            _presenter = presenter;
            _controls = controls;
            _catalog = catalog;
            _gameOver = gameOver;
            _player = GetComponent<G3Player>();
            _cc = GetComponent<CharacterController>();
            _camera = _player.playerCamera != null ? _player.playerCamera.GetComponent<G3PlayerCamera>() : null;
            var hero = GetComponentInChildren<G3HeroAnimator>(true);
            _fighter = gameObject.AddComponent<CombatFighter>();
            _fighter.Init(ActorId.Player, hero != null ? hero.transform : transform, humanoid: true, reach: 0.6f, Displace);
            _fighter.Moving = () => _player.Velocity.sqrMagnitude > 0.25f;
            _fighter.Turner = yaw => _player.bodyYaw = yaw; // finishing placement (§5.5) and combat-move turns (§5.6)
            _fighter.MoverCollides = true; // the CharacterController stops at walls
            _fighter.MoveEnded += OnMoveEnded;
            _fighter.SatDown += OnSatDown;
            _fighter.ReactionStarting += OnReactionStarting;
            _fighter.Freed += OnFreed;
            _fighter.Animator.WeaponInHandChanged += ShowWeapon;
            _slot = FindDeep(_fighter.Visual, "Slot_RightHand_Weapon");
            _leftSlot = FindDeep(_fighter.Visual, "Slot_LeftHand_Weapon");
            _magic = new SpellCasterBody(presenter, _fighter);
            _magic.Finished += OnCastFinished;
            _fighter.Animator.WeaponInHandChanged += ShowSpell;
            _fighter.Animator.StepStarted += OnStepStarted;
            _fighter.Animator.LeftHandSwitched += OnLeftHandSwitched;
            _fighter.Animator.MoveEnded += OnAnimatorMoveEnded;
            _fighter.Animator.RangedEnded += OnAnimatorRangedEnded;
            presenter.PlayerFell += OnFell;
            presenter.PlayerHit += OnHit;
            _player.WaterChanged += OnWaterChanged;
            _player.DivedIn += OnDivedIn;
            _player.Jumped += OnJumped;
            _falls = new HeroFallDamage(_player, hero, () => Session, () => _dead);
            _paradeStance = hero;
            _hero = hero;
            _forms = gameObject.AddComponent<HeroFormView>();
            _forms.Init(this, controls, presenter); // animal forms, soul travel, Dusk to Dawn (magic-3)
        }

        public void Bind(GameSession session)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            _input = new MeleeInputInterpreter(MeleeInputTiming.For(session.Settings.Rules)) { Trace = CombatInputLog.Line };
            _subscriptions.Add(session.Events.Subscribe<ActorGotUp>(e => { if (e.Actor.IsPlayer) OnGotUp(); }));
            _subscriptions.Add(session.Events.Subscribe<TeleportRequested>(_ => PutAwayAtOnce()));
            _subscriptions.Add(session.Events.Subscribe<EquipmentChanged>(OnEquipmentChanged));
            if (_knockedOut || _dead) _controls.Release(this);
            _knockedOut = _dead = false;
            _gameOver?.Hide();
            SetFocus(null);
            ReleaseAlignment();
            _player.bodyYaw = null;
            _pendingDraw = null;
            _switchKey.Clear();
            ClearQueue();
            _magic.Abort();
            _magic.HideHeld();
            _presenter.Register(_fighter);
            _fighter.ResetState();
            _forms.Bind(session);
        }

        void OnEnable()
        {
            RebuildStamps();
            _lmbStamp?.Enable();
            _rmbStamp?.Enable();
        }

        void OnDisable()
        {
            _lmbStamp?.Disable();
            _rmbStamp?.Disable();
            if (_player != null) { _player.forceWalk = false; _player.holdGait = false; _player.moveState = G3.Player.HeroMoveState.Normal; _player.sprintStamina = int.MaxValue; }
        }

        /// <summary>
        /// The event stamps follow the session keys Use1/Use2 (G3 default left/right mouse button; rebindable in the
        /// Controls menu): rebuilt when their first binding changes.
        /// </summary>
        void RebuildStamps()
        {
            var use1 = FirstButton(G3.Player.SessionKey.Use1);
            var use2 = FirstButton(G3.Player.SessionKey.Use2);
            string pad1 = G3Input.PadControlPath(G3.Player.SessionKey.Use1), pad2 = G3Input.PadControlPath(G3.Player.SessionKey.Use2);
            if (_lmbStamp != null && use1 == _use1Input && _rmbStamp != null && use2 == _use2Input && pad1 == _use1Pad && pad2 == _use2Pad) return;
            _lmbStamp?.Dispose();
            _rmbStamp?.Dispose();
            _use1Input = use1;
            _use2Input = use2;
            _use1Pad = pad1;
            _use2Pad = pad2;
            _lmbStamp = Stamp(G3Input.ControlPath(use1) ?? "<Mouse>/leftButton", pad1, (down, t) => { if (down) _lmbDownAt = t; else _lmbUpAt = t; });
            _rmbStamp = Stamp(G3Input.ControlPath(use2) ?? "<Mouse>/rightButton", pad2, (down, t) => { if (down) _rmbDownAt = t; else _rmbUpAt = t; });
            if (isActiveAndEnabled)
            {
                _lmbStamp.Enable();
                _rmbStamp.Enable();
            }
        }

        static G3.Player.KeyInput FirstButton(G3.Player.SessionKey key)
        {
            foreach (var input in G3Input.Keys.Inputs(key))
                if (G3Input.ControlPath(input) != null) return input;
            return G3.Player.KeyInput.None;
        }

        /// <summary>
        /// Stamps the press and release times of a button (and of its pad button, gamepad-1). Edges only: an analog
        /// trigger reports every change of its travel, the stamp is the crossing of the press point.
        /// </summary>
        static InputAction Stamp(string binding, string padBinding, Action<bool, double> stamp)
        {
            var action = new InputAction(binding, InputActionType.PassThrough, binding);
            if (padBinding != null) action.AddBinding(padBinding);
            var held = new System.Collections.Generic.Dictionary<InputControl, bool>();
            void Edge(InputControl control, bool down, double time)
            {
                held.TryGetValue(control, out bool was);
                if (was == down) return;
                held[control] = down;
                stamp(down, time);
            }
            // buttons and mouse buttons read 0 or 1; a trigger is down at the pad option's press point
            action.performed += c => Edge(c.control, c.ReadValue<float>() >= G3Input.Options.Pad.TriggerThreshold, c.time);
            action.canceled += c => Edge(c.control, false, c.time);
            return action;
        }

        /// <summary>The game time of a mouse button event read this frame (now when it has no stamp).</summary>
        double EventTime(double inputTime) =>
            InputEventTime.ToGameTime(Now, UnityEngine.InputSystem.LowLevel.InputState.currentTime, inputTime, Time.timeScale, Now - Time.deltaTime);

        void OnDestroy()
        {
            _lmbStamp?.Dispose();
            _rmbStamp?.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
            if (_presenter != null)
            {
                _presenter.PlayerFell -= OnFell;
                _presenter.PlayerHit -= OnHit;
            }
            if (_player != null)
            {
                _player.WaterChanged -= OnWaterChanged;
                _player.DivedIn -= OnDivedIn;
                _player.Jumped -= OnJumped;
            }
            _falls?.Dispose();
        }

        // ------------------------------------------------------------------------------------------- commands (keys and tests)

        /// <summary>
        /// The weapon key (G3 OnPlayerSelectWeapon): draws the last weapon kind (melee, ranged or the last spell) or puts the
        /// drawn weapon away; while the body is not in a loop state the key waits for it (<see cref="OfferSwitch"/>).
        /// </summary>
        public bool ToggleWeapon()
        {
            if (FormLocked) return false;
            return OfferSwitch("weapon key", ToggleNow);
        }

        bool ToggleNow()
        {
            ClearQueue();
            if (_fighter.Animator.Drawn) return Holster();
            return _last == Readied.Ranged && DrawRanged() || _last == Readied.Spell && ReadySpell(_spell) || DrawMelee();
        }

        /// <summary>A weapon or quick-slot key waits for the body's next loop state (dev: the switch trace, the log).</summary>
        public string WaitingWeaponKey => _switchKey.Waiting?.Name;

        /// <summary>The body's state as G3's weapon keys see it (<see cref="G3.Player.WeaponSwitchInput"/>).</summary>
        G3.Player.WeaponSwitchState SwitchState()
        {
            var a = _fighter.Animator;
            return new G3.Player.WeaponSwitchState
            {
                MoveRuns = a.Busy, SwitchRuns = _pendingDraw != null, Reload = a.InReload, Shot = a.InShot,
                AimRaising = a.InAim && !a.AimHeld, AimHeld = a.AimHeld, Cast = a.InCast,
            };
        }

        /// <summary>
        /// fix-101 (owner: "bow → sword reacts slowly"): a weapon or quick-slot key acts now in a loop state; otherwise it
        /// waits in G3's one-slot queue (the latest key wins, a button press or a stumble clears it) and acts in the first
        /// frame the body is back in a loop state (<see cref="RunWaitingSwitch"/>). True = taken (now or waiting).
        /// </summary>
        bool OfferSwitch(string name, Func<bool> act)
        {
            var verdict = _switchKey.Offer(new SwitchKey { Name = name, Act = act }, SwitchState(), Now);
            if (verdict == G3.Player.WeaponSwitchVerdict.Wait)
            {
                CombatInputLog.Line($"{name}: waits for the loop state ({_fighter.Animator.StateText}{(_pendingDraw != null ? ", a switch runs" : "")})");
                return true;
            }
            return ActSwitch(verdict, act);
        }

        bool ActSwitch(G3.Player.WeaponSwitchVerdict verdict, Func<bool> act)
        {
            if (verdict == G3.Player.WeaponSwitchVerdict.EndAimThenNow)
            {
                // PS_Ranged_Aim_Loop takes the key: the new task's _AI_HoldInventoryItems puts bow and arrow away from the aim
                // pose (no lowering motion, no shot).
                _fighter.Animator.LowerAim();
                _aimWanted = false;
                ClearQueue();
            }
            return act();
        }

        /// <summary>The waiting weapon key acts once the body is in a loop state (called each frame after the ranged update).</summary>
        void RunWaitingSwitch()
        {
            if (_switchKey.Waiting != null && (FormLocked || _dead || _knockedOut))
            {
                DropWaitingSwitch(_dead || _knockedOut ? "the hero is down" : "the hero's form changed");
                return;
            }
            var key = _switchKey.Poll(SwitchState(), out var verdict);
            if (key == null) return;
            double waited = Now - _switchKey.Since;
            bool ok = ActSwitch(verdict, key.Act);
            CombatInputLog.Line($"{key.Name}: acts after {waited * 1000:F0} ms waiting ({(ok ? "started" : "refused: " + (DrawRefusal() ?? "-"))})");
        }

        /// <summary>
        /// The second half of a switch: the draw waiting for the put-away starts once the hands are empty. G3 runs it in the
        /// same script tick (the next instruction of _AI_HoldInventoryItems); fix-101: it starts at the sheathe's end
        /// (<see cref="OnAnimatorMoveEnded"/>), not one frame later in the next Update.
        /// </summary>
        void RunPendingDraw()
        {
            var a = _fighter.Animator;
            if (_pendingDraw == null) return;
            var step = G3.Player.PendingDrawRules.Decide(a.Busy, a.Drawn);
            if (step == G3.Player.PendingDrawStep.Wait) return;
            var draw = _pendingDraw;
            _pendingDraw = null;
            if (step == G3.Player.PendingDrawStep.Drop)
            {
                // fix-197: a stumble, a fall or another owner cut the put-away before the weapon left the hand: the switch
                // is over (G3: the ZS_* task replaced PS_*). Kept waiting, it held every later weapon key for good.
                CombatInputLog.Line($"switch dropped: the put-away was cut short ({a.StateText})");
                return;
            }
            draw();
        }

        void OnAnimatorMoveEnded(string action)
        {
            if (action == "Sheathe" && _pendingDraw != null) RunPendingDraw();
            ActWaitingSwitchAtOnce();
        }

        void OnAnimatorRangedEnded(string what) => ActWaitingSwitchAtOnce();

        /// <summary>
        /// The state the waiting key waited for ended in the animator (a move, the draw, the reload, the shot): it acts in
        /// this frame, as G3's loop state reads the queue in the tick its state returns. A bow without an arrow nocks first
        /// (G3 PS_Ranged → PS_Ranged_Reload, then the loop): that waits for the reload (Update starts it).
        /// </summary>
        void ActWaitingSwitchAtOnce()
        {
            if (_switchKey.Waiting == null) return;
            var a = _fighter.Animator;
            if (_fighter.RangedDrawn && a.WeaponInHand && !a.Nocked) return;
            RunWaitingSwitch();
        }

        /// <summary>A button press took G3's action slot, or ResetAll: a waiting weapon key is dropped.</summary>
        void DropWaitingSwitch(string why)
        {
            var name = _switchKey.Waiting?.Name;
            if (_switchKey.Clear()) CombatInputLog.Line($"{name}: dropped ({why})");
        }

        /// <summary>
        /// A quick-slot key (G3: a weapon or spell in a quick slot is readied): a melee weapon, bow or crossbow is put on if
        /// needed and drawn, a spell is taken into the spell hand; the one already in the hand is put away. False for items
        /// that are not weapons or spells (the inventory's use handles those) and for weapons that cannot be put on.
        /// </summary>
        public bool TryReady(string item)
        {
            if (Session == null || string.IsNullOrEmpty(item) || FormLocked) return false;
            bool spell = Session.Magic.IsCastItem(item);
            if (!spell && Session.Magic.IsSpellItem(item)) return false; // fix-193: a spell potion is drunk (HeroItemUse), not cast
            if (!spell)
            {
                if (!Session.Content.Database.TryGet(item, out ItemDef weapon)) return false;
                if (weapon.Slot != EquipSlot.MeleeWeapon && weapon.Slot != EquipSlot.RangedWeapon) return false;
                // a weapon that cannot be put on is refused at once (the quick slot's message), not after the wait
                var equipment = Session.Items.Equipment;
                if (!equipment.IsWorn(item) && equipment.Check(item, out _) != EquipResult.Done) return false;
            }
            return OfferSwitch("quick slot " + item, () => ReadyNow(item));
        }

        /// <summary>The quick-slot key acts (G3 OnPlayerUseQuickSlot in a loop state).</summary>
        bool ReadyNow(string item)
        {
            ClearQueue();
            if (Session.Magic.IsCastItem(item))
            {
                if (_fighter.SpellDrawn && _spell == item) return Holster();
                return ReadySpell(item);
            }
            if (!Session.Content.Database.TryGet(item, out ItemDef def)) return false;
            bool melee = def.Slot == EquipSlot.MeleeWeapon, ranged = def.Slot == EquipSlot.RangedWeapon;
            if (!melee && !ranged) return false;
            if (melee && TryStaffBesideSpell(item, def)) return true;
            var equipment = Session.Items.Equipment;
            if (melee && def.Kind == DualWieldRules.OneHandedKind && TryReadyOneHanded(item, out bool done)) return done;
            if (equipment.IsWorn(item) && _fighter.Animator.Drawn && _fighter.WeaponItem?.Id == item) return Holster();
            if (!equipment.IsWorn(item) && equipment.Equip(item) != EquipResult.Done) return false;
            return ReadyAfterHolster(melee ? (Func<bool>)DrawMelee : DrawRanged);
        }

        /// <summary>
        /// A one-handed weapon's quick-slot key with G3's dual-wield hand choice (OnPlayerUseQuickSlot → FUN_10002550 case 2,
        /// <see cref="DualWieldRules.QuickSlot"/>): with Perk_1H1H_1 and a one-handed main weapon another one goes into the
        /// left hand (drawn beside a drawn main one with HoldLeft, else both drawn), the off-hand weapon's key puts the left
        /// blade away or takes it again, the main weapon's key draws both or holsters. False = not a dual-wield case
        /// (<paramref name="done"/> unused): the ordinary equip-and-draw follows.
        /// </summary>
        bool TryReadyOneHanded(string item, out bool done)
        {
            done = false;
            var equipment = Session.Items.Equipment;
            var a = _fighter.Animator;
            string main = equipment.In(WornSlot.MeleeWeapon), off = equipment.In(WornSlot.OffHand);
            string mainKind = main != null && Session.Content.Database.TryGet(main, out ItemDef mainDef) ? mainDef.Kind : null;
            bool mainDrawn = a.Drawn && a.WeaponInHand && !_fighter.RangedDrawn && !_fighter.SpellDrawn && _fighter.WeaponItem?.Id == main &&
                             _fighter.WeaponClass == WeaponClass.OneHanded;
            bool isMain = item == main, isOffHand = item == off && !isMain;
            var use = DualWieldRules.QuickSlot(isOffHand, isMain, _fighter.DualWielding, mainDrawn, Session.Hero.Knows(DualWieldRules.Perk),
                                               mainKind, off != null);
            CombatInputLog.Line($"quick slot {item}: {use} (main {main ?? "-"}, off-hand {off ?? "-"}, left held {_fighter.DualWielding})");
            switch (use)
            {
                case OneHandedUse.PutAwayLeft:
                    done = a.HoldLeft(false);
                    return true;
                case OneHandedUse.DrawLeft:
                    _leftWanted = false; // the new left blade stays on the hip until HoldLeft brings it into the hand
                    if (!isOffHand && equipment.EquipOffHand(item) != EquipResult.Done) return true;
                    done = a.HoldLeft(true);
                    return true;
                case OneHandedUse.Holster:
                    done = Holster();
                    return true;
                case OneHandedUse.DrawBoth:
                    if (!isOffHand && !isMain && equipment.EquipOffHand(item) != EquipResult.Done) return true;
                    _leftWanted = true;
                    if (a.Drawn && !_fighter.RangedDrawn && !_fighter.SpellDrawn && _fighter.WeaponItem?.Id == main) return true; // already drawing
                    done = ReadyAfterHolster(DrawMelee);
                    return true;
                default:
                    // The off-hand weapon used beside a main weapon that is not one-handed: it becomes the main one [interp].
                    if (isOffHand) equipment.Unequip(WornSlot.OffHand);
                    return false;
            }
        }

        /// <summary>The left blade reached or left the hand (<see cref="G3CombatAnimator.HoldLeft"/>, between its two motions).</summary>
        void OnLeftHandSwitched(bool inHand)
        {
            _leftWanted = inHand;
            CombatInputLog.Line($"left blade {(inHand ? "drawn" : "put away")}");
            if (Session != null) ShowCarried();
        }

        /// <summary>Takes a spell into the spell hand (PS_Magic); a drawn weapon is put away first.</summary>
        public bool ReadySpell(string item)
        {
            if (Session == null || item == null || FormLocked || !Session.Magic.TryGetSpell(item, out _)) return false;
            if (!Session.Items.Inventories.Of(ActorId.Player).Has(item)) return false;
            if (!Session.HeroSpells.CanReady(item)) return false; // G3 OnPlayerUseQuickSlot/UseStack: spell books need HasLearnedSpell
            _spell = item;
            _last = Readied.Spell;
            // A teleport stone is held as the stone: switching to or from one puts the hand away and takes the new item up.
            if (_fighter.SpellDrawn && string.Equals(_fighter.Animator.WeaponTemplate ?? "", HeldStoneMesh() ?? "", StringComparison.OrdinalIgnoreCase))
            {
                ShowSpell();
                return true;
            }
            PlanStaffForSpell();
            return ReadyAfterHolster(() => CanDraw() && DrawSpellWithStaff());
        }

        /// <summary>Draws now, or once the weapon in the hand is put away.</summary>
        bool ReadyAfterHolster(Func<bool> draw)
        {
            var a = _fighter.Animator;
            if (a.Drawn && !Holster()) return false;
            if (!a.Busy) return draw();
            _pendingDraw = draw;
            return true;
        }

        /// <summary>Draws the equipped melee weapon (fists without one); a drawn bow is put away first.</summary>
        public bool DrawMelee()
        {
            string why = DrawRefusal();
            if (why == null && _fighter.Animator.Drawn)
            {
                if (_fighter.RangedDrawn || _fighter.SpellDrawn) return ReadyAfterHolster(DrawMelee);
                why = "a melee weapon is already drawn";
            }
            if (why == null)
            {
                var item = Equipped(WornSlot.MeleeWeapon);
                var weapon = item != null ? CombatLoadout.WeaponClassOf(item.Kind) ?? WeaponClass.Fist : WeaponClass.Fist;
                _fighter.SetWeapon(item, weapon);
                _last = Readied.Melee;
                // G3 OnPlayerSelectWeapon: BackLeft and BackRight together; a dual wielder draws both blades (Hold_Begin).
                if (DrawsBothBlades(weapon)) _fighter.Animator.OffHand = G3CombatMotions.DualOffHand;
                if (_fighter.Draw()) return true;
                why = "the animator refused";
            }
            Debug.Log($"[Myrtana] DrawMelee refused: {why} (animator {_fighter.Animator.StateText})");
            return false;
        }

        /// <summary>
        /// Draws the equipped bow or crossbow (PS_Ranged; needs ammunition, else nothing happens: "MissingItem"); a drawn
        /// melee weapon is put away first.
        /// </summary>
        public bool DrawRanged()
        {
            if (!CanDraw()) return false;
            if (_fighter.Animator.Drawn) return _fighter.RangedDrawn ? false : ReadyAfterHolster(DrawRanged);
            var bow = Equipped(WornSlot.RangedWeapon);
            var weapon = bow != null ? CombatLoadout.WeaponClassOf(bow.Kind) : null;
            string ammo = weapon == WeaponClass.Bow || weapon == WeaponClass.Crossbow ? SelectAmmo(weapon.Value) : null;
            if (ammo == null) return false;
            // G3 FUN_10002550 (bow/crossbow): the arrows or bolts it will shoot are equipped to gESlot_Ammo on the draw
            var equipment = Session.Items.Equipment;
            if (equipment.In(WornSlot.Ammo) != ammo) equipment.Equip(ammo);
            _fighter.SetWeapon(bow, weapon.Value);
            _last = Readied.Ranged;
            return _fighter.Draw();
        }

        bool CanDraw() => DrawRefusal() == null;

        /// <summary>Why no weapon can be drawn or put away now (null = it can); logged by <see cref="DrawMelee"/> and dev scenarios.</summary>
        public string DrawRefusal()
        {
            if (Session == null) return "no session (PlayerCombat not initialised)";
            if (_dead) return "the hero is dead";
            if (_knockedOut) return "the hero is knocked out";
            if (_fighter.Frozen) return "the hero is frozen";
            if (_player.Swimming) return "the hero is swimming";
            var a = _fighter.Animator;
            if (a.Busy) return "a combat motion runs";
            if (a.InAim || a.InShot || a.InReload || a.InCast) return "aiming, shooting, reloading or casting";
            return null;
        }

        /// <summary>A bow or crossbow is drawn or waiting to be drawn once the hands are free.</summary>
        bool RangedReadying => _fighter.RangedDrawn || _pendingDraw != null && _pendingDraw.Equals((Func<bool>)DrawRanged);

        void ClearQueue() => _aimQueued = _shotQueued = _aimCutByHit = false;

        bool Holster()
        {
            if (!CanDraw()) return false;
            ClearQueue();
            _input.Reset();
            SetFocus(null);
            _nockedAmmo = null;
            _magic.HideHeld();
            return _fighter.Sheathe();
        }

        ItemDef Equipped(WornSlot slot)
        {
            string id = Session.Items.Equipment.In(slot);
            return id != null && Session.Content.Database.TryGet(id, out ItemDef item) ? item : null;
        }

        string SelectAmmo(WeaponClass weapon)
        {
            var inventory = Session.Items.Inventories.Of(ActorId.Player);
            return RangedRules.SelectAmmo(weapon, Session.Items.Equipment.In(WornSlot.Ammo),
                                          id => Session.Content.Database.TryGet(id, out ItemDef d) ? d : null, inventory.Count);
        }

        /// <summary>The primary / secondary button is down (real or scripted presses; the dev scenarios read it).</summary>
        public bool PrimaryHeld { get; private set; }
        public bool SecondaryHeld { get; private set; }
        /// <summary>How many sitting knock-downs began (the dev scenarios press the get-up once per sit).</summary>
        public int SitCount { get; private set; }

        /// <param name="at">Game time of the press (scripted input); null = now.</param>
        public void PrimaryDown(double? at = null)
        {
            PrimaryHeld = true;
            CombatInputLog.Click("LMB down");
            DropWaitingSwitch("LMB took the action slot"); // G3 OnPlayerAction enqueues into the same one-slot queue
            if (_fighter.SittingKnockedDown)
            {
                // ZS_SitKnockDown: the press gets him up whatever is in the hands (a bow or a spell stands up plainly);
                // it must not start an aim or a cast (fix-39).
                Feed(_input.PrimaryPressed(at ?? Now, Stance()));
                return;
            }
            _aimWanted = true;
            if (_fighter.SpellDrawn)
            {
                if (!QueueCastDuringBlock()) BeginCast();
            }
            else if (RangedReadying)
            {
                _shotQueued = false;
                _aimQueued = !StartAim(); // not nocked yet (drawing, reloading): queued like G3's OnPlayerAction
            }
            else Feed(_input.PrimaryPressed(at ?? Now, Stance()));
        }

        public void PrimaryUp(double? at = null)
        {
            PrimaryHeld = false;
            CombatInputLog.Click("LMB up");
            _aimWanted = false;
            if (_fighter.SpellDrawn) ReleaseCast();
            else if (RangedReadying)
            {
                // G3 OnPlayerAction in PS_Ranged: the release enqueues the shot (0x29) into the one-slot queue, so a weapon
                // key waiting for the full draw (PS_Ranged_PowerAim does not read it) is replaced: the arrow flies (fix-101).
                DropWaitingSwitch("LMB release: the shot took the action slot");
                // A release before the aim started replaces the queued aim by a shot: the bow is still raised, then fires.
                // fix-209: a release during the stumble that cut a held aim raises the bow again and shoots.
                if (!Release())
                {
                    var queue = HitReactionRules.OnReleaseWithoutShot(_aimQueued, _aimCutByHit, _fighter.Animator.Nocked);
                    if (queue == RangedReleaseQueue.AimAndShot) _aimQueued = true;
                    if (queue != RangedReleaseQueue.None) _shotQueued = true;
                    if (queue == RangedReleaseQueue.AimAndShot) CombatInputLog.Line("LMB up after a stumble cut the aim: raise again and shoot");
                }
            }
            else Feed(_input.PrimaryReleased(at ?? Now, Stance()));
        }

        public void SecondaryDown(double? at = null)
        {
            SecondaryHeld = true;
            CombatInputLog.Click("RMB down");
            DropWaitingSwitch("RMB took the action slot"); // G3 OnPlayerSecondaryAction enqueues into the same one-slot queue
            if (_fighter.SittingKnockedDown)
            {
                Feed(_input.SecondaryPressed(at ?? Now, Stance())); // ZS_SitKnockDown: GetUpParade (fix-39)
                return;
            }
            if (_fighter.SpellDrawn) StaffBlockPressed(); // G3 OnMagicHandleInput: Use2 blocks with a staff, else nothing
            else if (_fighter.RangedDrawn)
            {
                // fix-64 (r31 draw_aim_strafe: "movestate=Ranged was RangedAim"): the cancel ends the aim for good (G3
                // PS_Ranged_Aim_Loop → PS_Ranged_Reload → bow PS_Ranged_Walk). G3 starts an aim only on a press event, so a
                // LMB still held must not raise it again the next frame (UpdateRanged did, and the release then shot).
                _fighter.Animator.LowerAim();
                _aimWanted = false;
                ClearQueue();
            }
            else Feed(_input.SecondaryPressed(at ?? Now, Stance()));
        }

        public void SecondaryUp(double? at = null)
        {
            SecondaryHeld = false;
            CombatInputLog.Click("RMB up");
            if (_fighter.SpellDrawn) StaffBlockReleased();
            else if (!_fighter.RangedDrawn)
            {
                Feed(_input.SecondaryReleased(at ?? Now, Stance()));
                // G3 OnPlayerSecondaryAction: every release lowers the parade (FUN_10001d90), whatever the input state says.
                var a = _fighter.Animator;
                if (!_input.InParade && a.HoldsParade && !a.InMove)
                {
                    CombatInputLog.Line($"RMB up: the body still held the parade ({a.StateText}): lowered");
                    a.LowerParade();
                }
            }
        }

        // ------------------------------------------------------------------------------------------- magic

        /// <summary>
        /// Loads the readied spell (PS_Magic_Cast/PowerCast): the pre-check first (no mana → fizzle; a focus spell that
        /// cannot be power-cast needs the locked target), then the minimum mana is paid and the Raise motion held.
        /// </summary>
        /// <summary>A cast was refused (the fizzle sound played); the hero's "doesn't work" line is said by the host.</summary>
        public event System.Action<CastRefusal> CastRefused;

        /// <summary>Why <see cref="BeginCast"/> cannot start a cast now (dev diagnostics), or null when it can (the pre-check aside).</summary>
        public string CastBlocker =>
            Session == null ? "no session" : _dead ? "dead" : _knockedOut ? "knocked out" : !_fighter.Animator.Drawn ? "not drawn" :
            _controls.IsLocked ? "controls locked" : _player.Swimming ? "swimming" : !_fighter.SpellDrawn ? "no spell drawn" :
            _magic.Casting ? "casting" : _spell == null ? "no spell" : !_fighter.Animator.WeaponInHand ? "spell not in the hand yet" :
            _fighter.Animator.InRangedOverlay ? "overlay motion" :
            _fighter.Animator.Busy ? "combat motion" : null;

        public bool BeginCast()
        {
            if (!CanFight || !_fighter.SpellDrawn || _magic.Casting || _spell == null) return false;
            // Telekinesis and Open Locks focus objects and items (WorldSpellView); Animal Form checks the focus's species (magic-3).
            Session.Magic.TryGetSpell(_spell, out var spellDef);
            bool objectFocus = SpellEffectServices.TargetsObjects(spellDef) && WorldSpellView.HasObjectFocus(_fighter.transform);
            var refusal = Session.Magic.Check(ActorId.Player, _spell, Focus != null || objectFocus, Session.Clock.HourOfDay,
                                              focusSpecies: Focus != null ? _presenter.CombatDefOf(Focus.Actor)?.Species : null,
                                              focusEvil: Focus != null && _presenter.CombatDefOf(Focus.Actor)?.Evil == true);
            if (refusal != CastRefusal.None)
            {
                G3EffectSounds.Play("eff_magic_fizzle_01", _fighter.transform.position + Vector3.up);
                Debug.Log($"[Myrtana] cast refused: {refusal}");
                CastRefused?.Invoke(refusal);
                return false;
            }
            if (!_magic.Begin(_spell, "Human"))
            {
                Debug.Log($"[Myrtana] cast {_spell} not begun: {CastBlocker ?? "the cast motion refused"}");
                return false;
            }
            if (!FirstPerson) Align(Focus); // PS_Magic_Cast/QuickCast/PowerCast
            if (CastTrigger.GoesOffByItself(_magic.Cast.Action)) GoOff();
            return true;
        }

        /// <summary>
        /// The cast button is let go (G3 OnMagicHandleInput, fix-114, <see cref="CastTrigger"/>): a cast goes off by itself
        /// during its motion, so the release does nothing — except for a power cast let go before it powered up (0.7 s),
        /// which restarts as a plain Cast (FullStop → PS_Magic_Cast) and goes off at the end of that cast's raise.
        /// </summary>
        public bool ReleaseCast()
        {
            if (!_magic.Casting) return false;
            var cast = _magic.Cast;
            var up = CastTrigger.OnButtonUp(cast.Action, cast.Released, cast.Powered, Time.timeAsDouble - cast.StartedAt,
                                            Session.Magic.Rules.PowerCastHoldSeconds);
            if (up == CastButtonUp.RecastPlain)
            {
                _magic.Abort();
                if (!_magic.Begin(_spell, "Human", CastAction.Cast)) return false;
                CombatInputLog.Line("power cast let go early: plain cast");
            }
            if (!_magic.ReleaseQueued && !_magic.Cast.Released) GoOff();
            return true;
        }

        /// <summary>The cast goes off at the end of its raise motion: at the locked target (homing only if power-cast), else towards the point under the screen centre.</summary>
        void GoOff() => _magic.ReleaseAfterRaise(Focus, () => AimPoint(ShotOrigin()));

        /// <summary>
        /// The cast is over: a used-up scroll puts the spell hand away (G3: scrolls do not reload); a spell no longer carried
        /// too; and a teleport stone (CP: "the hero unequips teleporter stones automatically after use").
        /// </summary>
        void OnCastFinished(SpellCast cast)
        {
            if (!cast.Released || !_fighter.SpellDrawn) return;
            if (cast.UsesUpItem || !Session.Items.Inventories.Of(ActorId.Player).Has(_spell)
                || Session.Teleports != null && Session.Teleports.IsTeleport(cast.Item ?? cast.Spell.Id)) Holster();
        }

        /// <summary>The held spell glows in the spell hand (a teleport stone is shown as the stone instead: <see cref="HeldStoneMesh"/>).</summary>
        void ShowSpell()
        {
            if (_fighter.SpellDrawn && _fighter.Animator.WeaponInHand && Session != null && HeldStoneMesh() == null &&
                Session.Magic.TryGetSpell(_spell, out var spell))
                _magic.ShowHeld(spell);
            else _magic.HideHeld();
        }

        /// <summary>
        /// fix-261 [owner]: the readied teleport stone is held in the hand as its own mesh, as in G3; it glows only while it
        /// is used (its load and cast effects), not with the big hold glow. Null for every other spell item.
        /// </summary>
        string HeldStoneMesh()
        {
            if (Session == null || _spell == null || Session.Teleports == null || !Session.Teleports.IsTeleport(_spell)) return null;
            if (!Session.Content.Database.TryGet(_spell, out ItemDef def)) return null;
            string mesh = CombatFighter.VisualKey(def);
            return mesh != null && _catalog != null && _catalog.TryGetMesh(mesh, out _) ? mesh : null;
        }

        // ------------------------------------------------------------------------------------------- ranged

        /// <summary>The draw time so far (s, from the bow's PowerRaise), for the HUD and tests.</summary>
        public float DrawSeconds => _fighter.Animator.DrawSeconds;

        /// <summary>Whether the HUD shows the aiming crosshair (PS_Ranged_Aim … PS_Ranged_Walk, Docs/formats-gui.md §6.3).</summary>
        public bool CrosshairShown
        {
            get
            {
                if (_fighter == null) return false;
                var a = _fighter.Animator;
                return Myrtana.Ui.Skin.CrosshairRules.Visible(_fighter.RangedDrawn, CanFight, a.InAim, a.InShot, _aimWanted,
                                                              a.InReload || a.Nocked);
            }
        }

        /// <summary>Distance (m) from the shot origin to the point under the screen centre, which the shot flies at.</summary>
        public float AimDistance()
        {
            var from = ShotOrigin();
            return Vector3.Distance(from, AimPoint(from));
        }

        /// <summary>Raises the bow and starts drawing (the HitMultiplier grows from now on).</summary>
        public bool StartAim()
        {
            if (!CanFight || !_fighter.RangedDrawn || !_fighter.Animator.Nocked || _nockedAmmo == null) return false; // fix-186
            return _fighter.Animator.Aim();
        }

        /// <summary>Releases the shot (PS_Ranged_Shoot): multiplier from the draw time, one ammunition used up.</summary>
        public bool Release()
        {
            if (!_fighter.Animator.InAim || _nockedAmmo == null || Session == null) return false;
            var ammoId = _nockedAmmo;
            if (Session.Items.Inventories.Consume(ActorId.Player, ammoId, 1) == 0 ||
                !Session.Content.Database.TryGet(ammoId, out ItemDef ammo))
            {
                _fighter.Animator.LowerAim();
                return false;
            }
            float multiplier = RangedRules.HitMultiplier(_fighter.WeaponClass, _fighter.Animator.DrawSeconds);
            var from = ShotOrigin();
            var dir = (AimPoint(from) - from).normalized;
            var shot = new Projectile.Shot
            {
                Shooter = _fighter,
                Ammo = ammo,
                Weapon = _fighter.WeaponItem,
                WeaponClass = _fighter.WeaponClass,
                HitMultiplier = multiplier,
                Quality = Session.Items.Inventories.Of(ActorId.Player).QualityOf(ammoId) | ammo.Quality,
            };
            _nockedAmmo = null;
            _fighter.Animator.Shoot();
            _presenter.Shoot(shot, from, dir * RangedRules.ShotVelocity(player: true, multiplier));
            return true;
        }

        /// <summary>The ammunition on the string (null: none nocked) — dev runner (fix-186).</summary>
        public string NockedAmmo => _nockedAmmo;

        /// <summary>Where the arrow or bolt leaves the bow (the dev runner's hold-over; fix-56).</summary>
        public Vector3 ShotFrom => ShotOrigin();

        Vector3 ShotOrigin()
        {
            var at = _slot != null ? _slot.position : _leftSlot != null ? _leftSlot.position : transform.position + Vector3.up * 1.5f;
            return at + transform.forward * 0.3f;
        }

        /// <summary>The point under the screen centre (the first thing that is not the hero), or far ahead.</summary>
        Vector3 AimPoint(Vector3 from) => AimPoint(from, out _);

        /// <summary>
        /// The point under the screen centre: the first collider that is not the hero, or the drawn relief in front of it
        /// (fix-257: the dunes have no colliders), else 300 m along the ray (<paramref name="hit"/> false).
        /// </summary>
        Vector3 AimPoint(Vector3 from, out bool hit)
        {
            hit = false;
            var cam = _camera != null ? _camera.transform : null;
            if (cam == null) return from + transform.forward * 100f;
            const float far = 300f;
            var hits = Physics.RaycastAll(cam.position, cam.forward, far, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            float? physics = null;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(transform)) continue;
                if (h.collider.isTrigger && !h.collider.TryGetComponent<CombatBody>(out _)) continue;
                if (Vector3.Dot(h.point - from, cam.forward) < 0.5f) continue; // between the camera and the bow
                physics = h.distance;
                break;
            }
            float? relief = null;
            if (global::G3.Unity.G3TerrainRelief.Active)
            {
                float start = Mathf.Max(0f, Vector3.Dot(from - cam.position, cam.forward) + 0.5f); // past the bow
                float reach = Mathf.Min(physics ?? far, BowAimPose.ReliefReach);
                if (BowAimPose.ReliefHit(ReliefGround, ToN(cam.position), ToN(cam.forward), start, reach, out float rd)) relief = rd;
            }
            return ToU(BowAimPose.CrosshairPoint(ToN(cam.position), ToN(cam.forward), physics, relief, far, out hit));
        }

        static readonly BowAimPose.Ground ReliefGround = SampleRelief;

        static bool SampleRelief(float x, float y, float z, out float collisionY, out float visualY) =>
            global::G3.Unity.G3TerrainRelief.TryGround(new Vector3(x, y, z), out collisionY, out visualY);

        /// <summary>Keeps an arrow nocked while the bow is drawn; the last one gone puts the bow away ("LastOneGone").</summary>
        void UpdateRanged()
        {
            var a = _fighter.Animator;
            if (!_fighter.RangedDrawn)
            {
                if (_pendingDraw == null) ClearQueue();
                return;
            }
            // G3 auto-aim (EnableAutoAiming + OnGetAimAngle) while aiming: the upper body pitches along the line from the
            // weapon (1 m behind it) to the point under the screen centre. fix-188 (owner: "the bow often first points straight
            // down when aiming, then aligns"): the same pitch before the aim too - the raise picks its Up/Fwd/Down motions from
            // it (BeginRanged); the camera pitch it had before (the third-person camera looks down at the hero, -25..-40)
            // chose Aim_Raise/PowerRaise _Down, and the hold switched to _Fwd only once the draw was over.
            a.AimPitch = AutoAimPitch();
            // The button is held while the next arrow is nocked, or a press during the draw/reload is queued.
            // fix-186 (fix176 'no shot left the bow': the bow drawn and held with an arrow on the string that stood for no
            // item, so the release shot nothing): an arrow on the string always stands for the ammunition chosen now
            if (a.Nocked && _nockedAmmo == null && !a.InAim && !a.InShot && SelectAmmo(_fighter.WeaponClass) is string adopt &&
                Session.Content.Database.TryGet(adopt, out ItemDef adoptDef) && a.SwapNocked(adoptDef.Visual?.Mesh))
                _nockedAmmo = adopt;
            if ((_aimWanted || _aimQueued) && a.Nocked && !a.InAim && !a.InShot && StartAim()) _aimQueued = _aimCutByHit = false;
            if (_shotQueued && a.InAim && a.DrawSeconds > 0f)
            {
                _shotQueued = false;
                Release();
            }
            // fix-184 (regress-1021b fix176: the explosive arrows equipped with the bow drawn, the plain arrow nocked at the draw
            // flew): the arrow on the string follows the ammunition choice until the bow is drawn back
            if (a.Nocked && _nockedAmmo != null && !a.InAim && !a.InShot &&
                RangedRules.Renock(_nockedAmmo, SelectAmmo(_fighter.WeaponClass)) is string renock &&
                Session.Content.Database.TryGet(renock, out ItemDef renockDef) && a.SwapNocked(renockDef.Visual?.Mesh))
                _nockedAmmo = renock;
            if (!a.WeaponInHand || a.Busy || a.Nocked || a.InReload || a.InShot || a.InAim) return;
            string ammo = SelectAmmo(_fighter.WeaponClass);
            if (ammo == null)
            {
                Holster();
                return;
            }
            Session.Content.Database.TryGet(ammo, out ItemDef def);
            float speed = _fighter.WeaponClass == WeaponClass.Crossbow && _fighter.SpeedFactor != null ? _fighter.SpeedFactor(CombatMotion.Cock, false) : 1f;
            if (a.Reload(def?.Visual?.Mesh, speed)) _nockedAmmo = ammo;
        }

        /// <summary>
        /// OnGetAimAngle (player): the pitch (degrees, up positive) from 1 m behind the weapon to the screen-centre pick point
        /// or the focus centre under the crosshair (fix-228), limited to the natural aim range (<see cref="BowAimPose"/>).
        /// </summary>
        float AutoAimPitch()
        {
            var weapon = ShotOrigin();
            var dir = PoseAimPoint(weapon) - (weapon - transform.forward);
            return BowAimPose.ClampPitch(BowAimPose.PitchOf(ToN(dir)));
        }

        /// <summary>
        /// Dev scenarios only: a fighter the per-frame focus scan keeps while it is alive and enabled (several foes around
        /// the hero, one of them to be fought: the scan would pick by facing). Null: the normal scan.
        /// </summary>
        public CombatFighter PinnedFocus { get; set; }

        /// <summary>Sets the focus (null = none). It is re-picked every frame; the camera ignores it (as in G3).</summary>
        public void SetFocus(CombatFighter target)
        {
            Focus = target;
            _fighter.Opponent = target;
        }

        static double Now => Time.timeAsDouble;

        bool CanFight => Session != null && !_dead && !_knockedOut && _fighter.Animator.Drawn && !_controls.IsLocked && !_player.Swimming;

        /// <summary>
        /// A get-up press of the sitting hero (fix-39): it only needs a live, conscious hero. The weapon config, a ranged
        /// weapon or a spell in the hand and a control lock do not stop it (the real keys are filtered by ReadKeys; the
        /// dev runner's press may come in the frame a seat use still holds the lock).
        /// </summary>
        bool IsGetUp(MeleeCommand command) =>
            command.Kind == MeleeCommandKind.StartMove && _fighter.SittingKnockedDown &&
            (command.Move == CombatAction.GetUpAttack || command.Move == CombatAction.GetUpParade) &&
            Session != null && !_dead && !_knockedOut;

        void Feed(MeleeCommand command)
        {
            if (!IsGetUp(command) && (!CanFight || _fighter.RangedDrawn || _fighter.SpellDrawn))
            {
                if (command.Kind == MeleeCommandKind.None) return;
                CombatInputLog.Line($"REFUSED {command}: {WhyNoFight()}");
                _input.Refused(command); // the interpreter must not stay busy with a move that never plays (combat-audit #4)
                if (command.Kind == MeleeCommandKind.LowerParade) _fighter.Animator.LowerParade();
                return;
            }
            for (int guard = 0; guard < 4 && command.Kind != MeleeCommandKind.None; guard++)
            {
                switch (command.Kind)
                {
                    case MeleeCommandKind.RaiseParade:
                        // _AI_Parade → FUN_10001e20(0): raising the block ends sneaking (fix-28).
                        if (_player.Sneaking) CombatInputLog.Line("sneak ends: block raised");
                        _player.SetSneaking(false);
                        if (!_fighter.Animator.RaiseParade())
                        {
                            CombatInputLog.Line($"REFUSED RaiseParade: the body cannot block now ({_fighter.Animator.StateText})");
                            _input.Refused(command);
                        }
                        return;
                    case MeleeCommandKind.LowerParade:
                        _fighter.Animator.LowerParade();
                        return;
                    case MeleeCommandKind.StartMove:
                        if (_fighter.SittingKnockedDown && command.Move != CombatAction.GetUpAttack && command.Move != CombatAction.GetUpParade)
                        {
                            // ZS_SitKnockDown: only the get-up presses count; a move queued before the fall is dropped unpaid.
                            CombatInputLog.Line($"DROPPED {command}: sitting knocked down");
                            _input.Reset();
                            return;
                        }
                        // G3 combo pose: a move that starts while the last attack's Hit or its Recover tail plays goes on
                        // from the pose that Hit ended in; otherwise P0 (MeleeComboRules.NextComboPose).
                        var a = _fighter.Animator;
                        bool chained = a.InMove && a.Phase >= G3SwingPhase.Hit || a.InRecoverTail; // a power attack cut in its Raise: P0
                        _combo = MeleeComboRules.NextComboPose(chained, a.HitEndPose, _combo);
                        _fighter.LeftOneHandedOrTorch = LeftOneHandedOrTorch;
                        var action = Session.Combat.StartMove(ActorId.Player, command.Move);
                        bool fromSneak = _player.Sneaking;
                        if (fromSneak && !SneakCombatRules.KeepsSneaking(action)) _player.SetSneaking(false);
                        if (_fighter.StartMove(action, _combo))
                        {
                            // The _AI_ command sets the move's stance: Stand (the hero rises for the swing) or Parade (fix-28).
                            if (fromSneak)
                                CombatInputLog.Line($"from sneak: {action} plays in the {SneakCombatRules.MoveStance(action)} stance" +
                                                    (_player.Sneaking ? ", sneaking resumes after it" : ""));
                            CombatInputLog.MoveStarted(action == command.Move ? action.ToString() : $"{action} (asked {command.Move})", _combo,
                                                       MeleeComboRules.ShapeOf(action).ToString(), chained);
                            // One line per combo stage: the move that played (a QuickAttack's side), its pose and shape.
                            _comboStage = MeleeComboRules.NextStage(chained, _comboStage);
                            var played = _fighter.CurrentAction != CombatAction.None ? _fighter.CurrentAction : action;
                            LastStartedMove = played;
                            MovesStarted++;
                            CombatInputLog.Line(MeleeComboRules.StageLine(_comboStage, played, _combo, command.Move) +
                                                (CombatAction.QuickAttack == command.Move || played == CombatAction.QuickAttackLeft || played == CombatAction.QuickAttackRight
                                                    ? $", hit direction now {(_fighter.HitDirection ? 1 : 0)}" : ""));
                            if (CombatAlignment.AlignsOnStart(action) && !FirstPerson) Align(Focus);
                            return;
                        }
                        CombatInputLog.Line($"REFUSED {action}: no motion for it (weapon {a.Weapon})");
                        command = _input.MoveEnded(Now, Stance()); // nothing to show: the move is over at once
                        break;
                }
            }
        }

        /// <summary>combatlog: each combat motion the body starts and the cross-fade it got (MotionBlendRules).</summary>
        static void OnStepStarted(string key, G3SwingPhase phase, float fade) =>
            CombatInputLog.Line(FormattableString.Invariant($"  motion {key} [{phase}] fade {fade * 1000:0} ms"));

        /// <summary>G3 ZS_SitKnockDown starts with ResetAll: the queued move and the held buttons are forgotten (a new press gets up).</summary>
        void OnSatDown()
        {
            SitCount++;
            CombatInputLog.Line("SIT knock-down: input reset; LMB = GetUpAttack, RMB = GetUpParade (he sits until a press, G3)");
            _input.Reset();
        }

        /// <summary>
        /// A hit reaction starts on the hero (G3 AssessHit → SetTask ZS_…): the running move is cut, the queue and the parade
        /// are reset by the unparried stumbles (ResetAll), input waits for the reaction's Hit (fix-45, combat-audit #2/#7).
        /// </summary>
        void OnReactionStarting(HitReaction reaction)
        {
            bool reset = HitReactionRules.ResetsInput(reaction);
            CombatInputLog.Line($"HIT REACTION {reaction}{(reset ? " (ResetAll)" : " (parried: queue and parade kept)")}");
            _input.HitTaken(Now, reset);
            if (!reset) return;
            DropWaitingSwitch("ResetAll: " + reaction); // G3 ResetAll clears the action queue
            ClearQueue(); // fix-197: the stumble cut the aim; a queued aim or shot must not fire after it
            // fix-209: the held shoot button keeps the aim intent; a release before the bow is up again queues the shot
            if (_fighter.RangedDrawn) _aimCutByHit = HitReactionRules.KeepsAimIntent(reaction, _aimWanted, _fighter.Animator.Nocked);
        }

        /// <summary>A reaction's Hit or the draw is over: the melee loop takes held buttons and the queue in this frame (G3 PS_Melee_Loop).</summary>
        void OnFreed()
        {
            if (!CanFight || _fighter.RangedDrawn || _fighter.SpellDrawn) return;
            Feed(_input.Update(Now, Stance()));
        }

        void OnMoveEnded(CombatAction action)
        {
            CombatInputLog.Line($"END {action}{(_fighter.Animator.InRecoverTail ? " (Hit over; Recover tail plays on)" : "")}{(_input.Queued != CombatAction.None ? ", queued " + _input.Queued : "")}");
            Feed(_input.MoveEnded(Now, Stance()));
        }

        /// <summary>Why the hero cannot fight now (the combat log).</summary>
        string WhyNoFight()
        {
            if (Session == null) return "no session";
            if (_dead) return "dead";
            if (_knockedOut) return "knocked out";
            if (!_fighter.Animator.Drawn) return "weapon not drawn";
            if (_controls.IsLocked) return "controls locked";
            if (_player.Swimming) return "swimming";
            if (_fighter.RangedDrawn) return "ranged weapon";
            if (_fighter.SpellDrawn) return "spell";
            return "?";
        }

        MeleeStance Stance()
        {
            var vitals = Session?.Combat.Vitals;
            var focusState = Focus != null && vitals != null ? vitals.StateOf(Focus.Actor) : VitalState.Alive;
            bool finisher = Focus != null && focusState == VitalState.Down &&
                            FinishingMove.InReach(focusState, Vector3.Distance(Focus.transform.position, transform.position) * 100f,
                                                  Session.Combat.Resolver.CanBeKilled(_presenter.Snapshot(Focus), Session.Hero.Level));
            return new MeleeStance
            {
                RightHand = _fighter.WeaponClass,
                LeftOneHandedOrTorch = LeftOneHandedOrTorch,
                CanParade = true,
                FinisherInReach = finisher,
                FocusDownOrDead = Focus != null && focusState != VitalState.Alive,
                SittingKnockedDown = _fighter.SittingKnockedDown,
                Reacting = _fighter.Animator.InReaction, // G3 ZS_*Stumble task: presses wait for the reaction's Hit (fix-45)
                Drawing = _fighter.Animator.InDraw,       // G3 PS_Melee entry (_AI_HoldInventoryItems): moves wait for the draw
                // fix-203: no parade on the body (a hit during the raise ended standing): the held button raises it again
                BodyOutOfParade = !_fighter.Animator.InParade && !_fighter.Animator.HoldsParade,
            };
        }

        // ------------------------------------------------------------------------------------------- frame

        /// <summary>
        /// A drawn spell or scroll is held in the right hand (G3 gESlot_RightHand): the quick bar and inventory tint it. The
        /// drawn weapon's kind tells the quick bar which weapon cell is the active one (fix-56).
        /// </summary>
        void LateUpdate()
        {
            if (Session == null) return;
            ApplyBowAimPose(); // fix-228: after the animation, the drawn bow turned onto its aim point
            var hands = Session.Items.Hands;
            hands.SetRight(_fighter != null && _fighter.SpellDrawn ? _spell : null);
            hands.SetWeapon(_fighter == null || _fighter.SpellDrawn || !_fighter.Animator.Drawn ? HandWeapon.None
                            : _fighter.RangedDrawn ? HandWeapon.Ranged : HandWeapon.Melee);
        }

        void Update()
        {
            if (Session == null || _dead || LoadingHold.IsHeld || GamePause.IsPaused) return; // no attacks behind a loading screen or a pause
            ShowCarried();
            HealKnockOutLock();
            ReadKeys();
            // The focus of this frame (its keys and block) before the input starts a move: an attack aligns to it (fix-68;
            // G3 PS_Melee_Loop picks the focus before it takes the queued press).
            UpdateFocus();
            if (CanFight && !_fighter.RangedDrawn && !_fighter.SpellDrawn) Feed(_input.Update(Now, Stance()));
            UpdateRanged();
            _magic.Update();
            // a power cast held until it powered up goes off by itself (G3 _AI_Cast: the release is ignored after 0.7 s)
            if (_magic.Casting && !_magic.ReleaseQueued && !_magic.Cast.Released && CastTrigger.GoesOff(_magic.Cast.Action, _magic.Cast.Powered)) GoOff();
            TickStaff();
            var a = _fighter.Animator;
            RunPendingDraw(); // normally already at the sheathe's end (OnAnimatorMoveEnded); a sheathe without motions ends here
            RunWaitingSwitch(); // after the ranged update: a bow that must nock first reloads before the key acts (G3 PS_Ranged)
            // G3 PS_Melee_Sneak: a human with a melee weapon (or fists) sneaks in the Parade stance (fix-28, combat.md §1.6).
            SneakInParade = SneakCombatRules.SneakStance(a.Drawn && !_fighter.RangedDrawn && !_fighter.SpellDrawn && !FormLocked) == BodyStance.Parade;
            if (_paradeStance != null) _paradeStance.SneakInParade = SneakInParade;
            // G3 lets the hero walk while blocking (Parade stance motions) once the parade is up (hero-polish).
            bool paradeWalk = a.InParade && _paradeStance != null && _paradeStance.ParadeLocomotion;
            _player.paradeWalk = paradeWalk;
            // A full-body draw or sheathe motion holds the body (G3 moves a body by its motion; fix-42).
            _player.moveInputEnabled = !(a.InMove || a.InReaction || a.IsDown || a.IsDead || (a.InParade && !paradeWalk) || _knockedOut ||
                                         _fighter.Frozen || _falls.Busy || a.HoldsBody);
            // G3 PS_Ranged_Aim: aiming walks (SetMovementMode walk; fix-42), the bow stays on the crosshair.
            MoveState = CurrentMoveState();
            _player.moveState = MoveState; // fix-73: out of Normal the Camera rotation keys turn the hero (KeyOrbitRules)
            _player.forceWalk = G3.Player.HeroMoveRules.ForcesWalk(MoveState);
            // G3 FUN_10002130 runs in every loop state, drawn or not: Shift sprints with a weapon, fists, bow or spell hand
            // too (fix-44); a melee move or a cast keeps the gait; the sprint needs 21 SP to start (1 SP to go on).
            _player.holdGait = !G3.Player.HeroMoveRules.UpdatesGait(MoveState);
            _player.sprintStamina = Session.Combat.Vitals.Stamina(ActorId.Player);
            if (a.InRecoverTail && _player.moveInputEnabled && _player.DesiredVelocity.sqrMagnitude > 0.01f)
            {
                a.CutRecover(); // the hero walks off: the Recover tail of the last attack is cosmetic (G3)
                CombatInputLog.Line("recover tail cut: walking");
            }
            _falls.Tick(Displace); // the acrobatic roll's 4.3 m (polish-3)
            UpdateAlignment(Time.deltaTime);
            TickStamina(Time.deltaTime);
            QuenchInWater();
            LogParadeState();
        }

        string _paradeLog;

        /// <summary>combatlog: one line whenever the block's state changes (body, input flags, the button; fix-45).</summary>
        void LogParadeState()
        {
            if (!CombatInputLog.Enabled || _fighter == null || !_fighter.Animator.Drawn) return;
            var a = _fighter.Animator;
            string text = $"PARADE body {(a.HoldsParade ? "UP" : "down")} [{a.StateText}{(a.StepStage != null ? " " + a.StepStage : "")}], " +
                          $"input parade {(_input.InParade ? "up" : "down")} flag {(_input.BlockHeld ? 1 : 0)}" +
                          $"{(_input.Reacting ? " reacting" : "")}{(_input.Busy ? " busy " + _input.Current : "")}" +
                          $"{(_input.Queued != CombatAction.None ? " queued " + _input.Queued : "")}, RMB {(SecondaryHeld ? "held" : "up")}";
            if (text == _paradeLog) return;
            _paradeLog = text;
            CombatInputLog.Line(text);
        }

        /// <summary>The hero's state for facing and gait (<see cref="G3.Player.HeroMoveRules"/>), updated every frame.</summary>
        public G3.Player.HeroMoveState MoveState { get; private set; }

        /// <summary>
        /// The player state as G3 names it for facing and gait: aiming covers PS_Ranged_Aim … Aim_Loop, Shoot and a reload
        /// with the button held (PS_Ranged_Reload → FUN_10001cf0); the rest follows the hand and the running move.
        /// </summary>
        G3.Player.HeroMoveState CurrentMoveState()
        {
            var a = _fighter.Animator;
            if (!a.Drawn) return G3.Player.HeroMoveState.Normal;
            if (_fighter.RangedDrawn)
            {
                if (a.InAim || a.InShot || (a.InReload && (_aimWanted || _aimQueued))) return G3.Player.HeroMoveState.RangedAim;
                return _player.Sneaking ? G3.Player.HeroMoveState.RangedSneak : G3.Player.HeroMoveState.Ranged;
            }
            if (_fighter.SpellDrawn) return _magic.Casting ? G3.Player.HeroMoveState.MagicCast : G3.Player.HeroMoveState.Magic;
            if (a.InMove) return G3.Player.HeroMoveState.MeleeMove;
            if (a.InParade) return G3.Player.HeroMoveState.MeleeParade;
            return _player.Sneaking ? G3.Player.HeroMoveState.MeleeSneak : G3.Player.HeroMoveState.Melee;
        }

        /// <summary>
        /// CP MagicTeleport (teleport runes, scrolls and stones): HoldStacks before Entity::Teleport, i.e. the weapon or
        /// spell in the hand goes away at once, without the holster motion (the cast would be cut by the jump anyway).
        /// The torch in the left hand stays (HeroTorchView). Plain moves of the hero (loading, scripts) keep the weapon.
        /// </summary>
        /// <summary>
        /// The weapon in the hand was taken off (inventory, script): it is put away (holster motion, at once when no motion
        /// can play), so the next draw takes what is equipped then - fists without a melee weapon [interp: G3 drops the
        /// item's stack from the hand when it is unequipped].
        /// </summary>
        void OnEquipmentChanged(EquipmentChanged e)
        {
            if (e.Item != null || _dead || _fighter.WeaponItem == null) return;
            if (e.Slot != WornSlot.MeleeWeapon && e.Slot != WornSlot.RangedWeapon) return;
            if (Session.Items.Equipment.IsWorn(_fighter.WeaponItem.Id)) return;
            if (_fighter.Animator.Drawn && !_fighter.SpellDrawn && !Holster()) PutAwayAtOnce();
        }

        void PutAwayAtOnce()
        {
            if (Session == null || _dead) return;
            ClearQueue();
            _input.Reset();
            SetFocus(null);
            ReleaseAlignment();
            _player.bodyYaw = null;
            _pendingDraw = null;
            _switchKey.Clear();
            _nockedAmmo = null;
            _magic.Abort();
            _magic.HideHeld();
            if (!_knockedOut) _fighter.ResetState();
        }

        // ------------------------------------------------------------------------------------------- water (Docs/hero.md)

        /// <summary>
        /// G3 PS_Swim: entering swimming clears the focus, puts out burning, aborts a spell and puts weapon, shield and
        /// spell away at once (ResetAll + HoldStacks(-1, -1), no holster motion); a burning torch in the left hand is lost
        /// (HeroTorchView). No drawing, fighting or casting while swimming (CanDraw, CanFight).
        /// </summary>
        void OnWaterChanged(G3.Player.WaterMode old, G3.Player.WaterMode now)
        {
            if (now != G3.Player.WaterMode.Swim || Session == null || _dead) return;
            _input.Reset();
            SetFocus(null);
            ReleaseAlignment();
            _player.bodyYaw = null;
            _pendingDraw = null;
            _switchKey.Clear();
            ClearQueue();
            _nockedAmmo = null;
            _magic.Abort();
            _magic.HideHeld();
            _fighter.ResetState();
            Session.Magic.Status.Clear(ActorId.Player, ActorStatus.Burning);
        }

        /// <summary>CP OnTick: burning ends when the hero is more than 75 cm under water.</summary>
        void QuenchInWater()
        {
            if (_player.WaterDepth > G3.Player.SwimRules.QuenchDepth && Session.Magic.Status.IsBurning(ActorId.Player))
                Session.Magic.Status.Clear(ActorId.Player, ActorStatus.Burning);
        }

        /// <summary>G3 OnMovementModeChanged: a fall of more than 7.45 s that ends in water teaches Perk_Acrobatic.</summary>
        /// <summary>
        /// G3 _AI_Jump (Script_Game 0x10046220, the same in CP and AB): a jump costs 10 SP in every stance, floored at 0;
        /// there is no stamina gate (OnPlayerJump only checks CanJump).
        /// </summary>
        void OnJumped()
        {
            if (Session != null && !_dead) Session.Combat.StartMove(ActorId.Player, CombatAction.Jump);
        }

        void OnDivedIn(float fallSeconds)
        {
            const string perk = "Perk_Acrobatic";
            if (Session == null || fallSeconds <= G3.Player.SwimRules.AcrobaticFallSeconds || Session.Hero.Knows(perk)) return;
            Session.Progression.LearnSkill(perk);
        }


        void ReadKeys()
        {
            // Session keys (G3Input): WeaponMode (Space / middle mouse) draws or holsters, Use1 (left mouse) is the
            // primary, Use2 (right mouse) the secondary action.
            RebuildStamps();
            bool use1Down = G3Input.WasPressed(G3.Player.SessionKey.Use1);
            if (_controls.IsLocked || !global::G3.Unity.G3Cursor.Captured)
            {
                _chordPress = false;
                if (CombatInputLog.Enabled && use1Down)
                    CombatInputLog.Line($"LMB down IGNORED: {(_controls.IsLocked ? "controls locked" : "cursor not locked")}");
                if (!_buttonsLost) LoseButtons();
                return;
            }
            _buttonsLost = false;
            if (_fighter.SittingKnockedDown)
            {
                // ZS_SitKnockDown_Loop: only the Use1/Use2 press edges count, before any stance or weapon check (fix-39).
                var exit = HeroSitInput.Decide(new HeroSitState { SittingKnockedDown = true, CursorLocked = true },
                                               new HeroSitPresses { Use1 = use1Down, Use2 = G3Input.WasPressed(G3.Player.SessionKey.Use2) });
                if (exit == HeroSitExit.GetUpAttack) PrimaryDown(EventTime(_lmbDownAt));
                else if (exit == HeroSitExit.GetUpParade) SecondaryDown(EventTime(_rmbDownAt));
                if (G3Input.WasReleased(G3.Player.SessionKey.Use1)) PrimaryHeld = false;
                if (G3Input.WasReleased(G3.Player.SessionKey.Use2)) SecondaryHeld = false;
                return;
            }
            if (G3Input.WasPressed(G3.Player.SessionKey.WeaponMode)) ToggleWeapon();
            // A press while the hands change (holster, then draw) still counts: G3 queues it per task.
            if (!StanceActive) // PS_Normal: the left button uses the focus (PlayerInteraction)
            {
                _chordPress = false;
                if (CombatInputLog.Enabled && use1Down) CombatInputLog.Line("LMB down IGNORED: weapon not drawn");
                return;
            }
            // fix-226: Ctrl + click onto a body in front loots it, no attack (its release neither)
            if (use1Down && TakeAllChord != null && TakeAllChord())
            {
                _chordPress = true;
                use1Down = false;
                if (CombatInputLog.Enabled) CombatInputLog.Line("LMB down TAKEN: take-all chord looted a body");
            }
            // Timed by the button events (a tap read late at a low frame rate stays a tap; G3 DurationPressedMSecs).
            if (use1Down) PrimaryDown(EventTime(_lmbDownAt));
            if (G3Input.WasReleased(G3.Player.SessionKey.Use1))
            {
                if (_chordPress) _chordPress = false;
                else PrimaryUp(EventTime(_lmbUpAt));
            }
            if (G3Input.WasPressed(G3.Player.SessionKey.Use2)) SecondaryDown(EventTime(_rmbDownAt));
            if (G3Input.WasReleased(G3.Player.SessionKey.Use2)) SecondaryUp(EventTime(_rmbUpAt));
        }

        bool FirstPerson => _camera != null && !_camera.thirdPerson;

        bool _buttonsLost;

        /// <summary>
        /// The controls were locked or the cursor freed (a menu, a dialog): the releases that come meanwhile cannot be read,
        /// so the held buttons count as released now; a raised block goes down (combat-audit #9: it stayed up after a menu).
        /// </summary>
        void LoseButtons()
        {
            _buttonsLost = true;
            bool held = PrimaryHeld || SecondaryHeld;
            PrimaryHeld = SecondaryHeld = false;
            if (_fighter != null && _fighter.RangedDrawn)
            {
                // fix-197: the held aim goes down with the lost button (a dialog, a menu); kept wanted, it was raised again
                // after the lock with no release ever coming. The arrow stays nocked.
                _aimWanted = false;
                ClearQueue();
                _fighter.Animator.LowerAim();
            }
            if (_fighter == null || _fighter.SpellDrawn || _fighter.RangedDrawn) return;
            var command = _input.ButtonsLost(Now);
            if (command.Kind == MeleeCommandKind.LowerParade || _fighter.Animator.HoldsParade && !_fighter.SittingKnockedDown)
            {
                if (held || _fighter.Animator.HoldsParade) CombatInputLog.Line("controls locked: the block goes down");
                _fighter.Animator.LowerParade();
            }
        }

        // ------------------------------------------------------------------------------------------- hero forms (magic-3)

        /// <summary>
        /// An animal form or a ghost: no weapon changes, spells or quick slots (G3 OnPlayerSelectWeapon, OnPlayerUseQuickSlot
        /// refuse while transformed; Docs/combat.md §12.5).
        /// </summary>
        bool FormLocked => Session != null && Session.SpellEffects.Hero.Kind != HeroFormKind.Hero;

        /// <summary>
        /// The hero's body changed (Docs/combat.md §12.5): the fighter animates and hits with <paramref name="visual"/>. An
        /// animal fights with its natural weapons (the Fist moves of its rig), drawn at once (G3: PS_Melee with the form's
        /// fist items); his own body comes back unarmed.
        /// </summary>
        public void SwapBody(Transform visual, bool humanoid, float reach)
        {
            PutAwayAtOnce();
            _fighter.Animator.WeaponInHandChanged -= ShowWeapon;
            _fighter.Animator.WeaponInHandChanged -= ShowSpell;
            _fighter.Animator.StepStarted -= OnStepStarted;
            _fighter.Animator.LeftHandSwitched -= OnLeftHandSwitched;
            _fighter.Animator.MoveEnded -= OnAnimatorMoveEnded;
            _fighter.Animator.RangedEnded -= OnAnimatorRangedEnded;
            _fighter.Init(ActorId.Player, visual, humanoid, reach, Displace);
            _fighter.Animator.WeaponInHandChanged += ShowWeapon;
            _fighter.Animator.WeaponInHandChanged += ShowSpell;
            _fighter.Animator.StepStarted += OnStepStarted;
            _fighter.Animator.LeftHandSwitched += OnLeftHandSwitched;
            _fighter.Animator.MoveEnded += OnAnimatorMoveEnded;
            _fighter.Animator.RangedEnded += OnAnimatorRangedEnded;
            _slot = _leftSlot = null;
            _hero = visual.GetComponent<G3HeroAnimator>();
            _paradeStance = _hero;
            _fighter.SetWeapon(null, WeaponClass.Fist);
            if (humanoid) return;
            _last = Readied.Melee;
            _fighter.Draw();
        }

        /// <summary>
        /// G3's focus (Docs/combat.md §9.2): every frame the characters in the scan (melee 5 m to the target's edge,
        /// ±90° from the reference direction; magic 35 m, ±45°; first person ±10°/±20° from the camera; free line of
        /// sight) are rated and the best one is the focus. Melee measures from the movement keys' direction in the walking
        /// frame (else the hero's facing) and, while the block is held, from the hero's facing; magic from the camera.
        /// When melee finds nothing the previous focus stays within 5 m (a downed one only while blocking). No lock, no
        /// target switching by mouse; who qualifies and comes first is G3's GetFocusEntityPriority (<see cref="FactsOf"/>,
        /// fix-68: companions, friends and allies fighting the hero's foe are never focused). Ranged (PS_Ranged: 35 m, ±45°, camera, no range rating) focuses
        /// for the HUD only: the arrow still flies at the screen centre, it never homes (G3 Shoot sets no target).
        /// </summary>
        void UpdateFocus()
        {
            if (!_fighter.Animator.Drawn || _dead || _knockedOut)
            {
                if (Focus != null) SetFocus(null);
                _heroTarget = null; // [interp] PS_Normal sets it to its own focus on entry; ours has none in combat terms
                return;
            }
            if (PinnedFocus != null)
            {
                if (PinnedFocus.isActiveAndEnabled && Session.Combat.Vitals.StateOf(PinnedFocus.Actor) != VitalState.Dead)
                {
                    if (Focus != PinnedFocus) SetFocus(PinnedFocus);
                    _heroTarget = PinnedFocus;
                    return;
                }
                PinnedFocus = null;
            }
            bool ranged = _fighter.RangedDrawn, magic = _fighter.SpellDrawn || ranged, firstPerson = FirstPerson;
            var reference = magic ? FocusReference.Camera : CombatFocus.MeleeReference(firstPerson, _input.BlockHeld);
            float cameraYaw = _camera != null ? _camera.transform.eulerAngles.y : _player.Yaw;
            var keys = _player.MoveKeys;
            float refYaw = reference == FocusReference.Keys
                ? CombatFocus.ReferenceYaw(reference, _player.Yaw, transform.eulerAngles.y, keys.x, keys.y)
                : CombatFocus.ReferenceYaw(reference, cameraYaw, transform.eulerAngles.y, 0, 0);
            var mode = ranged ? FocusScanMode.Ranged : magic ? FocusScanMode.Magic : FocusScanMode.Melee;
            var spellRule = mode == FocusScanMode.Magic ? ReadiedSpellFocus() : SpellFocusRule.Hostile;
            if (spellRule == SpellFocusRule.None)
            {
                // fix-207: a spell on the hero himself (Heal, the cures, teleports …) focuses nobody.
                if (Focus != null) SetFocus(null);
                _heroTarget = null;
                _candidates.Clear();
                _candidateFighters.Clear();
                _lastScan = "Magic self spell: no focus";
                return;
            }
            var found = Scan(refYaw, mode, firstPerson, rangeRating: !magic, spellRule);
            if (found == null && !magic && Focus != null && Focus.isActiveAndEnabled &&
                CombatFocus.KeepsPrevious(Session.Combat.Vitals.StateOf(Focus.Actor), DistanceCm(Focus), _input.BlockHeld))
                found = Focus;
            if (found != Focus) SetFocus(found);
            if (CombatFocus.SetsHeroTarget(mode)) _heroTarget = Focus;
        }

        /// <summary>The readied spell's focus rule (fix-207, <see cref="SpellFocus.RuleOf"/>): Heal Other focuses friends, self spells nobody.</summary>
        SpellFocusRule ReadiedSpellFocus() =>
            _spell != null && Session.Magic.TryGetSpell(_spell, out var def) ? SpellFocus.RuleOf(def) : SpellFocusRule.Hostile;

        CombatFighter Scan(float refYaw, FocusScanMode mode, bool hard, bool rangeRating, SpellFocusRule spellRule = SpellFocusRule.Hostile)
        {
            _candidates.Clear();
            _candidateFighters.Clear();
            var vitals = Session.Combat.Vitals;
            var origin = transform.position;
            float range = CombatFocus.ScanRangeCm(mode) / 100f;
            foreach (var f in _presenter.Fighters)
            {
                if (f == null || f == _fighter || !f.isActiveAndEnabled || vitals.StateOf(f.Actor) == VitalState.Dead) continue;
                var box = BodyBounds(f);
                float half = Mathf.Max(box.extents.x, box.extents.z);
                // G3 FocusCheck measures in the plane (the target's height set to the hero's) [fix-90]: a foe up a slope is
                // not farther than one on level ground.
                var flat = f.transform.position - origin;
                flat.y = 0f;
                float distance = flat.magnitude;
                if (distance * distance - half * half > range * range) continue;
                var to = box.center - origin;
                float elevation = hard && _camera != null
                    ? Elevation(box.center - _camera.transform.position) - Elevation(_camera.transform.forward)
                    : Elevation(to);
                _candidates.Add(new FocusCandidate
                {
                    AzimuthDeg = CombatFocus.DeltaAngle(refYaw, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg),
                    ElevationDeg = elevation,
                    DistanceCm = distance * 100f,
                    HalfExtentCm = half * 100f,
                    FreeLineOfSight = FreeLineOfSight(f, box),
                    Priority = mode == FocusScanMode.Melee ? CombatFocusPriority.Melee(FactsOf(f))
                             : mode == FocusScanMode.Magic ? CombatFocusPriority.Spell(FactsOf(f), spellRule) : CombatFocusPriority.Cast(FactsOf(f)),
                });
                _candidateFighters.Add(f);
            }
            int pick = CombatFocus.Pick(_candidates, mode, hard, rangeRating, ReachCm());
            _lastScan = $"{mode}{(hard ? " hard" : "")}{(spellRule != SpellFocusRule.Hostile ? " " + spellRule : "")} ref {refYaw:F1}";
            return pick >= 0 ? _candidateFighters[pick] : null;
        }

        string _lastScan = "-";

        /// <summary>Dev: the last focus scan, one entry per candidate (azimuth, distance, priority, line of sight, in scan).</summary>
        public string FocusScanText()
        {
            var sb = new System.Text.StringBuilder(_lastScan);
            for (int i = 0; i < _candidates.Count && i < _candidateFighters.Count; i++)
            {
                var c = _candidates[i];
                var f = _candidateFighters[i];
                sb.Append($"; {(f != null ? f.Actor.ToString() : "?")} azi {c.AzimuthDeg:F1} d {c.DistanceCm / 100f:F1} prio {c.Priority} los {c.FreeLineOfSight}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// The facts G3's GetFocusEntityPriority reads (fix-68): attitude to the hero, whether the NPC fights (not fleeing),
        /// whom (the hero, or his current target = the focus), the hero's party, the block.
        /// </summary>
        FocusFacts FactsOf(CombatFighter f)
        {
            var actor = f.Actor;
            var brain = actor.IsPlayer ? null : Session.NpcCombat.Get(actor);
            bool fighting = brain != null && brain.IsFighting && brain.State.Phase != Myrtana.Simulation.CombatPhase.Flee;
            var target = fighting ? brain.State.Target : default;
            var herosTarget = _heroTarget != null && _heroTarget != f ? _heroTarget.Actor : default;
            return new FocusFacts
            {
                IsNpc = !actor.IsPlayer && !actor.IsEmpty,
                State = Session.Combat.Vitals.StateOf(actor),
                AttitudeToHero = _presenter.AttitudeOf(actor, ActorId.Player),
                InCombat = fighting,
                TargetsHero = target.IsPlayer,
                TargetsHerosTarget = !target.IsEmpty && !herosTarget.IsEmpty && target == herosTarget,
                HerosParty = HitSides.InPartyOf(Session, actor, ActorId.Player) || HitSides.InPartyOf(Session, ActorId.Player, actor),
                HeroInParade = _fighter.Animator.HoldsParade || _input.InParade,
            };
        }

        static float Elevation(Vector3 v) => Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;

        float DistanceCm(CombatFighter f) => Vector3.Distance(f.transform.position, transform.position) * 100f;

        static Bounds BodyBounds(CombatFighter f)
        {
            var col = f.GetComponent<Collider>();
            return col != null && col.enabled ? col.bounds : new Bounds(f.transform.position + Vector3.up * 0.9f, new Vector3(0.8f, 1.8f, 0.8f));
        }

        /// <summary>The range rating's reach: the drawn melee weapon's length (else NormalModePreferedRange) plus half the hero's width.</summary>
        float ReachCm()
        {
            float half = (_cc != null ? _cc.radius : 0.35f) * 100f;
            var weapon = !_fighter.SpellDrawn && _prop != null && _prop.activeInHierarchy ? _prop.GetComponentInChildren<Renderer>() : null;
            if (weapon == null) return CombatFocus.PreferredRangeCm + half;
            var size = Vector3.Scale(weapon.localBounds.size, weapon.transform.lossyScale);
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 100f + half;
        }

        /// <summary>
        /// G3 FocusCheck → eCEntity::IsFreeLOS (fix-104, <see cref="FocusLineOfSight"/>, shared with the normal focus): a ray
        /// from 3/4 up the hero's box to 3/4 up the target's; world geometry blocks it, characters do not [interp].
        /// </summary>
        bool FreeLineOfSight(CombatFighter f, Bounds body) =>
            _sight.IsFree(PlayerInteraction.HeroSightPoint(transform, _cc), FocusLineOfSight.Point(body), transform, f.transform);

        /// <summary>Aligns the body to <paramref name="target"/> (null: the body holds its current yaw).</summary>
        void Align(CombatFighter target)
        {
            _aligned = true;
            _alignTarget = target;
            if (!_player.bodyYaw.HasValue) _player.bodyYaw = transform.eulerAngles.y;
        }

        /// <summary>Ends the alignment: the body turns back to the look direction at the same rate.</summary>
        void ReleaseAlignment()
        {
            _aligned = false;
            _alignTarget = null;
        }

        /// <summary>
        /// PS_Melee_Loop / PS_Magic_Loop between moves (CombatAlignment) and the body's turn at TurnSpeedMax: towards the
        /// alignment target while aligned, else back to the look yaw. The camera never turns with it.
        /// </summary>
        void UpdateAlignment(float dt)
        {
            if (_knockedOut || _dead) { ReleaseAlignment(); return; } // the lying body keeps its yaw
            if (_fighter.Placing || _fighter.Turning) return; // a finishing move or a move's motion turns the body itself (CombatFighter)
            var a = _fighter.Animator;
            if (!a.Drawn || _fighter.RangedDrawn) ReleaseAlignment();
            else if (!a.InMove && !_magic.Casting)
            {
                bool keys = _player.MoveKeys.sqrMagnitude > 1e-4f;
                var command = _fighter.SpellDrawn ? CombatAlignment.MagicLoop(keys)
                                                  : CombatAlignment.MeleeLoop(_input.BlockHeld, Focus != null, keys);
                if (command == CombatAlignment.LoopCommand.AlignToFocus) Align(Focus);
                else if (command == CombatAlignment.LoopCommand.Release) ReleaseAlignment();
            }
            if (!_player.bodyYaw.HasValue) return;
            bool sprinting = _player.MoveMode == G3MoveMode.Sprint;
            float body = _player.bodyYaw.Value, want = _player.Yaw;
            if (_aligned)
            {
                want = body;
                if (_alignTarget != null && _alignTarget.isActiveAndEnabled)
                {
                    var to = _alignTarget.transform.position - transform.position;
                    if (new Vector2(to.x, to.z).sqrMagnitude > 1e-4f) want = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                }
            }
            body = CombatAlignment.Turn(body, want, dt, sprinting);
            _player.bodyYaw = !_aligned && Mathf.Abs(Mathf.DeltaAngle(body, _player.Yaw)) < 0.01f ? (float?)null : body;
        }

        /// <summary>
        /// Opponents: NPCs fighting the hero and those hostile, angry or predatory towards him. Only the HUD colour uses
        /// this; which characters the focus takes is GetFocusEntityPriority (Docs/combat.md §9.2).
        /// </summary>
        bool IsEnemy(CombatFighter f)
        {
            var brain = Session.NpcCombat.Get(f.Actor);
            if (brain != null && brain.IsFighting && brain.State.Target.IsPlayer) return true;
            var attitude = _presenter.AttitudeOf(f.Actor, ActorId.Player);
            return attitude == Attitude.Hostile || attitude == Attitude.Angry || attitude == Attitude.Predator;
        }

        /// <summary>Per real second: the player's stamina change (StaminaRules) and the decay of the swing counter.</summary>
        void TickStamina(float dt)
        {
            _secondCarry += dt;
            if (_secondCarry < 1f) return;
            _secondCarry -= 1f;
            var state = new PlayerStaminaState
            {
                Sprinting = _player.MoveMode == G3MoveMode.Sprint && _player.Velocity.sqrMagnitude > 1f,
                Jumping = !_player.IsGrounded,
                HasSprinterPerk = Session.Hero.Knows("Perk_Sprinter"),
                Blocking = _fighter.Animator.HoldsParade,
                Diseased = Session.Magic.Status.Has(ActorId.Player, ActorStatus.Diseased), // +1 a second (combat-6)
                InMenu = _controls.IsLocked && !_knockedOut,
            };
            if (!_knockedOut) Session.Combat.Vitals.AddStamina(ActorId.Player, StaminaRules.PlayerPerSecond(state, Session.Settings.Rules));
            Session.Combat.PlayerPressure.TickSecond();
        }

        // ------------------------------------------------------------------------------------------- defeat

        /// <summary>Heavy hits shake the camera: stumbles a little, knock-downs more [interp: G3 shakes on heavy hits taken].</summary>
        void OnHit(HitOutcome outcome)
        {
            if (_camera == null || outcome.Result == HitResult.Ignored) return;
            switch (outcome.Reaction)
            {
                case HitReaction.Stumble:
                case HitReaction.PierceStumble:
                case HitReaction.HeavyParadeStumble:
                    _camera.Shake(hitShake);
                    break;
                case HitReaction.SitKnockDown:
                case HitReaction.LieKnockDown:
                case HitReaction.Unconscious:
                case HitReaction.PiercedKnockOut:
                case HitReaction.Dead:
                case HitReaction.PiercedDead:
                    _camera.Shake(hitShake * 2);
                    break;
            }
        }

        void OnFell(bool killed)
        {
            _input.Reset();
            DropWaitingSwitch("the hero fell");
            _pendingDraw = null; // fix-197: G3 ResetAll; the switch whose put-away the fall cut is over
            ClearQueue();
            _aimWanted = false;
            _magic.Abort();
            SetFocus(null);
            if (killed)
            {
                _dead = true;
                if (!_knockedOut) _controls.Acquire(this);
                _knockedOut = false;
                _gameOver?.Show();
                return;
            }
            if (!_knockedOut) _controls.Acquire(this);
            _knockedOut = true;
        }

        /// <summary>
        /// The knock-out lock outlived the down state (the vitals are back to Alive, but ActorGotUp never reached this view):
        /// the controls stay locked and the hero lies for good. Gets him up and frees the controls (fix-39).
        /// </summary>
        void HealKnockOutLock()
        {
            if (!_knockedOut || Session.Combat.Vitals.StateOf(ActorId.Player) != VitalState.Alive) return;
            Debug.Log("[Myrtana] hero knock-out lock outlived the down state: getting up");
            if (_fighter.Animator.IsDown && !_fighter.SittingKnockedDown) _fighter.GetUp();
            OnGotUp();
        }

        /// <summary>
        /// Dev safety valve (console <c>getup</c>, fix-39): out of any sit, seat or down state. A knocked-out hero is woken
        /// (the vitals' down state ends), a sitting one stands up plainly (the seat is the dev op's: HeroObjectUse).
        /// </summary>
        public string ForceGetUp()
        {
            if (Session == null) return "no session";
            if (_dead) return "dead: nothing to get up from";
            var parts = new List<string>();
            if (Session.Combat.Vitals.StateOf(ActorId.Player) == VitalState.Down)
            {
                Session.Combat.Vitals.WakeUp(ActorId.Player);
                parts.Add("woken from the knock-out");
            }
            if (_fighter.ForceStandUp()) parts.Add("stood up");
            _input.Reset();
            PrimaryHeld = SecondaryHeld = false;
            if (_knockedOut)
            {
                _knockedOut = false;
                _controls.Release(this);
                parts.Add("knock-out lock released");
            }
            return parts.Count == 0 ? "getup: not sitting, seated or down" : "getup: " + string.Join(", ", parts);
        }

        /// <summary>One line for the probe: how the hero sits, lies or is held (fix-39).</summary>
        public string SitStateText =>
            $"sit={_fighter.SittingKnockedDown}{(_fighter.PendingGetUp != CombatAction.None ? "(pending " + _fighter.PendingGetUp + ")" : "")} " +
            $"seated={Session != null && Session.Regeneration.Sitting} " +
            $"ko={_knockedOut} dead={_dead} locked={_controls.IsLocked}[{_controls.HoldersText}] cursor={CursorDirector.Mode}/{Cursor.lockState} " +
            $"stance={StanceActive} canFight={CanFight}{(CanFight ? "" : "(" + WhyNoFight() + ")")} anim={_fighter.Animator.StateText} " +
            $"stage={_fighter.Animator.StepStage ?? "-"} input={_input.Current}/{_input.Queued}";

        void OnGotUp()
        {
            if (!_knockedOut) return;
            _knockedOut = false;
            _controls.Release(this);
        }

        // ------------------------------------------------------------------------------------------- body

        void Displace(Vector3 offset)
        {
            if (_cc != null && _cc.enabled) _cc.Move(offset);
        }

        /// <summary>The weapon in its hand: melee in the right, a bow or crossbow in the left with the nocked ammunition in the right.</summary>
        void ShowWeapon()
        {
            var a = _fighter.Animator;
            string weapon = a.WeaponInHand ? a.WeaponTemplate : null;
            bool ranged = a.IsRanged;
            if (!a.WeaponInHand) _leftWanted = true; // the next draw takes BackLeft and BackRight together again (G3)
            if (_slot == null) _slot = FindDeep(_fighter.Visual, "Slot_RightHand_Weapon");
            if (_leftSlot == null) _leftSlot = FindDeep(_fighter.Visual, "Slot_LeftHand_Weapon");
            ShowProp(ref _prop, _slot, "Weapon", ranged ? (a.WeaponInHand ? a.AmmoTemplate : null) : weapon);
            ShowProp(ref _leftProp, _leftSlot, "Weapon L", ranged ? weapon : null);
            // Item effects (flame/ice/demon swords, staff glow, the fire arrow on the string; effects-2).
            G3ItemEffect.Sync(_prop, !a.WeaponInHand ? null : ranged ? _nockedAmmo : _fighter.WeaponItem?.Id);
            G3ItemEffect.Sync(_leftProp, ranged && a.WeaponInHand ? _fighter.WeaponItem?.Id : null);
        }

        void ShowProp(ref GameObject prop, Transform slot, string label, string template)
        {
            if (template == null || slot == null || _catalog == null || !_catalog.TryGetMesh(template, out var entry))
            {
                if (template != null) Debug.LogWarning($"[Myrtana] hero hand prop '{template}' not shown: slot {(slot != null ? slot.name : "missing")}, catalog {(_catalog != null ? "has no such mesh" : "missing")}");
                if (prop != null) prop.SetActive(false);
                return;
            }
            if (prop == null)
            {
                prop = new GameObject(label);
                prop.transform.SetParent(slot, false); // the item mesh is modelled in the slot frame
                prop.AddComponent<MeshFilter>();
                prop.AddComponent<MeshRenderer>();
            }
            prop.GetComponent<MeshFilter>().sharedMesh = entry.mesh;
            prop.GetComponent<MeshRenderer>().sharedMaterials = G3.Unity.G3CharacterRemaster.Map(entry.materials);
            prop.SetActive(true);
        }

        /// <summary>A sheathed weapon on its carry slot.</summary>
        sealed class Carried
        {
            public GameObject prop;
            public string key, slot;
        }

        /// <summary>
        /// The equipped melee and ranged weapons hang on their carry slots (<see cref="WeaponHold.CarrySlot"/>: 2H and axes
        /// on Slot_AxeBack, staves on Slot_StaffBack, 1H on Slot_MeleeBackRight, bows on Slot_Bow …) while not in the hand,
        /// modelled in the slot frame like the hand items.
        /// </summary>
        void ShowCarried()
        {
            var a = _fighter.Animator;
            string inHand = a.WeaponInHand ? a.WeaponTemplate : null;
            if (StaffInLeftHand) ShowOn(_carriedMelee, Equipped(WornSlot.MeleeWeapon), WeaponHold.LeftHand, "Carried"); // beside the spell
            else ShowCarried(_carriedMelee, Equipped(WornSlot.MeleeWeapon), inHand, "Carried");
            ShowCarried(_carriedRanged, Equipped(WornSlot.RangedWeapon), inHand, "Carried Ranged");
            ShowOffHand();
            ShowShield();
        }

        /// <summary>The hero holds the torch in the left hand.</summary>
        public bool TorchInLeftHand => _hero != null && _hero.OffHand == HeroTorchView.TorchField;

        /// <summary>A melee draw of <paramref name="weapon"/> takes the off-hand blade too (DualWieldRules.DrawsOffHand, and not put away by its key).</summary>
        bool DrawsBothBlades(WeaponClass weapon) =>
            Session != null && _leftWanted && DualWieldRules.DrawsOffHand(weapon, Session.Items.Equipment.DualWieldReady, TorchInLeftHand);

        /// <summary>
        /// G3 dual wielding (fix-32): the off-hand weapon (gESlot_BackLeft) is in the left hand (Slot_LeftHand_Weapon) while
        /// the main one-handed weapon is in the right one, else on Slot_MeleeBackLeft. In the hand the locomotion and the moves
        /// use the 1H_1H set and the fighter strikes with both blades (<see cref="CombatFighter.DualWielding"/>).
        /// </summary>
        void ShowOffHand()
        {
            var equipment = Session.Items.Equipment;
            var worn = Equipped(WornSlot.OffHand);
            var off = equipment.DualWieldReady ? worn : null;
            if (_fighter.OffHandItem != off) _fighter.SetOffHand(off);
            bool held = off != null && _leftWanted && !TorchInLeftHand && ShieldInHand;
            _fighter.OffHandHeld = held;
            ShowOn(_carriedOffHand, worn, worn != null ? WeaponHold.OffHandSlot(held) : null, "Off-hand");
        }

        /// <summary>
        /// The worn shield (combat-6): on Slot_ShieldBack, and in the left hand (Slot_LeftHand_Shield) while a one-handed
        /// weapon is in the right one (G3 HoldStacks draws them together); then the locomotion and the moves use the
        /// <c>Shield_1H</c> motion set (G3 GetAniName: the left field is the shield's use type). A torch in the left hand wins
        /// (the shield goes to the back).
        /// </summary>
        void ShowShield()
        {
            var shield = Equipped(WornSlot.Shield);
            bool torch = _hero != null && _hero.OffHand == HeroTorchView.TorchField;
            // fix-192: hidden while a two-handed weapon, staff, bow or crossbow is in the hands (WeaponHold.ShieldSlot)
            string shieldSlot = shield != null ? WeaponHold.ShieldSlot(MainMeleeClass, DrawnClass) : null;
            if (torch && shieldSlot == WeaponHold.LeftHandShield) shieldSlot = WeaponHold.ShieldBack;
            bool inHand = shieldSlot == WeaponHold.LeftHandShield;
            ShowOn(_carriedShield, shield, shieldSlot, "Shield");
            if (torch || _hero == null) return;
            bool dual = _fighter.DualWielding;
            string field = StaffInLeftHand ? G3CombatMotions.Staff : dual ? G3CombatMotions.DualOffHand : inHand ? G3CombatMotions.Shield : null;
            if (_hero.OffHand != field) _hero.SetOffHand(field);
            // The combat motions take the 1H_1H set from the draw on (Hold_Begin, before the blades reach the hands) and
            // through the sheathe (Hold_End).
            var a = _fighter.Animator;
            bool drawing = a.Drawn && !a.WeaponInHand && !_fighter.RangedDrawn && !_fighter.SpellDrawn && DrawsBothBlades(_fighter.WeaponClass);
            string motions = dual || drawing ? G3CombatMotions.DualOffHand : field;
            if (a.OffHand != motions) a.OffHand = motions;
        }

        /// <summary>
        /// A one-handed weapon or the hero's torch is in the left hand beside a melee weapon (G3 FUN_100041a0: SimpleWhirl;
        /// quick attacks alternate in P1): the torch, or the dual wielder's second blade.
        /// </summary>
        bool LeftOneHandedOrTorch => (TorchInLeftHand || _fighter.DualWielding) && !_fighter.RangedDrawn && !_fighter.SpellDrawn;

        /// <summary>The class of the worn main melee weapon (fists when none).</summary>
        WeaponClass MainMeleeClass =>
            Equipped(WornSlot.MeleeWeapon) is ItemDef melee && CombatLoadout.WeaponClassOf(melee.Kind) is WeaponClass w ? w : WeaponClass.Fist;

        /// <summary>The class of the weapon in the hands: the drawn bow or crossbow, a melee weapon, a staff beside a spell; null = none.</summary>
        WeaponClass? DrawnClass
        {
            get
            {
                if (_fighter == null) return null;
                if (_fighter.RangedDrawn) return _fighter.WeaponClass == WeaponClass.Crossbow ? WeaponClass.Crossbow : WeaponClass.Bow;
                var held = _fighter.HeldWeaponClass;
                return held == WeaponClass.Fist ? (WeaponClass?)null : held;
            }
        }

        /// <summary>A one-handed weapon is in the right hand (the shield, if worn, is in the left).</summary>
        public bool ShieldInHand => _fighter != null && _fighter.HeldWeaponClass == WeaponClass.OneHanded && !_fighter.RangedDrawn;

        void ShowCarried(Carried c, ItemDef item, string inHand, string label)
        {
            string key = CombatFighter.VisualKey(item);
            var weapon = item != null ? CombatLoadout.WeaponClassOf(item.Kind) : null;
            string slotName = weapon != null ? WeaponHold.CarrySlot(weapon.Value) : null;
            if (key == null || slotName == null || key == inHand) slotName = null;
            ShowOn(c, item, slotName, label);
        }

        /// <summary>The item's mesh on the named slot (null: hidden), modelled in the slot frame.</summary>
        void ShowOn(Carried c, ItemDef item, string slotName, string label)
        {
            string key = slotName != null ? CombatFighter.VisualKey(item) : null;
            if (key == null) slotName = null;
            if (key == c.key && slotName == c.slot) return;
            c.key = key;
            c.slot = slotName;
            var slot = slotName != null ? FindDeep(_fighter.Visual, slotName) : null;
            if (slot == null || _catalog == null || !_catalog.TryGetMesh(key, out var entry))
            {
                if (c.prop != null) c.prop.SetActive(false);
                return;
            }
            if (c.prop == null)
            {
                c.prop = new GameObject(label);
                c.prop.AddComponent<MeshFilter>();
                c.prop.AddComponent<MeshRenderer>();
            }
            c.prop.transform.SetParent(slot, false); // modelled in the slot frame
            c.prop.GetComponent<MeshFilter>().sharedMesh = entry.mesh;
            c.prop.GetComponent<MeshRenderer>().sharedMaterials = G3.Unity.G3CharacterRemaster.Map(entry.materials);
            c.prop.SetActive(true);
            G3ItemEffect.Sync(c.prop, item?.Id); // a flame sword burns on the back too (effects-2)
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t == null) return null;
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
