using G3.Player;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The NPC combat AI on a shown NPC (Docs/combat.md §6.1, §9): builds the <see cref="CombatSituation"/> from the
    /// scene every frame, ticks the NPC's <see cref="NpcCombatBrain"/> and carries out the <see cref="CombatOrder"/> on
    /// the body: draw the weapon the brain chose (melee or bow/crossbow from its loadout, without weapons taken from it)
    /// and holster, run, face, strafe and back off, play the attack or parade, shoot (nock or cock and reload, aim, a
    /// projectile aimed at the target; NPC ammunition is infinite), cast (the brain's spell in the spell hand: load, power up when it
    /// chose so, release at the target; <see cref="SpellCasterBody"/>), get up from sitting with an attack or the parade, flee, ransack a
    /// knocked-out victim. It reports the ends of body-timed moves (<see cref="NpcCombatBrain.OnMoveFinished"/>). While the
    /// NPC fights, lies or is dead, it has the body and the routine view (<see cref="NpcView"/>) waits.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class NpcCombatView : MonoBehaviour
    {
        const float Cm = 100f;
        const float StrafeSpeedFactor = 0.6f;     // of the run speed
        const float MinGap = 0.15f;               // between the two bodies when closing in, m
        const float RansackReach = 1.5f, RansackSeconds = 2f;
        const float FleePointDistance = 25f;      // a flight ends this far from the pursuer [interp: no flee points yet]
        const float VerticalReach = 2f;          // height difference still counted as flat for reach [interp]
        const float BackProbe = 2f;
        const float AimSeconds = 0.6f;            // NPC archers hold the aim this long before the shot [interp]
        const float FlyDescentSpeed = 4f;         // m/s a bloodfly over water settles to the surface height [interp]

        CombatPresenter _presenter;
        NpcView _view;
        CombatFighter _fighter;
        G3NpcAnimator _npcAnimator;
        float _runSpeed = 3.5f, _walkSpeed = 1.5f, _sprintSpeed = 4.9f, _circleSpeed = CombatFootwork.StrafeSpeedMax;
        /// <summary>The body sprints this frame (Move = ZS_Attack_Sprint): turns at TurnSpeedMax × TurnSpeedModifier.</summary>
        bool _sprinting;
        double _lastMoveStart = double.NaN;
        CombatMove _lastMove;
        bool _moveStarted, _wasFighting, _shotPending;
        float _ransackTime = -1, _aimStart, _drinkUntil = -1, _takeUntil = -1;
        /// <summary>ZS_FetchWeapon: a weapon farther than this (m) is left lying [2000].</summary>
        const float FetchRange = 20f;
        /// <summary>Close enough to take the weapon (m), and how long taking it lasts (s) [interp: no TakeItem motion].</summary>
        const float FetchReach = 0.8f, TakeSeconds = 0.8f;
        /// <summary>How long drinking a potion keeps the NPC busy (s) [interp: _AI_UseItem's motion is not played].</summary>
        const float DrinkSeconds = 1.5f;
        SpellCasterBody _magic;
        Vector3 _lastPos;
        CarriedWeaponsView _carried;

        GameSession Session => _presenter.Session;
        public CombatFighter Fighter => _fighter;

        public void Bind(CombatPresenter presenter, NpcView view)
        {
            _presenter = presenter;
            _view = view;
            var def = presenter.CombatDefOf(view.Actor);
            bool humanoid = def == null || def.Humanoid;
            _fighter = TryGetComponent<CombatFighter>(out var f) ? f : gameObject.AddComponent<CombatFighter>();
            float reach = def != null ? presenter.Session.NpcCombat.Rules.Reach.ReachOf(def.Species, true, FighterAction.Attack, presenter.Session.Settings.Rules.ExtendedAI) / Cm : 1.2f;
            _fighter.Init(view.Actor, transform, humanoid, reach, Displace);
            _reach = reach;
            ResetChase();
            _npcAnimator = GetComponent<G3NpcAnimator>();
            var loadout = presenter.Session.NpcCombat.LoadoutOf(view.Actor);
            _fighter.SetWeapon(loadout.MeleeItem, loadout.Melee);
            _fighter.GetUpChoice = GetUpChoice;
            _fighter.Animator.RangedEnded -= OnRangedEnded;
            _fighter.Animator.RangedEnded += OnRangedEnded;
            // A body in a hit reaction holds no parade, whatever the brain still thinks (audit #3).
            _fighter.ParadeUp = () => !_fighter.Animator.InReaction && Brain() is NpcCombatBrain b && b.IsParrying(Session.PlaySeconds);
            _fighter.Moving = () => (transform.position - _lastPos).sqrMagnitude > 1e-6f;
            _fighter.MoveEnded -= OnMoveEnded;
            _fighter.MoveEnded += OnMoveEnded;
            if (_magic == null)
            {
                _magic = new SpellCasterBody(presenter, _fighter);
                _magic.Finished += _ => FinishShot(Brain());
                _magic.Released += _ => Brain()?.OnSpellCast(Session.PlaySeconds, Session.Clock.GameSeconds);
                _fighter.Animator.WeaponInHandChanged += ShowSpell;
                _fighter.Animator.WeaponInHandChanged += RefreshCarried;
            }
            ReadSpeeds();
            _lastMoveStart = double.NaN;
            _ransackTime = -1;
            _wasFighting = false;
            _shotPending = false;
            ResetLife();
            _lastPos = transform.position;
            presenter.Register(_fighter);
            enabled = true;
            RefreshCarried();
        }

        /// <summary>
        /// The weapons on the body (G3: equipped weapons are always visible, on their carry slots while not in the hand):
        /// the loadout without the drawn and the dropped weapon (<see cref="CarriedWeaponsView"/>). Event-driven: binding,
        /// the weapon going into or out of the hand, dropping, picking up or losing it.
        /// </summary>
        public void RefreshCarried()
        {
            if (_presenter == null || _fighter == null) return;
            var loadout = Session.NpcCombat.LoadoutOf(_view.Actor);
            if (_carried == null)
            {
                if (loadout.MeleeItem == null && loadout.RangedItem == null && loadout.ShieldItem == null) return;
                _carried = TryGetComponent<CarriedWeaponsView>(out var c) ? c : gameObject.AddComponent<CarriedWeaponsView>();
            }
            var a = _fighter.Animator;
            var drawn = a.WeaponInHand && !_fighter.SpellDrawn ? _fighter.WeaponItem : null;
            var actor = _view.Actor;
            var presenter = _presenter;
            // fix-149: per item - a dropped shield or bow lies on the ground too, a reloaded corpse's drops still lie there.
            _carried.Show(loadout, drawn, item => presenter.Lies(actor, item.Id), _presenter.ItemVisuals);
        }

        public void Unbind()
        {
            if (_presenter != null && _fighter != null)
            {
                _presenter.Unregister(_fighter);
                // Out of the processing range: the fight ends (Disengage), a downed or dead NPC keeps its state in the vitals.
                Brain()?.Disengage();
                AbortHeal();
                Session?.Life?.Tasks.End(_view.Actor); // out of the processing range: the reaction task is over (G3 FullStop)
                _magic?.Abort();
                _magic?.HideHeld();
                _fighter.ResetState();
            }
            if (_carried != null) _carried.Hide();
            _presenter = null;
            enabled = false;
        }

        NpcCombatBrain Brain() => _presenter != null ? Session.NpcCombat.Get(_view.Actor) : null;

        void ReadSpeeds()
        {
            var player = GetComponent<G3MotionPlayer>();
            var lib = player != null ? player.library : null;
            if (lib == null) return;
            var run = lib.Find(new G3MotionKey(lib.rig, "Move", "Run"));
            var walk = lib.Find(new G3MotionKey(lib.rig, "Move", "Walk"));
            var sprint = lib.Find(new G3MotionKey(lib.rig, "Move", "Sprint"));
            var side = lib.Find(new G3MotionKey(lib.rig, "Move", "Run", "Left")) ?? lib.Find(new G3MotionKey(lib.rig, "Move", "Run", "Right"));
            if (run != null && run.speed > 0) _runSpeed = run.speed;
            if (walk != null && walk.speed > 0) _walkSpeed = walk.speed;
            _sprintSpeed = CombatFootwork.SprintSpeed(sprint != null ? sprint.speed : 0, _runSpeed);
            _circleSpeed = CombatFootwork.CircleSpeed(side != null ? side.speed : 0);
        }

        // ------------------------------------------------------------------------------------------- frame

        /// <summary>Every NPC's combat view steps from one batched Update (perf-3, <see cref="G3FrameBatch"/>; order 0 as before).</summary>
        static readonly global::G3.Unity.G3FrameBatch.Batch<NpcCombatView> s_batch =
            new global::G3.Unity.G3FrameBatch.Batch<NpcCombatView>(global::G3.Unity.G3FrameBatch.Slot.Update0, "NpcCombatView", v => v.FrameUpdate());

        void OnEnable() => s_batch.Add(this);
        void OnDisable() => s_batch.Remove(this);

        void FrameUpdate()
        {
            if (_presenter == null || Session == null || _view.Agent == null || LoadingHold.IsHeld || GamePause.IsPaused) return; // no fight behind a loading screen or a pause (r31 D1)
            // Update LOD (perf-8): a calm NPC beyond 30 m (aggro range is 7 m) is checked on its view's update frames only;
            // fighting / busy bodies (BodyTaken) run every frame.
            if (!_wasFighting && !_view.BodyTaken && _view.UpdateInterval > 1 && _npcAnimator != null && !_npcAnimator.UpdateDue) return;
            var state = Session.Combat.Vitals.StateOf(_view.Actor);
            var brain = Brain();
            bool fighting = brain != null && brain.IsFighting;
            // 1H + shield in the hands: the moves use the Shield_1H set (hero rig humans; locomotion keeps None_1H) (combat-6).
            // Dual wielders (G3 EquipWeaponry: two one-handed blades; assassins, Zuben): the 1H_1H set from the draw on (the draw
            // itself is the two-handed Hold motion), the second blade strikes too (fix-32).
            bool dual = brain != null && brain.Loadout.DualWield;
            if (_fighter.OffHandItem != (dual ? brain.Loadout.OffHandItem : null)) _fighter.SetOffHand(dual ? brain.Loadout.OffHandItem : null);
            _fighter.OffHandHeld = dual;
            _fighter.LeftOneHandedOrTorch = dual && _fighter.HeldWeaponClass == WeaponClass.OneHanded; // QuickAttack sides alternate in P1
            string offHand = dual && _fighter.WeaponClass == WeaponClass.OneHanded && !_fighter.RangedDrawn && !_fighter.SpellDrawn
                ? G3CombatMotions.DualOffHand
                : brain != null && brain.Loadout.Shield && _fighter.HeldWeaponClass == WeaponClass.OneHanded ? G3CombatMotions.Shield : null;
            if (_fighter.Animator.OffHand != offHand) _fighter.Animator.OffHand = offHand;
            Vector3 dropped = default;
            bool fetching = state == VitalState.Alive && _presenter.TryGetDroppedWeapon(_view.Actor, out dropped);
            if (fetching && fighting)
            {
                _presenter.PickUpDropped(_view.Actor); // attacked while fetching: the weapon is grabbed at once [interp]
                fetching = false;
                _takeUntil = -1;
            }
            // A revived corpse (CP Respawn) stands up again: its vitals are alive while the body still lies dead.
            if (state == VitalState.Alive && _fighter.Animator.IsDead) _fighter.ResetState();
            // A reaction task (threatening, eating a body) or healing after a fight has the body (par-npc).
            bool life = state == VitalState.Alive && !fighting && !fetching && LifeHoldsBody();
            bool busy = state != VitalState.Alive || fighting || fetching || _fighter.Animator.Busy || _fighter.Animator.Drawn || life;
            _view.BodyTaken = busy;
            // The routine's interaction (sitting at the fire …) ends: aiming and reloading leave the legs to that animator.
            if (busy && !life && _npcAnimator != null && _npcAnimator.Interaction != null) _npcAnimator.Interaction = null;
            if (state != VitalState.Alive)
            {
                ReportCorpse();
                _lastPos = transform.position;
                return;
            }
            // G3: an NPC that enters the SWIM movement mode dies (OnMovementModeChanged → ZS_RagDollDead): knocked back or
            // pushed into deep water, it drowns at once (pathing keeps it out otherwise; SwimRules.NpcDrowns, fix-4).
            if (_view.InSwimDepth)
            {
                // a death without a blow: logged (fix-46, r28 main_02 "Xardas is dead", no attacker known)
                Debug.Log($"[npc] {_view.Actor} drowns at {transform.position} (swim depth, G3 ZS_RagDollDead)");
                Session.Combat.Vitals.Kill(_view.Actor, byPlayerParty: false);
                _lastPos = transform.position;
                return;
            }
            if (!fighting)
            {
                if (_wasFighting) EndFight();
                else if (!_fighter.Animator.Busy && _fighter.Animator.Drawn && !Healing) _fighter.Sheathe();
                if (fetching)
                {
                    FetchWeapon(dropped);
                    _lastPos = transform.position;
                    return;
                }
                RunLife();
                _lastPos = transform.position;
                return;
            }
            if (!_wasFighting) FightStarts();
            _wasFighting = true;
            if (BackToMeal(brain)) return;
            _magic.Update();
            if (_fighter.Frozen) return; // frozen: no decisions, no moves (G3 ZS_Freeze)
            var target = _presenter.TryGet(brain.State.Target, out var t) ? t : null;
            _fighter.Opponent = target;
            _sprinting = false;
            // A reaction task (stumble, parade stumble, sit) has the body; ZS_Attack_Loop decides again after it (audit #3),
            // so the brain picks no move it could not show (an invisible parade).
            if (_fighter.Animator.InReaction || _fighter.Animator.IsDown)
            {
                _lastPos = transform.position;
                return;
            }
            var order = brain.Tick(Situation(brain, target));
            Execute(brain, order, target);
            if (target != null) Displace(Vector3.zero); // the opponent may have walked into the body
            Session.Npcs.ReportPose(_view.Actor, new WorldPose(transform.position.x, transform.position.y, transform.position.z, transform.eulerAngles.y));
            _lastPos = transform.position;
        }

        void EndFight()
        {
            _wasFighting = false;
            ResetChase();
            _healCheck = true; // ContinueRoutine: below 61 % an NPC that can heals itself (ZS_HealSelf)
            _fighter.Opponent = null;
            _ransackTime = -1;
            _shotPending = false;
            if (_fighter.Animator.InParade) _fighter.Animator.LowerParade();
            if (_fighter.Animator.InAim) _fighter.Animator.LowerAim();
        }

        // ------------------------------------------------------------------------------------------- situation

        CombatSituation Situation(NpcCombatBrain brain, CombatFighter target)
        {
            var self = transform.position;
            var vitals = Session.Combat.Vitals;
            var targetId = brain.State.Target;
            var s = new CombatSituation
            {
                Now = Session.PlaySeconds,
                GameNow = Session.Clock.GameSeconds,
                X = self.x * Cm,
                Z = self.z * Cm,
                TargetIsPlayer = targetId.IsPlayer,
                TargetState = vitals.StateOf(targetId),
                PlayerLevel = Session.Hero.Level,
                PlayerAttackPressure = Session.Combat.PlayerPressure.Value,
                HitPointsPercent = vitals.HitPointsMax(_view.Actor) > 0 ? vitals.HitPoints(_view.Actor) * 100 / vitals.HitPointsMax(_view.Actor) : 100,
                Arena = brain.State.Reason == AttackReason.Arena,
                AttitudeToTarget = _presenter.AttitudeOf(_view.Actor, targetId),
                UpsetWithTarget = targetId.IsPlayer && Session.Crime.Attitudes.IsUpset(_view.Actor, out _),
                InPlayerParty = Session.Party != null && Session.Party.Members.IsLedByPlayer(_view.Actor),
            };
            var hand = _fighter.Animator;
            s.Held = WeaponSelection.HeldOf(hand.WeaponInHand, !hand.Hand.Settled, hand.IsRanged, hand.IsCastStance);
            if (target == null)
            {
                s.Distance = float.MaxValue / 4;
                return s;
            }
            var to = target.transform.position - self;
            // Flat distance (fliers hover, big bodies differ in pivot height), but a target far above or below (a ledge, a
            // slope's foot) is not in reach: the 3-D distance makes the fighter chase, so the chase rule can end a fight
            // it could never land a hit in (editor-runner-18: Gorn swung at a scavenger for minutes, guiding stalled).
            if (Mathf.Abs(to.y) <= VerticalReach) to.y = 0;
            s.Distance = to.magnitude * Cm;
            s.TargetInWater = target.TryGetComponent<G3.Unity.G3Player>(out var swimmer) && swimmer.Swimming;
            s.TargetUnreachable = _reachTracker.Unreachable; // fix-142: the NavMesh chase found no way into reach
            s.LineOfSight = LineOfSight(target);
            s.FreeBack = Free(-transform.forward);
            s.FreeBackLeft = Free(-transform.forward - transform.right);
            s.FreeBackRight = Free(-transform.forward + transform.right);
            var targetSnapshot = _presenter.Snapshot(target);
            s.TargetKillable = Session.Combat.Resolver.CanBeKilled(targetSnapshot, Session.Hero.Level);
            s.TargetHumanoid = targetSnapshot.Humanoid;
            s.TargetSpecies = targetSnapshot.Species;
            s.TargetFists = target.HeldWeaponClass == WeaponClass.Fist;
            s.TargetDualWield = targetSnapshot.DualWield;
            s.TargetHoldsMelee = target.Animator.Drawn && target.HeldWeaponClass != WeaponClass.Fist && !target.RangedDrawn;
            s.TargetHoldsRangedOrMagic = target.RangedDrawn || target.SpellDrawn;
            s.HasSummons = Session.Summons.HasSummons(_view.Actor);
            s.HasPotion = NpcPotions.Has(Session.Items.Inventories, _view.Actor, Session.NpcCombat.Rules.Timing.PotionItem);
            s.TargetAction = target.FighterAction;
            s.TargetPhase = target.Phase;
            s.TargetSittingKnockedDown = target.SittingKnockedDown;
            s.TargetEngagedWithSelf = target.Opponent == _fighter;
            if (targetId.IsPlayer)
            {
                s.PlayerSeesSelf = CombatPresenter.Sees(target, _fighter); // AAI context +0x46: IsInFOV, ±90° (audit #15)
                s.PlayerSprinting = target.FighterAction == FighterAction.Move && target.TryGetComponent<G3.Unity.G3Player>(out var p) &&
                                    p.MoveMode == G3.Unity.G3MoveMode.Sprint;
            }
            s.ReachedFleePoint = brain.State.Phase == CombatPhase.Flee && (to.magnitude >= FleePointDistance || brain.Retreating && _retreatDone);
            return s;
        }

        bool LineOfSight(CombatFighter target)
        {
            var eye = transform.position + Vector3.up * 1.5f;
            var at = target.Body.Center;
            if (!Physics.Linecast(eye, at, out var hit, ~0, QueryTriggerInteraction.Ignore)) return true;
            return hit.transform.IsChildOf(target.transform);
        }

        bool Free(Vector3 dir)
        {
            var from = transform.position + Vector3.up * 0.8f;
            return !Physics.Raycast(from, dir.normalized, BackProbe, ~0, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------------------------------- orders

        void Execute(NpcCombatBrain brain, in CombatOrder order, CombatFighter target)
        {
            // The brain's shout ("FinishingMove", "RunAway" …) comes once, with the order that starts its phase.
            if (order.Shout != null) Session.Events.Publish(new CombatShout(_view.Actor, order.Shout));
            // A move that ends after the fight phase changed (ransacking, fleeing) must not end the new phase's work.
            if (order.Intent != CombatIntent.Fight) _moveStarted = false;
            if (order.Intent != CombatIntent.Heal)
            {
                _drinkUntil = -1;
                RestoreDrinkHands();
            }
            switch (order.Intent)
            {
                case CombatIntent.Holster:
                    EndFight();
                    if (_fighter.Animator.Drawn && !_fighter.Animator.Busy) _fighter.Sheathe();
                    return;
                case CombatIntent.Flee:
                    if (target == null) return;
                    if (brain.Retreating) Retreat(target); // fix-142: backing off from a target it cannot reach
                    else
                    {
                        _retreatPlanned = _retreatDone = false;
                        Run(transform.position - target.transform.position, _runSpeed);
                    }
                    return;
                case CombatIntent.WaitForFlee:
                    if (target != null) Face(target.transform.position);
                    return;
                case CombatIntent.Heal:
                    Drink(brain, target);
                    return;
                case CombatIntent.Ransack:
                    Ransack(brain, target);
                    return;
            }
            if (order.Intent != CombatIntent.Fight) return;
            if (!WeaponReady(brain, target)) return;
            if (_fighter.Animator.InReaction || _fighter.Animator.IsDown) return;

            bool newMove = brain.State.MoveStart != _lastMoveStart || order.Move != _lastMove;
            _lastMoveStart = brain.State.MoveStart;
            _lastMove = order.Move;
            if (_fighter.Animator.InParade && order.Move != CombatMove.Parade) _fighter.Animator.LowerParade();
            if (target == null) return;

            var move = order.Move;
            if (_fighter.RangedDrawn)
            {
                ExecuteRanged(brain, move, target, newMove);
                return;
            }
            if (_fighter.SpellDrawn && move == CombatMove.Cast)
            {
                ExecuteCast(brain, order, target, newMove);
                return;
            }
            if (move.IsAttack() || move == CombatMove.Parade || move == CombatMove.JumpBack)
            {
                if (newMove) StartBodyMove(brain, move);
                if (!_fighter.Animator.InMove) Face(target.transform.position);
                return;
            }
            var toTarget = target.transform.position - transform.position;
            toTarget.y = 0;
            float gap = toTarget.magnitude - _fighter.Body.Footprint - target.Body.Footprint;
            switch (move)
            {
                case CombatMove.Fwd:
                case CombatMove.Move:
                    // Move = ZS_Attack_Sprint: walk mode 3 (Sprint), the rig's sprint speed (audit #11).
                    float speed = move == CombatMove.Move ? _sprintSpeed : _runSpeed;
                    _sprinting = move == CombatMove.Move && gap > MinGap;
                    // fix-142: along the NavMesh round ledges (G3 ZS_Attack_Goto = engine goto), not straight into the edge
                    if (gap > MinGap) Chase(target, speed, gap);
                    else
                    {
                        _reachTracker.Reset();
                        Face(target.transform.position);
                    }
                    break;
                case CombatMove.Back:
                    Step(-toTarget.normalized, _walkSpeed);
                    Face(target.transform.position);
                    break;
                case CombatMove.Left:
                case CombatMove.Right:
                {
                    // Self's left as it faces the target (Cross(up, toTarget) is its RIGHT in Unity's frame: was mirrored).
                    var p = transform.position;
                    var t = target.transform.position;
                    CombatFootwork.Side(p.x, p.z, t.x, t.z, move == CombatMove.Left, out float sx, out float sz);
                    Step(new Vector3(sx, 0, sz), _runSpeed * StrafeSpeedFactor);
                    Face(target.transform.position);
                    break;
                }
                case CombatMove.TurnLeft:
                case CombatMove.TurnRight:
                    Circle(target, move == CombatMove.TurnLeft);
                    break;
                default: // Wait
                    Face(target.transform.position);
                    break;
            }
        }

        /// <summary>
        /// The weapon of the brain's weapon mode is in the hand (G3 _AI_SelectWeapon): decided from the hand itself
        /// (<see cref="WeaponHand.Next"/>), so an interrupted draw is simply drawn again. A different weapon or the spell
        /// hand is put away first, then the wanted one drawn (a knocked-out NPC picks its own weapon up again unless it
        /// was taken). The brain waits meanwhile (<see cref="WeaponSelection.Ready"/>).
        /// </summary>
        bool WeaponReady(NpcCombatBrain brain, CombatFighter target)
        {
            var loadout = brain.Loadout;
            bool spell = brain.State.Weapon == WeaponMode.Magic;
            bool ranged = !spell && brain.State.Weapon == WeaponMode.Ranged && loadout.CanShoot;
            var item = spell ? null : ranged ? loadout.RangedItem : loadout.MeleeItem;
            var weapon = spell ? WeaponClass.Fist : ranged ? loadout.Ranged.Value : loadout.Melee;
            string field = spell ? G3CombatMotions.Cast : CombatMotionNames.HandField(weapon);
            var a = _fighter.Animator;
            var step = a.Hand.Next(field, spell ? null : CombatFighter.VisualKey(item));
            if (step == WeaponStep.Ready)
            {
                if (!spell && (_fighter.WeaponItem != item || _fighter.WeaponClass != weapon)) _fighter.SetWeapon(item, weapon);
                return true;
            }
            if (!spell && _magic.Casting) _magic.Abort();
            if (a.InParade) a.LowerParade(); // a held parade would keep the body busy for good
            if (target != null) Face(target.transform.position);
            if (a.InAim)
            {
                a.LowerAim();
                FinishShot(brain);
            }
            if (step == WeaponStep.Wait || a.Busy || a.InReload || a.InShot) return false;
            if (step == WeaponStep.Sheathe) _fighter.Sheathe();
            else if (spell) _fighter.DrawSpell();
            else
            {
                _fighter.SetWeapon(item, weapon);
                _fighter.Draw();
            }
            return false;
        }

        /// <summary>
        /// A Cast order (ZS_Attack_Cast → _AI_Cast): load the brain's spell (powered at once when the brain chose a power
        /// cast), facing the target; it goes off by itself at the end of its raise motion (fix-114, <see cref="CastTrigger"/>:
        /// G3's cast commands never wait); the move ends with the cast motion.
        /// </summary>
        void ExecuteCast(NpcCombatBrain brain, in CombatOrder order, CombatFighter target, bool newMove)
        {
            Face(target.transform.position);
            if (!newMove) return;
            _magic.Abort();
            _moveStarted = true;
            var def = _presenter.CombatDefOf(_view.Actor);
            if (!_magic.Begin(order.Spell, def?.Species))
            {
                FinishShot(brain);
                return;
            }
            if (order.PowerCast) _magic.PowerUp();
            if (!_magic.ReleaseAfterRaise(target, null)) FinishShot(brain);
        }

        /// <summary>The NPC's spell glows in its hand while the spell hand is drawn.</summary>
        void ShowSpell()
        {
            var brain = Brain();
            if (_fighter.SpellDrawn && _fighter.Animator.WeaponInHand && brain?.State.Spell != null && Session.Magic.TryGetSpell(brain.State.Spell, out var spell))
                _magic.ShowHeld(spell);
            else _magic.HideHeld();
        }

        /// <summary>
        /// Bow or crossbow orders (ZS_Attack_Shoot): run closer, wait with an arrow nocked (crossbowmen reload before
        /// each shot), or shoot: aim, hold for a moment, release a projectile aimed at the target; the move ends with the recover.
        /// </summary>
        void ExecuteRanged(NpcCombatBrain brain, CombatMove move, CombatFighter target, bool newMove)
        {
            var a = _fighter.Animator;
            var to = target.Body.Center - (transform.position + Vector3.up * 1.4f);
            a.AimPitch = Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg;
            if (move == CombatMove.Fwd || move == CombatMove.Move)
            {
                if (a.InAim) a.LowerAim();
                var flat = new Vector3(to.x, 0, to.z);
                float gap = flat.magnitude - _fighter.Body.Footprint - target.Body.Footprint;
                if (gap > MinGap) Run(flat, _runSpeed, Mathf.Min(_runSpeed * Time.deltaTime, gap - MinGap));
                return;
            }
            Face(target.transform.position);
            if (move == CombatMove.Shoot && newMove)
            {
                _shotPending = true;
                _moveStarted = true;
            }
            if (!a.Nocked)
            {
                if (!a.InReload && !a.InShot) Reload(brain);
                return;
            }
            if (!_shotPending) return;
            if (!a.InAim)
            {
                if (a.Aim()) _aimStart = Time.time;
                else FinishShot(brain);
                return;
            }
            if (!a.AimHeld || Time.time - _aimStart < AimSeconds) return;
            Fire(brain, target);
        }

        void Reload(NpcCombatBrain brain)
        {
            float speed = _fighter.WeaponClass == WeaponClass.Crossbow && _fighter.SpeedFactor != null ? _fighter.SpeedFactor(CombatMotion.Cock, false) : 1f;
            _fighter.Animator.Reload(brain.Loadout.Ammo?.Visual?.Mesh, speed);
        }

        /// <summary>
        /// The NPC's shot: from the hand straight at the target's body where it is at release (no lead), at the template
        /// velocity; path style 0 (Line) flies on without re-aiming, so a target that steps aside is missed (G3 _AI_Shoot /
        /// gCProjectile_PS::Shoot, <see cref="ProjectileFlight"/>, fix-93);
        /// NPCs get every arrow back (infinite ammunition) [data]. HitMultiplier 1 (the template's) [interp].
        /// </summary>
        void Fire(NpcCombatBrain brain, CombatFighter target)
        {
            _shotPending = false;
            var hand = FindHand();
            var from = (hand != null ? hand.position : transform.position + Vector3.up * 1.5f) + transform.forward * 0.3f;
            var aim = target.SpinePoint; // G3 GetDirection aims at Spine_Spine_1
            var v = ProjectileFlight.ReleaseVelocity(new P3(from.x, from.y, from.z), new P3(aim.x, aim.y, aim.z),
                                                     RangedRules.ShotVelocity(player: false, 1f));
            var shot = new Projectile.Shot
            {
                Shooter = _fighter,
                Ammo = brain.Loadout.Ammo,
                Weapon = brain.Loadout.RangedItem,
                WeaponClass = _fighter.WeaponClass,
                HitMultiplier = 1f,
                Quality = brain.Loadout.Ammo?.Quality ?? 0, // poison arrows (combat-6)
                Homing = target,
            };
            if (!_fighter.Animator.Shoot())
            {
                FinishShot(brain);
                return;
            }
            _presenter.Shoot(shot, from, new Vector3(v.X, v.Y, v.Z));
        }

        Transform _hand;

        Transform FindHand()
        {
            if (_hand == null) _hand = FindDeep(transform, "Slot_RightHand_Weapon");
            return _hand;
        }

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

        void OnRangedEnded(string what)
        {
            if (what == "Shoot" && _presenter != null && Brain() is NpcCombatBrain brain) FinishShot(brain);
        }

        void FinishShot(NpcCombatBrain brain)
        {
            _shotPending = false;
            if (!_moveStarted || brain == null) return;
            _moveStarted = false;
            brain.OnMoveFinished(Session.PlaySeconds);
        }

        /// <summary>
        /// A sitting knock-down ends (G3 ZS_SitKnockDown_Loop for an NPC) with the GetUpParade when the attacker is in a
        /// PowerAttack, PierceAttack or HackAttack, else with the GetUpAttack; plainly without the melee weapon config
        /// (<see cref="SitKnockDownRules.NpcGetUp"/>). The move is paid like any move (0 stamina).
        /// </summary>
        CombatAction GetUpChoice()
        {
            var brain = Brain();
            if (brain == null) return CombatAction.None;
            var target = _fighter.Opponent;
            bool melee = _fighter.Animator != null && _fighter.Animator.Drawn && !_fighter.RangedDrawn && !_fighter.SpellDrawn;
            var action = SitKnockDownRules.NpcGetUp(target != null ? target.CurrentAction : CombatAction.None, melee);
            return action == CombatAction.None ? action : Session.Combat.StartMove(_view.Actor, action);
        }

        void StartBodyMove(NpcCombatBrain brain, CombatMove move)
        {
            _moveStarted = false;
            if (move == CombatMove.Parade)
            {
                if (!_fighter.Animator.RaiseParade()) brain.OnMoveFinished(Session.PlaySeconds, completed: false);
                return;
            }
            if (move == CombatMove.Shoot || move == CombatMove.Cast)
            {
                brain.OnMoveFinished(Session.PlaySeconds, completed: false); // a shot or cast without the bow or the spell hand drawn
                return;
            }
            var action = Session.Combat.StartMove(_view.Actor, move.ToAction());
            _moveStarted = _fighter.StartMove(action);
            if (!_moveStarted) brain.OnMoveFinished(Session.PlaySeconds, completed: false);
        }

        void OnMoveEnded(CombatAction action)
        {
            if (!_moveStarted || _presenter == null) return;
            _moveStarted = false;
            Brain()?.OnMoveFinished(Session.PlaySeconds);
        }

        /// <summary>
        /// ZS_FetchWeapon (after getting up unransacked): farther than 20 m the weapon is left; else walk to it, take it
        /// (_AI_TakeItem), back to the routine. Nothing is drawn afterwards. Perception is ignored meanwhile (CP).
        /// </summary>
        void FetchWeapon(Vector3 at)
        {
            if (_fighter.Animator.Busy) return; // still getting up
            var to = at - transform.position;
            to.y = 0;
            if (to.magnitude > FetchRange)
            {
                _presenter.AbandonDropped(_view.Actor);
                _takeUntil = -1;
                return;
            }
            if (to.magnitude > FetchReach)
            {
                Run(to, _walkSpeed, to.magnitude - FetchReach * 0.5f);
                return;
            }
            Face(at);
            if (_takeUntil < 0) _takeUntil = Time.time + TakeSeconds;
            if (Time.time < _takeUntil) return;
            _takeUntil = -1;
            _presenter.PickUpDropped(_view.Actor);
        }

        (string field, string template)? _drinkHands;

        /// <summary>The weapon put away for the drink goes back into the hand at once (G3 _AI_HoldInventoryItems; fix-68).</summary>
        void RestoreDrinkHands()
        {
            if (_drinkHands == null) return;
            var (field, template) = _drinkHands.Value;
            _drinkHands = null;
            if (Session != null && Session.Combat.Vitals.StateOf(_view.Actor) == VitalState.Alive) _fighter.Animator.HoldAtOnce(field, template);
        }

        /// <summary>
        /// ZS_Attack_DrinkPotion: one healing potion, facing the target, then back to the fight (combat-6). As for the hero
        /// (_AI_UseItem → _AI_UseInventoryItem, fix-68): the weapon leaves the hand at once, the potion is drunk with the
        /// use motion, then the weapon is back in the hand (no sheathe or draw motion).
        /// </summary>
        void Drink(NpcCombatBrain brain, CombatFighter target)
        {
            if (target != null) Face(target.transform.position);
            if (_drinkUntil < 0)
            {
                if (_fighter.Animator.InParade) _fighter.Animator.LowerParade();
                if (_fighter.Animator.Busy || !NpcPotions.Drink(Session.Items.Inventories, Session.Content.Database, Session.Combat.Vitals,
                                                                 _view.Actor, Session.NpcCombat.Rules.Timing.PotionItem))
                {
                    brain.OnMoveFinished(Session.PlaySeconds);
                    return;
                }
                _drinkUntil = Time.time + DrinkSeconds;
                if (_fighter.Animator.StowAtOnce(out string held, out string heldTemplate)) _drinkHands = (held, heldTemplate);
                if (_npcAnimator != null) _npcAnimator.UseItemOnce(Session.NpcCombat.Rules.Timing.PotionItem, "Potion");
                if (_presenter.logHits) Debug.Log($"[Myrtana] {name} drinks {Session.NpcCombat.Rules.Timing.PotionItem}: HP {Session.Combat.Vitals.HitPoints(_view.Actor)}");
                return;
            }
            if (Time.time < _drinkUntil) return;
            _drinkUntil = -1;
            RestoreDrinkHands();
            brain.OnMoveFinished(Session.PlaySeconds);
        }

        void Ransack(NpcCombatBrain brain, CombatFighter target)
        {
            // Once per knock-out (G3 victim.Ransacked): another winner already robs or robbed this body.
            if (target == null || Session.Combat.Vitals.StateOf(target.Actor) != VitalState.Down ||
                !_presenter.ClaimRansack(_view.Actor, target.Actor))
            {
                brain.OnMoveFinished(Session.PlaySeconds);
                return;
            }
            if (_fighter.Animator.InParade) _fighter.Animator.LowerParade();
            var to = target.transform.position - transform.position;
            to.y = 0;
            if (to.magnitude > RansackReach)
            {
                _ransackTime = -1;
                Run(to, _walkSpeed, Mathf.Min(_walkSpeed * Time.deltaTime, to.magnitude - RansackReach + 0.05f));
                return;
            }
            Face(target.transform.position);
            if (_ransackTime < 0) _ransackTime = Time.time;
            if (Time.time - _ransackTime < RansackSeconds) return;
            Debug.Log("[Myrtana] " + _presenter.Ransack(_view.Actor, target.Actor));
            _ransackTime = -1;
            brain.OnMoveFinished(Session.PlaySeconds);
        }

        // ------------------------------------------------------------------------------------------- body

        /// <summary>Turns towards <paramref name="point"/> at G3's TurnSpeedMax (200°/s, half while sprinting; audit #8).</summary>
        void Face(Vector3 point)
        {
            var dir = point - transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-4f) return;
            float speed = CombatFootwork.TurnSpeed(_sprinting);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir), speed * Time.deltaTime);
        }

        /// <summary>
        /// ZS_Attack_TurnLeft/TurnRight (audit #1): run sideways around the target at the current radius for the brain's
        /// 0.6 s, facing it (the locomotion plays the sideways run from the measured velocity).
        /// </summary>
        void Circle(CombatFighter target, bool left)
        {
            var p = transform.position;
            var t = target.transform.position;
            CombatFootwork.CircleStep(p.x, p.z, t.x, t.z, left, _circleSpeed * Time.deltaTime, out float x, out float z);
            Displace(new Vector3(x - p.x, 0, z - p.z));
            Face(t);
        }

        void Run(Vector3 dir, float speed, float maxStep = float.MaxValue)
        {
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-6f) return;
            Face(transform.position + dir);
            Displace(dir.normalized * Mathf.Min(speed * Time.deltaTime, maxStep));
        }

        void Step(Vector3 dir, float speed) => Displace(dir * (speed * Time.deltaTime));

        /// <summary>Moves the body on the ground, never into its opponent's body.</summary>
        int _corpseSerial;
        CorpseRest _corpse;

        /// <summary>
        /// A corpse that came to rest (<see cref="CorpseRest"/>) has its origin under the middle of the lying body: the
        /// simulation keeps that spot, so a save and the view streaming in again show it there (fix-80).
        /// </summary>
        void ReportCorpse()
        {
            if (_corpse == null && !TryGetComponent(out _corpse)) return;
            if (!_corpse.Rested || _corpse.Serial == _corpseSerial) return;
            _corpseSerial = _corpse.Serial;
            var p = transform.position;
            var f = transform.forward; // tilted onto a slope: the heading of the projected forward
            // fix-163: CorrectPose, not ReportPose: ReportPose is ignored for a Near NPC, so a corpse settled while Near kept
            // its old origin in the save and was off by the settling's move after every load
            Session.Npcs.CorrectPose(_view.Actor, new WorldPose(p.x, p.y, p.z, Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg));
        }

        void Displace(Vector3 offset)
        {
            var p = transform.position + offset;
            var other = _fighter != null ? _fighter.Opponent : null;
            // A finishing move places the body itself, close to the lying victim (staff: 0.5 m from its torso).
            if (other != null && other.Body != null && !_fighter.Placing)
            {
                float min = _fighter.Body.Footprint + other.Body.Footprint;
                var away = p - other.transform.position;
                away.y = 0;
                if (away.sqrMagnitude < min * min)
                {
                    var dir = away.sqrMagnitude > 1e-6f ? away.normalized : -transform.forward;
                    var q = other.transform.position + dir * min;
                    p.x = q.x;
                    p.z = q.z;
                }
            }
            var at = transform.position;
            // floor ray from the step height only (fix-99: from 1.5 m it found a low cave ceiling's top and lifted the body),
            // more for a longer move: the body's whole shift this frame, the push off the opponent included (fix-108: a
            // zero offset with a push of up to a metre up a slope probed from 65 cm, found no floor and stayed)
            float probe = NpcWalker.StepProbe(new Vector2(p.x - transform.position.x, p.z - transform.position.z).magnitude);
            if (NpcWalker.TryGroundHeight(p, out float ground, probe)) p.y = ground;
            else if (_view != null && _view.OverWater) { } // the water surface below decides (next lines)
            // fix-60: no floor within the step under the new spot (a tower top's or wall walk's edge): G3 bodies keep their
            // feet on collision, so the fighter stays at the edge instead of running on into the air; a body that already
            // stands on nothing comes down onto the floor below
            else if (NpcWalker.TryGroundHeight(at, out _, probe)) p = at;
            else p.y = NpcWalker.FloorHeight(p, p.y);
            // fix-160: a push off the opponent never lifts the fighter up the cave wall's foot into the rock (Hauke's cave:
            // the minecrawler and the meatbugs ended 1.3 m up inside Dungeon_SP11, out of sight)
            if (!CaveGround.StepAllowed(at, p)) p = at;
            // a bloodfly fights over water on the surface (TreatWaterAsSolid), so it reaches a swimming hero
            // (it sinks from a higher shore at FlyDescentSpeed instead of snapping) [interp]
            if (_view != null && _view.OverWater)
            {
                float surface = NpcWalker.SurfaceOrGround(p);
                p.y = surface != p.y && surface < transform.position.y
                    ? Mathf.Max(surface, transform.position.y - FlyDescentSpeed * Time.deltaTime)
                    : surface;
            }
            transform.position = p;
        }
    }
}
