using System;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The body of one fighter (the hero or a shown NPC) as the combat views see it: its combat motions
    /// (<see cref="G3CombatAnimator"/>, played at the rules' speed factors), its hittable <see cref="CombatBody"/>, the
    /// weapon sweep of its swings (<see cref="MeleeSwing"/>), the move it is in, and its reactions to resolved hits. It
    /// decides nothing: the player adapter or the NPC combat view starts its moves (and its shots), and
    /// <see cref="CombatPresenter"/> turns its touches into hits.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatFighter : MonoBehaviour
    {
        // An NPC gets up when its sit motion has played once (G3 ZS_SitKnockDown); this caps it when the rig has no sit
        // loop (it fell into LieKnockDown) [interp].
        const float SitKnockDownMaxSeconds = 4f;

        MeleeSwing _swing, _leftSwing; // the right-hand weapon (or claws), the dual wielder's left blade
        int _movePose; // combo pose the current move started in (which blade strikes, DualWieldRules.Blades)
        Action<Vector3> _mover;
        FighterAction _reaction;
        float _sitUntil = -1;
        bool _sitting;
        CombatAction _pendingGetUp; // a get-up asked during the fall: starts when the sit loop is reached (G3 queue)
        float _pendingSince;        // when it was asked
        float _getUpParadeAt = -1f; // when the GetUpParade motion began (fix-67)
        const float PendingGetUpMaxSeconds = 3f; // the longest fall motion is well under this (fix-39 safety cap)
        float _sitLoopPhase = -1;   // the sit loop's last normalised time (-1 = not reached); a wrap = played once
        bool _hitDirection;         // G3 PSRoutine HitDirection: picks the side of the next QuickAttack
        Vector3 _hitFrom; // the hitter's position of the last reaction, fall or death (the combat movement goes away from it)
        bool _hasHitFrom;
        CombatMotion _motion; // speed-table motion of the animator's current one-shot
        readonly FinishingStrike _finishing = new FinishingStrike();
        // Finishing move placement (FinishingPlacement, Docs/combat.md §5.5): the spot, the slide of the current phase.
        bool _placing, _stabLogged;
        FinishingReach _reach;
        FinishingSpot _spot;
        CombatFighter _victim;
        Transform _victimPelvis, _victimChest;
        G3SwingPhase _placePhase;
        Vector3 _slideFrom;
        float _yawFrom;
        // Log: the slide asked of the mover vs what the body took, the first body in the way, the last yaw asked of the turner.
        float _slideAsked, _slideTook, _turnAsked = float.NaN;
        string _slideBlocker;
        // Combat movement of the current motion (CombatLunge, Docs/combat.md §5.6).
        int _lungeSerial = -1;
        bool _lungeActive, _lungeContact, _turning;
        LungePlan _lunge;
        float _lungeK, _lungeYawFrom, _lungeDone, _lungeTook, _contactGap;
        string _heldBy, _blockedBy; // log: the fighter whose footprint held the body back, the collider that stopped the mover
        Vector3 _lungeStart;
        string _lungeLabel;
        CombatFighter _lungeTarget;
        static readonly Collider[] _overlap = new Collider[16];
        static readonly RaycastHit[] _walls = new RaycastHit[8];
        static readonly RaycastHit[] _ground = new RaycastHit[8];

        public ActorId Actor { get; private set; }
        public G3CombatAnimator Animator { get; private set; }
        public CombatBody Body { get; private set; }
        public Transform Visual { get; private set; }
        public bool Humanoid { get; private set; }
        /// <summary>The move being played (None between moves).</summary>
        public CombatAction CurrentAction { get; private set; }
        /// <summary>The weapon used for hits (null: fists or claws).</summary>
        public ItemDef WeaponItem { get; private set; }
        public WeaponClass WeaponClass { get; private set; } = WeaponClass.Fist;
        /// <summary>
        /// The weapon that really is in the hand: <see cref="WeaponItem"/> once the draw has put it there, else null (fists).
        /// Hits and drops use this, not the selected weapon: an NPC whose draw was cut short, or that put its weapon away,
        /// strikes with its fists and drops nothing (G3 reads the hand's stacks; combat-6).
        /// </summary>
        public ItemDef HeldWeaponItem => HoldsWeapon ? WeaponItem : null;
        /// <summary>
        /// The class of <see cref="HeldWeaponItem"/>; fists while nothing is in the hand. In the magic stance a staff in the left
        /// hand beside the spell (<see cref="CastStaff"/>) counts: it blocks (G3 HasStaffMagicParade).
        /// </summary>
        public WeaponClass HeldWeaponClass =>
            HoldsWeapon ? WeaponClass : CastStaff && Animator != null && Animator.IsCastStance && Animator.Drawn ? WeaponClass.Staff : WeaponClass.Fist;
        /// <summary>A staff is in the left hand beside the drawn spell (the hero's view sets it; G3 PS_Magic HoldStacks).</summary>
        public bool CastStaff { get; set; }
        bool HoldsWeapon => Animator != null && Animator.WeaponInHand && !Animator.IsCastStance;

        /// <summary>A dual wielder's second one-handed weapon (G3 gESlot_BackLeft; null = none). The view sets it.</summary>
        public ItemDef OffHandItem { get; private set; }
        /// <summary>The second blade is in the left hand (drawn with the main one, or by its own key; the view sets it).</summary>
        public bool OffHandHeld { get; set; }
        /// <summary>The second blade really in the left hand beside a one-handed weapon in the right; else null.</summary>
        public ItemDef HeldOffHandItem =>
            OffHandHeld && OffHandItem != null && HeldWeaponClass == WeaponClass.OneHanded && !Animator.IsRanged ? OffHandItem : null;
        /// <summary>Fights with a one-handed weapon in each hand now (G3 1H_1H; the rules' <see cref="Combatant.DualWield"/>).</summary>
        public bool DualWielding => HeldOffHandItem != null;

        public void SetOffHand(ItemDef item)
        {
            OffHandItem = item;
            if (item == null) OffHandHeld = false;
        }

        /// <summary>The weapon of a blade: the left one is the off-hand item, the right one the held weapon (null: fists).</summary>
        public ItemDef BladeItem(MeleeBlades blade) => blade == MeleeBlades.Left ? HeldOffHandItem : HeldWeaponItem;
        Transform _spine, _spineVisual;
        bool _spineSearched;
        /// <summary>Where NPC arrows fly: the Spine_Spine_1 bone (G3 gCProjectile_PS::GetDirection), else the body's centre.</summary>
        public Vector3 SpinePoint
        {
            get
            {
                if (!_spineSearched || _spine == null && Visual != null && _spineVisual != Visual)
                {
                    _spineSearched = true;
                    _spineVisual = Visual;
                    _spine = FindSuffix(Visual != null ? Visual : transform, "Spine_Spine_1");
                }
                return _spine != null ? _spine.position : Body != null ? Body.Center : transform.position + Vector3.up;
            }
        }
        Transform _weaponSlot, _weaponSlotVisual;
        /// <summary>
        /// Where G3 starts the collision effects of this fighter's blow (fix-55): DoLogicalDamage, AssessHit's parade clash
        /// and OnAI_FinishingAttack call EffectSystem::StartEffect on the attacker's held weapon item, i.e. at the item's
        /// origin in the right-hand slot. False when nothing is held there (fists, creatures).
        /// </summary>
        public bool TryGetWeaponPoint(out Vector3 point)
        {
            point = default;
            if (HeldWeaponItem == null || Visual == null) return false;
            if (_weaponSlot == null || _weaponSlotVisual != Visual)
            {
                _weaponSlotVisual = Visual;
                _weaponSlot = FindSuffix(Visual, "Slot_RightHand_Weapon");
            }
            if (_weaponSlot == null) return false;
            point = _weaponSlot.position;
            return true;
        }

        /// <summary>Farther than this from the victim's centre the weapon point is not used (a stale pose, a long reach) [interp].</summary>
        const float BlowPointMaxDistance = 1.6f;

        /// <summary>
        /// Where a blow's collision effect (blood, sparks, the parade clash) appears: at the attacker's held weapon as G3
        /// starts it (<see cref="TryGetWeaponPoint"/>); without one, on the victim's body surface facing the attacker, never
        /// at the body's centre where the particles stay hidden inside the mesh (fix-55, r30: no sparks on a parade).
        /// </summary>
        public static Vector3 BlowPoint(CombatFighter attacker, CombatFighter victim, bool melee = true)
        {
            Vector3 center = victim.Body != null ? victim.Body.Center : victim.transform.position + Vector3.up;
            if (attacker == null) return center;
            if (melee && attacker.TryGetWeaponPoint(out var weapon) && (weapon - center).sqrMagnitude <= BlowPointMaxDistance * BlowPointMaxDistance)
                return weapon;
            var to = attacker.transform.position - victim.transform.position;
            to.y = 0;
            if (to.sqrMagnitude < 1e-6f) return center;
            float radius = victim.Body != null ? victim.Body.Radius : 0.4f;
            return center + to.normalized * radius;
        }

        /// <summary>The fighter's opponent (the NPC's target, the hero's locked focus), or null.</summary>
        public CombatFighter Opponent { get; set; }
        /// <summary>The parade counts for blocking (set by the owner: NPCs raise it only after the stance time).</summary>
        public Func<bool> ParadeUp { get; set; }
        /// <summary>The body moves on its own (running, strafing), for the opponents' AI.</summary>
        public Func<bool> Moving { get; set; }
        /// <summary>Playback speed of a combat motion (the power attack's wind-up flagged); set by the presenter from the rules.</summary>
        public Func<CombatMotion, bool, float> SpeedFactor { get; set; }
        /// <summary>
        /// How a sitting knock-down ends when its time is up (GetUpAttack, GetUpParade, or None to just stand up); the
        /// NPC view decides and pays it. The player gets up by its buttons, or plainly when the time is up.
        /// </summary>
        public Func<CombatAction> GetUpChoice { get; set; }
        /// <summary>A bow or crossbow is drawn.</summary>
        public bool RangedDrawn => Animator != null && Animator.Drawn && Animator.IsRanged;
        /// <summary>The spell hand is drawn (G3 PS_Magic).</summary>
        public bool SpellDrawn => Animator != null && Animator.Drawn && Animator.IsCastStance;
        /// <summary>Frozen by ice: the body stands still (G3 ZS_Freeze, time scale 0).</summary>
        public bool Frozen { get; private set; }
        public bool SittingKnockedDown => _sitting;

        /// <summary>A get-up pressed during the fall, waiting for the sit loop (None: none; the dev probe).</summary>
        public CombatAction PendingGetUp => _pendingGetUp;
        /// <summary>A one-handed weapon or a torch in the left hand (G3 FUN_100041a0): SimpleWhirl, quick attacks alternate in P1.</summary>
        public bool LeftOneHandedOrTorch { get; set; }
        /// <summary>The hit direction (G3 HitDirection 0/1) the last attack left; the next QuickAttack's side depends on it.</summary>
        public bool HitDirection => _hitDirection;
        public bool IsParrying => (ParadeUp != null ? ParadeUp() : Animator != null && Animator.HoldsParade) || GetUpParadeRaised;

        /// <summary>
        /// fix-67 (r33 hammerclan_02 R33-2): G3 OnAI_GetUpParade switches the ani state to Parade 0.25 s into the get-up
        /// (<see cref="SitKnockDownRules.GetUpParadeBlocks"/>), so the rest of the GetUpParade motion parries like a raised
        /// block (DefenseLevel 2, AssessHit). Before, the motion never counted as a parade: every power attack during it
        /// landed with D &gt; 3 and sat the hero down again (43 s of sit / GetUpParade against the Big Mine boss).
        /// </summary>
        public bool GetUpParadeRaised =>
            CurrentAction == CombatAction.GetUpParade && _getUpParadeAt >= 0f && Animator != null && Animator.InMove &&
            Animator.Action == "GetUpParade" && SitKnockDownRules.GetUpParadeBlocks(Time.time - _getUpParadeAt);

        /// <summary>The weapon touched another fighter's body during a swing.</summary>
        public event Action<CombatFighter, CombatBody, MeleeBlades> Touched;

        /// <summary>A held weapon touched the level during a swing (G3 OnItemTouchesLevel): the fighter, the blade, the contact.</summary>
        public static event Action<CombatFighter, MeleeBlades, RaycastHit> WeaponTouchedLevel;
        /// <summary>A started move ended (or could not be shown).</summary>
        public event Action<CombatAction> MoveEnded;
        /// <summary>
        /// A hit reaction is about to play (before the running move is reported over): the owner's input leaves the melee
        /// loop (G3 AssessHit → SetTask ZS_…; <see cref="HitReactionRules"/>).
        /// </summary>
        public event Action<HitReaction> ReactionStarting;
        /// <summary>A hit reaction's Hit or a draw is over (<see cref="G3CombatAnimator.Freed"/>): input may act again.</summary>
        public event Action Freed;
        /// <summary>The blow of a finishing move landed on the target it started on (by time, G3 OnAI_FinishingAttack).</summary>
        public event Action<CombatFighter, ActorId> FinishingBlow;
        /// <summary>Sets the body's yaw (degrees, Unity eulerAngles.y); null: the fighter rotates its own transform. The hero's body yaw lives in G3Player.</summary>
        public Action<float> Turner { get; set; }
        /// <summary>A finishing move is placing the body (sliding to its spot and turning to the victim): owners leave the yaw alone.</summary>
        public bool Placing => _placing;
        /// <summary>A move's motion turns the body to its target (G3 combat rotation, §5.6): owners leave the yaw alone.</summary>
        public bool Turning => _turning;
        /// <summary>The owner's mover collides with the world by itself (the hero's CharacterController): no wall probe here.</summary>
        public bool MoverCollides { get; set; }
        /// <summary>Log one line per combat motion that travels: planned vs actual distance (Docs/combat.md §5.6).</summary>
        public static bool LogLunges;
        bool _wasSwinging;
        CombatAction _swingAction;

        /// <param name="visual">The animated character (has the <see cref="G3MotionPlayer"/>).</param>
        /// <param name="reach">Reach of a monster's bite, m.</param>
        /// <param name="mover">Displaces the body by a world offset (root motion of lunges and stumbles).</param>
        public void Init(ActorId actor, Transform visual, bool humanoid, float reach, Action<Vector3> mover)
        {
            Actor = actor;
            Humanoid = humanoid;
            _mover = mover;
            if (Visual != visual)
            {
                Visual = visual;
                Animator = visual.TryGetComponent<G3CombatAnimator>(out var a) ? a : visual.gameObject.AddComponent<G3CombatAnimator>();
                Animator.MoveEnded -= OnAnimatorMoveEnded;
                Animator.MoveEnded += OnAnimatorMoveEnded;
                Animator.Freed -= OnAnimatorFreed;
                Animator.Freed += OnAnimatorFreed;
                Animator.SpeedOf = MotionSpeed;
                if (Body == null) Body = CombatBody.Create(this, visual);
                else Body.Refit(visual); // a new body for the same fighter (the hero's animal form)
            }
            _swing = new MeleeSwing(transform, visual, Body, humanoid, reach);
            _leftSwing = humanoid ? new MeleeSwing(transform, visual, Body, true, reach, WeaponHold.LeftHand) : null;
            ResetState();
        }

        /// <summary>Unarmed, standing, no move (a reused view, a loaded game).</summary>
        public void ResetState()
        {
            CurrentAction = CombatAction.None;
            Opponent = null;
            _finishing.Cancel();
            _placing = false;
            _reaction = FighterAction.None;
            _sitting = false;
            _sitUntil = -1;
            _pendingGetUp = CombatAction.None;
            _hitDirection = false;
            _swing?.Stop();
            _leftSwing?.Stop();
            CorpseRest.RemoveFrom(Visual);
            Ragdoll.RemoveFrom(Visual);
            if (Animator != null) Animator.ResetAll();
            if (Frozen) SetFrozen(false);
            SetFooting(true);
        }

        /// <summary>fix-180: the relief footing of the body on (alive) or off with its pose put back (dead).</summary>
        void SetFooting(bool on)
        {
            if (Visual != null && Visual.TryGetComponent<global::G3.Unity.G3ReliefFooting>(out var foot) && foot.enabled != on) foot.enabled = on;
        }

        public void SetWeapon(ItemDef item, WeaponClass weapon)
        {
            WeaponItem = item;
            WeaponClass = weapon;
        }

        // ------------------------------------------------------------------------------------------- moves

        /// <summary>Draws the weapon; the hand shows the item's mesh (item visuals are keyed by mesh name).</summary>
        public bool Draw() => Animator.Draw(CombatMotionNames.HandField(WeaponClass), VisualKey(WeaponItem));

        /// <summary>The item visual's catalog key: its mesh, or its actor for bows and crossbows (<see cref="ItemVisualCatalog"/>).</summary>
        public static string VisualKey(ItemDef item) => item?.Visual?.Mesh ?? item?.Visual?.Body;

        public bool Sheathe() => Animator.Sheathe();

        /// <summary>
        /// Draws the spell hand (G3 PS_Magic: right-hand field "Cast"). Nothing is shown in the hand but the spell's glow,
        /// except <paramref name="heldMesh"/>: a teleport stone is held as the stone itself (fix-261 [owner]).
        /// </summary>
        public bool DrawSpell(string heldMesh = null)
        {
            SetWeapon(null, WeaponClass.Fist);
            return Animator.Draw(G3.Unity.Characters.G3CombatMotions.Cast, heldMesh);
        }

        /// <summary>Freezes or thaws the body: its motions stop (and the owner stops moving it).</summary>
        public void SetFrozen(bool frozen)
        {
            Frozen = frozen;
            if (Visual != null && Visual.TryGetComponent<G3MotionPlayer>(out var player)) player.TimeScale = frozen ? 0f : 1f;
        }

        /// <summary>
        /// Plays a move (attack, jump back); false when the rig has no motion for it. A QuickAttack takes its side here
        /// (G3 _AI_QuickAttack: by the combo pose and the hit direction, <see cref="MeleeComboRules.QuickAttackSide"/>);
        /// <see cref="CurrentAction"/> tells which one played.
        /// </summary>
        public bool StartMove(CombatAction action, int combo = 0)
        {
            _motion = MotionSpeedRules.MotionOf(action);
            if (_sitting && (action == CombatAction.GetUpAttack || action == CombatAction.GetUpParade))
            {
                if (Animator.StepStage == "Loop") return GetUpWith(action);
                // Still falling: G3 queues the press and ZS_SitKnockDown_Loop takes it once the sit is reached.
                _pendingGetUp = action;
                _pendingSince = Time.time;
                CurrentAction = action;
                return true;
            }
            if (action == CombatAction.Parade || action == CombatAction.GetUpParade) return Animator.RaiseParade();
            if (_sitting) return false;
            if (action == CombatAction.QuickAttack) action = MeleeComboRules.QuickAttackSide(combo, _hitDirection, LeftOneHandedOrTorch);
            _movePose = combo;
            var shape = MeleeComboRules.ShapeOf(action); // G3: which phases, and whether the Recover belongs to the move
            foreach (var name in CombatMotionNames.Actions(action))
            {
                if (!Animator.Attack(name, combo, shape.Raise, shape.RecoverInMove, (float)shape.WaitAfterHit)) continue;
                CurrentAction = action;
                _hitDirection = MeleeComboRules.HitDirectionAfter(action, combo, _hitDirection);
                _reaction = FighterAction.None;
                if (action == CombatAction.FinishingAttack) _finishing.Begin(Opponent != null ? Opponent.Actor : default, Time.timeAsDouble);
                else _finishing.Cancel();
                if (action == CombatAction.FinishingAttack) BeginPlacement();
                else _placing = false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets up from sitting knocked down with the GetUpAttack (a swing) or the GetUpParade (CP: the knocked-down
        /// player's primary or secondary button); plain getting up when the rig has no such motion.
        /// </summary>
        bool GetUpWith(CombatAction action)
        {
            _sitting = false;
            _sitUntil = -1;
            _pendingGetUp = CombatAction.None;
            _sitLoopPhase = -1;
            var shape = MeleeComboRules.ShapeOf(action); // G3 _AI_GetUpAttack: the Hit only, the Recover is a tail
            _getUpParadeAt = -1f;
            if (Animator.GetUpWith(action == CombatAction.GetUpAttack, shape.Raise, shape.RecoverInMove))
            {
                CurrentAction = action;
                if (action == CombatAction.GetUpParade) _getUpParadeAt = Time.time;
                _reaction = FighterAction.None;
                if (action == CombatAction.GetUpAttack) _hitDirection = MeleeComboRules.HitDirectionAfter(action, 0, _hitDirection);
                return true;
            }
            Animator.GetUp();
            return false;
        }

        public FighterAction FighterAction
        {
            get
            {
                if (Animator == null) return FighterAction.None;
                if (Animator.InMove) return CombatMotionNames.ToFighterAction(CurrentAction);
                if (Animator.InParade) return FighterAction.Parade;
                if (Animator.InReaction) return _reaction;
                if (Animator.InCast) return FighterAction.Cast;
                if (Animator.InAim || Animator.InShot) return FighterAction.Shoot;
                return Moving != null && Moving() ? FighterAction.Move : FighterAction.None;
            }
        }

        public MovePhase Phase => Animator != null && Animator.InMove ? CombatMotionNames.ToMovePhase(Animator.Phase) : MovePhase.None;

        // ------------------------------------------------------------------------------------------- reactions

        /// <summary>Shows a resolved hit on this fighter: a stumble, a parade stumble or a sitting knock-down.</summary>
        public void ShowHit(HitOutcome outcome, Vector3 from)
        {
            bool back = BackVariant(outcome.Reaction, from);
            bool right = Vector3.Dot(transform.right, from - transform.position) > 0;
            SetHitFrom(from);
            if (!HitReactionRules.ShowsReaction(Frozen, outcome.FreezeSeconds)) return; // frozen stiff: no stumble (fix-48)
            _motion = MotionSpeedRules.MotionOf(outcome.Reaction);
            if (outcome.Reaction == HitReaction.SitKnockDown)
            {
                SitDown(back);
                return;
            }
            var names = CombatMotionNames.Reaction(outcome.Reaction, right);
            if (names == null) return;
            // The hero's parried hit keeps his block up (G3 _AI_ParadeStumble: ani state Parade; ParadeHold, fix-43). NPCs
            // leave it: their brain orders the next parade.
            bool keepParade = Actor.IsPlayer && ParadeHold.KeepsParade(Animator.HoldsParade, outcome.Reaction);
            ReactionStarting?.Invoke(outcome.Reaction); // before InterruptMove: the queue must not start the cut move's follower
            InterruptMove();
            if (Animator.React(back, keepParade, names)) _reaction = ReactionAction(outcome.Reaction);
        }

        /// <summary>
        /// The reaction's or fall's motion variant (G3 sAICombatMoveStart, fix-244): Back for a stumble or fall whose hitter
        /// stands in front, Fwd otherwise (<see cref="HitReactionRules.PlaysBackVariant"/>).
        /// </summary>
        bool BackVariant(HitReaction reaction, Vector3 from) =>
            HitReactionRules.PlaysBackVariant(reaction, HitReactionRules.HitterInFront(Vector3.Dot(transform.forward, from - transform.position)));

        /// <summary>
        /// Falls into the sitting knock-down (G3 ZS_SitKnockDown): the player sits until a button is pressed, an NPC gets
        /// up after the sit motion played once (capped). Public for the dev scenarios (op <c>sit</c>).
        /// </summary>
        /// <param name="back">The Back variant of the fall (the hitter in front, <see cref="BackVariant"/>).</param>
        public void SitDown(bool back)
        {
            // Sitting before the running move is reported over: the move queued behind it must not start (G3 ResetAll).
            _sitting = true;
            InterruptMove();
            _sitting = Animator.FallDown("SitKnockDown", back);
            _pendingGetUp = CombatAction.None;
            _sitLoopPhase = -1;
            _sitUntil = SitKnockDownRules.GetsUpByItself(Actor.IsPlayer) ? Time.time + SitKnockDownMaxSeconds : -1;
            if (_sitting) SatDown?.Invoke();
        }

        /// <summary>The sitting knock-down began (the player adapter forgets its queued move and held buttons, G3 ResetAll).</summary>
        public event Action SatDown;

        /// <summary>Knocked out (or knocked down for good): falls and lies until <see cref="GetUp"/>.</summary>
        /// <param name="atOnce">Already lying (a loaded game, a view streaming in): the down pose without the fall (fix-80).</param>
        public void KnockOut(HitReaction reaction, Vector3 from, bool atOnce = false)
        {
            InterruptMove();
            SetHitFrom(from);
            _sitting = false;
            bool back = BackVariant(CombatMotionNames.DownStance(reaction) != null ? reaction : HitReaction.LieKnockDown, from);
            string stance = CombatMotionNames.DownStance(reaction) ?? "LieKnockDown";
            if (atOnce) Animator.LieAtOnce(false, stance, back);
            else Animator.FallDown(stance, back);
        }

        public void GetUp()
        {
            _sitting = false;
            Animator.GetUp();
        }

        /// <summary>
        /// Dev safety valve (console <c>getup</c>, fix-39): out of the sit or the down pose at once, weapon kept. False when
        /// he neither sat nor lay.
        /// </summary>
        public bool ForceStandUp()
        {
            bool was = _sitting || Animator.IsDown;
            _sitting = false;
            _sitUntil = -1;
            _pendingGetUp = CombatAction.None;
            InterruptMove();
            if (Animator.IsDown) Animator.GetUp();
            if (Animator.IsDown) Animator.StandUpNow();
            return was;
        }

        /// <summary>
        /// Shows the death: the dying motion (from a down state) or the LieDead / LiePiercedDead pose; with
        /// <paramref name="ragdoll"/> a standing NPC collapses as a ragdoll pushed away from <paramref name="from"/>
        /// (ZS_RagDollDead, <see cref="RagdollRules"/>) when its skeleton has the humanoid bones. <paramref name="atOnce"/>: a
        /// corpse met again (a loaded game, a view streaming in) lies in its dead pose at once, no dying motion (G3
        /// OnExitProcessingRange: AniState 0x13, ForceNextPose). An NPC's body then comes to rest on the ground
        /// (<see cref="CorpseRest"/>, fix-80).
        /// </summary>
        public void Die(HitReaction reaction, Vector3 from, RagdollImpulse? ragdoll = null, Collider ignore = null, bool atOnce = false)
        {
            InterruptMove();
            SetHitFrom(from);
            _sitting = false;
            // fix-101: G3 Kill → DropHandItems ends with HoldStacks(-1, -1): the hands are empty before the death motion
            // (an NPC's droppable weapon was dropped already, CombatPresenter.Drop). The hero kept a bow in the hand and,
            // with no Bow set death motion (Hero_Stand_None_None_P0_LieDead_Begin only), froze standing in the aim pose.
            if (Animator.Drawn || Animator.WeaponInHand) Animator.DropWeapon();
            // fix-180: a dead body lies in its dead pose: a frozen (ZS_Freeze) one would never fade into it, and the relief
            // footing (ground offset, leg IK, four-legged pitch) lets go of it; CorpseRest lays it on the drawn ground
            if (Frozen) SetFrozen(false);
            SetFooting(false);
            bool back = BackVariant(reaction == HitReaction.PiercedDead ? HitReaction.PiercedDead : HitReaction.Dead, from);
            bool wasDown = Animator.IsDown;
            string stance = reaction == HitReaction.PiercedDead ? "LiePiercedDead" : "LieDead";
            if (atOnce) Animator.LieAtOnce(true, stance, back);
            else Animator.Die(back, stance);
            if (!Actor.IsPlayer) CorpseRest.Begin(Visual, restored: atOnce);
            if (atOnce || !RagdollRules.UsesRagdoll(reaction, wasDown, live: ragdoll != null) || Actor.IsPlayer || !Humanoid) return;
            var away = transform.position - from;
            away.y = 0;
            away = away.sqrMagnitude > 1e-6f ? away.normalized : -transform.forward;
            if (Ragdoll.TryStart(Visual, away, ragdoll.Value, ignore) && LogLunges) Debug.Log($"[Myrtana] ragdoll {name}: {ragdoll.Value.Speed:F2} m/s, lift {ragdoll.Value.Lift:F2}");
        }

        public void DropWeapon()
        {
            Animator.DropWeapon();
            WeaponItem = null;
            WeaponClass = WeaponClass.Fist;
        }

        void InterruptMove()
        {
            if (CurrentAction == CombatAction.None) return;
            var action = CurrentAction;
            CurrentAction = CombatAction.None;
            _swing.Stop();
            _leftSwing?.Stop();
            _placing = false;
            _finishing.End(interrupted: true);
            MoveEnded?.Invoke(action);
        }

        /// <summary>Speed of the animator's current one-shot: moves and reactions by the rules' table; the rest as authored.</summary>
        float MotionSpeed(string g3Action, G3SwingPhase phase)
        {
            if (SpeedFactor == null) return 1f;
            CombatMotion motion;
            if (Animator.InMove) motion = _motion;
            else if (Animator.InReaction) motion = _motion;
            else if (Animator.InParade || g3Action == "Parade") motion = CombatMotion.Parade;
            else return 1f;
            return SpeedFactor(motion, phase == G3SwingPhase.Raise);
        }

        /// <summary>
        /// The sit loop (G3 ZS_SitKnockDown_Loop): a get-up asked during the fall starts once the loop is reached; an NPC
        /// gets up when the sit motion has played once (or at the cap: no sit loop in its rig).
        /// </summary>
        void UpdateSitting()
        {
            if (!Animator.IsDown)
            {
                // The body left the down state without a get-up (no sit loop in this rig, so the fall ended; an abort or a
                // reset): a sit flag kept now would turn every press into a get-up that never comes (fix-39).
                Debug.Log($"[combat] {name}: sitting flag outlived the down state ({Animator.StateText}): cleared");
                _sitting = false;
                _sitUntil = -1;
                _pendingGetUp = CombatAction.None;
                if (CurrentAction == CombatAction.GetUpAttack || CurrentAction == CombatAction.GetUpParade)
                {
                    var asked = CurrentAction;
                    CurrentAction = CombatAction.None;
                    MoveEnded?.Invoke(asked);
                }
                return;
            }
            bool inLoop = Animator.StepStage == "Loop";
            if (_pendingGetUp != CombatAction.None)
            {
                // The fall's motion has no hold stage or stalls: the queued press still gets him up (fix-39 cap).
                if (!inLoop && Time.time - _pendingSince < PendingGetUpMaxSeconds) return;
                var asked = _pendingGetUp;
                _pendingGetUp = CombatAction.None;
                _motion = MotionSpeedRules.MotionOf(asked);
                // Plain stand-up: its end reports the asked move as over (unless it was over at once, no Stand_Begin).
                if (!GetUpWith(asked) && Animator.IsDown) CurrentAction = asked;
                else if (!Animator.IsDown && CurrentAction == asked)
                {
                    CurrentAction = CombatAction.None;
                    MoveEnded?.Invoke(asked);
                }
                return;
            }
            if (_sitUntil < 0) return; // the player: only a button gets him up
            bool playedOnce = false;
            if (inLoop)
            {
                float phase = Animator.StepProgress;
                if (_sitLoopPhase >= 0 && phase < _sitLoopPhase - 0.5f) playedOnce = true;
                _sitLoopPhase = phase;
            }
            if (playedOnce || Time.time >= _sitUntil) GetUpFromSitting();
        }

        /// <summary>After sitting: get up with an attack or the parade (the owner's choice, paid by it), else plainly.</summary>
        void GetUpFromSitting()
        {
            var choice = GetUpChoice != null ? GetUpChoice() : CombatAction.None;
            if (choice == CombatAction.GetUpAttack || choice == CombatAction.GetUpParade)
            {
                _motion = MotionSpeedRules.MotionOf(choice);
                if (GetUpWith(choice)) return;
            }
            _sitting = false;
            _sitUntil = -1;
            Animator.GetUp();
        }

        static FighterAction ReactionAction(HitReaction r)
        {
            switch (r)
            {
                case HitReaction.QuickStumble: return FighterAction.QuickStumble;
                case HitReaction.PierceStumble: return FighterAction.PierceStumble;
                case HitReaction.ParadeStumble: return FighterAction.ParadeStumble;
                case HitReaction.HeavyParadeStumble: return FighterAction.HeavyParadeStumble;
                default: return FighterAction.Stumble;
            }
        }

        // ------------------------------------------------------------------------------------------- frame

        /// <summary>Horizontal speed (m/s) of the body that cuts a Recover tail: the fighter walks off (G3: the Recover is cosmetic).</summary>
        const float RecoverCutSpeed = 0.5f;
        Vector3 _tailPos;
        bool _wasTail;

        /// <summary>Every fighter steps from batched Update / LateUpdate callbacks (perf-3, <see cref="G3FrameBatch"/>; order 0 as before).</summary>
        static readonly global::G3.Unity.G3FrameBatch.Batch<CombatFighter> s_batch =
            new global::G3.Unity.G3FrameBatch.Batch<CombatFighter>(global::G3.Unity.G3FrameBatch.Slot.Update0, "CombatFighter", f => f.FrameUpdate());
        static readonly global::G3.Unity.G3FrameBatch.Batch<CombatFighter> s_lateBatch =
            new global::G3.Unity.G3FrameBatch.Batch<CombatFighter>(global::G3.Unity.G3FrameBatch.Slot.LateUpdate0, "CombatFighter.Late", f => f.FrameLateUpdate());

        void OnEnable()
        {
            s_batch.Add(this);
            s_lateBatch.Add(this);
        }

        void OnDisable()
        {
            s_batch.Remove(this);
            s_lateBatch.Remove(this);
        }

        void FrameLateUpdate()
        {
            if (Animator == null) return;
            bool tail = Animator.InRecoverTail;
            if (!tail)
            {
                _wasTail = false; // the position is only compared from one tail frame to the next (perf-3: no read otherwise)
                return;
            }
            var pos = transform.position;
            if (_wasTail && Time.deltaTime > 0 && Flat(pos - _tailPos) > RecoverCutSpeed * Time.deltaTime)
            {
                Animator.CutRecover();
                tail = false;
            }
            _tailPos = pos;
            _wasTail = tail;
        }

        void FrameUpdate()
        {
            if (Animator == null || GamePause.IsPaused) return; // r31 D1: no swings or hits while paused
            if (_sitting) UpdateSitting();
            PierceFollowUp();
            if (_finishing.Tick(Time.timeAsDouble)) FinishingBlow?.Invoke(this, _finishing.Target);
            bool swinging = Animator.IsSwinging && CurrentAction != CombatAction.None;
            if (swinging)
            {
                _swing.Watched = LogLunges && Opponent != null ? Opponent.Body : null;
                _swingAction = CurrentAction;
                // G3 enables the touch damage per hand item: a dual wielder strikes with both blades or one of them
                // (DualWieldRules.Blades); each blade hits a body once per swing and deals its own weapon's damage.
                var blades = DualWieldRules.Blades(CurrentAction, _movePose, DualWielding);
                if ((blades & MeleeBlades.Right) != 0)
                {
                    foreach (var body in _swing.Sweep(Animator.SwingCount))
                        Touched?.Invoke(this, body, MeleeBlades.Right);
                    if (_swing.TakeLevelTouch(out var ground) && HeldWeaponItem != null) WeaponTouchedLevel?.Invoke(this, MeleeBlades.Right, ground);
                }
                if ((blades & MeleeBlades.Left) != 0 && _leftSwing != null && _leftSwing.HasSlot)
                {
                    foreach (var body in _leftSwing.Sweep(Animator.SwingCount))
                        Touched?.Invoke(this, body, MeleeBlades.Left);
                    if (_leftSwing.TakeLevelTouch(out var ground) && HeldOffHandItem != null) WeaponTouchedLevel?.Invoke(this, MeleeBlades.Left, ground);
                }
            }
            else if (_wasSwinging && LogLunges && Opponent != null && _swing.MissReport() is string miss)
                Debug.Log($"[Myrtana] swing {name} {_swingAction} missed {Opponent.name}: {miss}");
            _wasSwinging = swinging;
            if (_placing)
            {
                // The finishing move's own placement replaces the name's root speed (G3: the Hit's number is the
                // distance to end at from the victim, not a distance to travel).
                if (CurrentAction == CombatAction.FinishingAttack && _victim != null) { Place(); return; }
                _placing = false;
            }
            UpdateLunge();
        }

        /// <summary>
        /// fix-244, G3 _AI_PierceAttack stage 2 (<see cref="MeleeComboRules.AfterRaise"/>): the pierce's Raise just ended
        /// (this runs before the animator, which switches to the Hit in the same frame) and the opponent is still in its
        /// PierceStumble: the move goes on as a QuickAttack from the Raise's end pose instead of the pierce's Hit and Recover.
        /// </summary>
        void PierceFollowUp()
        {
            if (CurrentAction != CombatAction.PierceAttack || !Animator.InMove || Animator.StepStage != "Raise" || Animator.StepProgress < 1f) return;
            bool stumbling = Opponent != null && Opponent.isActiveAndEnabled && Opponent.FighterAction == FighterAction.PierceStumble;
            var next = MeleeComboRules.AfterRaise(CurrentAction, stumbling);
            if (next == CombatAction.PierceAttack) return;
            int pose = Mathf.Max(0, Animator.StepEndPose);
            if (LogLunges) Debug.Log($"[Myrtana] pierce follow-up {name}: {Opponent.name} still in PierceStumble -> {next} P{pose}");
            StartMove(next, pose);
        }

        /// <summary>Where the last hit, fall or death reaction came from (false when there was none yet).</summary>
        public bool TryGetLastHitFrom(out Vector3 from)
        {
            from = _hitFrom;
            return _hasHitFrom;
        }

        void SetHitFrom(Vector3 from)
        {
            _hitFrom = from;
            _hasHitFrom = true;
        }

        // ------------------------------------------------------------------------------------------- combat movement

        /// <summary>
        /// G3's combat movement (CombatLunge, Docs/combat.md §5.6): each combat motion carries the body its name's number
        /// in metres, at a constant speed over the motion, towards the target (attacks) or away from the target or hitter
        /// (jump back, stumbles, falls); a move also turns the body to face its target over the motion. Stops for good at
        /// the target's body (attacks); other fighters' bodies, walls and ledges hold it back.
        /// </summary>
        void UpdateLunge()
        {
            if (!Animator.Busy)
            {
                EndLunge();
                _lungeSerial = Animator.StepSerial;
                return;
            }
            // CombatFighter runs before G3CombatAnimator (150): a step's last frame (progress 1) is seen before the switch.
            if (Animator.StepSerial != _lungeSerial) BeginLunge();
            if (!_lungeActive) return;
            float k = Animator.StepProgress;
            float dk = k - _lungeK;
            if (dk <= 0) return;
            _lungeK = k;
            if (_turning) Turn(CombatLunge.TurnYaw(_lungeYawFrom, _lunge.FaceYaw, k));
            float step = _lunge.Distance * dk;
            if (step <= 0 || _lungeContact || _mover == null) return;
            var dir = new Vector3(_lunge.DirX, 0, _lunge.DirZ);
            step = Clear(dir, step);
            if (step <= 1e-5f) return;
            var before = transform.position;
            _mover(dir * step);
            float took = Vector3.Dot(transform.position - before, dir);
            _lungeTook += took;
            if (LogLunges && _blockedBy == null && took < step * 0.5f) _blockedBy = Blocker(before, dir, step);
            _lungeDone += step;
        }

        void BeginLunge()
        {
            EndLunge();
            _lungeSerial = Animator.StepSerial;
            _lungeK = 0;
            _lungeContact = false;
            _lungeDone = _lungeTook = _contactGap = 0;
            _heldBy = _blockedBy = null;
            _lungeTarget = null;
            if (_placing) return; // the finishing move places the body itself (§5.5)
            bool move = Animator.InMove;
            bool hasOther;
            Vector3 other;
            if (move)
            {
                _lungeTarget = Opponent != null && Opponent.isActiveAndEnabled ? Opponent : null;
                hasOther = _lungeTarget != null;
                other = hasOther ? _lungeTarget.transform.position : default;
            }
            else if (Animator.InReaction || Animator.IsDown || Animator.IsDead)
            {
                // fix-202: a ragdoll falls by physics; the dying motion's distance would drag its bodies along
                if (!RagdollRules.LungeMovesBody(Animator.IsDead, Visual != null && Visual.TryGetComponent<Ragdoll>(out _))) return;
                hasOther = _hasHitFrom;
                other = _hitFrom;
            }
            else return; // draw, sheathe, parade: not moved (their motions carry 0)
            var p = transform.position;
            float d = Animator.StepDistance;
            _lunge = CombatLunge.Plan(Animator.Action, Animator.StepStage, d, p.x, p.z, Yaw, hasOther, other.x, other.z, turn: move);
            _turning = _lunge.Turns;
            _lungeActive = _lunge.Distance > 0 || _turning;
            _lungeYawFrom = Yaw;
            _lungeStart = p;
            _lungeLabel = _lunge.Distance > 0
                ? $"{Animator.Action} {Animator.StepStage} name {d:F2} m, plan {_lunge.Distance:F2} m {(_lunge.Towards ? "towards" : "away")}" +
                  (hasOther ? $", other at {Flat(other - p):F2} m" : ", no other") + (_turning ? $", turn {Mathf.DeltaAngle(Yaw, _lunge.FaceYaw):F0} deg" : "")
                : null;
        }

        void EndLunge()
        {
            if (_lungeActive && LogLunges && _lungeLabel != null)
            {
                var moved = transform.position - _lungeStart;
                float along = moved.x * _lunge.DirX + moved.z * _lunge.DirZ;
                Debug.Log($"[Myrtana] lunge {name}: {_lungeLabel}; ended at {_lungeK:P0} of the motion: expected {_lunge.At(_lungeK):F2} m, " +
                          $"moved {_lungeDone:F2} m, body travelled {along:F2} m along ({Flat(moved):F2} m in all){(_lungeContact ? $", stopped at contact (gap then {_contactGap:F2} m)" : "")}" +
                          (MoverCollides ? $", controller took {_lungeTook:F2} m" : "") + (_heldBy != null ? $", held back by {_heldBy}" : "") +
                          (_blockedBy != null ? $", mover blocked by {_blockedBy}" : "") +
                          (_lungeTarget != null ? $", gap to target {Gap(_lungeTarget):F2} m" : ""));
            }
            _lungeActive = false;
            _turning = false;
            _lungeLabel = null;
        }

        /// <summary>The part of a step the body may take: up to the target's body (then it stops for good), other fighters, walls, ledges.</summary>
        float Clear(Vector3 dir, float step)
        {
            var p = transform.position;
            float own = Body != null ? Body.Footprint : 0.35f;
            if (_lunge.StopsAtContact && _lungeTarget != null && _lungeTarget.Body != null)
            {
                var t = _lungeTarget.transform.position;
                float allowed = CombatLunge.Allowed(p.x, p.z, dir.x, dir.z, step, t.x, t.z, own + _lungeTarget.Body.Footprint);
                if (allowed < step)
                {
                    step = allowed;
                    _lungeContact = true;
                    _contactGap = Flat(t - (p + dir * step)) - own - _lungeTarget.Body.Footprint;
                }
            }
            // Other fighters' bodies (trigger capsules) are never walked through; lying bodies are stepped over.
            int n = Physics.OverlapSphereNonAlloc(p, step + own + 3f, _overlap, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n && step > 0; i++)
            {
                if (!_overlap[i].TryGetComponent<CombatBody>(out var body) || body.Fighter == null || body.Fighter == this) continue;
                if (body.Fighter == _lungeTarget && _lunge.StopsAtContact) continue; // done above
                var a = body.Fighter.Animator;
                if (a != null && (a.IsDown || a.IsDead)) continue;
                var o = body.Fighter.transform.position;
                float held = CombatLunge.Allowed(p.x, p.z, dir.x, dir.z, step, o.x, o.z, own + body.Footprint);
                if (held < step && LogLunges) _heldBy = body.Fighter.name;
                step = held;
            }
            if (step <= 0) return 0;
            // Walls: NPC views move by position; the hero's controller collides by itself.
            if (!MoverCollides)
            {
                float r = Mathf.Clamp(own, 0.2f, 0.5f) * 0.9f;
                var bottom = p + Vector3.up * (0.35f + r);
                var top = p + Vector3.up * Mathf.Max(0.35f + r, 1.7f - r);
                n = Physics.CapsuleCastNonAlloc(bottom, top, r, dir, _walls, step + 0.05f, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    if (_walls[i].distance <= 0 || OwnOrFighter(_walls[i].collider)) continue; // starting overlaps: let the body free itself
                    step = Mathf.Min(step, Mathf.Max(0, _walls[i].distance - 0.05f));
                }
                if (step <= 0) return 0;
            }
            // G3: the point 0.15 s ahead must be walkable ground, else no movement this frame.
            float ahead = Mathf.Max(CombatLunge.LookAhead(step, Time.deltaTime), step);
            if (GroundY(p + dir * ahead, out float groundY))
                return CombatLunge.GroundAhead(p.y, true, groundY, ahead) ? step : 0;
            return GroundY(p, out _) ? 0 : step; // no ground ahead: a ledge; no ground here either: a world without colliders
        }

        /// <summary>Log: the first collider (not a fighter's) in a body-sized capsule along the step the mover could not take.</summary>
        string Blocker(Vector3 from, Vector3 dir, float step)
        {
            float r = Body != null ? Mathf.Clamp(Body.Footprint, 0.2f, 0.5f) * 0.9f : 0.3f;
            var bottom = from + Vector3.up * (0.35f + r);
            var top = from + Vector3.up * Mathf.Max(0.35f + r, 1.7f - r);
            int n = Physics.CapsuleCastNonAlloc(bottom, top, r, dir, _walls, step + 0.1f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _walls[i].collider;
                if (OwnOrFighter(c)) continue;
                return $"{(c.transform.parent != null ? c.transform.parent.name + "/" : "")}{c.name} ({c.GetType().Name}, normal y {_walls[i].normal.y:F2})";
            }
            return "nothing found (slope or step under the feet?)";
        }

        bool GroundY(Vector3 at, out float y)
        {
            int n = Physics.RaycastNonAlloc(at + Vector3.up * 1.5f, Vector3.down, _ground, 3.5f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            y = 0;
            for (int i = 0; i < n; i++)
            {
                if (OwnOrFighter(_ground[i].collider)) continue;
                if (_ground[i].distance < best) { best = _ground[i].distance; y = _ground[i].point.y; }
            }
            return best < float.MaxValue;
        }

        bool OwnOrFighter(Collider c)
        {
            var t = c.transform;
            if (t.IsChildOf(transform) || (Visual != null && t.IsChildOf(Visual))) return true;
            return c.GetComponentInParent<CombatFighter>() != null;
        }

        /// <summary>Gap between the two bodies' footprints, m (log).</summary>
        float Gap(CombatFighter other)
        {
            float own = Body != null ? Body.Footprint : 0.35f, theirs = other.Body != null ? other.Body.Footprint : 0.35f;
            return Flat(other.transform.position - transform.position) - own - theirs;
        }

        void OnAnimatorFreed(string g3Action) => Freed?.Invoke();

        void OnAnimatorMoveEnded(string g3Action)
        {
            if (CurrentAction == CombatAction.None) return;
            var action = CurrentAction;
            CurrentAction = CombatAction.None;
            _swing.Stop();
            _leftSwing?.Stop();
            _placing = false;
            if (_finishing.End(interrupted: false)) FinishingBlow?.Invoke(this, _finishing.Target);
            MoveEnded?.Invoke(action);
        }

        // ------------------------------------------------------------------------------------------- finishing placement

        float Yaw => transform.eulerAngles.y;

        /// <summary>
        /// A finishing move starts: picks the spot <see cref="FinishingReach.Strike"/> from the victim's torso (another
        /// side when blocked). Only for the real FinishingAttack motion (a rig without one cannot finish: CombatMotionNames).
        /// </summary>
        void BeginPlacement()
        {
            _placing = false;
            _stabLogged = false;
            _slideAsked = _slideTook = 0;
            _slideBlocker = null;
            _turnAsked = float.NaN;
            if (Opponent == null || Animator.Action != "FinishingAttack") return;
            var reach = FinishingPlacement.ReachOf(Animator.Rig, Animator.Weapon);
            if (reach == null) return;
            _reach = reach.Value;
            _victim = Opponent;
            var body = _victim.Visual != null ? _victim.Visual : _victim.transform;
            _victimPelvis = FindSuffix(body, "Spine_Spine_1");
            _victimChest = FindSuffix(body, "Spine_Spine_3");
            var torso = TorsoPoint();
            var p = transform.position;
            var origin = _victim.transform.position;
            _spot = FinishingPlacement.ChooseLevel(p.x, p.z, Yaw, torso.x, torso.z, _reach.Strike, (x, z) => SpotGround(new Vector3(x, p.y, z)), origin.y);
            _placing = true;
            _placePhase = G3SwingPhase.None;
            var spotGround = SpotGround(new Vector3(_spot.X, p.y, _spot.Z));
            Debug.Log($"[Myrtana] finishing place {name}: {Animator.Rig} {Animator.Weapon} strike {_reach.Strike:F2} m (G3 {_reach.G3Distance:F2}), " +
                      $"to torso {Flat(torso - p):F2} m, torso off origin {Flat(torso - origin):F2} m, {torso.y - origin.y:F2} m up{(_victimPelvis == null ? " (no spine bones: origin)" : "")}, " +
                      $"spot ({_spot.X:F2}, {_spot.Z:F2}) yaw {_spot.Yaw:F0} detour {_spot.Detour:F0} moved {_spot.Moved}{(_spot.Forced ? " (all blocked: forced)" : "")}, " +
                      $"spot ground {(spotGround.HasValue ? (spotGround.Value - origin.y).ToString("F2") : "?")} m above the victim's");
        }

        /// <summary>Per frame of the finishing move: turn during the Raise, slide (linear, done at the stab frame) and turn during the Hit.</summary>
        void Place()
        {
            var phase = Animator.Phase;
            float k = Animator.StepProgress;
            if (phase != _placePhase)
            {
                _placePhase = phase;
                _slideFrom = transform.position;
                _yawFrom = Yaw;
            }
            var torso = TorsoPoint();
            _spot = FinishingPlacement.Follow(_spot, torso.x, torso.z, _reach.Strike);
            if (phase == G3SwingPhase.Raise)
                Turn(FinishingPlacement.TurnYaw(_yawFrom, _spot.Yaw, k));
            else if (phase == G3SwingPhase.Hit)
            {
                float f = FinishingPlacement.SlideFraction(k, _reach.StabFraction);
                if (_spot.Moved && _mover != null)
                {
                    var (x, z) = FinishingPlacement.Along(_slideFrom.x, _slideFrom.z, _spot, f);
                    var d = new Vector3(x - transform.position.x, 0, z - transform.position.z);
                    if (d.sqrMagnitude > 1e-8f)
                    {
                        var before = transform.position;
                        _mover(d);
                        float asked = d.magnitude, took = Flat(transform.position - before);
                        _slideAsked += asked;
                        _slideTook += took;
                        if (_slideBlocker == null && took < asked * 0.5f) _slideBlocker = SlideBlocker(before, d / asked, asked);
                    }
                }
                Turn(FinishingPlacement.TurnYaw(_yawFrom, _spot.Yaw, f));
                if (!_stabLogged && k >= _reach.StabFraction)
                {
                    _stabLogged = true;
                    Debug.Log($"[Myrtana] finishing stab {name}: to torso {Flat(torso - transform.position):F2} m (want {_reach.Strike:F2}), " +
                              $"yaw off {Mathf.DeltaAngle(Yaw, _spot.Yaw):F0} deg, weapon bounds to torso {WeaponToTorso(torso):F2} m, {TipReport(torso)}, " +
                              $"feet {transform.position.y - _victim.transform.position.y:F2} m above the victim's; " +
                              $"slide asked {_slideAsked:F2} m took {_slideTook:F2} m{(_slideBlocker != null ? $" (held by {_slideBlocker})" : "")}{(_mover == null ? " NO MOVER" : "")}, " +
                              $"turn asked {_turnAsked:F0} body {Yaw:F0}{(Turner == null ? " (no turner)" : "")}; " +
                              $"victim lying {_victim.Animator.IsDown} (torso {torso.y - _victim.transform.position.y:F2} m up), " +
                              $"weapon in hand {Animator.WeaponInHand} ({Animator.WeaponTemplate})");
                }
            }
        }

        void Turn(float yaw)
        {
            if (_placing) _turnAsked = yaw;
            if (Turner != null) Turner(yaw);
            else transform.rotation = Quaternion.Euler(0, yaw, 0);
        }

        /// <summary>The victim's torso: the midpoint of its pelvis and chest bones in the lying pose, else its origin.</summary>
        Vector3 TorsoPoint()
        {
            if (_victimPelvis != null && _victimChest != null) return (_victimPelvis.position + _victimChest.position) * 0.5f;
            if (_victimPelvis != null) return _victimPelvis.position;
            return _victim.transform.position;
        }

        /// <summary>
        /// The attacker fits at <paramref name="p"/>: ground within a step of its height and no collider (other than its own
        /// and the victim's) in a capsule of its footprint above that ground. Returns that ground's height, null when blocked.
        /// </summary>
        float? SpotGround(Vector3 p)
        {
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 1.2f, Vector3.down, _ground, 2.4f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, groundY = 0;
            for (int i = 0; i < n; i++)
            {
                if (Ours(_ground[i].collider)) continue;
                if (_ground[i].distance < best) { best = _ground[i].distance; groundY = _ground[i].point.y; }
            }
            if (best == float.MaxValue || Mathf.Abs(groundY - p.y) > 0.6f) return null;
            float r = Body != null ? Mathf.Clamp(Body.Footprint, 0.2f, 0.5f) : 0.35f;
            var bottom = new Vector3(p.x, groundY + 0.35f + r, p.z);
            var top = new Vector3(p.x, groundY + 1.7f - r, p.z);
            n = Physics.OverlapCapsuleNonAlloc(bottom, top, r, _overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!Ours(_overlap[i])) return null;
            return groundY;
        }

        /// <summary>Log: the first collider (other than ours and the victim's; fighters count) in the way of a finishing slide.</summary>
        string SlideBlocker(Vector3 from, Vector3 dir, float step)
        {
            float r = Body != null ? Mathf.Clamp(Body.Footprint, 0.2f, 0.5f) * 0.9f : 0.3f;
            var bottom = from + Vector3.up * (0.35f + r);
            var top = from + Vector3.up * Mathf.Max(0.35f + r, 1.7f - r);
            int n = Physics.CapsuleCastNonAlloc(bottom, top, r, dir, _walls, step + 0.1f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _walls[i].collider;
                if (Ours(c)) continue;
                var fighter = c.GetComponentInParent<CombatFighter>();
                return fighter != null ? $"fighter {fighter.name}" : $"{(c.transform.parent != null ? c.transform.parent.name + "/" : "")}{c.name}";
            }
            return "nothing found";
        }

        bool Ours(Collider c)
        {
            var t = c.transform;
            return t.IsChildOf(transform) || (Visual != null && t.IsChildOf(Visual)) || (_victim != null && t.IsChildOf(_victim.transform));
        }

        /// <summary>Distance (m) from the torso to the bounds of what the weapon slot holds; -1 without a weapon renderer (log only).</summary>
        float WeaponToTorso(Vector3 torso)
        {
            var slot = Visual != null ? FindSuffix(Visual, "Slot_RightHand_Weapon") : null;
            if (slot == null) return -1;
            bool any = false;
            var b = new Bounds();
            foreach (var r in slot.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any ? Vector3.Distance(b.ClosestPoint(torso), torso) : -1;
        }

        /// <summary>
        /// Log: the blade tip (the far end of the weapon mesh's longest axis, as G3Cli anim reach) relative to the torso in the
        /// attacker's frame, and the tip's own offset from the attacker's feet (measured offline, Hero 1H: 1.95 ahead, 0.08–0.12
        /// right, 0.15–0.25 up).
        /// </summary>
        string TipReport(Vector3 torso)
        {
            var slot = Visual != null ? FindSuffix(Visual, "Slot_RightHand_Weapon") : null;
            var mf = slot != null ? slot.GetComponentInChildren<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) return "no weapon mesh";
            var b = mf.sharedMesh.bounds;
            int axis = b.size.x >= b.size.y && b.size.x >= b.size.z ? 0 : b.size.y >= b.size.z ? 1 : 2;
            var local = b.center;
            local[axis] = Mathf.Abs(b.max[axis]) >= Mathf.Abs(b.min[axis]) ? b.max[axis] : b.min[axis];
            var tip = mf.transform.TransformPoint(local);
            var rot = Quaternion.Euler(0, Yaw, 0);
            var off = Quaternion.Inverse(rot) * (tip - torso);
            var own = Quaternion.Inverse(rot) * (tip - transform.position);
            return $"tip to torso {(tip - torso).magnitude:F2} m (ahead {off.z:F2}, right {off.x:F2}, up {off.y:F2}), " +
                   $"tip from feet ahead {own.z:F2} right {own.x:F2} up {own.y:F2}, blade {mf.transform.lossyScale.x * b.size[axis]:F2} m";
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        static Transform FindSuffix(Transform root, string suffix)
        {
            if (root.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var t = FindSuffix(root.GetChild(i), suffix);
                if (t != null) return t;
            }
            return null;
        }

        void OnDestroy()
        {
            if (Animator != null) { Animator.MoveEnded -= OnAnimatorMoveEnded; Animator.Freed -= OnAnimatorFreed; }
        }
    }
}
