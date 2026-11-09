#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-149 probes (Docs/dev-scenarios.md): 'carried:X' (how many weapons X's body shows on its slots, CarriedWeaponsView;
    /// "-" when X has no view), 'carriedlist:X' (those props, "key@slot …"), 'droppedby:X' (world items X dropped that still
    /// lie in the world: the hand weapon and the left-hand shield of G3 DropHandItems), 'lootgear:X' (weaponry stacks X's loot
    /// offers: neither taken nor lying).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        static bool IsFix149Probe(string kind) => kind == "carried" || kind == "carriedlist" || kind == "droppedby" || kind == "lootgear";

        string Fix149Probe(string kind, ActorId who)
        {
            var s = _session;
            if (kind == "lootgear") return s.Items.NpcLoot.Pool(who).Count(l => l.Gear).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (kind == "droppedby") return s.Items.World.DroppedBy(who).Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryActorPosition(who, out _, out var t) || t == null) return "-";
            if (!t.TryGetComponent<CarriedWeaponsView>(out var carried)) return kind == "carried" ? "0" : "";
            string list = carried.Describe();
            if (kind == "carriedlist") return list;
            return (list.Length == 0 ? 0 : list.Split(' ').Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
#endif
