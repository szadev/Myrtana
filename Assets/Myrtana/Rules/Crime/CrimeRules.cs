using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Crime, reputation and enclave numbers (crime.json): guard news levels, bribe and excuse, amnesty, the enclave
    /// alarm. Docs/perception-crime.md §3, §6 and §7.
    /// </summary>
    public sealed class CrimeRules : IDefinition
    {
        public const string DefinitionId = "crimeRules";

        [JsonConstructor]
        public CrimeRules(string id, IReadOnlyList<int> murderNewsThresholds, IReadOnlyList<int> otherNewsThresholds,
                          int bribeBelowLevel, string excusePerk, int excuseRoll, int excuseRollBelow,
                          string masterThiefPerk, Balanced<bool> masterThiefHidesTheft, int amnestyFame,
                          int enclaveFleeRatio, int maxDefenders, TheftRules theft = null,
                          LivestockRules livestock = null)
        {
            Id = id ?? DefinitionId;
            MurderNewsThresholds = murderNewsThresholds ?? Array.Empty<int>();
            OtherNewsThresholds = otherNewsThresholds ?? Array.Empty<int>();
            BribeBelowLevel = bribeBelowLevel;
            ExcusePerk = excusePerk;
            ExcuseRoll = excuseRoll;
            ExcuseRollBelow = excuseRollBelow;
            MasterThiefPerk = masterThiefPerk;
            MasterThiefHidesTheft = masterThiefHidesTheft;
            AmnestyFame = amnestyFame;
            EnclaveFleeRatio = enclaveFleeRatio;
            MaxDefenders = maxDefenders;
            Theft = theft ?? TheftRules.None;
            Livestock = livestock ?? LivestockRules.None;
        }

        public string Id { get; }
        /// <summary>Crime counts at which guards talk about a murder: first, second, known (1/2/3).</summary>
        public IReadOnlyList<int> MurderNewsThresholds { get; }
        /// <summary>The same for every other crime (20/40/60).</summary>
        public IReadOnlyList<int> OtherNewsThresholds { get; }
        /// <summary>A bribe works while the news level is below this (3 = Known).</summary>
        public int BribeBelowLevel { get; }
        public string ExcusePerk { get; }
        /// <summary>The excuse roll is uniform in [0, ExcuseRoll) (130) …</summary>
        public int ExcuseRoll { get; }
        /// <summary>… and must stay below this (118) and at most the player's Theft skill.</summary>
        public int ExcuseRollBelow { get; }
        public string MasterThiefPerk { get; }
        /// <summary>AB: with the master-thief perk a theft is never counted.</summary>
        public Balanced<bool> MasterThiefHidesTheft { get; }
        /// <summary>Enclave fame after an amnesty (the enclave going back to routine): 100.</summary>
        public int AmnestyFame { get; }
        /// <summary>An attacked enclave gives up when total defenders &gt; ratio · active defenders (5).</summary>
        public int EnclaveFleeRatio { get; }
        /// <summary>At most this many defenders run at an enclave's attacker (10).</summary>
        public int MaxDefenders { get; }

        public TheftRules Theft { get; }
        public LivestockRules Livestock { get; }

        string IDefinition.Key => Id;

        /// <summary>Nothing is ever news, no bribes or excuses (a pack without crime rules).</summary>
        public static CrimeRules Neutral() =>
            new CrimeRules(DefinitionId, null, null, 0, null, 1, 0, null, false, 100, 5, 10);
    }
}
