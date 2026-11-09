using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3's focus line of sight (fix-104, <see cref="FocusSight"/>: gCFocus_PS::FocusCheck → eCEntity::IsFreeLOS) for the
    /// hero's normal focus (<see cref="PlayerInteraction"/>: characters, items, objects) and his combat focus
    /// (<see cref="PlayerCombat"/>): one ray from the hero's sight point to the candidate's against the world's solid
    /// colliders. The hero, the candidate's own colliders (its view hierarchy, or its mesh in the cell scene), characters,
    /// world items, triggers, for an item the shelf or crate it lies on or in (fix-114), and decorative plant meshes (fix-254: hollow rotten trunks, ferns, reed) do not block; terrain, buildings, floors, closed doors and other objects do.
    /// </summary>
    public sealed class FocusLineOfSight
    {
        /// <summary>A man's box (0.8 × 1.8 × 0.8 m over his feet) when a character has no collider: point 1.35 m up.</summary>
        static readonly Vector3 FallbackBody = new Vector3(0.8f, 1.8f, 0.8f);

        readonly RaycastHit[] _hits = new RaycastHit[32];

        /// <summary>The last ray's blocker (dev probe), or null when it was free.</summary>
        public Collider LastBlocker { get; private set; }

        /// <summary>
        /// Whether nothing solid lies between <paramref name="from"/> and <paramref name="to"/>. <paramref name="self"/>: the
        /// hero's root; <paramref name="target"/>: the candidate's view root (its colliders never block); with
        /// <paramref name="ownMeshPivot"/> a world collider whose mesh sits at that pivot is the candidate's own mesh;
        /// <paramref name="endTolerance"/>: hits this close to <paramref name="to"/> belong to the candidate.
        /// </summary>
        public bool IsFree(Vector3 from, Vector3 to, Transform self, Transform target, Vector3? ownMeshPivot = null,
                           float endTolerance = FocusSight.EndToleranceM) =>
            IsFree(from, to, self, target, ownMeshPivot, endTolerance, null);

        /// <summary>
        /// <see cref="IsFree(Vector3, Vector3, Transform, Transform, Vector3?, float)"/> for a world item whose box is
        /// <paramref name="itemBox"/>: the solid shapes it lies on or in (<see cref="FocusSight.HoldsItem"/>, fix-114) do not block.
        /// </summary>
        public bool IsFreeToItem(Vector3 from, Vector3 to, Transform self, Transform item, Bounds itemBox) =>
            IsFree(from, to, self, item, null, FocusSight.EndToleranceM, itemBox);

        bool IsFree(Vector3 from, Vector3 to, Transform self, Transform target, Vector3? ownMeshPivot, float endTolerance, Bounds? itemBox)
        {
            LastBlocker = null;
            var ray = to - from;
            float length = ray.magnitude;
            if (length < 0.01f) return true;
            int n = Physics.RaycastNonAlloc(from, ray / length, _hits, length, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null) continue;
                var kind = Classify(c, self, target, ownMeshPivot);
                if (kind == FocusSightHit.World && itemBox.HasValue && HoldsItem(c, itemBox.Value)) kind = FocusSightHit.Support;
                if (FocusSight.Blocks(kind, _hits[i].distance, length, endTolerance))
                {
                    LastBlocker = c;
                    return false;
                }
            }
            return true;
        }

        /// <summary>Whether a world collider is the shelf, crate or barrel an item with the box <paramref name="item"/> lies on or in (fix-114).</summary>
        public static bool HoldsItem(Collider c, Bounds item)
        {
            bool solid = !(c is TerrainCollider) && !(c is MeshCollider mc && !mc.convex);
            if (!solid) return false;
            var support = c.bounds;
            if (!support.Intersects(new Bounds(item.center, item.size + Vector3.one * (2 * FocusSight.SupportTouchM)))) return false;
            var centre = item.center;
            var closest = c.ClosestPoint(centre); // primitives and convex meshes: the point itself when inside
            bool contains = (closest - centre).sqrMagnitude < 1e-8f;
            float gap = contains ? 0f : Mathf.Max(0f, Mathf.Sqrt(item.SqrDistance(closest)));
            var s = support.size;
            return FocusSight.HoldsItem(true, contains, gap, Mathf.Max(s.x, Mathf.Max(s.y, s.z)));
        }

        static FocusSightHit Classify(Collider c, Transform self, Transform target, Vector3? ownMeshPivot)
        {
            var t = c.transform;
            if (self != null && t.IsChildOf(self)) return FocusSightHit.Self;
            if (target != null && t.IsChildOf(target)) return FocusSightHit.Target;
            if (c is CharacterController || c.GetComponentInParent<CombatFighter>() != null || c.GetComponentInParent<NpcView>() != null)
                return FocusSightHit.Character;
            if (c.GetComponentInParent<WorldItemView>() != null) return FocusSightHit.Item;
            if (ownMeshPivot.HasValue && IsOwnMesh(c, ownMeshPivot.Value)) return FocusSightHit.TargetGeometry;
            if (IsFoliage(c)) return FocusSightHit.Foliage;
            return FocusSightHit.World;
        }

        static readonly System.Collections.Generic.Dictionary<Collider, bool> _foliage = new System.Collections.Generic.Dictionary<Collider, bool>();

        /// <summary>
        /// A collider of a decorative plant mesh (fix-254, <see cref="FocusSight.IsFoliageMesh"/>): named by its mesh object,
        /// or by the mesh object above G3CollisionBuilder's "G3Collision" child. Cached per collider (names allocate).
        /// </summary>
        static bool IsFoliage(Collider c)
        {
            if (_foliage.TryGetValue(c, out bool f)) return f;
            var t = c.transform;
            if (t.name == CollisionChildName && t.parent != null) t = t.parent;
            f = FocusSight.IsFoliageMesh(t.name);
            if (_foliage.Count > 4096) _foliage.Clear(); // colliders of unloaded cells
            _foliage[c] = f;
            return f;
        }

        /// <summary>G3CollisionBuilder's child object that holds an object's colliders.</summary>
        const string CollisionChildName = "G3Collision";

        /// <summary>
        /// Whether a collider belongs to the object whose view is <paramref name="view"/> and whose entity sits at
        /// <paramref name="pivot"/>: one of the view's own colliders (a chest's box), or a world collider of the object's mesh in the
        /// cell scene (G3CollisionBuilder puts it on the mesh object or its "G3Collision" child, at the entity's pivot). G3
        /// excludes the target entity from the ray; the dev runner's stand-spot search uses the same test (fix-105).
        /// </summary>
        public static bool BelongsTo(Collider c, Transform view, Vector3 pivot) =>
            c != null && (view != null && c.transform.IsChildOf(view) || IsOwnMesh(c, pivot));

        /// <summary>A world collider (not terrain) whose mesh object sits at <paramref name="pivot"/> (<see cref="FocusSight.IsOwnGeometry"/>).</summary>
        public static bool IsOwnMesh(Collider c, Vector3 pivot) => !(c is TerrainCollider) && OwnMesh(c.transform, pivot);

        /// <summary>
        /// The world box of an object of the cell scene: the union of the bounds of its own mesh colliders at
        /// <paramref name="pivot"/> (G3's entity world box, whose sight point <see cref="FocusSight.Point(System.Numerics.Vector3, System.Numerics.Vector3)"/>
        /// the focus ray aims at). False when no such collider is loaded (the mesh has no collision, or its cell is not in).
        /// </summary>
        public static bool TryOwnMeshBox(Vector3 pivot, out Bounds box)
        {
            box = default;
            bool any = false;
            // the mesh need not touch its pivot (a hollow hearth, a board on a post): every collider within reach, kept by its pivot
            int n = Physics.OverlapSphereNonAlloc(pivot, OwnMeshSearchM, _near, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _near[i];
                if (c == null || c is CharacterController || !IsOwnMesh(c, pivot) || c.GetComponentInParent<NpcView>() != null) continue;
                if (!any) box = c.bounds;
                else box.Encapsulate(c.bounds);
                any = true;
            }
            return any;
        }

        /// <summary>The search radius for an object's own mesh colliders around its pivot, metres.</summary>
        const float OwnMeshSearchM = 3f;

        static readonly Collider[] _near = new Collider[256];

        /// <summary>The collider's object or its parent (G3CollisionBuilder's "G3Collision" child) sits at the pivot.</summary>
        static bool OwnMesh(Transform t, Vector3 pivot)
        {
            for (int i = 0; i < 2 && t != null; i++, t = t.parent)
                if (FocusSight.IsOwnGeometry(ToNumerics(t.position), ToNumerics(pivot))) return true;
            return false;
        }

        /// <summary>The sight point of a world box (<see cref="FocusSight.Point(System.Numerics.Vector3, System.Numerics.Vector3)"/>).</summary>
        public static Vector3 Point(Bounds box) => ToUnity(FocusSight.Point(ToNumerics(box.min), ToNumerics(box.max)));

        /// <summary>
        /// A character's sight point: its solid or body collider's box (an enabled collider on its root), else a man's box
        /// over its feet.
        /// </summary>
        public static Vector3 CharacterPoint(Transform character)
        {
            var col = character.GetComponent<Collider>();
            return Point(col != null && col.enabled ? col.bounds : new Bounds(character.position + Vector3.up * (FallbackBody.y * 0.5f), FallbackBody));
        }

        /// <summary>The sight point of a view by the box of the collider on its root (an item's), else its position (G3: no box).</summary>
        public static Vector3 ColliderPoint(Transform view)
        {
            var col = view.GetComponent<Collider>();
            return col != null && col.enabled ? Point(col.bounds) : ToUnity(FocusSight.Point(ToNumerics(view.position)));
        }

        static System.Numerics.Vector3 ToNumerics(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);

        static Vector3 ToUnity(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
