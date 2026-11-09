using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>A member of an enclave that could defend it.</summary>
    public readonly struct Defender
    {
        public Defender(ActorId id, float distance, bool active)
        {
            Id = id;
            Distance = distance;
            Active = active;
        }

        public ActorId Id { get; }
        /// <summary>Centimetres to the attacker.</summary>
        public float Distance { get; }
        /// <summary>Not talking, down or dead.</summary>
        public bool Active { get; }
    }

    /// <summary>
    /// G3 NotifyEnclave (crime.json enclaveAlarm; Docs/perception-crime.md §4.4): when an enclave is attacked, up to 10
    /// of its nearest active defenders run at the attacker; the enclave gives up (flees, which means liberation) when
    /// fewer than one in five of its defenders is still standing.
    /// </summary>
    public static class EnclaveAlarm
    {
        /// <summary>The enclave keeps fighting while ratio · active ≥ total.</summary>
        public static bool GivesUp(IReadOnlyCollection<Defender> members, CrimeRules rules)
        {
            int total = members.Count, active = members.Count(m => m.Active);
            return total > 0 && total > rules.EnclaveFleeRatio * active;
        }

        /// <summary>
        /// CP NotifyEnclave: the deaths of Faring's three story orcs (Orc_Scout_05, Orc_Scout_06, Orc_Warrior_12) do not
        /// alarm the town, so their quest fights stay private.
        /// </summary>
        public static bool IgnoresVictim(string enclave, ActorId victim) =>
            enclave == "Faring" && (victim.Name == "Orc_Scout_05" || victim.Name == "Orc_Scout_06" || victim.Name == "Orc_Warrior_12");

        /// <summary>The nearest active defenders, at most <see cref="CrimeRules.MaxDefenders"/>.</summary>
        public static IReadOnlyList<ActorId> Defenders(IEnumerable<Defender> members, CrimeRules rules) =>
            members.Where(m => m.Active).OrderBy(m => m.Distance).ThenBy(m => m.Id.Name, System.StringComparer.Ordinal)
                   .Take(rules.MaxDefenders).Select(m => m.Id).ToList();
    }
}
