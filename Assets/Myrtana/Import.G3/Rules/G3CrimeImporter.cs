using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The perception, attitude and crime tables of Gothic 3 (Community Patch 1.75) as content. G3 keeps them in
    /// Script_Game.dll; the values follow Docs/gameplay-data/perception.json, attitudes.json and crime.json, plus the
    /// field of view read from Engine.dll (Docs/perception-crime.md). Alignments use the G3 enum names (Orc, Nrd, Reb,
    /// Mid, Ass, Nom, Out, Slave, Pirate).
    /// </summary>
    public static class G3CrimeImporter
    {
        static readonly string[] Alignments = { "Orc", "Nrd", "Reb", "Mid", "Ass", "Nom", "Out", "Slave", "Pirate" };

        /// <summary>attitudes.json politicalMatrix: row = self, column = other (same order as <see cref="Alignments"/>).</summary>
        static readonly string[] Matrix =
        {
            "FHHNFHHNF", // Orc
            "HFNNHNHNF", // Nrd
            "HNFNHNHFF", // Reb
            "NNNFNNNNF", // Mid
            "FHHNFHHNF", // Ass
            "HNNNHFHNF", // Nom
            "HHHHHHFHN", // Out
            "NNNNNNNNN", // Slave
        };

        public static AttitudeRules Attitudes()
        {
            var relations = new List<PoliticalRelation>();
            for (int row = 0; row < Matrix.Length; row++)
                for (int col = 0; col < Alignments.Length; col++)
                    relations.Add(new PoliticalRelation(Alignments[row], Alignments[col], Of(Matrix[row][col])));
            // Pirates: friends among themselves, neutral to outlaws; otherwise they take their enclave's alignment.
            relations.Add(new PoliticalRelation("Pirate", "Pirate", Attitude.Friendly));
            relations.Add(new PoliticalRelation("Pirate", "Out", Attitude.Neutral));
            // Chapter 2: the orc–Hashishin alliance breaks, except toward the temple of Mora Sul and Al Shedim.
            relations.Add(new PoliticalRelation("Orc", "Ass", Attitude.Hostile, 2, new[] { "MoraSulTemple", "AlShedim" }));
            relations.Add(new PoliticalRelation("Ass", "Orc", Attitude.Hostile, 2));

            var minor = new[]
            {
                AttackReason.Livestock, AttackReason.StopThief, AttackReason.Theft, AttackReason.UseInteractObject,
                AttackReason.ReactToWeapon, AttackReason.StopFight, AttackReason.ReactToDamage, AttackReason.GateGuard,
            };
            // Playing time is HH.MM: 0.24 = 24 game minutes, 1.2 = 1 h 20 min.
            var grudges = new GrudgeRules(minor, 24 / 60.0, new[] { AttackReason.Enemy, AttackReason.Murder }, 80 / 60.0,
                                          new[] { AttackReason.Kill }, 80 / 60.0);
            return new AttitudeRules(AttitudeRules.DefinitionId, relations, new[] { "Out" }, "Pirate", 75, grudges);
        }

        public static CrimeRules Crime() => new CrimeRules(
            CrimeRules.DefinitionId,
            murderNewsThresholds: new[] { 1, 2, 3 },
            otherNewsThresholds: new[] { 20, 40, 60 },
            bribeBelowLevel: 3,
            excusePerk: "Perk_Excuse", excuseRoll: 130, excuseRollBelow: 118,
            masterThiefPerk: "Perk_MasterThief", masterThiefHidesTheft: new Balanced<bool>(false, true),
            amnestyFame: 100,
            enclaveFleeRatio: 5, maxDefenders: 10,
            theft: new TheftRules(
                freeNameParts: new[] { "_Teleport_", "Bolt", "Arrow" },
                freeItems: new[] { "It_Bacon", "It_Gancka_Wood" },
                freeUseTypes: new[] { "Plant" },
                workplaceItems: new[] { "It_WeaponPack" },
                restricted: new[] { new RestrictedItem("It_WeaponPack", new[] { "Reb" }) },
                workplaceRange: 1000,
                // G3 game event Tjalf_Room_Plunder = the flag Tjalf's key info sets (dialog SKALVERAM3548, fix-218)
                freedZones: new[] { new FreedZone("Tjalf_Room", "TJALF_ROOM_PLUNDER") }),
            livestock: new LivestockRules(new[] { "Chicken", "Pig", "Cow" }, new[] { new LivestockException("Montera_Cow_01", "Orc") }));

        public static PerceptionRules Perception() => new PerceptionRules(
            PerceptionRules.DefinitionId,
            activeInterval: 1, gateGuardInterval: 0.1,
            fieldOfView: 180, hearingRange: 1000, maxHeightDifference: 1000, sneakVsAnimalsRange: 1000,
            slots: new[]
            {
                new SlotRange(PerceptionSlot.Player, 2000), new SlotRange(PerceptionSlot.Friendly, 700), new SlotRange(PerceptionSlot.Neutral, 700),
                new SlotRange(PerceptionSlot.Angry, 1000), new SlotRange(PerceptionSlot.Hostile, 2000), new SlotRange(PerceptionSlot.Prey, 2000),
                new SlotRange(PerceptionSlot.Panic, 2000), new SlotRange(PerceptionSlot.Body, 1000), new SlotRange(PerceptionSlot.Food, 1000),
            },
            arenaOrAttackerRange: 4000,
            broadcasts: new[]
            {
                new BroadcastRule(BroadcastKind.Warn, 1000, false),
                new BroadcastRule(BroadcastKind.FightSound, 2000, false, revolutionRange: 4000),
                new BroadcastRule(BroadcastKind.Defeat, 2000, true),
                new BroadcastRule(BroadcastKind.Murder, 2000, true),
                new BroadcastRule(BroadcastKind.TakeItem, 1000, true),
                new BroadcastRule(BroadcastKind.UseInteractObject, 1000, true),
                new BroadcastRule(BroadcastKind.CallHelp, 1000, false),
                new BroadcastRule(BroadcastKind.EnterZone, 1000, false),
            },
            weaponWarnings: new WarningTimings(4, 8, 1000),
            zoneWarnings: new WarningTimings(4, 8, 500),
            weaponRange: 700, zoneMaxHeight: 330, sneakRange: 700,
            watchFightMin: 550, watchFightMax: 650,
            wakeRangeIndoor: 1000, wakeRangeOutdoor: 500,
            pursueGiveUpRange: 2000,
            // port (fix-204): creatures need sight beyond 10 m; sound through walls / the ground carries half as far
            creatureSenseRange: PerceptionRules.DefaultCreatureSenseRange,
            occludedHearingFactor: PerceptionRules.DefaultOccludedHearingFactor);

        static Attitude Of(char c) => c == 'F' ? Attitude.Friendly : c == 'H' ? Attitude.Hostile : Attitude.Neutral;
    }
}
