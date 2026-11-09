using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace G3.Formats.World
{
    /// <summary>
    /// Engine-free rules for which G3 collision a world entity gets (shared by G3CollisionBuilder and `G3Cli collision audit`).
    /// eEShapeGroup: 0 Undefined, 1 Static, 2 Dynamic, 3 Shield, 4 MeleeWeapon, 5 Projectile, 6 Movement, 7 WeaponTrigger,
    /// 8 ParadeSphere, 9 Tree_Trunk, 10 Tree_Branches, 11 Camera, 12 Movement_ZoneNPC, 13 HeightRepulsor, 14 Cloth,
    /// 15 PhysicalBodyPart, 16 KeyframedBodyPart, 17 Camera_Obstacle, 18 Projectile_Level, 19 Trigger, 20 Door.
    /// </summary>
    public static class CollisionRules
    {
        /// <summary>A shape the player's movement collides with (static world, dynamic props, trunks, doors).</summary>
        public static bool BlocksPlayer(CollisionShape cs)
        {
            if (cs == null || cs.DisableCollision || cs.IsTrigger) return false;
            switch (cs.Group)
            {
                case 0: case 1: case 2: case 9: case 20: return true;
                default: return false;
            }
        }

        /// <summary>
        /// True when the entity collides with the player in G3: an eCCollisionShape_PS that is either empty (the engine
        /// cooks the visual mesh lazily) or holds at least one <see cref="BlocksPlayer"/> shape.
        /// </summary>
        public static bool HasPlayerCollision(WorldEntity e)
        {
            if (e?.CollisionShapes == null) return false;
            if (e.CollisionShapes.Count == 0) return true;
            foreach (var s in e.CollisionShapes) if (BlocksPlayer(s)) return true;
            return false;
        }

        /// <summary>
        /// A water surface material: drawn with a G3 water shader ("G3/Water", "G3/WaterFall", built from eCShaderRiver /
        /// eCShaderOcean / eCShaderWater) or carrying G3's "_Water_" name token (as MaterialFactory.IsWater). A plain
        /// "water" substring is not enough: "G3_Architecture_Cutwater_01_A" (beams of the fishing houses) and
        /// "G3_Nature_Plant_WaterLily_01_A" are solid materials (fix-83).
        /// </summary>
        public static bool IsWaterMaterial(string materialName, string shaderName)
        {
            if (!string.IsNullOrEmpty(shaderName) && shaderName.StartsWith("G3/Water", StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(materialName) && materialName.IndexOf("_Water_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The render mesh is a water surface (no collider: swimming uses the water zone) only when every drawn material
        /// is water. Meshes that merely contain a water part keep their G3 collision: the fishing house (Cutwater beams),
        /// the interactive water barrel and the fountains (a "_Water_River_" surface inside a solid body) lost their
        /// colliders to the old "any material named *water*" test and the hero walked/fell through them (fix-83).
        /// Null entries (invisible sub-meshes) are ignored.
        /// </summary>
        public static bool IsWaterSurface(IEnumerable<(string material, string shader)> materials)
        {
            if (materials == null) return false;
            int drawn = 0;
            foreach (var (material, shader) in materials)
            {
                if (material == null && shader == null) continue;
                drawn++;
                if (!IsWaterMaterial(material, shader)) return false;
            }
            return drawn > 0;
        }

        static readonly Regex ScaleSuffix = new Regex(@"_SC(X_\d+_\d+_SCY_\d+_\d+_SCZ_\d+_\d+|_\d+_\d+)(\.xnvmsh)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// "&lt;mesh&gt;_SCX_1_2000_SCY_…_SCZ_…" (per-axis) and "&lt;mesh&gt;_SC_1_2960" (uniform) .xnvmsh files are cooked with the
        /// entity's scale already applied: the collider must not be scaled by the entity's transform again.
        /// </summary>
        public static bool IsPrescaled(string xnvmsh) => !string.IsNullOrEmpty(xnvmsh) && ScaleSuffix.IsMatch(xnvmsh);

        /// <summary>
        /// Triangle list for a two-sided triangle-mesh collider: degenerate triangles dropped, every remaining triangle
        /// followed by its reversed copy. G3's collision triangles block the hero from both sides, but their winding is
        /// not consistent (`G3Cli collision winding`: 645 of 4902 cooked meshes show back faces from above — hut and
        /// house walls, roofs, floors); a one-sided Unity MeshCollider lets the CharacterController pass back faces, so
        /// the hero walked through such walls and sank through floors onto the terrain (where RecoverIfSunk's ray, which
        /// ignores back faces, could not lift him).
        /// </summary>
        /// <param name="positions">xyz per vertex.</param>
        /// <param name="indices">Triangle list.</param>
        public static int[] TwoSided(float[] positions, IReadOnlyList<int> indices)
        {
            var o = new List<int>(indices.Count * 2);
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a == b || b == c || a == c) continue;
                float ux = positions[b * 3] - positions[a * 3], uy = positions[b * 3 + 1] - positions[a * 3 + 1], uz = positions[b * 3 + 2] - positions[a * 3 + 2];
                float vx = positions[c * 3] - positions[a * 3], vy = positions[c * 3 + 1] - positions[a * 3 + 1], vz = positions[c * 3 + 2] - positions[a * 3 + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                if (nx * nx + ny * ny + nz * nz < 1e-12f) continue; // zero area (positions in metres: < 1 mm²)
                o.Add(a); o.Add(b); o.Add(c);
                o.Add(a); o.Add(c); o.Add(b);
            }
            return o.ToArray();
        }
    }
}
