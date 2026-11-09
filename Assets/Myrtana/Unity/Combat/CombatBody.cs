using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The body of a fighter: a trigger capsule (every world query of the game ignores triggers) fitted to the skeleton
    /// (<see cref="CombatBodyShape"/>) that arrows, spells, the camera and spacing use, and the fighter it belongs to. Melee
    /// weapons touch its separate, much taller <see cref="CombatWeaponTrigger"/> (G3 shape group 7, <see cref="MeleeHitGeometry"/>),
    /// a sibling object so the queries that look for bodies never see it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatBody : MonoBehaviour
    {
        CapsuleCollider _capsule;
        CombatWeaponTrigger _trigger;
        Transform _transform;

        /// <summary>The body's position (its transform, cached: walkers test every live body against their step, perf-3).</summary>
        public Vector3 Position => (_transform ??= transform).position;

        /// <summary>
        /// How far (m, horizontally) <see cref="Center"/> plus <see cref="WalkBlock"/> may reach from <see cref="Position"/> at most:
        /// a walker skips bodies farther than its reach plus this before it reads them closely (perf-3; the largest G3 creature
        /// bodies stay well inside).
        /// </summary>
        public const float MaxReachFromPosition = 8f;

        public CombatFighter Fighter { get; private set; }

        /// <summary>Every enabled body (fix-93: NPC walks look for characters in their way, <see cref="NpcWalker"/>).</summary>
        public static readonly System.Collections.Generic.List<CombatBody> Live = new System.Collections.Generic.List<CombatBody>(64);

        /// <summary>Counts the changes of <see cref="Live"/> (mt-1: the walkers' evasion grid is rebuilt with it).</summary>
        public static int LiveVersion { get; private set; }

        /// <summary>Ground speed over the last frame, m/s (G3 CorrectDCCPositions: a body moves above 10 cm/s).</summary>
        public float PlanarSpeed { get; private set; }

        Vector3 _lastPosition;
        bool _hasLast;
        static int _serials;

        /// <summary>A number unique to this body for the session (NpcEvadeRules: which body an NPC goes round).</summary>
        public int Serial { get; } = ++_serials;

        /// <summary>Every body measures its speed in one batched LateUpdate (perf-3, <see cref="G3.Unity.G3FrameBatch"/>; order 0 as before).</summary>
        static readonly G3.Unity.G3FrameBatch.Batch<CombatBody> s_lateBatch =
            new G3.Unity.G3FrameBatch.Batch<CombatBody>(G3.Unity.G3FrameBatch.Slot.LateUpdate0, "CombatBody", b => b.FrameLateUpdate());

        void OnEnable()
        {
            if (!Live.Contains(this)) Live.Add(this);
            LiveVersion++;
            _hasLast = false;
            s_lateBatch.Add(this);
            Snapshot.Add(_transform ??= transform, _snapshotSlot);
        }

        void OnDisable()
        {
            Live.Remove(this);
            LiveVersion++;
            PlanarSpeed = 0f;
            s_lateBatch.Remove(this);
            Snapshot.Remove(_snapshotSlot);
        }

        // ------------------------------------------------------------------ mt-1: body snapshot (Docs/multithreading-plan.md C)

        /// <summary>
        /// Every enabled body's transform, read in one Burst job where the speed batch runs (LateUpdate order 0, after every
        /// body moved; -g3mt bit 1) instead of one engine call per body. The positions stay until the next LateUpdate: the
        /// walkers' far cull of the next frame starts from them (<see cref="TryLatePosition"/>).
        /// </summary>
        public static readonly G3.Unity.G3TransformSnapshot Snapshot = new G3.Unity.G3TransformSnapshot("CombatBodies");

        readonly G3.Unity.G3TransformSnapshot.Slot _snapshotSlot = new G3.Unity.G3TransformSnapshot.Slot();
        static bool s_snapshotRun;

        static CombatBody()
        {
            s_lateBatch.BeforeRun = () =>
            {
                s_snapshotRun = G3.Unity.G3Mt.On(G3.Unity.MtBits.BodySnapshot);
                if (s_snapshotRun) Snapshot.Read(false);
            };
            s_lateBatch.AfterRun = () => s_snapshotRun = false;
        }

        /// <summary>
        /// The body's position at the last LateUpdate snapshot and the time of that read (false: no snapshot holds it: bit 1
        /// off, or the body joined since).
        /// </summary>
        public bool TryLatePosition(out Vector3 position, out double readTime)
        {
            readTime = Snapshot.ReadTime;
            if (Snapshot.Has(_snapshotSlot))
            {
                position = Snapshot.Position(_snapshotSlot);
                return true;
            }
            position = default;
            return false;
        }

        void FrameLateUpdate()
        {
            Vector3 p;
            if (s_snapshotRun && Snapshot.HasThisFrame(_snapshotSlot))
            {
                p = Snapshot.Position(_snapshotSlot);
                if (G3.Unity.G3Mt.Verifying) G3.Unity.G3Mt.Check(G3.Unity.G3Mt.Same(p, Position), "body", $"{name} body position {p:R}/{Position:R}");
            }
            else p = Position;
            float dt = G3.Unity.G3FrameBatch.DeltaTime;
            if (_hasLast && dt > 1e-5f) PlanarSpeed = new Vector2(p.x - _lastPosition.x, p.z - _lastPosition.z).magnitude / dt;
            _lastPosition = p;
            _hasLast = true;
        }
        public float Radius => _capsule != null ? _capsule.radius * transform.lossyScale.x : 0.4f;
        /// <summary>Capsule length along its axis (the body length of a long animal; the height of an upright body).</summary>
        public float Height => _capsule != null ? _capsule.height * transform.lossyScale.y : 1.8f;
        /// <summary>Top of the body above the feet, m (long animals: the back, not the body length).</summary>
        public float Top { get; private set; } = 1.8f;
        /// <summary>How close another body's centre may come, m (half the length of a long animal, else the radius).</summary>
        public float Footprint => _capsule != null && _capsule.direction == 2 ? Height * 0.35f : Radius;
        /// <summary>Half the body's length along the ground (a long animal's capsule), else its radius, m (fix-108 floor reach).</summary>
        public float HalfLength => _capsule != null && _capsule.direction == 2 ? Height * 0.5f : Radius;
        /// <summary>
        /// How close the hero's walk may come to this body's centre beyond his own radius, m (fix-86, G3.Player.BodyBlockRules:
        /// G3 characters collide as physics character controllers); 0 = he walks over it: a lying (knocked down or out) or
        /// dead body, as the combat moves step over lying bodies (<see cref="CombatFighter"/> Clear). The hero's own body
        /// blocks NPC walks (NpcWalker) by his character controller's radius (0.35 m), not his hit capsule's (0.24 m): the
        /// one radius his own walk uses against them (fix-105, r41 fix93: Hamlar came to 0.51 m of a 0.62 m contact).
        /// </summary>
        public float WalkBlock
        {
            get
            {
                if (_capsule == null || !isActiveAndEnabled || Fighter == null) return 0f;
                var a = Fighter.Animator;
                if (a != null && (a.IsDown || a.IsDead)) return 0f;
                if (_controllerOf != Fighter && !Fighter.Actor.IsEmpty)
                {
                    _controllerOf = Fighter;
                    _controller = Fighter.Actor.IsPlayer ? Fighter.GetComponentInParent<CharacterController>() : null;
                }
                return _controller != null ? Mathf.Max(Footprint, _controller.radius) : Footprint;
            }
        }

        CombatFighter _controllerOf;
        CharacterController _controller;
        /// <summary>The shape melee weapons touch (fighter frame).</summary>
        public WeaponTrigger Trigger { get; private set; }
        /// <summary>The collider object of <see cref="Trigger"/>.</summary>
        public CombatWeaponTrigger WeaponTrigger => _trigger;
        /// <summary>Centre of the body (chest height).</summary>
        public Vector3 Center => _capsule != null ? transform.TransformPoint(_capsule.center) : transform.position + Vector3.up;

        /// <summary>
        /// The capsule in world space: its axis end points (inner sphere centres) and radius (fix-68: the projectile path
        /// test, <see cref="ProjectilePath"/>). False before the body is fitted.
        /// </summary>
        public bool WorldCapsule(out Vector3 a, out Vector3 b, out float radius)
        {
            a = b = Vector3.zero;
            radius = 0f;
            if (_capsule == null || !isActiveAndEnabled) return false;
            var t = transform;
            var axis = _capsule.direction == 0 ? t.right : _capsule.direction == 1 ? t.up : t.forward;
            float scale = _capsule.direction == 0 ? t.lossyScale.x : _capsule.direction == 1 ? t.lossyScale.y : t.lossyScale.z;
            radius = _capsule.radius * Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.z));
            float half = Mathf.Max(0f, _capsule.height * Mathf.Abs(scale) * 0.5f - radius);
            var center = t.TransformPoint(_capsule.center);
            a = center - axis * half;
            b = center + axis * half;
            return true;
        }

        /// <summary>Adds the body to <paramref name="fighter"/>'s view, sized to <paramref name="visual"/>'s renderers (feet at the root).</summary>
        public static CombatBody Create(CombatFighter fighter, Transform visual)
        {
            var go = new GameObject("Combat body");
            go.transform.SetParent(fighter.transform, false);
            var body = go.AddComponent<CombatBody>();
            body.Fighter = fighter;
            body._capsule = go.AddComponent<CapsuleCollider>();
            body._capsule.isTrigger = true;
            body.Fit(visual);
            return body;
        }

        /// <summary>Fits the capsule to another body (the hero turned into an animal and back; Docs/combat.md §12.5).</summary>
        public void Refit(Transform visual) => Fit(visual);

        void Fit(Transform visual)
        {
            // The skeleton's joints in the body's frame (renderer bounds of skinned meshes are loose culling boxes).
            var bounds = new Bounds(new Vector3(0, 0.9f, 0), new Vector3(0.6f, 1.8f, 0.6f));
            bool any = false;
            float relief = global::G3.Unity.G3ReliefFooting.OffsetOf(visual);   // the model may stand on the drawn relief: fit the body's own pose
            foreach (var r in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                foreach (var bone in r.bones)
                {
                    if (bone == null) continue;
                    var p = transform.InverseTransformPoint(bone.position - Vector3.up * relief);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            if (any) bounds.Expand(new Vector3(0.2f, 0.15f, 0.2f)); // flesh around the joints
            var body = CombatBodyShape.Body(bounds.size.x, bounds.max.y, bounds.size.z, bounds.center.z);
            Apply(_capsule, body);
            Top = body.Top;
            Trigger = MeleeHitGeometry.Trigger(bounds.size.x, bounds.max.y, bounds.size.z, bounds.center.z);
            if (_trigger == null) _trigger = CombatWeaponTrigger.Create(this);
            _trigger.Fit(Trigger);
        }

        void OnDestroy()
        {
            if (_trigger != null) Destroy(_trigger.gameObject);
        }

        static void Apply(CapsuleCollider c, BodyCapsule s)
        {
            c.direction = s.Axis;
            c.radius = s.Radius;
            c.height = s.Length;
            c.center = new Vector3(0, s.CenterY, s.CenterZ);
        }
    }
}
