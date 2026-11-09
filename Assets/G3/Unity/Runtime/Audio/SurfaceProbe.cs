using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Finds the footstep surface under a point: ray down; first G3's own floor material, the collision shape's
    /// eEShapeMaterial, which the collision builder keeps as the collider's shared PhysicsMaterial
    /// "G3Surface_&lt;n&gt;_&lt;Name&gt;" (→ <see cref="FootstepRules.SurfaceFromShapeMaterial"/>, as gCCharacterMovement_PS does); else the hit
    /// renderer's material (the exact sub-mesh when the collider uses the render mesh) classified by name with
    /// <see cref="FootstepRules.SurfaceFromName"/>; the object name is the last guess.
    /// </summary>
    public static class SurfaceProbe
    {
        const float Up = 0.5f, Down = 1.5f;
        static readonly RaycastHit[] s_hits = new RaycastHit[8];

        /// <returns>Surface token, or null if nothing could be classified. <paramref name="source"/> = the name used.</returns>
        public static string Probe(Vector3 feet, Transform ignore, out string source)
        {
            source = null;
            if (!Nearest(feet, ignore, out var hit)) return null;
            return Classify(hit, out source);
        }

        /// <summary>Surface token of what a ray hit (render material, then object names), or null.</summary>
        public static string Classify(RaycastHit hit, out string source)
        {
            var physic = hit.collider.sharedMaterial;
            if (physic != null && TryShapeMaterial(physic.name, out int shape))
            {
                source = physic.name;
                return FootstepRules.SurfaceFromShapeMaterial(shape);
            }
            source = MaterialName(hit);
            var s = FootstepRules.SurfaceFromName(source);
            if (s != null) return s;
            source = hit.collider.name;
            s = FootstepRules.SurfaceFromName(source);
            if (s != null) return s;
            var parent = hit.collider.transform.parent;
            if (parent != null) { source = parent.name; s = FootstepRules.SurfaceFromName(source); }
            return s;
        }

        public const string SurfacePrefix = "G3Surface_";

        /// <summary>Name of the shared PhysicsMaterial that stands for eEShapeMaterial <paramref name="shapeMaterial"/>.</summary>
        public static string SurfaceMaterialName(int shapeMaterial, string engineName) =>
            $"{SurfacePrefix}{shapeMaterial}_{engineName ?? "Unknown"}";

        /// <summary>Reads the eEShapeMaterial back from a "G3Surface_&lt;n&gt;_…" name (also with " (Instance)").</summary>
        public static bool TryShapeMaterial(string physicsMaterialName, out int shapeMaterial)
        {
            shapeMaterial = 0;
            if (physicsMaterialName == null || !physicsMaterialName.StartsWith(SurfacePrefix, System.StringComparison.Ordinal)) return false;
            int start = SurfacePrefix.Length, end = start;
            while (end < physicsMaterialName.Length && char.IsDigit(physicsMaterialName[end])) end++;
            return end > start && int.TryParse(physicsMaterialName.Substring(start, end - start), out shapeMaterial) && shapeMaterial > 0;
        }

        static bool Nearest(Vector3 feet, Transform ignore, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(feet + Vector3.up * Up, Vector3.down, s_hits, Up + Down, ~0, QueryTriggerInteraction.Ignore);
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = s_hits[i];
                if (ignore != null && h.collider.transform.IsChildOf(ignore)) continue;
                if (h.distance < bestD) { bestD = h.distance; best = h; }
            }
            return bestD < float.MaxValue;
        }

        static string MaterialName(RaycastHit hit)
        {
            var col = hit.collider;
            var renderer = col.GetComponent<Renderer>();
            if (renderer == null) renderer = col.GetComponentInParent<Renderer>();
            if (renderer == null) return null;
            var mats = renderer.sharedMaterials;
            if (mats == null || mats.Length == 0) return null;
            int sub = 0;
            if (mats.Length > 1 && col is MeshCollider mc && hit.triangleIndex >= 0)
            {
                var mf = renderer.GetComponent<MeshFilter>();
                // the terrain relief draws a copy (G3TerrainRelief): the hit's triangle index belongs to the collider's (source) mesh
                if (mf != null && mf.sharedMesh != null && G3TerrainRelief.SourceOf(mf.sharedMesh) == mc.sharedMesh) sub = SubMeshOf(mc.sharedMesh, hit.triangleIndex);
            }
            var m = mats[Mathf.Clamp(sub, 0, mats.Length - 1)];
            return m != null ? m.name : null;
        }

        static int SubMeshOf(Mesh mesh, int triangle)
        {
            int index = triangle * 3;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var d = mesh.GetSubMesh(i);
                if (index >= d.indexStart && index < d.indexStart + d.indexCount) return i;
            }
            return 0;
        }
    }
}
