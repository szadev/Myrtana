using System.Numerics;

namespace Myrtana.Rules
{
    /// <summary>What a focus ray hit on its way from the hero to a candidate (classified by the Unity side).</summary>
    public enum FocusSightHit
    {
        /// <summary>Level geometry: terrain, buildings, floors, walls, rocks, closed doors, other chests.</summary>
        World,
        /// <summary>The hero himself (his controller and body).</summary>
        Self,
        /// <summary>A collider of the candidate's own view (its body, a chest's box, an item).</summary>
        Target,
        /// <summary>A world collider of the candidate's own mesh (a bed or anvil of the cell scene, <see cref="FocusSight.IsOwnGeometry"/>).</summary>
        TargetGeometry,
        /// <summary>Any character: NPC, creature, ragdoll [interp].</summary>
        Character,
        /// <summary>A world item lying about [interp].</summary>
        Item,
        /// <summary>
        /// The solid shape a world item lies on or in (the shelf, crate, barrel or rock hollow it rests in, fix-114,
        /// <see cref="FocusSight.HoldsItem"/>) [interp].
        /// </summary>
        Support,
        /// <summary>
        /// The collision of a decorative plant mesh: a hollow rotten trunk, ferns, reed, garden plants
        /// (<see cref="FocusSight.IsFoliageMesh"/>, fix-254) [interp].
        /// </summary>
        Foliage,
    }

    /// <summary>
    /// The free line of sight every focus candidate needs in G3 CP 1.75 (fix-104) [Game.dll gCFocus_PS::FocusCheck
    /// 0x2013d1e0 → Engine.dll eCEntity::IsFreeLOS(target, false) 0x10228000]. FocusCheck runs for every focus candidate
    /// (characters to talk to or fight, items, interactive objects; normal and combat modes), after range, angles and
    /// priority. IsFreeLOS traces one ray (eCPhysicsScene::TraceRayFirstHit) from the hero's sight point to the target's,
    /// both entities excluded; any hit means no focus. A sight point is the centre of the entity's world box, raised by a
    /// quarter of its height when the box is lower than 350 cm (so 3/4 up: a man's chest at 1.35 m); without a box its
    /// position (+0.005). The PC_Hero scan data has no vertical limit (all ElevUp/Down ±90), so the ray alone stops the
    /// focus through floors, ceilings and walls. Doors and windows let it through where the geometry has an opening.
    /// [interp] Characters and lying items do not block the ray (G3 traces against its physics scene, whose character
    /// and item shapes we cannot map 1:1); a hit within <see cref="EndToleranceM"/> of the target point counts as the
    /// target (a point sunk into the ground or a mesh).
    /// </summary>
    public static class FocusSight
    {
        /// <summary>Boxes lower than this (metres; G3 350 cm) are looked at 3/4 up, taller ones at their centre.</summary>
        public const float TallBoxM = 3.5f;
        /// <summary>The raise of a low box's point over its centre, in box heights [0.25].</summary>
        public const float LowBoxRaise = 0.25f;
        /// <summary>Without a valid box: the point over the entity's position, metres [0.005 units].</summary>
        public const float NoBoxRaiseM = 0.00005f;
        /// <summary>Hits this close to the end of the ray (metres) belong to the target [interp].</summary>
        public const float EndToleranceM = 0.1f;
        /// <summary>
        /// The same for an object of the cell scene whose view is only a focus point (position + 0.5 m), which may sit inside
        /// the base or table it stands on (metres) [interp].
        /// </summary>
        public const float CellObjectEndToleranceM = 0.35f;
        /// <summary>
        /// A world collider whose mesh pivot lies this close to the candidate's pivot (metres) is the candidate's own mesh: an
        /// interactive object of the cell scene (bed, anvil, signpost) has only a focus point as its view [interp].
        /// </summary>
        public const float OwnGeometryM = 0.25f;

        /// <summary>
        /// A solid shape (box, sphere, capsule, convex hull) no larger than this (metres) that touches a world item's box holds
        /// the item (a shelf, crate, barrel, table) [interp, fix-114].
        /// </summary>
        public const float MaxSupportM = 4f;

        /// <summary>
        /// Whether a solid world shape holds the world item the focus ray aims at, so that it does not block the ray (fix-114)
        /// [interp]. G3's collision shapes of furniture are coarse volumes (a wall shelf is one box, a sack group one convex
        /// hull), and the items the level designers put on them lie inside those volumes: G3 still lets the hero focus and
        /// take them (CP 1.75: the Kap Dun shed's teleport stone in a shelf box, gold and caskets on shelves), so such a
        /// shape cannot be what blocks his line of sight. A shape holds the item when it is a solid volume (not terrain,
        /// not a triangle mesh: those are surfaces the ray only touches next to the item) and it either contains the item's
        /// centre (any size: an item in a rock's hollow inside the rock's convex hull) or touches the item's box
        /// (<paramref name="gapM"/> ≤ 0.02 m) and is no larger than <see cref="MaxSupportM"/> (furniture, not a building).
        /// </summary>
        public static bool HoldsItem(bool solidVolume, bool containsCentre, float gapM, float supportSizeM) =>
            solidVolume && (containsCentre || gapM <= SupportTouchM && supportSizeM <= MaxSupportM);

        /// <summary>
        /// Whether a world mesh is decorative vegetation whose collision does not block the focus ray (fix-254) [interp]: G3's
        /// plant objects (G3_Object_Plant_*: the hollow rotten trunks, ferns, reed, Hercules plant; the garden beds
        /// *_Objects_Plants_*) and cave roots. Their collision meshes are coarse closed shells: the CP's lurker druid stone,
        /// Akor's diary, potions and herbs (Varant coast cave west of Bakaresh, (223, 28.5, -754.5) m) lie inside the hollow
        /// trunk G3_Object_Plant_RottenTree_02, whose 186-triangle collision tube closes the openings its bark mesh shows; CP
        /// players take them, ours could never focus them (owner). Trees (their trunk capsules), stumps, logs and branches
        /// still block.
        /// </summary>
        public static bool IsFoliageMesh(string meshName)
        {
            if (string.IsNullOrEmpty(meshName)) return false;
            return meshName.IndexOf("_Plant_", System.StringComparison.OrdinalIgnoreCase) >= 0 && !meshName.StartsWith("G3_Item_", System.StringComparison.OrdinalIgnoreCase)
                || meshName.IndexOf("_Objects_Plants_", System.StringComparison.OrdinalIgnoreCase) >= 0
                || meshName.IndexOf("_Caves_Roots_", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>A shape this close to an item's box (metres) touches it (<see cref="HoldsItem"/>) [interp].</summary>
        public const float SupportTouchM = 0.02f;

        /// <summary>The sight point of an entity with the world box <paramref name="min"/>..<paramref name="max"/>.</summary>
        public static Vector3 Point(Vector3 min, Vector3 max)
        {
            var c = (min + max) * 0.5f;
            float h = max.Y - min.Y;
            if (h < TallBoxM) c.Y += LowBoxRaise * h;
            return c;
        }

        /// <summary>The sight point of an entity without a box (G3: its position plus 0.005 units).</summary>
        public static Vector3 Point(Vector3 position) => position + new Vector3(0f, NoBoxRaiseM, 0f);

        /// <summary>Whether a hit of this kind <paramref name="distance"/> metres along a ray of <paramref name="length"/> blocks the focus.</summary>
        public static bool Blocks(FocusSightHit hit, float distance, float length, float endTolerance = EndToleranceM) =>
            hit == FocusSightHit.World && distance < length - endTolerance;

        /// <summary>Whether a world collider whose mesh has this pivot is the candidate's own geometry (<see cref="FocusSightHit.TargetGeometry"/>).</summary>
        public static bool IsOwnGeometry(Vector3 meshPivot, Vector3 targetPivot) =>
            Vector3.DistanceSquared(meshPivot, targetPivot) <= OwnGeometryM * OwnGeometryM;
    }
}
