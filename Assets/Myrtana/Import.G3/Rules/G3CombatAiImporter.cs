using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The NPC combat AI tables of Gothic 3 (Community Patch 1.75) as content. G3 keeps them in Script_Game.dll; the values
    /// were re-derived from the binaries and are documented in Docs/gameplay-data/combat_ai.json and attitudes.json
    /// (speciesTable), which this class follows one to one.
    /// </summary>
    public static class G3CombatAiImporter
    {
        static readonly string[] HumanoidCore = { "Human", "Skeleton", "Zombie", "Orc" };

        public static CombatAiRules CombatAi() => new CombatAiRules(
            CombatAiRules.DefinitionId, Reach(),
            new AttackTokenRules(maxHolders: 1, maxHoldersVsPlayer: new ByDifficulty(1, 2, 3), stealIfFartherBy: 100, stealMinDistance: 75),
            Choice(), Availability(),
            new MonsterTacticsRules(sprintPressureAbove: 119, turnRightChance: 51, quickChance: 49, duelPowerChance: 40, groupPowerBase: 50,
                                    hitPhaseJumpBackMargin: 75, hitPhaseApproachMargin: 120, raisePhaseJumpBackMargin: 75, raisePhaseApproachMargin: 150),
            new ChaseRules(defaultSeconds: 5, extendedHumanoidSeconds: 5, extendedOtherSeconds: 9, extendedHumanoids: new[] { "Human", "Orc" },
                           longChaseSpecies: "Wolf", longChaseSeconds: 20, longChaseVsSprintingSeconds: 15, giveUpDistance: 2000, waitCheckSeconds: 0.5f),
            new FightTimingRules(paradeStanceSeconds: 0.5f, extendedParadeStanceSeconds: 0.3f, extendedParadeLeaveSeconds: 2f, footworkSeconds: 0.6f,
                                 backReevaluateSeconds: 0.4f, healBelowPercent: 40, potionMinDistance: 700, healSpellChance: 50,
                                 potionItem: "It_Potion_Health"),
            new WeaponChoiceRules(meleeBelow: 700, keepCurrentBelow: 1000, meleeVsSpecies: new[] { "Skeleton", "Golem", "FireGolem", "IceGolem" },
                                  shootRange: 2000),
            Attitudes(),
            humanoidEnemies: new[]
            {
                "Goblin", "Troll", "Minecrawler", "Wolf", "Boar", "Sabertooth", "Shadowbeast", "Ripper", "Lurker", "Varan", "Snapper",
                "Alligator", "Trex", "FireVaran", "SwampLurker", "Ogre", "Stalker", "Dragon",
            },
            cannotAttack: new[] { "Deer", "Pig", "Cow" },
            magic: Magic());

        /// <summary>combat_ai.json moveSelection.magic, weaponChoice (mage, IceGolem, spells) and castAction.</summary>
        static MagicAiRules Magic() => new MagicAiRules(
            mageMeleePlayerWithin: 250, mageSummonerPlayerWithin: 400, mageMeleeNpcWithin: 400, mageStayMeleeBelow: 470,
            iceGolem: "IceGolem", iceGolemMeleeBelow: 750, iceGolemCastCooldownSeconds: 50,
            tooClose: 350, tooCloseBackChance: 80, maxRange: 2000, kiteChance: 49, kiteDistance: 1175, waitAbove: 80,
            balancedAlwaysCast: new[] { "Xardas" }, powerCastChance: 20, alwaysPowerCast: new[] { "IceGolem" },
            healBelowPercent: 40, healChance: 50, healSpell: "It_Spell_Healing",
            speciesSpells: new[]
            {
                new SpeciesSpell("Goblin", "It_Spell_Fireball"), new SpeciesSpell("FireGolem", "It_Spell_Fireball"),
                new SpeciesSpell("Dragon", "It_Spell_Fireball"), new SpeciesSpell("IceGolem", "It_Spell_Icelance"),
            },
            versusSpecies: new[] { new SpeciesSpell("IceGolem", "It_Spell_Fireball"), new SpeciesSpell("FireGolem", "It_Spell_Icelance") },
            defaultSpells: new[] { "It_Spell_Fireball", "It_Spell_Icelance" },
            summonerBosses: new[]
            {
                new SpeciesSpell("Xardas", "It_Spell_ArmyOfDarkness"), new SpeciesSpell("Tizgar", "It_Spell_SummonDemon"),
                new SpeciesSpell("Grok", "It_Spell_SummonDemon"), new SpeciesSpell("Ningal", "It_Spell_SummonSkeleton"),
                new SpeciesSpell("Runak", "It_Spell_SummonMonster"), new SpeciesSpell("Saturas", "It_Spell_SummonGolem"),
            });

        static CombatReachRules Reach()
        {
            var big = new[] { S("Troll", 300), S("Trex", 300), S("Dragon", 300) };
            var extended = new List<SpeciesValue>
            {
                S("Golem", 110), S("Bloodfly", 110), S("FireGolem", 110), S("IceGolem", 110), S("Scavenger", 115), S("ScorpionKing", 120),
                S("Goblin", 140), S("Bison", 150), S("Rhino", 150), S("Minecrawler", 170), S("Wolf", 170), S("Ripper", 170), S("Boar", 180),
                S("Sabertooth", 200), S("Shadowbeast", 200), S("Varan", 200), S("Snapper", 200), S("Alligator", 200), S("Gargoyle", 200),
                S("Lurker", 150), S("Lurker", 200, FighterAction.QuickAttack), S("SwampLurker", 150), S("SwampLurker", 200, FighterAction.QuickAttack),
                S("Ogre", 220), S("Troll", 300), S("Trex", 300), S("Dragon", 300),
            };
            var lengths = new[]
            {
                A(FighterAction.Attack, 120), A(FighterAction.SimpleWhirl, 120), A(FighterAction.PierceAttack, 120), A(FighterAction.HackAttack, 120),
                A(FighterAction.GetUpAttack, 120), A(FighterAction.PowerAttack, 180), A(FighterAction.SprintAttack, 180), A(FighterAction.WhirlAttack, 180),
                A(FighterAction.QuickAttack, 60), A(FighterAction.QuickAttackRight, 60), A(FighterAction.QuickAttackLeft, 60),
                A(FighterAction.QuickStumble, 100), A(FighterAction.Stumble, 100), A(FighterAction.StumbleRight, 100), A(FighterAction.StumbleLeft, 100),
                A(FighterAction.PierceStumble, 100),
            };
            return new CombatReachRules(defaultBase: 200, defaultBySpecies: big, weaponBase: 200, fistsBase: 50, extendedBySpecies: extended,
                                        defaultMoveLength: 120, moveLengths: lengths,
                                        speciesMoveLengths: new[] { S("Zombie", 150, FighterAction.Attack) }, ownReachMargin: 25);
        }

        static MoveChoiceRules Choice()
        {
            var candidates = new[]
            {
                CombatMove.Fwd, CombatMove.TurnLeft, CombatMove.TurnRight, CombatMove.PierceAttack, CombatMove.PowerAttack, CombatMove.WhirlAttack,
                CombatMove.HackAttack, CombatMove.SimpleWhirl, CombatMove.Attack, CombatMove.QuickAttack, CombatMove.Parade, CombatMove.JumpBack,
                CombatMove.Left, CombatMove.Right, CombatMove.Back,
            };
            const int e = 0; // "E" alone: the error value itself
            var situations = new[]
            {
                new SituationScores(FightSituation.Approach, new[] { M(CombatMove.Fwd, 85) }, mageScores: new[] { M(CombatMove.Back, 85) }),
                new SituationScores(FightSituation.GroupNoSight, Same(65, CombatMove.TurnLeft, CombatMove.TurnRight, CombatMove.Back)),
                new SituationScores(FightSituation.GroupOutOfReach, new MoveScore[0]),
                new SituationScores(FightSituation.GroupInReach,
                    new[] { M(CombatMove.Attack, 65), M(CombatMove.QuickAttack, 55), M(CombatMove.SimpleWhirl, 55), M(CombatMove.WhirlAttack, 45),
                            ME(CombatMove.TurnLeft, 40), ME(CombatMove.TurnRight, 40), ME(CombatMove.Back, e) },
                    otherAttacks: V(45), turnPenaltyUnseen: -10),
                new SituationScores(FightSituation.DuelThreatened,
                    Join(Same(65, CombatMove.Attack, CombatMove.QuickAttack, CombatMove.SimpleWhirl, CombatMove.WhirlAttack, CombatMove.TurnLeft,
                              CombatMove.TurnRight, CombatMove.Parade), ME(CombatMove.Back, 45), ME(CombatMove.Fwd, e)),
                    otherAttacks: VE(e), lightAttacksNeedOwnReach: true),
                new SituationScores(FightSituation.DuelAttackedNoSight,
                    Same(75, CombatMove.TurnLeft, CombatMove.TurnRight, CombatMove.Back, CombatMove.Left, CombatMove.Right)),
                new SituationScores(FightSituation.DuelAttackedLight,
                    Join(Same(75, CombatMove.Attack, CombatMove.QuickAttack, CombatMove.SimpleWhirl, CombatMove.WhirlAttack),
                         ME(CombatMove.TurnLeft, e), ME(CombatMove.TurnRight, e), ME(CombatMove.Back, e)),
                    otherAttacks: VE(45)),
                new SituationScores(FightSituation.DuelAttackedHeavy,
                    Join(Same(75, CombatMove.Attack, CombatMove.PowerAttack, CombatMove.WhirlAttack, CombatMove.PierceAttack, CombatMove.HackAttack),
                         ME(CombatMove.TurnLeft, e), ME(CombatMove.TurnRight, e), ME(CombatMove.Fwd, e)),
                    otherAttacks: VE(45)),
                new SituationScores(FightSituation.DuelNoSight,
                    Same(55, CombatMove.TurnLeft, CombatMove.TurnRight, CombatMove.Back, CombatMove.Left, CombatMove.Right)),
                new SituationScores(FightSituation.DuelTargetMoving,
                    Join(Same(75, CombatMove.Attack, CombatMove.PowerAttack, CombatMove.QuickAttack, CombatMove.WhirlAttack, CombatMove.PierceAttack,
                              CombatMove.HackAttack), ME(CombatMove.TurnLeft, e), ME(CombatMove.TurnRight, e), ME(CombatMove.Back, e)),
                    otherAttacks: VE(65)),
                new SituationScores(FightSituation.DuelTargetParading,
                    Join(Same(75, CombatMove.PierceAttack, CombatMove.HackAttack), ME(CombatMove.TurnLeft, 45), ME(CombatMove.TurnRight, 45),
                         ME(CombatMove.Fwd, e)),
                    otherAttacks: VE(45)),
                new SituationScores(FightSituation.DuelTargetBusy,
                    Join(Same(70, CombatMove.Attack, CombatMove.QuickAttack, CombatMove.SimpleWhirl, CombatMove.WhirlAttack),
                         ME(CombatMove.TurnLeft, e), ME(CombatMove.TurnRight, e), ME(CombatMove.Back, e)),
                    otherAttacks: VE(45)),
                new SituationScores(FightSituation.DuelTargetStaggered,
                    Join(Same(75, CombatMove.Attack, CombatMove.PowerAttack, CombatMove.PierceAttack, CombatMove.HackAttack),
                         ME(CombatMove.TurnLeft, e), ME(CombatMove.TurnRight, e), ME(CombatMove.Fwd, e)),
                    otherAttacks: VE(45)),
                new SituationScores(FightSituation.DuelOther, new[] { ME(CombatMove.TurnLeft, e), ME(CombatMove.TurnRight, e) },
                    otherAttacks: V(65), turnWhenNoAttack: VE(55)),
            };
            return new MoveChoiceRules(
                errorBase: 5, errorByDifficulty: new ByDifficulty(20, 10, 0), lowPlayerLevelAdd: 5, lowPlayerLevelMinimum: 20,
                highPlayerLevelAdd: -5, veryHighPlayerLevelAdd: -5, arenaAdd: -10, mageAdd: -5, partyAdd: -10,
                candidates: candidates, situations: situations, penalty: -35, hitRecentlyGameSeconds: 30, rankedWindow: 35, listCap: 3,
                mistakeDivisor: 55, vanillaBase: 50, vanillaSituationalBase: 75, vanillaBonus: 25, vanillaSkillAdjust: new ByDifficulty(-10, 0, 10),
                vanillaFixedSkill: new[] { S("Zombie", 85) }, passiveBackOffBelow: 350, passiveApproachAbove: 450,
                sitRetreatBelow: 500, sitApproachAbove: 600);
        }

        static MoveAvailabilityRules Availability() => new MoveAvailabilityRules(
            humanoidLike: new[] { "Human", "Skeleton", "Zombie", "Demon", "Orc", "Ogre", "ScorpionKing", "Stalker", "Minecrawler" },
            vanillaSpecies: new[] { "Gargoyle" },
            attackExcluded: new[] { "Lurker", "SwampLurker" },
            quickHumanoids: new[] { "Human", "Skeleton", "Zombie", "Demon", "Orc", "Goblin", "Ogre", "Stalker" },
            quickMonsters: new[] { "Troll", "Minecrawler", "Scavenger", "Sabertooth", "Lurker", "Snapper", "Trex", "SwampLurker", "ScorpionKing" },
            weaponMoveSpecies: HumanoidCore,
            paradeSpecies: new[] { "Human", "Skeleton", "Zombie", "Orc", "Minecrawler" },
            jumpBackExcluded: HumanoidCore,
            strafeSpecies: new[]
            {
                "Human", "Skeleton", "Zombie", "Golem", "Demon", "Orc", "Goblin", "Troll", "Minecrawler", "Varan", "FireVaran", "Bloodfly",
                "Ogre", "FireGolem", "IceGolem", "ScorpionKing", "Stalker",
            },
            turnExcluded: new[] { "Troll", "Golem", "FireGolem", "IceGolem", "Goblin" },
            moveExcluded: new[] { "Human", "Skeleton", "Zombie", "Demon", "Orc", "Minecrawler", "Wolf", "Bloodfly" });

        static SpeciesAttitudeRow[] Attitudes()
        {
            string[] humans = { "Human", "Orc" };
            string[] wolves = { "Wolf", "Sabertooth", "Shadowbeast", "Ripper", "Snapper" };
            string[] lurkers = { "Lurker", "SwampLurker" };
            string[] giants = { "Trex", "Dragon" };
            return new[]
            {
                Row(new[] { "Goblin" }, Attitude.Hostile, humans), Row(new[] { "Goblin" }, Attitude.Friendly, "Troll", "Ogre", "Stalker"),
                Row(new[] { "Goblin" }, Attitude.Angry, lurkers),
                Row(new[] { "Troll", "Ogre", "Stalker" }, Attitude.Hostile, humans), Row(new[] { "Troll", "Ogre", "Stalker" }, Attitude.Friendly, "Goblin"),
                Row(new[] { "Minecrawler", "Boar" }, Attitude.Hostile, humans),
                Row(new[] { "Scavenger", "Rhino", "Varan", "Alligator", "FireVaran", "Bloodfly" }, Attitude.Angry, humans),
                Row(new[] { "Deer" }, Attitude.Panic, humans),
                Row(wolves, Attitude.Angry, humans), Row(wolves, Attitude.Predator, "Scavenger", "Boar"),
                Row(lurkers, Attitude.Hostile, "Human"), Row(lurkers, Attitude.Predator, "Goblin"),
                Row(giants, Attitude.Angry, humans), Row(giants, Attitude.Predator, "Bison", "Rhino"),
            };
        }

        static SpeciesValue S(string species, int value, FighterAction action = FighterAction.None) => new SpeciesValue(species, value, action);
        static ActionValue A(FighterAction action, int value) => new ActionValue(action, value);
        static MoveScore M(CombatMove move, int score) => new MoveScore(move, score);
        static MoveScore ME(CombatMove move, int score) => new MoveScore(move, score, plusError: true);
        static ScoreValue V(int score) => new ScoreValue(score);
        static ScoreValue VE(int score) => new ScoreValue(score, plusError: true);

        static MoveScore[] Same(int score, params CombatMove[] moves)
        {
            var list = new MoveScore[moves.Length];
            for (int i = 0; i < moves.Length; i++) list[i] = M(moves[i], score);
            return list;
        }

        static MoveScore[] Join(MoveScore[] first, params MoveScore[] rest)
        {
            var list = new List<MoveScore>(first);
            list.AddRange(rest);
            return list.ToArray();
        }

        static SpeciesAttitudeRow Row(string[] self, Attitude attitude, params string[] toward) => new SpeciesAttitudeRow(self, attitude, toward);
    }
}
