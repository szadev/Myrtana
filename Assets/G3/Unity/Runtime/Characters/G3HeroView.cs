using System.Collections.Generic;
using G3.Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity.Characters
{
    /// <summary>
    /// First-person rendering of the hero model (fix-208): the head and everything on it (hair, beard, helmet, hood, eyes,
    /// the remaster's MetaHuman head and its extras) is never drawn for the camera, it only casts its shadow, from any
    /// pitch and in every animation; the body stays visible (or shadow-only, see <see cref="hideBodyInFirstPerson"/>).
    /// A skinned mesh with head and body vertices (a full armour with its helmet or hood) only casts the shadow and a copy
    /// without the head triangles ("FP cut") is drawn in its place (<see cref="FirstPersonBodyRules"/>). The cut also takes
    /// the collar of body and armour meshes (pauldrons, collars, hoods, built-in helmets: shoulder / top-of-spine vertices
    /// near the head or above the shoulder line, bind pose; <see cref="FirstPersonBodyRules.IsCollarVertex"/>).
    /// The view re-classifies whenever the hero's visual set changes (equip, remaster on/off, transform back, load): each
    /// frame in first person it compares a cheap signature of the hero's renderers.
    /// In third person a camera pressed close to the hero hides all of him (<see cref="SetHidden"/>; G3 fades the whole
    /// hero, fix-94). <see cref="headRenderers"/> (filled by G3HeroImporter and the runtime remaster) are always head.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)] // after the views that dress the hero (armour, remaster, hand props) in their Update / LateUpdate
    [AddComponentMenu("G3/G3 Hero View")]
    public sealed class G3HeroView : MonoBehaviour, IG3BodyView
    {
        /// <summary>Name prefix of the drawn copies of mixed meshes (other code skips them; never saved).</summary>
        public const string CutPrefix = "FP cut ";

        public Renderer[] headRenderers = new Renderer[0];
        [Tooltip("Also reduce the body to its shadow in first person.")]
        public bool hideBodyInFirstPerson;

        public bool FirstPerson { get; private set; }
        public bool Hidden { get; private set; }

        /// <summary>Renderers drawn only as shadow now (diagnostics, scenarios).</summary>
        public int ShadowOnlyCount { get; private set; }
        /// <summary>Mixed meshes drawn through a cut copy now.</summary>
        public int CutCount { get; private set; }

        sealed class Cut
        {
            public SkinnedMeshRenderer source, copy;
        }

        readonly Dictionary<Renderer, ShadowCastingMode> _original = new Dictionary<Renderer, ShadowCastingMode>();
        readonly Dictionary<Renderer, Cut> _cuts = new Dictionary<Renderer, Cut>();
        // per source mesh: its part and the cut mesh (null unless mixed); a mesh keeps its part for the hero's bone set
        readonly Dictionary<Mesh, (FirstPersonPart part, Mesh cut)> _meshParts = new Dictionary<Mesh, (FirstPersonPart, Mesh)>();
        readonly List<Renderer> _renderers = new List<Renderer>();
        readonly List<Material> _mats = new List<Material>();
        readonly List<string> _chain = new List<string>();
        readonly HashSet<Renderer> _seen = new HashSet<Renderer>();
        long _signature;
        bool _dirty = true;

        public void SetFirstPerson(bool firstPerson)
        {
            FirstPerson = firstPerson;
            Apply();
        }

        public void SetHidden(bool hidden)
        {
            Hidden = hidden;
            Apply();
        }

        /// <summary>Re-classify on the next frame (a caller that changed the hero's renderers in a way the signature misses).</summary>
        public void MarkDirty() => _dirty = true;

        void OnEnable() => _dirty = true;

        void LateUpdate()
        {
            if (!FirstPerson && !Hidden && !_dirty) return; // third person: nothing to keep up
            long sig = Signature();
            if (!_dirty && sig == _signature) { SyncCuts(); return; }
            Apply();
        }

        void OnDestroy()
        {
            foreach (var c in _cuts.Values) if (c.copy != null) Destroy(c.copy.gameObject);
            _cuts.Clear();
            foreach (var kv in _meshParts) if (kv.Value.cut != null) Destroy(kv.Value.cut);
            _meshParts.Clear();
        }

        /// <summary>A hash of the hero's renderers: which ones, their meshes, materials and enabled state.</summary>
        long Signature()
        {
            GetComponentsInChildren(true, _renderers);
            unchecked
            {
                long h = 17;
                foreach (var r in _renderers)
                {
                    if (r == null || IsCut(r)) continue;
                    h = h * 31 + r.GetHashCode();
                    h = h * 31 + (r.enabled ? 1 : 2) + (r.gameObject.activeInHierarchy ? 4 : 8);
                    var mesh = MeshOf(r);
                    h = h * 31 + (mesh != null ? mesh.GetHashCode() : 0);
                    r.GetSharedMaterials(_mats);
                    foreach (var m in _mats) h = h * 31 + (m != null ? m.GetHashCode() : 0);
                }
                return h;
            }
        }

        static bool IsCut(Renderer r) => r.name.StartsWith(CutPrefix, System.StringComparison.Ordinal);

        void Apply()
        {
            _signature = Signature(); // also fills _renderers
            _dirty = false;
            _seen.Clear();
            int shadowOnly = 0, cuts = 0;
            foreach (var r in _renderers)
            {
                if (r == null || IsCut(r)) continue;
                _seen.Add(r);
                if (!_original.TryGetValue(r, out var original))
                {
                    original = r.shadowCastingMode;
                    // a renderer created while its owner was shadow-only (remaster extras copy the owner's mode) is a normal one
                    if (original == ShadowCastingMode.ShadowsOnly) original = ShadowCastingMode.On;
                    _original[r] = original;
                }
                bool isMesh = r is SkinnedMeshRenderer || r is MeshRenderer;
                var part = FirstPersonPart.Body;
                if (FirstPerson && !Hidden && isMesh) part = hideBodyInFirstPerson ? FirstPersonPart.Head : PartOf(r);
                bool hide = Hidden || part != FirstPersonPart.Body;
                var mode = hide ? ShadowCastingMode.ShadowsOnly : original;
                if (r.shadowCastingMode != mode) r.shadowCastingMode = mode;
                if (hide) shadowOnly++;
                if (part == FirstPersonPart.Mixed && r is SkinnedMeshRenderer smr) { if (ShowCut(smr)) cuts++; }
                else HideCut(r);
            }
            // renderers gone (destroyed remaster extras, swapped armour parts): forget them, drop their copies
            if (_original.Count > _seen.Count)
            {
                var gone = new List<Renderer>();
                foreach (var k in _original.Keys) if (k == null || !_seen.Contains(k)) gone.Add(k);
                foreach (var k in gone) _original.Remove(k);
            }
            PurgeCuts();
            ShadowOnlyCount = shadowOnly;
            CutCount = cuts;
        }

        static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer s) return s.sharedMesh;
            return r.TryGetComponent<MeshFilter>(out var f) ? f.sharedMesh : null;
        }

        FirstPersonPart PartOf(Renderer r)
        {
            if (headRenderers != null && System.Array.IndexOf(headRenderers, r) >= 0) return FirstPersonPart.Head;
            var mesh = MeshOf(r);
            if (mesh != null && FirstPersonBodyRules.IsHeadMeshName(mesh.name)) return FirstPersonPart.Head;
            if (!(r is SkinnedMeshRenderer smr))
                return IsHeadNode(r.transform) ? FirstPersonPart.Head : FirstPersonPart.Body; // a static mesh on a head slot
            if (mesh == null) return FirstPersonPart.Body;
            if (_meshParts.TryGetValue(mesh, out var known)) return known.part;
            var result = ClassifySkinned(smr, mesh);
            _meshParts[mesh] = result;
            return result.Item1;
        }

        bool IsHeadNode(Transform t)
        {
            _chain.Clear();
            for (var p = t; p != null && p != transform.parent; p = p.parent) _chain.Add(p.name);
            return FirstPersonBodyRules.IsHeadNode(_chain);
        }

        (FirstPersonPart, Mesh) ClassifySkinned(SkinnedMeshRenderer smr, Mesh mesh)
        {
            var bones = smr.bones;
            var boneIsHead = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++) boneIsHead[i] = bones[i] != null && IsHeadNode(bones[i]);
            if (!mesh.isReadable) return (FirstPersonBodyRules.ClassifyByBones(boneIsHead), null);

            int n = mesh.vertexCount;
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (perVertex.Length != n || weights.Length == 0) return (FirstPersonBodyRules.ClassifyByBones(boneIsHead), null);
            var headVertex = new bool[n];
            var boneKind = new FirstPersonBone[bones.Length];
            for (int i = 0; i < bones.Length; i++) boneKind[i] = bones[i] != null ? FirstPersonBodyRules.BoneKind(bones[i].name) : FirstPersonBone.Other;
            bool collar = CollarFrameOf(smr, mesh, bones, out var frame);
            var verts = collar ? mesh.vertices : null;
            int w = 0;
            for (int v = 0; v < n; v++)
            {
                float head = 0f, total = 0f, best = -1f;
                int dominant = -1;
                int count = perVertex[v];
                for (int k = 0; k < count && w < weights.Length; k++, w++)
                {
                    var bw = weights[w];
                    total += bw.weight;
                    bool valid = bw.boneIndex >= 0 && bw.boneIndex < boneIsHead.Length;
                    if (valid && boneIsHead[bw.boneIndex]) head += bw.weight;
                    if (valid && bw.weight > best) { best = bw.weight; dominant = bw.boneIndex; }
                }
                headVertex[v] = FirstPersonBodyRules.IsHeadVertex(head, total);
                if (!headVertex[v] && collar && dominant >= 0)
                {
                    var kind = boneKind[dominant];
                    if (kind == FirstPersonBone.Shoulder || kind == FirstPersonBone.Spine)
                    {
                        var p = verts[v];
                        frame.Measure(new System.Numerics.Vector3(p.x, p.y, p.z), out float height, out float lateral, out float dist);
                        headVertex[v] = FirstPersonBodyRules.IsCollarVertex(kind, height, lateral, dist, frame.HalfSpan);
                    }
                }
            }

            int removedAll = 0, totalAll = 0;
            var subTris = new int[mesh.subMeshCount][];
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                subTris[s] = FirstPersonBodyRules.CutTriangles(tris, headVertex, out int removed);
                removedAll += removed;
                totalAll += tris.Length / 3;
            }
            var part = FirstPersonBodyRules.ClassifyTriangles(removedAll, totalAll);
            if (part != FirstPersonPart.Mixed) return (part, null);
            var cut = Instantiate(mesh);
            cut.name = CutPrefix + mesh.name.Replace("_Body_", " body ");
            cut.hideFlags = HideFlags.DontSave;
            for (int s = 0; s < subTris.Length; s++) cut.SetTriangles(subTris[s], s, false);
            return (part, cut);
        }

        /// <summary>
        /// The collar frame of a body mesh in its bind space (fix-208): its shoulder joints (upper-arm roots) and the head
        /// joint. Armour bodies carry no head bones, so the head joint is taken from the head renderer's bind pose relative
        /// to a bone both share (else from the live skeleton).
        /// </summary>
        bool CollarFrameOf(SkinnedMeshRenderer smr, Mesh mesh, Transform[] bones, out CollarFrame frame)
        {
            frame = default;
            var bind = mesh.bindposes;
            if (bind.Length != bones.Length) return false;
            Transform left = null, right = null;
            int lRank = 0, rRank = 0;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                int side = FirstPersonBodyRules.ShoulderJointSide(t.name, out int rank);
                if (side == 1 && rank > lRank) { left = t; lRank = rank; }
                else if (side == 2 && rank > rRank) { right = t; rRank = rank; }
            }
            var head = HeadJoint();
            if (left == null || right == null || head == null) return false;
            if (!JointInMesh(left, bones, bind, out var l) || !JointInMesh(right, bones, bind, out var r) || !JointInMesh(head, bones, bind, out var h))
                return false;
            frame = new CollarFrame(N(h), N(l), N(r));
            return frame.Valid;
        }

        static System.Numerics.Vector3 N(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);

        /// <summary>The head joint of the rig: &lt;Rig&gt;_Head_Head_ROOT (else _Head_Head_1, Bip01 Head).</summary>
        Transform HeadJoint()
        {
            Transform best = null;
            int bestRank = 0;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                string nm = t.name;
                int rank = nm.EndsWith("_Head_Head_ROOT", System.StringComparison.OrdinalIgnoreCase) ? 3
                    : nm.EndsWith("_Head_Head_1", System.StringComparison.OrdinalIgnoreCase) ? 2
                    : string.Equals(nm, "Bip01 Head", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (rank > bestRank) { best = t; bestRank = rank; }
            }
            return best;
        }

        /// <summary>
        /// A skeleton joint in a mesh's bind space: directly from the mesh's bind pose when it skins the joint; else from a
        /// head renderer's bind pose that skins the joint and a bone this mesh shares; else via the joint's nearest skinned
        /// ancestor in the live skeleton.
        /// </summary>
        bool JointInMesh(Transform joint, Transform[] bones, Matrix4x4[] bind, out Vector3 p)
        {
            int i = System.Array.IndexOf(bones, joint);
            if (i >= 0) { p = bind[i].inverse.MultiplyPoint3x4(Vector3.zero); return true; }
            if (headRenderers != null)
                foreach (var hr in headRenderers)
                {
                    if (!(hr is SkinnedMeshRenderer hs) || hs.sharedMesh == null) continue;
                    var hb = hs.bones; var hp = hs.sharedMesh.bindposes;
                    int hj = hp.Length == hb.Length ? System.Array.IndexOf(hb, joint) : -1;
                    if (hj < 0) continue;
                    var inHead = hp[hj].inverse.MultiplyPoint3x4(Vector3.zero);
                    for (int b = 0; b < hb.Length; b++)
                    {
                        int mi = hb[b] != null && hb[b] != joint ? System.Array.IndexOf(bones, hb[b]) : -1;
                        if (mi < 0) continue;
                        p = bind[mi].inverse.MultiplyPoint3x4(hp[b].MultiplyPoint3x4(inHead));
                        return true;
                    }
                }
            for (var a = joint.parent; a != null && a != transform.parent; a = a.parent)
            {
                int ri = System.Array.IndexOf(bones, a);
                if (ri < 0) continue;
                p = bind[ri].inverse.MultiplyPoint3x4(a.InverseTransformPoint(joint.position));
                return true;
            }
            p = default;
            return false;
        }

        bool ShowCut(SkinnedMeshRenderer source)
        {
            var mesh = source.sharedMesh;
            if (mesh == null || !_meshParts.TryGetValue(mesh, out var known) || known.cut == null) return false;
            if (!_cuts.TryGetValue(source, out var c) || c.copy == null)
            {
                var go = new GameObject(CutPrefix + source.name) { layer = source.gameObject.layer, hideFlags = HideFlags.DontSave };
                go.transform.SetParent(source.transform.parent, false);
                go.transform.SetLocalPositionAndRotation(source.transform.localPosition, source.transform.localRotation);
                go.transform.localScale = source.transform.localScale;
                c = new Cut { source = source, copy = go.AddComponent<SkinnedMeshRenderer>() };
                c.copy.shadowCastingMode = ShadowCastingMode.Off; // the original casts the whole shadow
                _cuts[source] = c;
            }
            var copy = c.copy;
            if (copy.sharedMesh != known.cut) copy.sharedMesh = known.cut;
            copy.bones = source.bones;
            copy.rootBone = source.rootBone;
            copy.localBounds = source.localBounds;
            copy.updateWhenOffscreen = source.updateWhenOffscreen;
            copy.receiveShadows = source.receiveShadows;
            copy.sharedMaterials = source.sharedMaterials;
            copy.renderingLayerMask = source.renderingLayerMask;
            copy.enabled = source.enabled;
            if (copy.gameObject.activeSelf != source.gameObject.activeSelf) copy.gameObject.SetActive(source.gameObject.activeSelf);
            SyncBlendShapes(c);
            return true;
        }

        void HideCut(Renderer r)
        {
            if (!_cuts.TryGetValue(r, out var c) || c.copy == null) return;
            if (c.copy.gameObject.activeSelf) c.copy.gameObject.SetActive(false);
        }

        /// <summary>Copies whose source is gone (destroyed remaster extras, armour parts) go with it.</summary>
        void PurgeCuts()
        {
            List<Renderer> dead = null;
            foreach (var kv in _cuts)
                if (kv.Key == null || kv.Value.copy == null || !_seen.Contains(kv.Key)) (dead ??= new List<Renderer>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var k in dead)
            {
                if (_cuts[k].copy != null) Destroy(_cuts[k].copy.gameObject);
                _cuts.Remove(k);
            }
        }

        /// <summary>Every frame: the copies follow their source's enabled state and face shapes.</summary>
        void SyncCuts()
        {
            foreach (var c in _cuts.Values)
            {
                if (c.copy == null || c.source == null || !c.copy.gameObject.activeSelf) continue;
                if (c.copy.enabled != c.source.enabled) c.copy.enabled = c.source.enabled;
                if (c.copy.updateWhenOffscreen != c.source.updateWhenOffscreen) c.copy.updateWhenOffscreen = c.source.updateWhenOffscreen; // ragdoll
                SyncBlendShapes(c);
            }
        }

        static void SyncBlendShapes(Cut c)
        {
            var mesh = c.source.sharedMesh;
            int n = mesh != null ? mesh.blendShapeCount : 0;
            for (int i = 0; i < n; i++)
            {
                float w = c.source.GetBlendShapeWeight(i);
                if (c.copy.GetBlendShapeWeight(i) != w) c.copy.SetBlendShapeWeight(i, w);
            }
        }

        /// <summary>
        /// A still copy of the hero (soul travel's body, made with Instantiate while the view is in first person) drawn
        /// whole: the cut copies go, the head and the shadow-only parts are drawn again.
        /// </summary>
        public static void MakeWhole(GameObject copy)
        {
            if (copy == null) return;
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (IsCut(r)) { r.gameObject.SetActive(false); Destroy(r.gameObject); continue; }
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) r.shadowCastingMode = ShadowCastingMode.On;
            }
        }
    }
}
