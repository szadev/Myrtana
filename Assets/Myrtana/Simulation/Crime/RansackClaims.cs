using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The victims' "Ransacked" flags (G3 WantToRansack sets <c>victim.Ransacked</c> before the looter goes to the body, and
    /// nobody else may loot it while the flag is set). A knocked-out actor is robbed once: the first winner that
    /// wants to ransack claims the body; every other winner of the same fight leaves it. The flag is cleared when the victim
    /// gets up (G3 ContinueRoutine resets it for NPCs; the hero's flag goes with his getting up [interp]).
    /// </summary>
    public sealed class RansackClaims
    {
        readonly Dictionary<ActorId, ActorId> _byVictim = new Dictionary<ActorId, ActorId>();

        /// <summary>
        /// The winner may ransack the victim: true when nobody claimed the body yet (it is claimed for the winner now) or the
        /// winner already holds the claim.
        /// </summary>
        public bool TryClaim(ActorId victim, ActorId winner)
        {
            if (_byVictim.TryGetValue(victim, out var holder)) return holder == winner;
            _byVictim[victim] = winner;
            return true;
        }

        /// <summary>The victim was ransacked (or claimed) during its current knock-out.</summary>
        public bool IsRansacked(ActorId victim) => _byVictim.ContainsKey(victim);

        /// <summary>The victim got up (or died, or left the world): it can be robbed again after its next knock-out.</summary>
        public void Clear(ActorId victim) => _byVictim.Remove(victim);
    }
}
