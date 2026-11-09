using System;
using System.Collections.Generic;

namespace Myrtana.Simulation
{
    /// <summary>
    /// One town's liberation world switch (CP 1.75 OnEnclaveSectorRefresh; Docs/story-coverage.md §6): once the enclave
    /// fled (was liberated) and the hero is more than <see cref="EnclaveSectorSwitches.Metres"/> away from it, its NPC
    /// sector <see cref="SectorOff"/> goes off, <see cref="SectorOn"/> comes on, the enclave takes
    /// <see cref="Alignment"/> and (with <see cref="Routine"/>) returns to daily life; it counts as switched from then on.
    /// </summary>
    public sealed class EnclaveSectorSwitch
    {
        public EnclaveSectorSwitch(string enclave, string sectorOff, string sectorOn, string alignment, bool routine,
                                   IReadOnlyList<string> freed = null, string freedRoutine = null)
        {
            Enclave = enclave;
            SectorOff = sectorOff;
            SectorOn = sectorOn;
            Alignment = alignment;
            Routine = routine;
            Freed = freed ?? Array.Empty<string>();
            FreedRoutine = freedRoutine;
        }

        /// <summary>The enclave (gCEnclave_PS entity name).</summary>
        public string Enclave { get; }
        /// <summary>The occupiers' NPC sector that goes off; null = none.</summary>
        public string SectorOff { get; }
        /// <summary>The liberators' NPC sector that comes on; null = none (the town stays empty).</summary>
        public string SectorOn { get; }
        /// <summary>
        /// The enclave's new political alignment ("Reb", "Nom", or <see cref="EnclaveStates.NoAlignment"/>); null = unchanged.
        /// </summary>
        public string Alignment { get; }
        /// <summary>ChangeEnclaveStatus(Routine): fame 100, crimes forgiven, members take the enclave's alignment.</summary>
        public bool Routine { get; }
        /// <summary>NPCs (alive) that switch to <see cref="FreedRoutine"/> (Montera's freed slaves, Ben Erai's Lukar).</summary>
        public IReadOnlyList<string> Freed { get; }
        public string FreedRoutine { get; }
    }

    /// <summary>
    /// CP 1.75's liberation table (OnEnclaveSectorRefresh). Ardea (CP quest
    /// staging, 420 m) and Nordmar's big mine (party members sent to BigMine_Success) are special cases handled where the
    /// table is used. Myrtana towns go to the rebels, Varant towns to the nomads; Faring and Ishtar get no alignment;
    /// Nemora, Okara, Reddock and Vangard (rebel towns taken by force) only lose their people.
    /// </summary>
    public static class EnclaveSectorSwitches
    {
        /// <summary>The hero must be farther than this from the enclave (G3: 20000 cm).</summary>
        public const float Metres = 200f;

        const string Reb = "Reb", Nom = "Nom", None = EnclaveStates.NoAlignment;

        static EnclaveSectorSwitch Town(string enclave, string sectors, string alignment) =>
            new EnclaveSectorSwitch(enclave, sectors + "_01", sectors + "_02", alignment, true);

        static EnclaveSectorSwitch Emptied(string enclave, string sector) =>
            new EnclaveSectorSwitch(enclave, sector, null, null, false);

        public static readonly IReadOnlyList<EnclaveSectorSwitch> All = new[]
        {
            Town("Faring", "G3_Myrtana_01_Faring_NPC", None),
            Town("Geldern", "G3_Myrtana_01_Geldern_NPC", Reb),
            Town("KapDun", "G3_Myrtana_01_Kap_Dun_NPC", Reb),
            new EnclaveSectorSwitch("Montera", "G3_Myrtana_01_Montera_NPC_01", "G3_Myrtana_01_Montera_NPC_02", Reb, true,
                                    new[] { "Otis", "Mason", "Daryl" }, "Montera_Free"),
            Town("Silden", "G3_Myrtana_01_Silden_NPC", Reb),
            Town("Trelis", "G3_Myrtana_01_Trelis_NPC", Reb),
            Emptied("Nemora", "G3_Myrtana_01_Nemora_NPC_01"),
            Emptied("Okara", "G3_Myrtana_01_Okara_NPC_01"),
            Emptied("Reddock", "G3_Myrtana_01_Reddock_NPC_01"),
            Emptied("Vangard", "G3_Myrtana_01_Vangard_NPC_01"),
            Town("AlShedim", "G3_Varant_01_AlShedim_NPC", Nom),
            Town("Bakaresh", "G3_Varant_01_Bakaresh_NPC", Nom),
            Town("Braga", "G3_Varant_01_Braga_NPC", Nom),
            Town("MoraSul", "G3_Varant_01_MoraSul_NPC", Nom),
            Town("Ishtar", "G3_Varant_01_Ishtar_NPC", None),
            Town("Enclave_Lago", "G3_Varant_01_Lago_NPC", Nom),
            new EnclaveSectorSwitch("BenErai", "G3_Varant_01_BenErai_NPC_01", "G3_Varant_01_BenErai_NPC_02", Nom, true,
                                    new[] { "Lukar" }, "Free"),
            Town("BenSala", "G3_Varant_01_BenSala_NPC", Nom),
            Town("MoraSul_Temple", "G3_Varant_01_MoraSul_Temple_NSCs", Nom),
        };

        /// <summary>CP: Lukar (Ben Erai) becomes a nomad outlaw: this body and alignment Nom.</summary>
        public const string LukarBody = "Body_Nom_Warrior_Outlaw";

        /// <summary>Nordmar's big mine: once it fled and the hero is 200 m away, these companions go to BigMine_Success.</summary>
        public const string BigMine = "Nrd_BigMine", BigMineRoutine = "BigMine_Success";
        public static readonly IReadOnlyList<string> BigMineCompanions = new[] { "Jorn", "Lisk", "Mort", "Thorald" };

        /// <summary>Every sector the table switches (the content importer makes them switchable).</summary>
        public static IEnumerable<string> Sectors
        {
            get
            {
                foreach (var s in All)
                {
                    if (s.SectorOff != null) yield return s.SectorOff;
                    if (s.SectorOn != null) yield return s.SectorOn;
                }
            }
        }
    }
}
