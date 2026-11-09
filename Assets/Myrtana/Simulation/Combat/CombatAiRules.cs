using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The tables of the NPC combat AI (content kind "combatAi"; G3 values from <c>G3RulesImporter.CombatAi()</c>,
    /// combat_ai.json): reach, attack token, move scores, chase, timings, healing, weapon choice and the species attitude
    /// stub. Docs/combat.md §6.
    /// </summary>
    public sealed class CombatAiRules : IDefinition
    {
        public const string DefinitionId = "combatAi";

        [JsonConstructor]
        public CombatAiRules(string id, CombatReachRules reach, AttackTokenRules token, MoveChoiceRules choice, MoveAvailabilityRules availability,
                             MonsterTacticsRules monsters, ChaseRules chase, FightTimingRules timing, WeaponChoiceRules weapons,
                             IReadOnlyList<SpeciesAttitudeRow> attitudes, IReadOnlyList<string> humanoidEnemies, IReadOnlyList<string> cannotAttack,
                             MagicAiRules magic = null)
        {
            Id = id ?? DefinitionId;
            Reach = reach ?? throw new ArgumentNullException(nameof(reach));
            Token = token ?? throw new ArgumentNullException(nameof(token));
            Choice = choice ?? throw new ArgumentNullException(nameof(choice));
            Availability = availability ?? throw new ArgumentNullException(nameof(availability));
            Monsters = monsters ?? throw new ArgumentNullException(nameof(monsters));
            Chase = chase ?? throw new ArgumentNullException(nameof(chase));
            Timing = timing ?? throw new ArgumentNullException(nameof(timing));
            Weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
            Attitudes = attitudes ?? Array.Empty<SpeciesAttitudeRow>();
            HumanoidEnemies = humanoidEnemies ?? Array.Empty<string>();
            CannotAttack = cannotAttack ?? Array.Empty<string>();
            Magic = magic ?? MagicAiRules.None();
        }

        public string Id { get; }
        public CombatReachRules Reach { get; }
        public AttackTokenRules Token { get; }
        public MoveChoiceRules Choice { get; }
        public MoveAvailabilityRules Availability { get; }
        public MonsterTacticsRules Monsters { get; }
        public ChaseRules Chase { get; }
        public FightTimingRules Timing { get; }
        public WeaponChoiceRules Weapons { get; }
        /// <summary>Attitude stub: how a non-humanoid species regards others (attitudes.json speciesTable).</summary>
        public IReadOnlyList<SpeciesAttitudeRow> Attitudes { get; }
        /// <summary>Species humans and orcs are hostile to (besides every evil creature).</summary>
        public IReadOnlyList<string> HumanoidEnemies { get; }
        /// <summary>Species that never fight and flee instead (ambient creatures, deer, pigs, cows).</summary>
        public IReadOnlyList<string> CannotAttack { get; }

        /// <summary>The NPC never fights and flees from every attacker (ambient creatures and the <see cref="CannotAttack"/> species).</summary>
        public bool NeverFights(NpcCombatDef npc) => npc != null && (npc.AmbientCreature || CannotAttack.Contains(npc.Species));
        /// <summary>How mages, paladins and monster casters use their spells (Milestone 5).</summary>
        public MagicAiRules Magic { get; }

        string IDefinition.Key => Id;

        /// <summary>Rules for content without combat AI tables: everyone walks up and swings, nobody scores moves.</summary>
        public static CombatAiRules Neutral()
        {
            var none = Array.Empty<string>();
            var zero = new ByDifficulty(0, 0, 0);
            return new CombatAiRules(DefinitionId,
                new CombatReachRules(200, null, 200, 50, null, 120, null, null, 25),
                new AttackTokenRules(1, new ByDifficulty(1, 1, 1), 100, 75),
                new MoveChoiceRules(0, zero, 0, 0, 0, 0, 0, 0, 0, new[] { CombatMove.Attack }, null, 0, 0, 0, 1, 1, 50, 50, 0, zero, null,
                                    350, 450, 500, 600),
                new MoveAvailabilityRules(none, none, none, none, none, none, none, none, none, none, none),
                new MonsterTacticsRules(int.MaxValue, 50, 0, 0, 0, 0, 0, 0, 0),
                new ChaseRules(5, 5, 5, none, null, 5, 5, 2000, 0.5f),
                new FightTimingRules(0.5f, 0.3f, 2f, 0.6f, 0.4f, 0, 0, 0, null),
                new WeaponChoiceRules(700, 1000, none, 2000),
                null, null, null);
        }
    }

    /// <summary>A value per difficulty.</summary>
    public sealed class ByDifficulty
    {
        [JsonConstructor]
        public ByDifficulty(int easy, int medium, int hard)
        {
            Easy = easy;
            Medium = medium;
            Hard = hard;
        }

        public int Easy { get; }
        public int Medium { get; }
        public int Hard { get; }

        public int For(Difficulty difficulty) => difficulty == Difficulty.Easy ? Easy : difficulty == Difficulty.Hard ? Hard : Medium;
    }

    /// <summary>A number for one species (and optionally one action).</summary>
    public sealed class SpeciesValue
    {
        [JsonConstructor]
        public SpeciesValue(string species, int value, FighterAction action = FighterAction.None)
        {
            Species = species;
            Value = value;
            Action = action;
        }

        public string Species { get; }
        public int Value { get; }
        /// <summary>None = every action.</summary>
        public FighterAction Action { get; }
    }

    /// <summary>A number for one action.</summary>
    public sealed class ActionValue
    {
        [JsonConstructor]
        public ActionValue(FighterAction action, int value)
        {
            Action = action;
            Value = value;
        }

        public FighterAction Action { get; }
        public int Value { get; }
    }

    /// <summary>
    /// Reach of a move = attack-range base of the species + move length of the action (combat_ai.json ranges). With extended
    /// AI every species has its own base; humanoids use 200 with a weapon and 50 with fists.
    /// </summary>
    public sealed class CombatReachRules
    {
        [JsonConstructor]
        public CombatReachRules(int defaultBase, IReadOnlyList<SpeciesValue> defaultBySpecies, int weaponBase, int fistsBase,
                                IReadOnlyList<SpeciesValue> extendedBySpecies, int defaultMoveLength, IReadOnlyList<ActionValue> moveLengths,
                                IReadOnlyList<SpeciesValue> speciesMoveLengths, int ownReachMargin)
        {
            DefaultBase = defaultBase;
            DefaultBySpecies = defaultBySpecies ?? Array.Empty<SpeciesValue>();
            WeaponBase = weaponBase;
            FistsBase = fistsBase;
            ExtendedBySpecies = extendedBySpecies ?? Array.Empty<SpeciesValue>();
            DefaultMoveLength = defaultMoveLength;
            MoveLengths = moveLengths ?? Array.Empty<ActionValue>();
            SpeciesMoveLengths = speciesMoveLengths ?? Array.Empty<SpeciesValue>();
            OwnReachMargin = ownReachMargin;
        }

        /// <summary>Without extended AI: 200, Troll/Trex/Dragon 300.</summary>
        public int DefaultBase { get; }
        public IReadOnlyList<SpeciesValue> DefaultBySpecies { get; }
        /// <summary>Extended AI, species not listed: with a weapon / with fists.</summary>
        public int WeaponBase { get; }
        public int FistsBase { get; }
        public IReadOnlyList<SpeciesValue> ExtendedBySpecies { get; }
        public int DefaultMoveLength { get; }
        public IReadOnlyList<ActionValue> MoveLengths { get; }
        public IReadOnlyList<SpeciesValue> SpeciesMoveLengths { get; }
        /// <summary>"In own reach" = distance ≤ reach of an Attack − this margin.</summary>
        public int OwnReachMargin { get; }

        public int RangeBase(string species, bool fists, FighterAction action, bool extendedAI)
        {
            if (!extendedAI) return Find(DefaultBySpecies, species, action) ?? DefaultBase;
            return Find(ExtendedBySpecies, species, action) ?? (fists ? FistsBase : WeaponBase);
        }

        public int MoveLength(string species, FighterAction action) =>
            Find(SpeciesMoveLengths, species, action) ?? MoveLengths.FirstOrDefault(m => m.Action == action)?.Value ?? DefaultMoveLength;

        /// <summary>How far a fighter of this species reaches with this action.</summary>
        public int ReachOf(string species, bool fists, FighterAction action, bool extendedAI) =>
            RangeBase(species, fists, action, extendedAI) + MoveLength(species, action);

        static int? Find(IReadOnlyList<SpeciesValue> values, string species, FighterAction action)
        {
            SpeciesValue any = null;
            foreach (var v in values)
            {
                if (!string.Equals(v.Species, species, StringComparison.Ordinal)) continue;
                if (v.Action == action) return v.Value;
                if (v.Action == FighterAction.None) any = v;
            }
            return any?.Value;
        }
    }

    /// <summary>The attack token (combat_ai.json attackToken): how many NPCs may attack one target at a time.</summary>
    public sealed class AttackTokenRules
    {
        [JsonConstructor]
        public AttackTokenRules(int maxHolders, ByDifficulty maxHoldersVsPlayer, int stealIfFartherBy, int stealMinDistance)
        {
            MaxHolders = maxHolders;
            MaxHoldersVsPlayer = maxHoldersVsPlayer;
            StealIfFartherBy = stealIfFartherBy;
            StealMinDistance = stealMinDistance;
        }

        public int MaxHolders { get; }
        /// <summary>With AB or extended AI, against the player.</summary>
        public ByDifficulty MaxHoldersVsPlayer { get; }
        /// <summary>A holder is displaced by an NPC this much closer to the target…</summary>
        public int StealIfFartherBy { get; }
        /// <summary>…that is at least this far from the holder.</summary>
        public int StealMinDistance { get; }
    }

    /// <summary>The fight situations of the extended-AI score table, in the order they are tested.</summary>
    public enum FightSituation
    {
        /// <summary>Not threatened and not in own reach.</summary>
        Approach,
        GroupNoSight,
        GroupOutOfReach,
        GroupInReach,
        /// <summary>Duel: the target attacks and self is inside its reach.</summary>
        DuelThreatened,
        DuelAttackedNoSight,
        DuelAttackedLight,
        DuelAttackedHeavy,
        DuelNoSight,
        DuelTargetMoving,
        DuelTargetParading,
        DuelTargetBusy,
        DuelTargetStaggered,
        DuelOther,
    }

    /// <summary>A score: a number, plus the error value E when <see cref="PlusError"/>.</summary>
    public sealed class MoveScore
    {
        [JsonConstructor]
        public MoveScore(CombatMove move, int score, bool plusError = false)
        {
            Move = move;
            Score = score;
            PlusError = plusError;
        }

        public CombatMove Move { get; }
        public int Score { get; }
        public bool PlusError { get; }

        public int Value(int errorValue) => Score + (PlusError ? errorValue : 0);
    }

    /// <summary>A score not tied to one move: a number, plus E when <see cref="PlusError"/>.</summary>
    public sealed class ScoreValue
    {
        [JsonConstructor]
        public ScoreValue(int score, bool plusError = false)
        {
            Score = score;
            PlusError = plusError;
        }

        public int Score { get; }
        public bool PlusError { get; }

        public int Value(int errorValue) => Score + (PlusError ? errorValue : 0);
    }

    /// <summary>The scores of one situation (combat_ai.json extendedAI.score.situations).</summary>
    public sealed class SituationScores
    {
        [JsonConstructor]
        public SituationScores(FightSituation situation, IReadOnlyList<MoveScore> scores, ScoreValue otherAttacks = null,
                               IReadOnlyList<MoveScore> mageScores = null, ScoreValue turnWhenNoAttack = null,
                               int turnPenaltyUnseen = 0, bool lightAttacksNeedOwnReach = false)
        {
            Situation = situation;
            Scores = scores ?? Array.Empty<MoveScore>();
            OtherAttacks = otherAttacks;
            MageScores = mageScores;
            TurnWhenNoAttack = turnWhenNoAttack;
            TurnPenaltyUnseen = turnPenaltyUnseen;
            LightAttacksNeedOwnReach = lightAttacksNeedOwnReach;
        }

        public FightSituation Situation { get; }
        public IReadOnlyList<MoveScore> Scores { get; }
        /// <summary>The score of every attack not listed in <see cref="Scores"/> (null = none).</summary>
        public ScoreValue OtherAttacks { get; }
        /// <summary>Replaces <see cref="Scores"/> for a mage in a group fight (null = same).</summary>
        public IReadOnlyList<MoveScore> MageScores { get; }
        /// <summary>The turn score when no attack is available (null = the listed one).</summary>
        public ScoreValue TurnWhenNoAttack { get; }
        /// <summary>Added to turns when the target is the player and does not see self.</summary>
        public int TurnPenaltyUnseen { get; }
        /// <summary>Attack, QuickAttack and SimpleWhirl score only when self is in its own reach.</summary>
        public bool LightAttacksNeedOwnReach { get; }

        public int? ScoreOf(CombatMove move, int errorValue, bool mage, bool anyAttackAvailable)
        {
            var list = mage && MageScores != null ? MageScores : Scores;
            if (TurnWhenNoAttack != null && !anyAttackAvailable && (move == CombatMove.TurnLeft || move == CombatMove.TurnRight))
                return TurnWhenNoAttack.Value(errorValue);
            foreach (var s in list)
                if (s.Move == move) return s.Value(errorValue);
            if (move.IsMeleeAttack() && OtherAttacks != null) return OtherAttacks.Value(errorValue);
            return null;
        }
    }

    /// <summary>The extended-AI move choice (error value, scores, aggregation) and the vanilla pick for Gargoyles and AAI off.</summary>
    public sealed class MoveChoiceRules
    {
        [JsonConstructor]
        public MoveChoiceRules(int errorBase, ByDifficulty errorByDifficulty, int lowPlayerLevelAdd, int lowPlayerLevelMinimum,
                               int highPlayerLevelAdd, int veryHighPlayerLevelAdd, int arenaAdd, int mageAdd, int partyAdd,
                               IReadOnlyList<CombatMove> candidates, IReadOnlyList<SituationScores> situations, int penalty,
                               int hitRecentlyGameSeconds, int rankedWindow, int listCap, int mistakeDivisor,
                               int vanillaBase, int vanillaSituationalBase, int vanillaBonus, ByDifficulty vanillaSkillAdjust,
                               IReadOnlyList<SpeciesValue> vanillaFixedSkill, int passiveBackOffBelow, int passiveApproachAbove,
                               int sitRetreatBelow, int sitApproachAbove)
        {
            ErrorBase = errorBase;
            ErrorByDifficulty = errorByDifficulty;
            LowPlayerLevelAdd = lowPlayerLevelAdd;
            LowPlayerLevelMinimum = lowPlayerLevelMinimum;
            HighPlayerLevelAdd = highPlayerLevelAdd;
            VeryHighPlayerLevelAdd = veryHighPlayerLevelAdd;
            ArenaAdd = arenaAdd;
            MageAdd = mageAdd;
            PartyAdd = partyAdd;
            Candidates = candidates ?? Array.Empty<CombatMove>();
            Situations = situations ?? Array.Empty<SituationScores>();
            Penalty = penalty;
            HitRecentlyGameSeconds = hitRecentlyGameSeconds;
            RankedWindow = rankedWindow;
            ListCap = listCap;
            MistakeDivisor = mistakeDivisor;
            VanillaBase = vanillaBase;
            VanillaSituationalBase = vanillaSituationalBase;
            VanillaBonus = vanillaBonus;
            VanillaSkillAdjust = vanillaSkillAdjust;
            VanillaFixedSkill = vanillaFixedSkill ?? Array.Empty<SpeciesValue>();
            PassiveBackOffBelow = passiveBackOffBelow;
            PassiveApproachAbove = passiveApproachAbove;
            SitRetreatBelow = sitRetreatBelow;
            SitApproachAbove = sitApproachAbove;
        }

        public int ErrorBase { get; }
        public ByDifficulty ErrorByDifficulty { get; }
        /// <summary>Player level below 2: E + this, and at least <see cref="LowPlayerLevelMinimum"/>.</summary>
        public int LowPlayerLevelAdd { get; }
        public int LowPlayerLevelMinimum { get; }
        /// <summary>Player level above 30 / above 70.</summary>
        public int HighPlayerLevelAdd { get; }
        public int VeryHighPlayerLevelAdd { get; }
        public int ArenaAdd { get; }
        public int MageAdd { get; }
        public int PartyAdd { get; }
        /// <summary>The unified humanoid pick's candidate moves.</summary>
        public IReadOnlyList<CombatMove> Candidates { get; }
        public IReadOnlyList<SituationScores> Situations { get; }
        /// <summary>Added to an attack while self was hit recently, and to repeating the current action.</summary>
        public int Penalty { get; }
        /// <summary>"Hit recently" = within this many game seconds (30 = 2.5 real s).</summary>
        public int HitRecentlyGameSeconds { get; }
        /// <summary>Moves within this distance of the best score are ranked…</summary>
        public int RankedWindow { get; }
        /// <summary>…at most this many.</summary>
        public int ListCap { get; }
        /// <summary>A mistake is mixed in with chance E / this.</summary>
        public int MistakeDivisor { get; }
        public int VanillaBase { get; }
        public int VanillaSituationalBase { get; }
        public int VanillaBonus { get; }
        public ByDifficulty VanillaSkillAdjust { get; }
        public IReadOnlyList<SpeciesValue> VanillaFixedSkill { get; }
        /// <summary>A passive NPC (no attack token) backs off below and approaches above this band around the target.</summary>
        public int PassiveBackOffBelow { get; }
        public int PassiveApproachAbove { get; }
        /// <summary>Against a target knocked into sitting: retreat below, wait between, approach above.</summary>
        public int SitRetreatBelow { get; }
        public int SitApproachAbove { get; }

        public SituationScores ScoresFor(FightSituation situation) => Situations.FirstOrDefault(s => s.Situation == situation);
    }

    /// <summary>Which species and weapons can make which move (combat_ai.json moveAvailability).</summary>
    public sealed class MoveAvailabilityRules
    {
        [JsonConstructor]
        public MoveAvailabilityRules(IReadOnlyList<string> humanoidLike, IReadOnlyList<string> vanillaSpecies, IReadOnlyList<string> attackExcluded,
                                     IReadOnlyList<string> quickHumanoids, IReadOnlyList<string> quickMonsters, IReadOnlyList<string> weaponMoveSpecies,
                                     IReadOnlyList<string> paradeSpecies, IReadOnlyList<string> jumpBackExcluded, IReadOnlyList<string> strafeSpecies,
                                     IReadOnlyList<string> turnExcluded, IReadOnlyList<string> moveExcluded)
        {
            HumanoidLike = humanoidLike ?? Array.Empty<string>();
            VanillaSpecies = vanillaSpecies ?? Array.Empty<string>();
            AttackExcluded = attackExcluded ?? Array.Empty<string>();
            QuickHumanoids = quickHumanoids ?? Array.Empty<string>();
            QuickMonsters = quickMonsters ?? Array.Empty<string>();
            WeaponMoveSpecies = weaponMoveSpecies ?? Array.Empty<string>();
            ParadeSpecies = paradeSpecies ?? Array.Empty<string>();
            JumpBackExcluded = jumpBackExcluded ?? Array.Empty<string>();
            StrafeSpecies = strafeSpecies ?? Array.Empty<string>();
            TurnExcluded = turnExcluded ?? Array.Empty<string>();
            MoveExcluded = moveExcluded ?? Array.Empty<string>();
        }

        /// <summary>Species that use the unified humanoid pick under extended AI (others use monster tactics).</summary>
        public IReadOnlyList<string> HumanoidLike { get; }
        /// <summary>Species that keep the vanilla pick under extended AI (Gargoyle).</summary>
        public IReadOnlyList<string> VanillaSpecies { get; }
        /// <summary>Extended AI: species without a plain Attack (lurkers).</summary>
        public IReadOnlyList<string> AttackExcluded { get; }
        /// <summary>QuickAttack: these species unless fighting with fists…</summary>
        public IReadOnlyList<string> QuickHumanoids { get; }
        /// <summary>…and these monsters always.</summary>
        public IReadOnlyList<string> QuickMonsters { get; }
        /// <summary>Whirl, Pierce and Hack need one of these species (plus the weapon).</summary>
        public IReadOnlyList<string> WeaponMoveSpecies { get; }
        public IReadOnlyList<string> ParadeSpecies { get; }
        public IReadOnlyList<string> JumpBackExcluded { get; }
        /// <summary>Species that can strafe (Left/Right) and turn.</summary>
        public IReadOnlyList<string> StrafeSpecies { get; }
        public IReadOnlyList<string> TurnExcluded { get; }
        /// <summary>Extended AI: species without the flank/sprint Move.</summary>
        public IReadOnlyList<string> MoveExcluded { get; }
    }

    /// <summary>Extended-AI tactics of monsters (combat_ai.json extendedAI.monsterTactics), chances in percent.</summary>
    public sealed class MonsterTacticsRules
    {
        [JsonConstructor]
        public MonsterTacticsRules(int sprintPressureAbove, int turnRightChance, int quickChance, int duelPowerChance, int groupPowerBase, int hitPhaseJumpBackMargin, int hitPhaseApproachMargin,
                                   int raisePhaseJumpBackMargin, int raisePhaseApproachMargin)
        {
            SprintPressureAbove = sprintPressureAbove;
            TurnRightChance = turnRightChance;
            QuickChance = quickChance;
            DuelPowerChance = duelPowerChance;
            GroupPowerBase = groupPowerBase;
            HitPhaseJumpBackMargin = hitPhaseJumpBackMargin;
            HitPhaseApproachMargin = hitPhaseApproachMargin;
            RaisePhaseJumpBackMargin = raisePhaseJumpBackMargin;
            RaisePhaseApproachMargin = raisePhaseApproachMargin;
        }

        /// <summary>The player's attack pressure above which a monster charges with a SprintAttack.</summary>
        public int SprintPressureAbove { get; }
        public int TurnRightChance { get; }
        /// <summary>In own reach: QuickAttack with this chance; in a duel otherwise Attack, else PowerAttack with the next chance.</summary>
        public int QuickChance { get; }
        public int DuelPowerChance { get; }
        /// <summary>In a group the power chance is this minus E.</summary>
        public int GroupPowerBase { get; }
        /// <summary>Target in its Hit phase: jump back within reach − move length + this, approach beyond reach − move length + the next.</summary>
        public int HitPhaseJumpBackMargin { get; }
        public int HitPhaseApproachMargin { get; }
        /// <summary>Target raising or aiming: jump back within reach + this, approach beyond reach + the next.</summary>
        public int RaisePhaseJumpBackMargin { get; }
        public int RaisePhaseApproachMargin { get; }
    }

    /// <summary>Chasing a fleeing target and giving up (combat_ai.json chase).</summary>
    public sealed class ChaseRules
    {
        [JsonConstructor]
        public ChaseRules(float defaultSeconds, float extendedHumanoidSeconds, float extendedOtherSeconds, IReadOnlyList<string> extendedHumanoids,
                          string longChaseSpecies, float longChaseSeconds, float longChaseVsSprintingSeconds, float giveUpDistance,
                          float waitCheckSeconds)
        {
            DefaultSeconds = defaultSeconds;
            ExtendedHumanoidSeconds = extendedHumanoidSeconds;
            ExtendedOtherSeconds = extendedOtherSeconds;
            ExtendedHumanoids = extendedHumanoids ?? Array.Empty<string>();
            LongChaseSpecies = longChaseSpecies;
            LongChaseSeconds = longChaseSeconds;
            LongChaseVsSprintingSeconds = longChaseVsSprintingSeconds;
            GiveUpDistance = giveUpDistance;
            WaitCheckSeconds = waitCheckSeconds;
        }

        public float DefaultSeconds { get; }
        public float ExtendedHumanoidSeconds { get; }
        public float ExtendedOtherSeconds { get; }
        public IReadOnlyList<string> ExtendedHumanoids { get; }
        /// <summary>Wolves chase longer (shorter when the player sprints).</summary>
        public string LongChaseSpecies { get; }
        public float LongChaseSeconds { get; }
        public float LongChaseVsSprintingSeconds { get; }
        /// <summary>A target beyond this distance ends the fight (and a flight).</summary>
        public float GiveUpDistance { get; }
        public float WaitCheckSeconds { get; }

        public float Duration(string species, bool extendedAI, bool playerSprinting)
        {
            if (string.Equals(species, LongChaseSpecies, StringComparison.Ordinal))
                return playerSprinting ? LongChaseVsSprintingSeconds : LongChaseSeconds;
            if (!extendedAI) return DefaultSeconds;
            return ExtendedHumanoids.Contains(species) ? ExtendedHumanoidSeconds : ExtendedOtherSeconds;
        }
    }

    /// <summary>NPC parade timing, footwork durations, hit cooldown and healing.</summary>
    public sealed class FightTimingRules
    {
        [JsonConstructor]
        public FightTimingRules(float paradeStanceSeconds, float extendedParadeStanceSeconds, float extendedParadeLeaveSeconds,
                                float footworkSeconds, float backReevaluateSeconds, int healBelowPercent, float potionMinDistance,
                                int healSpellChance, string potionItem)
        {
            ParadeStanceSeconds = paradeStanceSeconds;
            ExtendedParadeStanceSeconds = extendedParadeStanceSeconds;
            ExtendedParadeLeaveSeconds = extendedParadeLeaveSeconds;
            FootworkSeconds = footworkSeconds;
            BackReevaluateSeconds = backReevaluateSeconds;
            HealBelowPercent = healBelowPercent;
            PotionMinDistance = potionMinDistance;
            HealSpellChance = healSpellChance;
            PotionItem = potionItem;
        }

        /// <summary>The parade is up after this time in the parade state (0.5 s; extended AI 0.3 s)…</summary>
        public float ParadeStanceSeconds { get; }
        public float ExtendedParadeStanceSeconds { get; }
        /// <summary>…and dropped after this time (extended AI only).</summary>
        public float ExtendedParadeLeaveSeconds { get; }
        /// <summary>Turning and strafing last this long.</summary>
        public float FootworkSeconds { get; }
        public float BackReevaluateSeconds { get; }
        public int HealBelowPercent { get; }
        /// <summary>A potion is drunk only this far from the target.</summary>
        public float PotionMinDistance { get; }
        public int HealSpellChance { get; }
        public string PotionItem { get; }
    }

    /// <summary>Melee or ranged by distance (combat_ai.json weaponChoice and moveSelection.ranged; magic: <see cref="MagicAiRules"/>).</summary>
    public sealed class WeaponChoiceRules
    {
        [JsonConstructor]
        public WeaponChoiceRules(float meleeBelow, float keepCurrentBelow, IReadOnlyList<string> meleeVsSpecies, float shootRange)
        {
            MeleeBelow = meleeBelow;
            KeepCurrentBelow = keepCurrentBelow;
            MeleeVsSpecies = meleeVsSpecies ?? Array.Empty<string>();
            ShootRange = shootRange;
        }

        public float MeleeBelow { get; }
        public float KeepCurrentBelow { get; }
        /// <summary>Targets fought in melee at any distance (skeletons and golems shrug off arrows).</summary>
        public IReadOnlyList<string> MeleeVsSpecies { get; }
        public float ShootRange { get; }
    }

    /// <summary>One row of the species attitude table: how <see cref="Self"/> species regard others.</summary>
    public sealed class SpeciesAttitudeRow
    {
        [JsonConstructor]
        public SpeciesAttitudeRow(IReadOnlyList<string> self, Attitude attitude, IReadOnlyList<string> toward)
        {
            Self = self ?? Array.Empty<string>();
            Attitude = attitude;
            Toward = toward ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> Self { get; }
        public Attitude Attitude { get; }
        public IReadOnlyList<string> Toward { get; }
    }
}
