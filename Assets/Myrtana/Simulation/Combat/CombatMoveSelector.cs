using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>Everything the melee move choice reads (built by <see cref="NpcCombatBrain"/> from its state and the situation).</summary>
    public struct MoveContext
    {
        public NpcCombatDef Self;
        public CombatLoadout Loadout;
        /// <summary>Self's current move (repeating it is penalised).</summary>
        public CombatMove Current;
        /// <summary>The hit cooldown has elapsed: attacks may start (PSNpc::CanHit(MinHitDuration)).</summary>
        public bool AttackReady;
        /// <summary>Self started an attack within the last 30 game s (!PSNpc::CanHit(30)): attacks score −35.</summary>
        public bool HitRecently;
        public bool Group;
        public CombatSituation Situation;
    }

    /// <summary>
    /// The NPC melee move choice of G3 SelectCombatMove (CP 1.75, Docs/combat.md §6): with extended AI the unified
    /// humanoid pick (situation scores, error value E, ranked list and mistake pool) for humanoid-like species and the
    /// monster tactics for the others; the vanilla pick (skill roll over scored candidates) without extended AI and for
    /// Gargoyles; and the passive keep-distance behaviour of NPCs without the attack token. Pure.
    /// </summary>
    public sealed class CombatMoveSelector
    {
        static readonly CombatMove[] Offensive =
            { CombatMove.PierceAttack, CombatMove.PowerAttack, CombatMove.WhirlAttack, CombatMove.HackAttack, CombatMove.SimpleWhirl, CombatMove.Attack, CombatMove.QuickAttack };
        static readonly CombatMove[] Defensive = { CombatMove.Parade, CombatMove.JumpBack, CombatMove.Left, CombatMove.Right, CombatMove.Back };

        readonly CombatAiRules _ai;
        readonly RuleOptions _options;
        readonly IRandom _random;

        public CombatMoveSelector(CombatAiRules ai, RuleOptions options, IRandom random)
        {
            _ai = ai ?? throw new ArgumentNullException(nameof(ai));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _random = random ?? throw new ArgumentNullException(nameof(random));
        }

        /// <summary>The port's live combat options (the session's <c>CombatService.Port</c>); defaults = CP 1.75.</summary>
        public CombatPortOptions Port { get; set; } = new CombatPortOptions();

        // ------------------------------------------------------------------------------------------- ranges

        /// <summary>Self's reach with an Attack minus the margin: "in own reach".</summary>
        public bool InOwnReach(in MoveContext c) => c.Situation.Distance <= OwnReach(c, FighterAction.Attack) - _ai.Reach.OwnReachMargin;

        public int OwnReach(in MoveContext c, FighterAction action) =>
            _ai.Reach.ReachOf(c.Self.Species, c.Loadout.Melee == WeaponClass.Fist, action, _options.ExtendedAI);

        /// <summary>The target's reach with its current action.</summary>
        public int TargetReach(in MoveContext c) =>
            _ai.Reach.ReachOf(c.Situation.TargetSpecies ?? "Human", c.Situation.TargetFists, c.Situation.TargetAction, _options.ExtendedAI);

        public bool InTargetReach(in MoveContext c) => c.Situation.Distance <= TargetReach(c);

        // ------------------------------------------------------------------------------------------- error value

        /// <summary>The extended-AI error value E: how sloppy the NPC fights (higher = more mistakes).</summary>
        public int ErrorValue(in MoveContext c)
        {
            var r = _ai.Choice;
            int e = r.ErrorBase + r.ErrorByDifficulty.For(_options.Difficulty);
            int level = c.Situation.PlayerLevel;
            if (level < 2) e = Math.Max(e + r.LowPlayerLevelAdd, r.LowPlayerLevelMinimum);
            if (level > 30) e += r.HighPlayerLevelAdd;
            if (level > 70) e += r.VeryHighPlayerLevelAdd;
            if (c.Situation.Arena) e += r.ArenaAdd;
            if (c.Self.Mage) e += r.MageAdd;
            if (c.Situation.InPlayerParty) e += r.PartyAdd;
            return Math.Max(0, e);
        }

        // ------------------------------------------------------------------------------------------- availability

        public bool IsAvailable(CombatMove move, in MoveContext c)
        {
            var a = _ai.Availability;
            string species = c.Self.Species;
            bool fists = c.Loadout.Melee == WeaponClass.Fist;
            if (move.IsMeleeAttack() && !c.AttackReady) return false;
            switch (move)
            {
                case CombatMove.Wait:
                case CombatMove.Fwd:
                case CombatMove.PowerAttack:
                    return true;
                case CombatMove.Attack:
                    return !_options.ExtendedAI || !a.AttackExcluded.Contains(species);
                case CombatMove.QuickAttack:
                    return a.QuickHumanoids.Contains(species) && !fists || a.QuickMonsters.Contains(species);
                case CombatMove.SimpleWhirl:
                    return c.Loadout.DualWield; // G3 availability: dual 1H (EquipWeaponry gives assassins and Zuben two blades)
                case CombatMove.WhirlAttack:
                case CombatMove.HackAttack:
                    return a.WeaponMoveSpecies.Contains(species) && c.Loadout.HeavyMelee;
                case CombatMove.PierceAttack:
                    return a.WeaponMoveSpecies.Contains(species) && c.Loadout.Melee == WeaponClass.OneHanded;
                case CombatMove.FinishingAttack:
                    return a.WeaponMoveSpecies.Contains(species) && (c.Loadout.HeavyMelee || c.Loadout.Melee == WeaponClass.OneHanded);
                case CombatMove.SprintAttack:
                    return true;
                case CombatMove.Parade:
                    return a.ParadeSpecies.Contains(species) && c.Current != CombatMove.Parade && CanParry(c);
                case CombatMove.JumpBack:
                    return !a.JumpBackExcluded.Contains(species) && c.Situation.FreeBack;
                case CombatMove.Back:
                    return c.Situation.FreeBack;
                case CombatMove.Left:
                    return a.StrafeSpecies.Contains(species) && c.Situation.FreeBackLeft;
                case CombatMove.Right:
                    return a.StrafeSpecies.Contains(species) && c.Situation.FreeBackRight;
                case CombatMove.TurnLeft:
                    return a.StrafeSpecies.Contains(species) && !a.TurnExcluded.Contains(species) && c.Situation.FreeBackLeft;
                case CombatMove.TurnRight:
                    return a.StrafeSpecies.Contains(species) && !a.TurnExcluded.Contains(species) && c.Situation.FreeBackRight;
                case CombatMove.Move:
                    return _options.ExtendedAI && !a.MoveExcluded.Contains(species) &&
                           (c.Situation.TargetIsPlayer || c.Situation.TargetHumanoid && c.Situation.TargetHoldsMelee);
                default:
                    return false;
            }
        }

        /// <summary>
        /// CanParadeMoveOf (CP 0x1000d640) against the target's current move: fists only against fists between humanoids;
        /// pierce and hack need shield + 1H; with extended AI a dual-wielder's power attack needs shield + 1H too.
        /// </summary>
        bool CanParry(in MoveContext c)
        {
            // IsInFistMode is false for a Minecrawler: its claws parry weapons and can be parried (fix-81).
            bool fists = CombatResolver.IsInFistMode(c.Loadout.Melee, c.Self.Species);
            bool targetFists = c.Situation.TargetFists && !string.Equals(c.Situation.TargetSpecies, "Minecrawler", StringComparison.Ordinal);
            bool shield = c.Loadout.Shield && c.Loadout.Melee == WeaponClass.OneHanded;
            if (fists || targetFists)
            {
                if (fists && targetFists && c.Self.Humanoid && c.Situation.TargetHumanoid) return true;
                // fix-109 [owner] option (UP 1.04): a shield parries animals and fist fighters (ShieldCreatureParade)
                return Port.ShieldBlocksCreatures && targetFists && !fists &&
                       ShieldCreatureParade.BlocksFistAttack(c.Situation.TargetSpecies, shield, false, null);
            }
            if (Port.ShieldBlocksCreatures && ShieldCreatureParade.NeedsShield(c.Situation.TargetSpecies) && !shield) return false;
            var incoming = c.Situation.TargetAction;
            if (incoming == FighterAction.PierceAttack || incoming == FighterAction.HackAttack ||
                _options.ExtendedAI && incoming == FighterAction.PowerAttack && c.Situation.TargetDualWield)
                return shield;
            return true;
        }

        // ------------------------------------------------------------------------------------------- active (token holder)

        /// <summary>The move of an NPC holding the attack token, in melee.</summary>
        public CombatMove SelectActive(in MoveContext c)
        {
            var s = c.Situation;
            if (!_options.ExtendedAI || _ai.Availability.VanillaSpecies.Contains(c.Self.Species)) return SelectVanillaActive(c);
            if (s.TargetSittingKnockedDown)
            {
                var r = _ai.Choice;
                if (s.Distance < r.SitRetreatBelow) return IsAvailable(CombatMove.JumpBack, c) ? CombatMove.JumpBack : Footwork(CombatMove.Back, c);
                return s.Distance > r.SitApproachAbove ? CombatMove.Fwd : CombatMove.Wait;
            }
            if (_ai.Availability.HumanoidLike.Contains(c.Self.Species)) return SelectUnified(c);
            return SelectMonster(c);
        }

        /// <summary>The move of an NPC without the token: keep 350–450 away, answer only an attack (CP bug under extended AI, see FixPassiveExtendedAi).</summary>
        public CombatMove SelectPassive(in MoveContext c)
        {
            var s = c.Situation;
            if (s.TargetAction.IsAttacking() && (InTargetReach(c) || InOwnReach(c) && s.LineOfSight))
            {
                if (!_options.ExtendedAI) return InTargetReach(c) ? VanillaPick(Defensive, c) : VanillaPick(Offensive, c);
                // CP 1.75: the extended-AI picks never return a move here, so passive NPCs neither parry nor strike back.
                // FixPassiveExtendedAi lets them answer with the unified pick [interp].
                if (_options.FixPassiveExtendedAi && _ai.Availability.HumanoidLike.Contains(c.Self.Species))
                {
                    var answer = SelectUnified(c);
                    if (answer != CombatMove.Wait) return answer;
                }
            }
            var r = _ai.Choice;
            if (s.Distance < r.PassiveBackOffBelow) return Footwork(CombatMove.Back, c);
            if (s.Distance > r.PassiveApproachAbove) return CombatMove.Fwd;
            // FAI_Passive (0x1004ffb0) in 350–450: TurnLeft if available, else TurnRight, else Wait (no roll).
            if (IsAvailable(CombatMove.TurnLeft, c)) return CombatMove.TurnLeft;
            return Footwork(CombatMove.TurnRight, c);
        }

        CombatMove Footwork(CombatMove move, in MoveContext c) => IsAvailable(move, c) ? move : CombatMove.Wait;

        // ------------------------------------------------------------------------------------------- unified humanoid pick

        /// <summary>The fight situation of the extended-AI score table (first match in table order).</summary>
        public FightSituation Classify(in MoveContext c)
        {
            var s = c.Situation;
            bool attacking = s.TargetAction.IsAttacking();
            bool inTargetReach = InTargetReach(c);
            bool threatened = attacking && inTargetReach;
            bool inOwnReach = InOwnReach(c);
            if (!threatened && !inOwnReach) return FightSituation.Approach;
            if (c.Group)
            {
                if (!s.LineOfSight) return FightSituation.GroupNoSight;
                return inOwnReach ? FightSituation.GroupInReach : FightSituation.GroupOutOfReach;
            }
            if (threatened) return FightSituation.DuelThreatened;
            if (attacking)
            {
                if (!s.LineOfSight) return FightSituation.DuelAttackedNoSight;
                if (s.TargetAction.IsLightAttack()) return FightSituation.DuelAttackedLight;
                return FightSituation.DuelAttackedHeavy;
            }
            if (!s.LineOfSight) return FightSituation.DuelNoSight;
            if (s.TargetAction.IsMoving()) return FightSituation.DuelTargetMoving;
            if (s.TargetAction.IsParading()) return FightSituation.DuelTargetParading;
            if (s.TargetAction.IsBusy()) return FightSituation.DuelTargetBusy;
            if (s.TargetAction.IsStaggered()) return FightSituation.DuelTargetStaggered;
            return FightSituation.DuelOther;
        }

        /// <summary>The scored candidates of the unified pick (score &gt; 0 only), in candidate order.</summary>
        public List<KeyValuePair<CombatMove, int>> ScoreCandidates(in MoveContext c, int errorValue)
        {
            var result = new List<KeyValuePair<CombatMove, int>>();
            var table = _ai.Choice.ScoresFor(Classify(c));
            if (table == null) return result;
            var available = new List<CombatMove>();
            foreach (var m in _ai.Choice.Candidates)
                if (IsAvailable(m, c)) available.Add(m);
            bool anyAttack = available.Any(m => m.IsMeleeAttack());
            bool inOwnReach = InOwnReach(c);
            foreach (var move in available)
            {
                if (table.LightAttacksNeedOwnReach && !inOwnReach &&
                    (move == CombatMove.Attack || move == CombatMove.QuickAttack || move == CombatMove.SimpleWhirl)) continue;
                int? score = table.ScoreOf(move, errorValue, c.Self.Mage && c.Group, anyAttack);
                if (score == null) continue;
                int value = score.Value;
                if ((move == CombatMove.TurnLeft || move == CombatMove.TurnRight) && c.Situation.TargetIsPlayer && !c.Situation.PlayerSeesSelf)
                    value += table.TurnPenaltyUnseen;
                // CP (0x100057d0): ONE −35 for "an attack while !CanHit(30)" OR "the current action" (not both).
                if (move.IsMeleeAttack() && c.HitRecently || move == c.Current) value += _ai.Choice.Penalty;
                if (value > 0) result.Add(new KeyValuePair<CombatMove, int>(move, value));
            }
            return result;
        }

        /// <summary>
        /// Extended AI: the moves scoring above E and within the ranked window of the best form a list (weights 3 for the best
        /// score, 1 for the lowest, 2 between); moves scoring in (0, E] are mistakes, mixed in with chance E / 55.
        /// </summary>
        public CombatMove SelectUnified(in MoveContext c)
        {
            int e = ErrorValue(c);
            var scored = ScoreCandidates(c, e);
            var mistakes = scored.Where(p => p.Value <= e).ToList();
            var ranked = scored.Where(p => p.Value > e).OrderByDescending(p => p.Value).ToList();
            if (ranked.Count > 0)
            {
                int top = ranked[0].Value;
                ranked = ranked.Where(p => p.Value >= top - _ai.Choice.RankedWindow).Take(_ai.Choice.ListCap).ToList();
            }
            if (mistakes.Count > 0 && (ranked.Count == 0 || _random.NextFloat() < e / (float)_ai.Choice.MistakeDivisor))
                return mistakes[_random.Next(mistakes.Count)].Key;
            if (ranked.Count > 0) return WeightedPick(ranked);
            if (!c.Situation.LineOfSight) return IsAvailable(CombatMove.Back, c) ? CombatMove.Back : CombatMove.Fwd;
            return CombatMove.Wait;
        }

        CombatMove WeightedPick(List<KeyValuePair<CombatMove, int>> ranked)
        {
            int top = ranked[0].Value, lowest = ranked[ranked.Count - 1].Value;
            var weights = ranked.Select(p => p.Value == top ? 3 : p.Value == lowest ? 1 : 2).ToList();
            int roll = _random.Next(weights.Sum());
            for (int i = 0; i < ranked.Count; i++)
            {
                if (roll < weights[i]) return ranked[i].Key;
                roll -= weights[i];
            }
            return ranked[0].Key;
        }

        // ------------------------------------------------------------------------------------------- monster tactics

        /// <summary>Extended-AI tactics of non-humanoid species: sprint charges, timed dodges, quick bites, circling.</summary>
        public CombatMove SelectMonster(in MoveContext c)
        {
            var m = _ai.Monsters;
            var s = c.Situation;
            bool inOwnReach = InOwnReach(c);
            bool attacking = s.TargetAction.IsAttacking();

            // The player spams attacks: charge (sprint, then a sprint attack, which the rules treat as a power attack).
            if (s.TargetIsPlayer && s.PlayerAttackPressure > m.SprintPressureAbove && !c.Group)
            {
                if (s.Distance > OwnReach(c, FighterAction.PowerAttack))
                {
                    if (IsAvailable(CombatMove.Move, c)) return CombatMove.Move;
                }
                else if (IsAvailable(CombatMove.PowerAttack, c)) return c.Current == CombatMove.Move ? CombatMove.SprintAttack : CombatMove.PowerAttack;
            }

            if (c.Current == CombatMove.Move)
            {
                if (!inOwnReach) return CombatMove.Move;
                if (c.AttackReady)
                {
                    if (s.Distance <= OwnReach(c, FighterAction.QuickAttack) && IsAvailable(CombatMove.QuickAttack, c)) return CombatMove.QuickAttack;
                    if (IsAvailable(CombatMove.Attack, c)) return CombatMove.Attack;
                    return CombatMove.PowerAttack;
                }
                if (attacking && IsAvailable(CombatMove.JumpBack, c)) return CombatMove.JumpBack;
                return Turn(c);
            }

            if (!c.Group && attacking)
            {
                if (inOwnReach && c.AttackReady)
                {
                    foreach (var first in new[] { CombatMove.QuickAttack, CombatMove.Attack, CombatMove.PowerAttack })
                        if (IsAvailable(first, c)) return first;
                }
                int reach = TargetReach(c);
                int length = _ai.Reach.MoveLength(s.TargetSpecies ?? "Human", s.TargetAction);
                switch (s.TargetPhase)
                {
                    case MovePhase.Recover:
                        return IsAvailable(CombatMove.Move, c) ? CombatMove.Move : CombatMove.Fwd;
                    case MovePhase.Hit:
                        if (s.Distance <= reach - length + m.HitPhaseJumpBackMargin) return Footwork(CombatMove.JumpBack, c);
                        return s.Distance > reach - length + m.HitPhaseApproachMargin ? CombatMove.Fwd : CombatMove.Wait;
                    default:
                        if (s.Distance <= reach + m.RaisePhaseJumpBackMargin) return Footwork(CombatMove.JumpBack, c);
                        return s.Distance > reach + m.RaisePhaseApproachMargin ? CombatMove.Fwd : Turn(c);
                }
            }

            if (inOwnReach)
            {
                if (IsAvailable(CombatMove.QuickAttack, c) && Roll(m.QuickChance)) return CombatMove.QuickAttack;
                if (c.Group)
                {
                    if (IsAvailable(CombatMove.PowerAttack, c) && Roll(m.GroupPowerBase - ErrorValue(c))) return CombatMove.PowerAttack;
                    if (IsAvailable(CombatMove.Attack, c)) return CombatMove.Attack;
                    return Footwork(CombatMove.JumpBack, c);
                }
                if (IsAvailable(CombatMove.Attack, c)) return CombatMove.Attack;
                if (IsAvailable(CombatMove.PowerAttack, c) && Roll(m.DuelPowerChance)) return CombatMove.PowerAttack;
                return Turn(c);
            }
            if (c.Group) return CombatMove.Fwd;
            return IsAvailable(CombatMove.Move, c) ? CombatMove.Move : CombatMove.Fwd;
        }

        CombatMove Turn(in MoveContext c) => Footwork(Roll(_ai.Monsters.TurnRightChance) ? CombatMove.TurnRight : CombatMove.TurnLeft, c);

        bool Roll(int percent) => _random.Next(100) < percent;

        // ------------------------------------------------------------------------------------------- vanilla

        CombatMove SelectVanillaActive(in MoveContext c)
        {
            var s = c.Situation;
            if (s.TargetSittingKnockedDown) return CombatMove.Wait;
            if (s.TargetAction.IsAttacking() && InTargetReach(c)) return VanillaPick(Defensive, c);
            if (InOwnReach(c))
            {
                if (!s.LineOfSight) return IsAvailable(CombatMove.Back, c) ? CombatMove.Back : CombatMove.Fwd;
                return VanillaPick(Offensive, c);
            }
            return CombatMove.Fwd;
        }

        /// <summary>The vanilla skill (percent): clamp(LevelMax with the difficulty offset + difficulty adjustment, 0, 100).</summary>
        public int VanillaSkill(NpcCombatDef self)
        {
            var fixedLevel = _ai.Choice.VanillaFixedSkill.FirstOrDefault(v => v.Species == self.Species);
            int level = fixedLevel?.Value ?? CombatStats.NpcLevel(self.LevelMax, _options.Difficulty);
            return Math.Max(0, Math.Min(100, level + _ai.Choice.VanillaSkillAdjust.For(_options.Difficulty)));
        }

        /// <summary>The vanilla score of a candidate (combat_ai.json vanillaPick.score.rules).</summary>
        public int VanillaScore(CombatMove move, in MoveContext c)
        {
            var r = _ai.Choice;
            int level = c.Situation.PlayerLevel / 2;
            int normal = r.VanillaBase + level, situational = r.VanillaSituationalBase + level;
            var target = c.Situation.TargetAction;
            bool selfInParade = c.Current == CombatMove.Parade;
            switch (move)
            {
                case CombatMove.Attack:
                case CombatMove.SimpleWhirl:
                    return normal + (target == FighterAction.None || target == FighterAction.PowerAttack || target == FighterAction.PierceAttack ||
                                     target == FighterAction.HackAttack ? r.VanillaBonus : 0);
                case CombatMove.PowerAttack:
                    return (target == FighterAction.Parade || target == FighterAction.PierceStumble ? situational : normal) +
                           (c.Current == CombatMove.Back || c.Current == CombatMove.Left || c.Current == CombatMove.Right ? r.VanillaBonus : 0);
                case CombatMove.QuickAttack:
                    return target == FighterAction.Attack || target == FighterAction.PierceAttack ? situational : normal;
                case CombatMove.WhirlAttack:
                case CombatMove.PierceAttack:
                case CombatMove.HackAttack:
                case CombatMove.Parade:
                    return normal + (selfInParade ? r.VanillaBonus : 0);
                case CombatMove.Back:
                case CombatMove.Left:
                case CombatMove.Right:
                    return (selfInParade ? normal : situational) +
                           (target == FighterAction.PowerAttack || target == FighterAction.HackAttack ? r.VanillaBonus : 0);
                default:
                    return normal;
            }
        }

        /// <summary>With probability skill % a random best move, else a random valid one; Wait when none is available.</summary>
        CombatMove VanillaPick(CombatMove[] candidates, in MoveContext c)
        {
            var valid = new List<KeyValuePair<CombatMove, int>>();
            foreach (var m in candidates)
            {
                if (!IsAvailable(m, c)) continue;
                int score = VanillaScore(m, c);
                if (score > 0) valid.Add(new KeyValuePair<CombatMove, int>(m, score));
            }
            if (valid.Count == 0) return CombatMove.Wait;
            int best = valid.Max(p => p.Value);
            var pool = Roll(VanillaSkill(c.Self)) ? valid.Where(p => p.Value == best).ToList() : valid;
            return pool[_random.Next(pool.Count)].Key;
        }
    }
}
