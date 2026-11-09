using System;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>Where an NPC is in a fight (Docs/combat.md §6).</summary>
    public enum CombatPhase
    {
        /// <summary>Not fighting.</summary>
        Idle,
        /// <summary>Fighting the target: approach, active (token) or passive moves, chasing.</summary>
        Fight,
        /// <summary>The chase ran out: stand and watch whether the target keeps fleeing (ZS_WaitForFlee).</summary>
        WaitForFlee,
        /// <summary>Running from the target (Panic, or a species that cannot fight).</summary>
        Flee,
        /// <summary>The target is down: the token holder finishes it off.</summary>
        Finishing,
        /// <summary>The token holder loots the downed target.</summary>
        Ransacking,
    }

    /// <summary>The saved state of one NPC's combat brain: plain data (<see cref="NpcCombatSave"/>).</summary>
    public sealed class NpcCombatState
    {
        public CombatPhase Phase { get; internal set; }
        public ActorId Target { get; internal set; }
        public AttackReason Reason { get; internal set; }
        public WeaponMode Weapon { get; internal set; }
        /// <summary>The running move and when it started (real seconds); footwork also has an end time.</summary>
        public CombatMove Move { get; internal set; }
        public double MoveStart { get; internal set; }
        public double MoveUntil { get; internal set; }
        /// <summary>An attack, parade or jump waits for the body to report its end.</summary>
        public bool MoveRunning { get; internal set; }
        public float ChaseSeconds { get; internal set; }
        public float WaitDistance { get; internal set; }
        public double NextWaitCheck { get; internal set; }
        /// <summary>Game seconds of the last own hit that landed and of the last hit taken (fight activity, party watch).</summary>
        public double LastHitLanded { get; internal set; } = double.NegativeInfinity;
        public double LastHitTaken { get; internal set; } = double.NegativeInfinity;
        /// <summary>
        /// Game seconds of the END of the last own attack or cast: G3 PSNpc::SetLastHit (Script.dll 0x1000bc80). Every
        /// ZS_Attack_Attack/_SimpleWhirl/_PowerAttack/_WhirlAttack/_QuickAttack/_PierceAttack/_HackAttack/_Cast calls it in
        /// its second step, after its _AI_ command (the swing, _AI_QuickAttack's 100 ms wait included) has finished, right
        /// before ZS_Attack_Loop (fix-81, CP 1.75; it was taken for the start). A move cut short by a hit reaction
        /// never gets there. PSNpc::CanHit (0x1000bd00) reads it: the MinHitDuration gate and the AAI −35 for 30 game s.
        /// </summary>
        public double LastAttackEnd { get; internal set; } = double.NegativeInfinity;
        public double LastTick { get; internal set; }
        /// <summary>The spell item in the hand while fighting with magic (null = none), the last one cast, and when (real s).</summary>
        public string Spell { get; internal set; }
        public string LastSpell { get; internal set; }
        public double LastCastAt { get; internal set; } = double.NegativeInfinity;
        /// <summary>The running cast is a power cast (ZS_Attack_Cast: 20 %, ice golems always).</summary>
        public bool PowerCast { get; internal set; }

        public override string ToString() => $"{Phase} vs {Target} ({Reason}) {Move}{(MoveRunning ? "…" : "")}";
    }

    /// <summary>
    /// The combat AI of one NPC (G3 ZS_Attack and friends, CP 1.75 with extended AI): an explicit state machine over
    /// <see cref="NpcCombatState"/>. Every tick the world hands in a <see cref="CombatSituation"/> and gets a
    /// <see cref="CombatOrder"/> back; the body reports when a move has ended and when hits land. Weapon choice, the
    /// attack token (<see cref="CombatCoordinator"/>), the move choice (<see cref="CombatMoveSelector"/>), chasing and
    /// giving up, fleeing on Panic, healing, and the end of a fight (finishing move, ransacking, holstering). Pure.
    /// </summary>
    public sealed class NpcCombatBrain
    {
        public const string ShoutRunAway = "RunAway", ShoutYesGetLost = "YesGetLost", ShoutStandStill = "StandStill";
        /// <summary>
        /// The say gate's kinds of the battle lines (<see cref="SpeechGate"/>): StartSayAttackReason and StartSayAttackEnd
        /// share G3's kind 3, so one battle line of either sort per game minute.
        /// </summary>
        public const string SayAttackReason = "AttackLine", SayAttackEnd = "AttackLine";

        readonly CombatAiRules _ai;
        readonly CombatRules _combat;
        readonly RuleOptions _options;
        readonly IRandom _random;
        readonly CombatMoveSelector _selector;
        readonly CombatCoordinator _coordinator;

        public NpcCombatBrain(ActorId self, NpcCombatDef definition, CombatLoadout loadout, CombatAiRules ai, CombatRules combat,
                              RuleOptions options, IRandom random, CombatCoordinator coordinator, NpcCombatState state = null)
        {
            Self = self;
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Loadout = loadout ?? CombatLoadout.Fists;
            _ai = ai ?? throw new ArgumentNullException(nameof(ai));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _selector = new CombatMoveSelector(ai, options, random);
            State = state ?? new NpcCombatState();
        }

        public ActorId Self { get; }
        /// <summary>The port's live combat options for the move choice (set by <see cref="NpcCombatSystem"/>).</summary>
        public CombatPortOptions Port { get => _selector.Port; set => _selector.Port = value ?? new CombatPortOptions(); }
        public NpcCombatDef Definition { get; }
        public CombatLoadout Loadout { get; internal set; }
        public NpcCombatState State { get; }
        public CombatMoveSelector Selector => _selector;
        /// <summary>
        /// The NPC gave up a fight because its target got away (beyond the give-up distance; G3 CancelAttack). Set by
        /// <see cref="NpcCombatSystem"/>, which publishes <see cref="FightGivenUp"/>.
        /// </summary>
        public Action<ActorId, AttackReason> GaveUp { get; set; }

        /// <summary>The say gate of the battle lines (null = every line is said). Set by <see cref="NpcCombatSystem"/>.</summary>
        public SpeechGate Speech { get; set; }

        /// <summary>A shout waits for the next order (the attack reason after entering a fight, "RunAway" after a flight starts).</summary>
        bool _sayAttackReason;
        string _pendingShout;

        /// <summary>The spell an item casts (null = no magic content: nobody casts). Set by <see cref="NpcCombatSystem"/>.</summary>
        public Func<string, SpellDef> Spells { get; set; }

        /// <summary>Mages, paladins and ice golems fight with spells (G3 creates the spells they cast).</summary>
        public bool CanCast => Spells != null &&
            (Definition.Class == NpcClass.Mage || Definition.Class == NpcClass.Paladin || IsIceGolem);

        bool IsIceGolem => _ai.Magic.IceGolem != null && string.Equals(Definition.Species, _ai.Magic.IceGolem, StringComparison.Ordinal);

        public bool IsFighting => State.Phase != CombatPhase.Idle;
        /// <summary>In a fight against the target (not fleeing from it).</summary>
        public bool IsAttacking => State.Phase == CombatPhase.Fight || State.Phase == CombatPhase.WaitForFlee || State.Phase == CombatPhase.Finishing;
        public bool HoldsToken => _coordinator.Holds(Self);

        /// <summary>The parade is up: the parade move has run past its stance time (0.3 s with extended AI, else 0.5 s).</summary>
        public bool IsParrying(double now) =>
            State.Phase == CombatPhase.Fight && State.Move == CombatMove.Parade && State.MoveRunning && now - State.MoveStart >= ParadeStance;

        float ParadeStance => _options.ExtendedAI ? _ai.Timing.ExtendedParadeStanceSeconds : _ai.Timing.ParadeStanceSeconds;

        // ------------------------------------------------------------------------------------------- commands

        /// <summary>
        /// Start (or continue) a fight against <paramref name="target"/>. A running fight against the same target only
        /// escalates its reason (reasons never go down during one fight).
        /// </summary>
        public void Engage(ActorId target, AttackReason reason, double now)
        {
            // fix-142: a target this NPC could not reach is left alone for a while (perception); a hit from up there makes
            // it back off again instead of standing under the arrows; an order (player command, duel, arena, story kill)
            // still starts the fight.
            if (reason < AttackReason.PlayerCommand && IgnoresUnreachable(target, now))
            {
                if (reason == AttackReason.ReactToDamage && State.Phase == CombatPhase.Idle) Retreat(target, now);
                return;
            }
            if (reason >= AttackReason.PlayerCommand) _unreachableUntil = double.NegativeInfinity;
            if (State.Phase != CombatPhase.Idle && State.Phase != CombatPhase.Flee && State.Target == target)
            {
                if (reason > State.Reason) State.Reason = reason;
                return;
            }
            // ZS_Attack: StartSayAttackReason only when the NPC was not in AI mode Combat yet (a first entry, or after fleeing).
            if (State.Phase == CombatPhase.Idle || State.Phase == CombatPhase.Flee) _sayAttackReason = true;
            State.Phase = CombatPhase.Fight;
            State.Target = target;
            State.Reason = reason;
            State.ChaseSeconds = 0;
            State.LastTick = now;
            StopMove(now);
        }

        /// <summary>Runs away from <paramref name="from"/> (ZS_Flee, e.g. after the Fear spell) until it is far enough.</summary>
        public void Flee(ActorId from, double now)
        {
            // ZS_Flee: "RunAway" unless already in AI mode Flee.
            _pendingShout = State.Phase != CombatPhase.Flee && Definition.Humanoid ? ShoutRunAway : null;
            _sayAttackReason = false;
            _coordinator.Leave(Self);
            _retreating = false;
            State.Phase = CombatPhase.Flee;
            State.Target = from;
            State.Reason = AttackReason.None;
            State.ChaseSeconds = 0;
            State.LastTick = now;
            StopMove(now);
        }

        /// <summary>Ends the fight at once (left the processing range, talked to, knocked out).</summary>
        public void Disengage()
        {
            _coordinator.Leave(Self);
            State.Phase = CombatPhase.Idle;
            State.Target = default;
            State.Reason = AttackReason.None;
            State.Move = CombatMove.Wait;
            State.MoveRunning = false;
            _sayAttackReason = false;
            _pendingShout = null;
            _attackClockPending = false;
            _retreating = false;
        }

        /// <summary>
        /// The body finished the running move (attack, parade, jump, finishing move, ransacking). <paramref name="completed"/>
        /// false: it could not even start (no weapon in the hand, the animator refused), so the attack clock stays.
        /// </summary>
        public void OnMoveFinished(double now, bool completed = true)
        {
            bool attack = completed && State.MoveRunning && StartsAttackClock(State.Move);
            State.MoveRunning = false;
            State.MoveUntil = now;
            if (!attack) return;
            // _AI_QuickAttack ends with sAIWaitInstr(100 ms) after the bite or stab: ZS_Attack_Loop decides only after it.
            if (State.Move == CombatMove.QuickAttack) State.MoveUntil = now + QuickAttackWaitSeconds;
            // ZS_Attack_* step 2: SetLastHit once the command (and its wait) is done, stamped with the game time of the
            // first tick after that (the brain has no game clock here).
            _attackClockPending = true;
            _attackClockAt = State.MoveUntil;
        }

        /// <summary>_AI_QuickAttack's wait after the Hit (sAIWaitInstr 0x64 ms, CP 0x100487e0).</summary>
        public const float QuickAttackWaitSeconds = 0.1f;

        bool _attackClockPending;
        double _attackClockAt;

        /// <summary>The body released a spell (ZS_Attack_Cast sets LastCast; the attack clock started with the cast, <see cref="BeginCast"/>).</summary>
        public void OnSpellCast(double now, double gameNow) => State.LastCastAt = now;

        /// <summary>Own strike hit someone (fight activity only: the attack clock runs from the attack's start).</summary>
        public void OnHitLanded(double gameNow) => State.LastHitLanded = gameNow;

        /// <summary>Self was hit (fight activity only).</summary>
        public void OnHitTaken(double gameNow) => State.LastHitTaken = gameNow;

        /// <summary>
        /// A hit put self into a reaction task (G3 AssessHit → ZS_Stumble/QuickStumble/PierceStumble/ParadeStumble/
        /// HeavyParadeStumble/SitKnockDown): the running move is over, a raised parade included; when the reaction ends
        /// ZS_Attack_Loop picks a new move (audit #3).
        /// </summary>
        public void OnInterrupted(double now)
        {
            if (State.Phase != CombatPhase.Fight) return;
            StopMove(now);
        }

        /// <summary>The hit reactions that are tasks in G3 (PipiStumble, the flinch, is a plain motion and interrupts nothing).</summary>
        public static bool Interrupts(HitReaction reaction) => reaction != HitReaction.None && reaction != HitReaction.Flinch;

        // ------------------------------------------------------------------------------------------- tick

        public CombatOrder Tick(in CombatSituation s)
        {
            float dt = (float)Math.Max(0, s.Now - State.LastTick);
            State.LastTick = s.Now;
            if (_attackClockPending && s.Now >= _attackClockAt)
            {
                _attackClockPending = false;
                State.LastAttackEnd = s.GameNow;
            }
            switch (State.Phase)
            {
                case CombatPhase.Fight: return WithAttackReason(TickFight(s, dt), s);
                case CombatPhase.WaitForFlee: return TickWaitForFlee(s);
                case CombatPhase.Flee: return TickFlee(s);
                case CombatPhase.Finishing:
                    if (State.MoveRunning && s.TargetState == VitalState.Down) return Order(CombatIntent.Fight, CombatMove.FinishingAttack);
                    return EndFight(s);
                case CombatPhase.Ransacking:
                    if (State.MoveRunning) return Order(CombatIntent.Ransack, CombatMove.Wait);
                    return Done(null);
                default:
                    return default;
            }
        }

        CombatOrder TickFight(in CombatSituation s, float dt)
        {
            if (s.TargetState != VitalState.Alive) return EndFight(s);
            // No distance gate here (G3 AssessHit/OnAttack have none: an animal shot from 30 m comes for the shooter);
            // 2000 ends the fight only after the chase ran out (ZS_Attack_WaitForFlee_Loop) (combat-6).
            if (s.AttitudeToTarget == Attitude.Panic || _ai.NeverFights(Definition)) return StartFlee(s);
            if (s.TargetInWater) return StartWaitForFlee(s);

            var c = Context(s);
            State.Weapon = ChooseWeapon(s);
            // fix-142: no way leads into melee reach (the view's chase: NavMesh path, drop links) -> retreat, never stand at
            // the edge under the target's arrows. Ranged and magic fighters shoot from where they are.
            if (s.TargetUnreachable && !s.Arena && State.Weapon == WeaponMode.Melee) return StartRetreat(s);
            if (State.Weapon != WeaponMode.Magic) State.Spell = null;
            // Chasing = running after a target that stays out of reach (melee) or out of shooting range; passive NPCs
            // holding their distance do not chase. Intruders are chased to the end.
            bool running = State.Move == CombatMove.Fwd || State.Move == CombatMove.Move;
            bool chasing = State.Weapon == WeaponMode.Melee ? running && !_selector.InOwnReach(c) : s.Distance > _ai.Weapons.ShootRange || !s.LineOfSight;
            if (!chasing || State.Reason == AttackReason.Intruder) State.ChaseSeconds = 0;
            else State.ChaseSeconds += dt;
            if (State.ChaseSeconds > _ai.Chase.Duration(Definition.Species, _options.ExtendedAI, s.TargetIsPlayer && s.PlayerSprinting))
                return StartWaitForFlee(s);

            bool active = _coordinator.Update(Self, State.Target, s.Distance, s.X, s.Z);

            if (State.MoveRunning || s.Now < State.MoveUntil)
            {
                if (!(State.Move == CombatMove.Parade && ParadeOver(s))) return Order(CombatIntent.Fight, State.Move);
                StopMove(s.Now);
            }
            if (WantsToHeal(s))
            {
                StartMove(CombatMove.Wait, s.Now, running: true);
                return Order(CombatIntent.Heal, CombatMove.Wait);
            }
            if (State.Weapon == WeaponMode.Magic)
            {
                State.Spell = PickSpell(s);
                if (State.Spell == null) State.Weapon = WeaponMode.Melee;
            }
            // G3 _AI_SelectWeapon blocks until the wanted weapon is in the hand: no move with the wrong one.
            if (!WeaponSelection.Ready(s.Held, State.Weapon))
            {
                StartMove(CombatMove.Wait, s.Now, running: false);
                return Order(CombatIntent.Fight, CombatMove.Wait);
            }
            var move = ChooseMove(c, active);
            if (move == CombatMove.Cast) BeginCast();
            StartMove(move, s.Now, running: IsBodyTimed(move));
            if (!IsBodyTimed(move)) State.MoveUntil = s.Now + FootworkSeconds(move);
            return Order(CombatIntent.Fight, move);
        }

        /// <summary>Extended AI drops the parade after 2 s; without it the parade lasts while the target attacks [interp].</summary>
        bool ParadeOver(in CombatSituation s) =>
            _options.ExtendedAI ? s.Now - State.MoveStart >= _ai.Timing.ExtendedParadeLeaveSeconds
                                : s.Now - State.MoveStart >= ParadeStance && !s.TargetAction.IsAttacking();

        CombatMove ChooseMove(in MoveContext c, bool active)
        {
            var s = c.Situation;
            switch (State.Weapon)
            {
                case WeaponMode.Ranged:
                    // FAI_Ranged (0x10050370): Shoot within range and sight, else Fwd; no CanHit gate (audit #6).
                    if (s.Distance > _ai.Weapons.ShootRange || !s.LineOfSight) return CombatMove.Fwd;
                    return CombatMove.Shoot;
                case WeaponMode.Magic:
                    return MagicMove(c);
                default:
                    return active ? _selector.SelectActive(c) : _selector.SelectPassive(c);
            }
        }

        /// <summary>
        /// Weapon choice (_AI_SelectWeapon, combat_ai.json weaponChoice): mages by their own rules (<see cref="MageWeapon"/>);
        /// ice golems melee close or while their cast cools down; everybody else melee close, the current weapon in between,
        /// and beyond that paladins magic, the rest a bow (but melee against skeletons and golems, and without ammunition).
        /// </summary>
        WeaponMode ChooseWeapon(in CombatSituation s)
        {
            var w = _ai.Weapons;
            var m = _ai.Magic;
            bool canCast = CanCast;
            if (canCast && Definition.Class == NpcClass.Mage) return MageWeapon(s);
            if (canCast && IsIceGolem)
                return s.Distance < m.IceGolemMeleeBelow || s.Now < State.LastCastAt + m.IceGolemCastCooldownSeconds ? WeaponMode.Melee : WeaponMode.Magic;
            if (s.Distance < w.MeleeBelow) return WeaponMode.Melee;
            if (s.Distance < w.KeepCurrentBelow) return WeaponSelection.KeepCurrent(s.Held, State.Weapon);
            if (canCast && Definition.Class == NpcClass.Paladin) return WeaponMode.Magic;
            if (!Loadout.CanShoot) return WeaponMode.Melee;
            bool meleeTarget = s.TargetSpecies != null && w.MeleeVsSpecies.Contains(s.TargetSpecies);
            return meleeTarget ? WeaponMode.Melee : WeaponMode.Ranged;
        }

        /// <summary>
        /// A mage against a humanoid: the player within 250 → melee, within 400 → magic only for a summoner boss that can
        /// summon; an NPC within 400 with no room to back off → melee; already in melee and closer than 470 → stay. Else magic.
        /// </summary>
        WeaponMode MageWeapon(in CombatSituation s)
        {
            var m = _ai.Magic;
            if (!s.TargetHumanoid) return WeaponMode.Magic;
            if (s.TargetIsPlayer)
            {
                if (s.Distance < m.MageMeleePlayerWithin) return WeaponMode.Melee;
                if (s.Distance < m.MageSummonerPlayerWithin) return SummonerSpell(s) != null ? WeaponMode.Magic : WeaponMode.Melee;
            }
            else if (s.Distance < m.MageMeleeNpcWithin && !s.FreeBack) return WeaponMode.Melee;
            if (s.Distance < m.MageStayMeleeBelow && State.Weapon == WeaponMode.Melee) return WeaponMode.Melee;
            return WeaponMode.Magic;
        }

        /// <summary>
        /// The spell picker: a summoner boss summons; monster casters use their own spell; below 40 % HP a 50 % chance to
        /// heal; the counter-element against golems; else keep the held default spell or pick one at random. Only spells the
        /// content knows are picked (null = none: fight in melee).
        /// </summary>
        string PickSpell(in CombatSituation s)
        {
            var m = _ai.Magic;
            string spell = SummonerSpell(s) ?? MagicAiRules.Find(m.SpeciesSpells, Definition.Species);
            if (spell == null && s.HitPointsPercent < m.HealBelowPercent && m.HealSpell != null && _random.Next(100) < m.HealChance)
                spell = m.HealSpell;
            spell ??= MagicAiRules.Find(m.VersusSpecies, s.TargetSpecies);
            if (spell == null && State.Spell != null && m.DefaultSpells.Contains(State.Spell)) spell = State.Spell;
            if (spell == null && m.DefaultSpells.Count > 0) spell = m.DefaultSpells[_random.Next(m.DefaultSpells.Count)];
            return spell != null && Spells?.Invoke(spell) != null ? spell : null;
        }

        /// <summary>A summoner boss (AB or extended AI) fighting the player without living summons summons.</summary>
        string SummonerSpell(in CombatSituation s) =>
            (_options.AlternativeBalancing || _options.ExtendedAI) && s.TargetIsPlayer && !s.HasSummons
                ? MagicAiRules.Find(_ai.Magic.SummonerBosses, Self.Name) : null;

        /// <summary>
        /// FAI_Magic: non-focus spells (heal, summon) are cast at once. Focus spells keep the distance: closer than 350 back
        /// off (80 %, room behind) or cast; beyond 2000 come closer; in 49 % of the decisions adjust around 1175; repeating
        /// the last spell waits now and then (and until the hit cooldown allows), a new spell is cast at once.
        /// </summary>
        CombatMove MagicMove(in MoveContext c)
        {
            var s = c.Situation;
            var m = _ai.Magic;
            var spell = Spells?.Invoke(State.Spell);
            if (spell == null || !spell.NeedsFocus) return CombatMove.Cast;
            if (s.Distance < m.TooClose)
            {
                if (s.FreeBack && _random.Next(100) < m.TooCloseBackChance) return CombatMove.Back;
                if (c.AttackReady) return CombatMove.Cast;
            }
            else if (s.Distance > m.MaxRange) return CombatMove.Fwd;
            if (_random.Next(100) < m.KiteChance)
            {
                if (s.Distance > m.KiteDistance) return CombatMove.Fwd;
                if (s.FreeBack) return CombatMove.Back;
                if (c.AttackReady) return CombatMove.Cast;
            }
            if (!string.Equals(State.Spell, State.LastSpell, StringComparison.Ordinal)) return CombatMove.Cast;
            if (!s.LineOfSight) return CombatMove.Fwd; // [interp: G3 retargets first]
            if (_options.AlternativeBalancing && m.BalancedAlwaysCast.Contains(Self.Name)) return CombatMove.Cast;
            if (_random.Next(100) > m.WaitAbove || !c.AttackReady) return CombatMove.Wait;
            return CombatMove.Cast;
        }

        /// <summary>ZS_Attack_Cast: remembers the spell and decides the power cast (20 %, ice golems always, Power spells only).</summary>
        void BeginCast()
        {
            var m = _ai.Magic;
            var spell = Spells?.Invoke(State.Spell);
            State.LastSpell = State.Spell;
            State.PowerCast = spell != null && spell.CastType == SpellCastType.Power &&
                              (m.AlwaysPowerCast.Contains(Definition.Species) || _random.Next(100) < m.PowerCastChance);
        }

        /// <summary>Below 40 % HP, more than 700 away from a melee target, not a mage or paladin, with a potion.</summary>
        bool WantsToHeal(in CombatSituation s) =>
            s.HasPotion && s.HitPointsPercent < _ai.Timing.HealBelowPercent && s.Distance > _ai.Timing.PotionMinDistance &&
            !s.TargetHoldsRangedOrMagic && Definition.Class != NpcClass.Mage && Definition.Class != NpcClass.Paladin;

        // ------------------------------------------------------------------------------------------- chase, flee, end

        CombatOrder StartWaitForFlee(in CombatSituation s)
        {
            _coordinator.Leave(Self);
            State.Phase = CombatPhase.WaitForFlee;
            State.WaitDistance = s.Distance;
            State.NextWaitCheck = s.Now + _ai.Chase.WaitCheckSeconds;
            StopMove(s.Now);
            string shout = Definition.Humanoid ? (State.Reason == AttackReason.Intruder ? ShoutYesGetLost : ShoutStandStill) : null;
            return Order(CombatIntent.WaitForFlee, CombatMove.Wait, shout);
        }

        /// <summary>ZS_Attack_WaitOnShore: the attacker waits on the shore while the swimming target is within this (cm).</summary>
        public const float WaitOnShoreDistance = 1000f;

        /// <summary>Every 0.5 s: beyond 2000 → give up; the distance stopped growing → resume the fight.</summary>
        CombatOrder TickWaitForFlee(in CombatSituation s)
        {
            if (s.TargetState != VitalState.Alive) return EndFight(s);
            if (s.Now < State.NextWaitCheck) return Order(CombatIntent.WaitForFlee, CombatMove.Wait);
            State.NextWaitCheck = s.Now + _ai.Chase.WaitCheckSeconds;
            if (s.Distance > _ai.Chase.GiveUpDistance) return GiveUp();
            // ZS_Attack_WaitOnShore: a swimming target farther than 10 m ends the attack (CancelAttack).
            if (s.TargetInWater && s.Distance > WaitOnShoreDistance) return GiveUp();
            if (s.Distance <= State.WaitDistance && !s.TargetInWater)
            {
                State.Phase = CombatPhase.Fight;
                State.ChaseSeconds = 0;
                return Order(CombatIntent.Fight, CombatMove.Fwd);
            }
            State.WaitDistance = s.Distance;
            return Order(CombatIntent.WaitForFlee, CombatMove.Wait);
        }

        CombatOrder StartFlee(in CombatSituation s)
        {
            _coordinator.Leave(Self);
            State.Phase = CombatPhase.Flee;
            StopMove(s.Now);
            return Order(CombatIntent.Flee, CombatMove.Fwd, Definition.Humanoid ? ShoutRunAway : null);
        }

        /// <summary>
        /// A target no way leads to (fix-142; <see cref="CombatChaseRules"/>): G3 stands (FindWay fails, the NPC waits in
        /// ZS_Attack_WaitForFlee and attacks again while the target does not get farther). [interp] The NPC gives the chase
        /// up instead: it says the WaitForFlee line, backs off like a flight to the flee point (out of the target's easy
        /// shot), then ends the fight as CancelAttack does (<see cref="GaveUp"/>) and goes back to its routine; for
        /// <see cref="UnreachableMemorySeconds"/> it does not take the fight up again against that target.
        /// </summary>
        CombatOrder StartRetreat(in CombatSituation s)
        {
            _coordinator.Leave(Self);
            State.Phase = CombatPhase.Flee;
            State.ChaseSeconds = 0;
            StopMove(s.Now);
            _retreating = true;
            _unreachableTarget = State.Target;
            _unreachableUntil = s.Now + UnreachableMemorySeconds;
            string shout = Definition.Humanoid ? (State.Reason == AttackReason.Intruder ? ShoutYesGetLost : ShoutStandStill) : null;
            return Order(CombatIntent.Flee, CombatMove.Fwd, shout);
        }

        /// <summary>Backs off again from a target it gave up as unreachable (hit by it from up there).</summary>
        void Retreat(ActorId from, double now)
        {
            _coordinator.Leave(Self);
            _sayAttackReason = false;
            _pendingShout = null;
            State.Phase = CombatPhase.Flee;
            State.Target = from;
            State.Reason = AttackReason.ReactToDamage;
            State.ChaseSeconds = 0;
            State.LastTick = now;
            StopMove(now);
            _retreating = true;
            _unreachableTarget = from;
            _unreachableUntil = now + UnreachableMemorySeconds;
        }

        /// <summary>How long an NPC leaves a target alone it could not reach (real s) [interp].</summary>
        public const float UnreachableMemorySeconds = 30f;

        bool _retreating;
        ActorId _unreachableTarget;
        double _unreachableUntil = double.NegativeInfinity;

        /// <summary>Backing off from a target it cannot reach (fix-142): a flight that ends the fight as a give-up.</summary>
        public bool Retreating => _retreating && State.Phase == CombatPhase.Flee;

        /// <summary>The NPC gave a chase up as unreachable within <see cref="UnreachableMemorySeconds"/> and leaves the target alone.</summary>
        public bool IgnoresUnreachable(ActorId target, double now) => target == _unreachableTarget && now < _unreachableUntil;

        /// <summary>No HP threshold: a flight ends at the flee point or when the pursuer is beyond 2000 (then back to the routine).</summary>
        CombatOrder TickFlee(in CombatSituation s)
        {
            if (_retreating && (s.ReachedFleePoint || s.Distance > _ai.Chase.GiveUpDistance)) return GiveUp();
            if (s.ReachedFleePoint || s.Distance > _ai.Chase.GiveUpDistance || s.TargetState == VitalState.Dead) return Done(null);
            var shout = _pendingShout;
            _pendingShout = null;
            return Order(CombatIntent.Flee, CombatMove.Fwd, shout);
        }

        /// <summary>
        /// The first order of a fight carries StartSayAttackReason's line (a humanoid speaker through the say gate;
        /// <see cref="BattleLines.AttackReasonLine"/>), unless it brings a shout of its own.
        /// </summary>
        CombatOrder WithAttackReason(CombatOrder order, in CombatSituation s)
        {
            if (!_sayAttackReason) return order;
            _sayAttackReason = false;
            if (order.Shout != null || !Definition.Humanoid || State.Phase != CombatPhase.Fight) return order;
            string line = BattleLines.AttackReasonLine(State.Reason, s.UpsetWithTarget, s.TargetHumanoid, s.TargetSpecies,
                                                       s.TargetHoldsMelee || s.TargetFists, _random);
            if (line == null || (Speech != null && !Speech.TrySay(SayAttackReason))) return order;
            return order.Intent == CombatIntent.Fight ? Order(order.Intent, order.Move, line) : order;
        }

        /// <summary>
        /// ZS_Attack_End: the token holder (CombatState Active) says StartSayAttackEnd's line about its target (humanoid
        /// speakers, say gate).
        /// </summary>
        string AttackEndLine(in CombatSituation s, bool holder)
        {
            if (!holder || !Definition.Humanoid || State.Target.IsEmpty) return null;
            string line = BattleLines.AttackEndLine(State.Reason, s.TargetHumanoid, s.TargetSpecies, s.TargetState,
                                                    s.TargetIsPlayer && s.AttitudeToTarget == Attitude.Angry, _random);
            return line != null && (Speech == null || Speech.TrySay(SayAttackEnd)) ? line : null;
        }

        /// <summary>
        /// ZS_Attack_End: the holder finishes a downed, killable target when the reason is to kill; otherwise turn to
        /// another enemy nearby; otherwise "AttackEnd", the holder ransacks a downed humanoid, and everybody holsters.
        /// </summary>
        CombatOrder EndFight(in CombatSituation s)
        {
            bool holder = _coordinator.Holds(Self) || State.Phase == CombatPhase.Finishing;
            bool down = s.TargetState == VitalState.Down;
            if (State.Phase != CombatPhase.Finishing && holder && down && s.TargetKillable &&
                State.Reason.IsToKill(s.HostileZone) && _selector.IsAvailable(CombatMove.FinishingAttack, ContextForFinish(s)))
            {
                State.Phase = CombatPhase.Finishing;
                StartMove(CombatMove.FinishingAttack, s.Now, running: true);
                // ZS_Attack_FinishingAttack: a humanoid shouts "FinishingMove" as it starts.
                return Order(CombatIntent.Fight, CombatMove.FinishingAttack, CombatLines.OfFinisher(false, Definition.Humanoid));
            }
            if (!s.NextEnemy.IsEmpty && s.NextEnemy != State.Target)
            {
                var reason = State.Reason;
                Engage(s.NextEnemy, reason, s.Now);
                return Order(CombatIntent.Fight, CombatMove.Wait);
            }
            _coordinator.Leave(Self);
            string end = AttackEndLine(s, holder);
            if (holder && down && s.TargetHumanoid && MayRansack(s))
            {
                State.Phase = CombatPhase.Ransacking;
                StartMove(CombatMove.Wait, s.Now, running: true);
                return Order(CombatIntent.Ransack, CombatMove.Wait, end);
            }
            return Done(end);
        }

        /// <summary>
        /// G3 WantToRansack: only a humanoid loots a downed humanoid (animals and monsters never do); mages and paladins
        /// loot nobody; friend-type NPCs do not loot the player.
        /// </summary>
        bool MayRansack(in CombatSituation s) =>
            Definition.Humanoid && Definition.Class != NpcClass.Mage && Definition.Class != NpcClass.Paladin &&
            !(s.TargetIsPlayer && Definition.FriendType);

        /// <summary>The target got away: the fight ends without a result.</summary>
        CombatOrder GiveUp()
        {
            var target = State.Target;
            var reason = State.Reason;
            var order = Done(null);
            GaveUp?.Invoke(target, reason);
            return order;
        }

        CombatOrder Done(string shout)
        {
            var target = State.Target;
            var weapon = State.Weapon;
            Disengage();
            return new CombatOrder(CombatIntent.Holster, CombatMove.Wait, target, weapon, shout);
        }

        // ------------------------------------------------------------------------------------------- helpers

        MoveContext Context(in CombatSituation s) => new MoveContext
        {
            Self = Definition,
            Loadout = Loadout,
            Current = State.Move,
            AttackReady = CanHit(s, _combat.MinHitDurationGameSeconds),
            HitRecently = !CanHit(s, _ai.Choice.HitRecentlyGameSeconds),
            Group = _coordinator.FightersOn(State.Target) >= 2 && !s.TargetEngagedWithSelf,
            Situation = s,
        };

        /// <summary>
        /// PSNpc::CanHit(seconds) (0x1000bd00): with AB or AAI, true once <paramref name="seconds"/> game seconds passed since
        /// the end of the last own attack (<see cref="NpcCombatState.LastAttackEnd"/>); always true without them (and in
        /// the arena).
        /// </summary>
        bool CanHit(in CombatSituation s, int seconds) =>
            s.Arena || !(_options.AlternativeBalancing || _options.ExtendedAI) || s.GameNow >= State.LastAttackEnd + seconds;

        /// <summary>The moves whose ZS_Attack_* state calls SetLastHit when it is done (a sprint attack in reach is a power attack).</summary>
        static bool StartsAttackClock(CombatMove move) =>
            move == CombatMove.Cast || move.IsMeleeAttack() && move != CombatMove.FinishingAttack;

        MoveContext ContextForFinish(in CombatSituation s)
        {
            var c = Context(s);
            c.AttackReady = true;
            return c;
        }

        /// <summary>Attacks, the parade and jumps run until the body reports their end; footwork runs for a fixed time.</summary>
        static bool IsBodyTimed(CombatMove move) => move.IsAttack() || move == CombatMove.Parade || move == CombatMove.JumpBack;

        float FootworkSeconds(CombatMove move)
        {
            switch (move)
            {
                case CombatMove.TurnLeft:
                case CombatMove.TurnRight:
                case CombatMove.Left:
                case CombatMove.Right:
                    return _ai.Timing.FootworkSeconds;
                case CombatMove.Back:
                    return _ai.Timing.BackReevaluateSeconds;
                case CombatMove.Wait when State.Weapon == WeaponMode.Magic:
                    return _ai.Timing.FootworkSeconds; // a caster's wait is a pause, not a per-tick roll [interp]
                default:
                    return 0; // Fwd, Move and Wait are decided again every tick
            }
        }

        void StartMove(CombatMove move, double now, bool running)
        {
            State.Move = move;
            State.MoveStart = now;
            State.MoveUntil = now;
            State.MoveRunning = running;
        }

        void StopMove(double now)
        {
            State.MoveRunning = false;
            State.MoveUntil = now;
        }

        CombatOrder Order(CombatIntent intent, CombatMove move, string shout = null) =>
            State.Weapon == WeaponMode.Magic
                ? new CombatOrder(intent, move, State.Target, State.Weapon, shout, State.Spell, State.PowerCast)
                : new CombatOrder(intent, move, State.Target, State.Weapon, shout);
    }
}
