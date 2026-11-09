using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// G3 dialog flags of an NPC (gCDialog_PS *Enabled). Trade and Teach offer the matching topics; Party, Mob and
    /// Slavery let the NPC join the hero as a companion, a mob member or a freed slave (info commands Set*Enabled) and are
    /// read by the party feature. New values go at the end (saves store them in order).
    /// </summary>
    public enum ActorRole { Trade, Teach, Party, Mob, Slavery }

    /// <summary>
    /// Which NPCs currently trade or teach (G3's dialog flags TradeEnabled/TeachEnabled). Starts from the merchant and
    /// teacher definitions; dialog commands switch roles on and off.
    /// </summary>
    public sealed class ActorRoles : IActorStore
    {
        readonly Dictionary<ActorRole, HashSet<ActorId>> _enabled = new Dictionary<ActorRole, HashSet<ActorId>>();

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            foreach (var set in _enabled.Values) set.Remove(actor);
        }

        public bool Holds(ActorId actor) => _enabled.Values.Any(set => set.Contains(actor));

        public ActorRoles(IContentDatabase content)
        {
            foreach (ActorRole role in Enum.GetValues(typeof(ActorRole))) _enabled[role] = new HashSet<ActorId>();
            foreach (var m in content.All<MerchantDef>().Where(m => m.TradesAtStart)) _enabled[ActorRole.Trade].Add(m.Actor);
            foreach (var t in content.All<TeacherDef>().Where(t => t.TeachesAtStart)) _enabled[ActorRole.Teach].Add(t.Actor);
        }

        public bool Has(ActorId actor, ActorRole role) => _enabled[role].Contains(actor);

        public void Set(ActorId actor, ActorRole role, bool enabled)
        {
            if (enabled) _enabled[role].Add(actor);
            else _enabled[role].Remove(actor);
        }

        internal IEnumerable<ActorId> All(ActorRole role) => _enabled[role];

        internal void ClearAll()
        {
            foreach (var set in _enabled.Values) set.Clear();
        }
    }
}
