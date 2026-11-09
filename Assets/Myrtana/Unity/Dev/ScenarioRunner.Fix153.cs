#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using G3.Unity.Characters;
using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-153 (Docs/dev-scenarios.md): an NPC uses an item an info gives it (G3 Use → _AI_UseInventoryItem: Mojok drinks the
    /// schnapps): <c>npcuse:X</c> = its last item use and the steps played (<see cref="G3NpcAnimator.LastItemUse"/>),
    /// <c>npcprop:X</c> = the last item put into its hand and whether it got a mesh, <c>usingitem:X</c> = the use runs.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        string ItemUseProbe(string kind, ActorId id)
        {
            if (id.IsPlayer || !TryActorPosition(id, out _, out var view) || view == null) return "-";
            var animator = view.GetComponentInChildren<G3NpcAnimator>();
            switch (kind)
            {
                case "usingitem": return (animator != null && animator.IsUsingItem).ToString();
                case "npcprop":
                    var props = view.GetComponentInChildren<NpcHandProps>();
                    return props != null && props.LastAsked != null ? props.LastAsked : "-";
                default: return animator != null && animator.LastItemUse != null ? animator.LastItemUse : "-";
            }
        }
    }
}
#endif
