#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using Myrtana.Core;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-142 probes (Docs/dev-scenarios.md): 'chaseway:X' (the combat chase's way: Straight, Path or Unreachable; "-" when
    /// X has no combat view), 'chasestand:X' (the longest X stood still while chasing in this fight, s),
    /// 'chaseunreachable:X' (the brain hears that no way leads into reach), 'retreating:X' (X backs off
    /// from a target it cannot reach), 'ignoresunreachable:X' (X leaves the hero alone after such a give-up).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        static bool IsFix142Probe(string kind) =>
            kind == "chaseway" || kind == "chaseunreachable" || kind == "chasestand" || kind == "retreating" || kind == "ignoresunreachable";

        string Fix142Probe(string kind, ActorId who)
        {
            var s = _session;
            var brain = s.NpcCombat.Get(who);
            switch (kind)
            {
                case "retreating": return (brain != null && brain.Retreating).ToString();
                case "ignoresunreachable": return (brain != null && brain.IgnoresUnreachable(ActorId.Player, s.PlaySeconds)).ToString();
            }
            if (!TryActorPosition(who, out _, out var t) || t == null || !t.TryGetComponent<NpcCombatView>(out var view)) return "-";
            if (kind == "chasestand") return view.ChaseStandMax.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return kind == "chaseway" ? view.ChaseWay.ToString() : view.ChaseUnreachable.ToString();
        }
    }
}
#endif
