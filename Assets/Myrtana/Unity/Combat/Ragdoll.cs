using System.Collections.Generic;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3's ZS_RagDollDead on a humanoid skeleton (Hero and Orc rigs: <c>…_Spine_Spine_1</c>, <c>…_Left_Leg_Leg_1</c> …),
    /// built at the moment of death (Docs/combat.md §5.1, <see cref="RagdollRules"/>): eleven capsule bodies joined by
    /// character joints, the motion player's Animator switched off, an impulse away from the killer; once the body rests
    /// (or after <see cref="MaxSeconds"/>) every part turns kinematic and the corpse keeps that pose, as G3 freezes it.
    /// Skeletons without these bones (creatures) are not touched: <see cref="TryStart"/> returns false and the caller plays
    /// the death motion. <see cref="Remove"/> takes it all away again (a pooled view reused, the actor revived).
    /// </summary>
    public sealed class Ragdoll : MonoBehaviour
    {
        /// <summary>Bone suffix, parent part suffix, child bone suffix (capsule end), radius m, mass kg.</summary>
        static readonly (string bone, string parent, string end, float radius, float mass)[] Layout =
        {
            ("Spine_Spine_1", null, "Spine_Spine_3", 0.15f, 16f),
            ("Spine_Spine_3", "Spine_Spine_1", "Spine_Spine_END", 0.16f, 16f),
            ("Head_Head_1", "Spine_Spine_3", null, 0.11f, 5f),
            ("Left_Arm_Arm_1", "Spine_Spine_3", "Left_Arm_Arm_2", 0.06f, 3f),
            ("Left_Arm_Arm_2", "Left_Arm_Arm_1", "Left_Arm_Arm_END", 0.05f, 2f),
            ("Right_Arm_Arm_1", "Spine_Spine_3", "Right_Arm_Arm_2", 0.06f, 3f),
            ("Right_Arm_Arm_2", "Right_Arm_Arm_1", "Right_Arm_Arm_END", 0.05f, 2f),
            ("Left_Leg_Leg_1", "Spine_Spine_1", "Left_Leg_Leg_2", 0.08f, 8f),
            ("Left_Leg_Leg_2", "Left_Leg_Leg_1", "Left_Leg_Leg_END", 0.06f, 5f),
            ("Right_Leg_Leg_1", "Spine_Spine_1", "Right_Leg_Leg_2", 0.08f, 8f),
            ("Right_Leg_Leg_2", "Right_Leg_Leg_1", "Right_Leg_Leg_END", 0.06f, 5f),
        };

        /// <summary>Longest a body stays dynamic (s) before it is frozen anyway.</summary>
        const float MaxSeconds = 8f;
        /// <summary>The body rests when every part moves slower than this (m/s) for <see cref="RestSeconds"/>.</summary>
        const float RestSpeed = 0.08f, RestSeconds = 0.6f, MinSeconds = 0.5f;
        /// <summary>
        /// The body counts as fallen once a part moved faster than this (m/s) or after <see cref="FallSeconds"/>: before,
        /// a body still balancing at the start (no push yet felt) is not "resting" and must not freeze upright.
        /// </summary>
        const float FallSpeed = 0.5f, FallSeconds = 3f;

        readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        readonly List<Component> _added = new List<Component>();
        readonly List<SkinnedMeshRenderer> _skins = new List<SkinnedMeshRenderer>();
        Animator _animator;
        global::G3.Unity.Characters.G3MotionPlayer _player;
        bool _animatorWasEnabled;
        float _started, _restFor;
        bool _frozen, _fell;

        /// <summary>The corpse is lying still (frozen).</summary>
        public bool Settled => _frozen;
        /// <summary>The height of the body's origin when it died (its feet on the ground), m.</summary>
        public float DeathHeight { get; private set; }
        /// <summary>The pelvis went under the ground (a slope's surface above it): frozen at once, <see cref="CorpseRest"/> lifts it out (fix-80).</summary>
        public bool FellThrough { get; private set; }
        /// <summary>The body's origin when it died (fix-188: a body that fell out of the world is laid back there).</summary>
        public Vector3 DeathPosition { get; private set; }
        /// <summary>The pelvis bone's position now (the origin without one).</summary>
        public Vector3 PelvisPosition => _pelvis != null ? _pelvis.position : transform.position;

        /// <summary>Below the death height by more than this with ground above it, the pelvis has fallen through, m.</summary>
        const float FallThroughDepth = 0.5f, FallThroughProbe = 3f;
        /// <summary>fix-188: this far below the death height with no floor under it, the body falls out of the world, m.</summary>
        const float FallOutDepth = 2.5f;
        Transform _pelvis;

        /// <summary>
        /// Turns the skeleton under <paramref name="visual"/> into a ragdoll pushed along <paramref name="away"/> (horizontal,
        /// normalised by the caller) with <paramref name="impulse"/>. False when the bones are missing (nothing changed).
        /// </summary>
        public static bool TryStart(Transform visual, Vector3 away, RagdollImpulse impulse, Collider ignore = null)
        {
            if (visual == null) return false;
            var bones = new Dictionary<string, Transform>();
            foreach (var t in visual.GetComponentsInChildren<Transform>(true))
                foreach (var part in Layout)
                {
                    if (!bones.ContainsKey(part.bone) && t.name.EndsWith(part.bone, System.StringComparison.OrdinalIgnoreCase)) bones[part.bone] = t;
                    if (part.end != null && !bones.ContainsKey(part.end) && t.name.EndsWith(part.end, System.StringComparison.OrdinalIgnoreCase)) bones[part.end] = t;
                }
            foreach (var part in Layout)
                if (!bones.ContainsKey(part.bone)) return false;
            global::G3.Unity.G3ReliefFooting.Release(visual);   // the bodies start from the collision pose (remaster-world 14)
            var r = visual.GetComponent<Ragdoll>();
            if (r != null) r.Remove();
            r = visual.gameObject.AddComponent<Ragdoll>();
            r.Build(bones, away, impulse, ignore);
            return true;
        }

        /// <summary>Removes a ragdoll from <paramref name="visual"/> (if any) and gives the skeleton back to the Animator.</summary>
        public static void RemoveFrom(Transform visual)
        {
            if (visual != null && visual.TryGetComponent<Ragdoll>(out var r)) r.Remove();
        }

        void Build(Dictionary<string, Transform> bones, Vector3 away, RagdollImpulse impulse, Collider ignore)
        {
            _animator = GetComponent<Animator>();
            if (_animator == null) _animator = GetComponentInChildren<Animator>();
            if (_animator != null)
            {
                _animatorWasEnabled = _animator.enabled;
                _animator.enabled = false;
            }
            // fix-202: the Animator off does not stop the motion player's playing graph: the dying motion kept posing the
            // bones against the bodies, and its switch to the LieDead loop pulled the falling body back up (owner, build 23e)
            _player = GetComponent<global::G3.Unity.Characters.G3MotionPlayer>();
            if (_player != null) _player.HoldPose(this);
            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>())
                if (!skin.updateWhenOffscreen)
                {
                    skin.updateWhenOffscreen = true; // bounds follow the falling bones
                    _skins.Add(skin);
                }
            DeathHeight = transform.position.y;
            DeathPosition = transform.position;
            _pelvis = bones[Layout[0].bone];
            LiftOutOfGround(bones);
            var byBone = new Dictionary<string, Rigidbody>();
            var velocity = away * impulse.Speed + Vector3.up * impulse.Lift;
            foreach (var part in Layout)
            {
                var bone = bones[part.bone];
                var body = bone.gameObject.AddComponent<Rigidbody>();
                body.mass = part.mass;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                _added.Add(body);
                _bodies.Add(body);
                byBone[part.bone] = body;
                var collider = AddCollider(bone, part.end != null && bones.TryGetValue(part.end, out var end) ? end : null, part.radius);
                if (ignore != null) Physics.IgnoreCollision(collider, ignore);
                if (part.parent != null && byBone.TryGetValue(part.parent, out var parentBody))
                {
                    var joint = bone.gameObject.AddComponent<CharacterJoint>();
                    joint.connectedBody = parentBody;
                    joint.enableProjection = true;
                    bool hinge = part.bone.EndsWith("_2"); // elbows and knees bend further
                    joint.lowTwistLimit = new SoftJointLimit { limit = -20f };
                    joint.highTwistLimit = new SoftJointLimit { limit = 20f };
                    joint.swing1Limit = new SoftJointLimit { limit = hinge ? 80f : 40f };
                    joint.swing2Limit = new SoftJointLimit { limit = hinge ? 10f : 30f };
                    _added.Add(joint);
                }
                body.AddForce(velocity, ForceMode.VelocityChange);
            }
            // Bodies of the same ragdoll do not collide with each other (joints only keep their own pair apart).
            var colliders = new List<Collider>();
            foreach (var c in _added) if (c is Collider col) colliders.Add(col);
            for (int i = 0; i < colliders.Count; i++)
                for (int j = i + 1; j < colliders.Count; j++) Physics.IgnoreCollision(colliders[i], colliders[j]);
            _started = Time.time;
        }

        /// <summary>
        /// On a slope the standing body's uphill leg is inside the hill: a part starting under a one-sided ground surface is
        /// pushed out the wrong side and the body falls through (fix-80). The whole body is raised until every part clears it.
        /// </summary>
        void LiftOutOfGround(Dictionary<string, Transform> bones)
        {
            float lift = 0f;
            foreach (var part in Layout)
                for (int k = 0; k < 2; k++)
                {
                    Transform t = k == 0 ? bones[part.bone] : part.end != null && bones.TryGetValue(part.end, out var end) ? end : null;
                    if (t == null) continue;
                    var p = t.position;
                    // only the low points (knees, ankles): a roof or beam above a head or a shoulder is no ground
                    if (p.y - DeathHeight > 0.8f || !NpcWalker.TryFloorHeight(p, out float ground, 1.5f) || ground - p.y > 0.8f) continue;
                    if (ground + part.radius > p.y) lift = Mathf.Max(lift, ground + part.radius - p.y);
                }
            if (lift > 1e-3f) transform.position += Vector3.up * Mathf.Min(lift + 0.01f, 1f);
        }

        static PhysicsMaterial s_flesh;

        /// <summary>
        /// Rough, dead flesh: a body on a slope stops instead of sliding down it for the whole 8 s (fix-80; the level's
        /// surfaces keep Unity's 0.6 friction, the higher of the two counts) [interp].
        /// </summary>
        static PhysicsMaterial Flesh => s_flesh != null ? s_flesh : s_flesh = new PhysicsMaterial("RagdollFlesh")
        {
            dynamicFriction = 1f,
            staticFriction = 1.2f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Maximum,
            bounceCombine = PhysicsMaterialCombine.Minimum,
        };

        Collider AddCollider(Transform bone, Transform end, float radius)
        {
            float scale = Mathf.Max(1e-4f, bone.lossyScale.x);
            if (end == null)
            {
                var sphere = bone.gameObject.AddComponent<SphereCollider>();
                sphere.radius = radius / scale;
                sphere.sharedMaterial = Flesh;
                _added.Add(sphere);
                return sphere;
            }
            var local = bone.InverseTransformPoint(end.position);
            var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
            var abs = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
            capsule.direction = abs.x >= abs.y && abs.x >= abs.z ? 0 : abs.y >= abs.z ? 1 : 2;
            capsule.center = local * 0.5f;
            capsule.radius = radius / scale;
            capsule.height = local.magnitude + 2f * radius / scale;
            capsule.sharedMaterial = Flesh;
            _added.Add(capsule);
            return capsule;
        }

        void FixedUpdate()
        {
            if (_frozen || _bodies.Count == 0) return;
            float t = Time.time - _started;
            bool still = true;
            foreach (var b in _bodies)
            {
                if (b == null) continue;
                float v2 = b.linearVelocity.sqrMagnitude;
                if (v2 > FallSpeed * FallSpeed) _fell = true;
                if (v2 > RestSpeed * RestSpeed) still = false;
            }
            if (t > FallSeconds) _fell = true;
            if (_pelvis != null && _pelvis.position.y < DeathHeight - FallThroughDepth &&
                NpcWalker.TryFloorHeight(_pelvis.position, out float ground, FallThroughProbe) && ground > _pelvis.position.y + 0.05f)
            {
                FellThrough = true; // the surface is above the pelvis: it went through the ground, it never comes back up
                Freeze();
                return;
            }
            // fix-188 (fix180_corpse_pose: FreeSlave_01 killed on the 33° Nordmar slope went through the landscape and fell to
            // y -122; the check above needs the surface within 3 m over the pelvis): deep under the death height with no floor
            // below either, it is falling out of the world: frozen, CorpseRest lays it back where it died
            if (_pelvis != null && _pelvis.position.y < DeathHeight - FallOutDepth && !AnyFloorBelow(_pelvis.position))
            {
                FellThrough = true;
                Freeze();
                return;
            }
            _restFor = still && _fell ? _restFor + Time.fixedDeltaTime : 0f;
            if (t > MaxSeconds || t > MinSeconds && _restFor >= RestSeconds) Freeze();
        }

        /// <summary>Any surface (no body, no trigger) within 200 m under <paramref name="p"/>: a body tumbling down a slope has one.</summary>
        static bool AnyFloorBelow(Vector3 p)
        {
            foreach (var h in Physics.RaycastAll(p, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore))
                if (h.rigidbody == null && !(h.collider is CharacterController)) return true;
            return false;
        }

        /// <summary>ZS_RagDollDead_Loop: the body rests: physics off, the pose stays (colliders stay for arrows).</summary>
        void Freeze()
        {
            _frozen = true;
            foreach (var b in _bodies)
                if (b != null)
                {
                    b.linearVelocity = Vector3.zero;
                    b.angularVelocity = Vector3.zero;
                    b.isKinematic = true;
                    // no interpolation at rest: CorpseRest moves the body's origin, an interpolating body would be drawn
                    // between its last two physics poses (the old place) for a step
                    b.interpolation = RigidbodyInterpolation.None;
                }
        }

        public void Remove()
        {
            // Joints first (they need their bodies), then colliders and bodies.
            for (int pass = 0; pass < 3; pass++)
                foreach (var c in _added)
                {
                    if (c == null) continue;
                    bool joint = c is Joint, body = c is Rigidbody;
                    if (pass == 0 && joint || pass == 1 && !joint && !body || pass == 2 && body) DestroyImmediate(c);
                }
            _added.Clear();
            _bodies.Clear();
            foreach (var skin in _skins) if (skin != null) skin.updateWhenOffscreen = false;
            _skins.Clear();
            if (_animator != null) _animator.enabled = _animatorWasEnabled || _animator.enabled;
            if (_player != null) _player.ReleasePose(this);
            DestroyImmediate(this);
        }
    }
}
