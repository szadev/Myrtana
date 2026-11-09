using System.Collections.Generic;
using System.Linq;
using G3.Formats.Remaster;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// R2 LODs (Docs/remaster-pipeline.md §10): groups a character's renderers into a LODGroup when remaster parts
    /// brought LOD renderers (<c>&lt;part&gt;_LOD&lt;n&gt;</c>, added by <see cref="G3RemasterOverride"/>). Layout and
    /// transitions: <see cref="RemasterLods"/> (parts without LODs, e.g. hair, show in every level). Distance cross-fade
    /// off: an instant switch, so the GPU Resident Drawer keeps taking the renderers (Docs/performance.md, tree LODs).
    /// </summary>
    public static class G3RemasterLods
    {
        /// <summary>Adds the LODGroup; returns the number of levels (0 = no LOD renderers, nothing changed).</summary>
        public static int Setup(G3CharacterImporter imp, GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var plan = RemasterLods.Plan(renderers.Select(r => r.name));
            if (plan.Count < 2) return 0;
            var byName = new Dictionary<string, List<Renderer>>();
            foreach (var r in renderers)
            {
                if (!byName.TryGetValue(r.name, out var list)) byName[r.name] = list = new List<Renderer>();
                list.Add(r);
            }
            var lods = new LOD[plan.Count];
            for (int i = 0; i < plan.Count; i++)
                lods[i] = new LOD(RemasterLods.ScreenHeights[i], plan[i].Distinct().SelectMany(n => byName[n]).ToArray());
            if (!root.TryGetComponent<LODGroup>(out var group)) group = root.AddComponent<LODGroup>(); // ?? misses Unity's fake null
            group.fadeMode = LODFadeMode.None;
            group.animateCrossFading = false;
            group.SetLODs(lods);
            group.RecalculateBounds();
            imp?.Log.Add($"remaster: LODGroup {string.Join(" / ", lods.Select(l => l.renderers.Length + " renderers"))}");
            return plan.Count;
        }
    }
}
