using System.Collections.Generic;

namespace G3.Formats.World
{
    /// <summary>
    /// Usable world objects that the game stores as dynamic entities in the .lrentdat files: beds, benches, stools,
    /// thrones, anvils, bookstands, cauldrons, fire altars and so on (eCVisualMeshDynamic_PS + gCInteraction_PS).
    /// They are not in any .node, so the static scene has to add them (Docs/formats-world.md §7.4).
    /// </summary>
    public static class DynamicObjects
    {
        /// <summary>
        /// True for a .lrentdat entity that belongs to the static scene: an enabled, rendered mesh that is neither an
        /// animated NPC, a part of an NPC nor an item (items and NPCs have their own importers). The Innos and Beliar
        /// statues (gCNPC_PS with a static mesh and a dialog, no animation) are static meshes: the hero talks to them.
        /// </summary>
        /// <param name="archive">The whole archive the entity came from (to look up its parent).</param>
        public static bool IsStaticMesh(WorldEntity e, IReadOnlyList<WorldEntity> archive)
        {
            // An animated NPC has eCVisualAnimation_PS and no mesh visual, so Visual == Mesh leaves only the statues.
            if (!e.Enabled || !e.RenderingEnabled || e.Visual != VisualKind.Mesh || e.IsItem) return false;
            return e.Parent < 0 || e.Parent >= archive.Count || !archive[e.Parent].IsNpc;
        }

        /// <summary>
        /// A light of a .lrentdat archive that has no mesh of its own (e.g. G3_Nordmar_01_Ancestor_Lights_01): the
        /// engine renders it like a light of a .node file, so it is placed with the static world. Lights carried by an NPC
        /// and item lights are not (the views make those).
        /// </summary>
        public static bool IsStaticLight(WorldEntity e, IReadOnlyList<WorldEntity> archive)
        {
            if (!e.Enabled || !e.RenderingEnabled || e.Light == null || e.Visual != VisualKind.None || e.IsItem || e.IsNpc) return false;
            return e.Parent < 0 || e.Parent >= archive.Count || !archive[e.Parent].IsNpc;
        }

        /// <summary>The mesh-less lights of one .lrentdat archive (see <see cref="IsStaticLight"/>).</summary>
        public static IEnumerable<WorldEntity> StaticLights(IReadOnlyList<WorldEntity> archive)
        {
            foreach (var e in archive)
                if (IsStaticLight(e, archive)) yield return e;
        }

        /// <summary>The static meshes of one .lrentdat archive (see <see cref="IsStaticMesh"/>).</summary>
        public static IEnumerable<WorldEntity> StaticMeshes(IReadOnlyList<WorldEntity> archive)
        {
            foreach (var e in archive)
                if (IsStaticMesh(e, archive)) yield return e;
        }
    }
}
