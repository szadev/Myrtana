using System.Collections.Generic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace G3.Unity.Characters
{
    /// <summary>
    /// The beard and moustache of a talking character skinned to its talking head (fix-246, Docs/dialog-presentation.md
    /// §4.1, maths in <see cref="FacialHairSkin"/>): while <see cref="G3TalkingFace"/> is worn, every static facial hair
    /// mesh (Slot_Beard) is drawn by a skinned copy whose vertices carry the bone weights of the head skin under them (bone
    /// kit: jaw, lips) and the head's speech blend shapes (blend shape kit: MetaHuman), so the beard opens with the chin
    /// instead of the chin poking through it. The static renderer is only hidden (<c>forceRenderingOff</c>, which the
    /// character remaster does not touch) and comes back on <see cref="Remove"/>.
    /// </summary>
    sealed class G3FacialHair
    {
        sealed class Part
        {
            public MeshRenderer source;
            public bool wasForcedOff;
            public GameObject go;
            public SkinnedMeshRenderer smr;
            public Mesh mesh;
            public int[] shapes; // the copy's shape index per kit shape (-1 = none)
        }

        /// <summary>Hair edges up to this long (metres, head mesh space) are smoothed over (<see cref="FacialHairSkin.Neighbours"/>).</summary>
        const float SmoothEdge = 0.01f;

        readonly List<Part> _parts = new List<Part>();
        static readonly HashSet<string> _warned = new HashSet<string>();

        public int Count => _parts.Count;

        /// <summary>
        /// Skins the facial hair under <paramref name="character"/> to <paramref name="skin"/>; for a blend shape head
        /// <paramref name="skinShapes"/> is the head's shape index per kit shape (null for a bone kit). Null when the
        /// character wears no facial hair or nothing could be skinned.
        /// </summary>
        public static G3FacialHair Attach(Transform character, SkinnedMeshRenderer skin, int[] skinShapes)
        {
            if (character == null || skin == null || skin.sharedMesh == null) return null;
            var hairs = new List<MeshRenderer>();
            var ancestors = new List<string>();
            foreach (var r in character.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!r.enabled || r.forceRenderingOff) continue;
                var filter = r.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                ancestors.Clear();
                for (var t = r.transform.parent; t != null && t != character; t = t.parent) ancestors.Add(t.name);
                if (FacialHairSkin.IsFacialHair(r.name, filter.sharedMesh.name, ancestors)) hairs.Add(r);
            }
            if (hairs.Count == 0) return null;

            var skinMesh = skin.sharedMesh;
            var bones = skin.bones;
            if (!skinMesh.isReadable || bones == null || bones.Length == 0 || skinMesh.bindposes.Length != bones.Length)
            {
                if (_warned.Add(skinMesh.name)) Debug.LogWarning($"[G3] facial hair: head {skinMesh.name} is not readable / skinned, the beard stays rigid");
                return null;
            }
            var data = SkinData.Read(skin, skinShapes);
            var hair = new G3FacialHair();
            foreach (var r in hairs)
            {
                var part = hair.Build(r, skin, data);
                if (part != null) hair._parts.Add(part);
            }
            return hair._parts.Count > 0 ? hair : null;
        }

        /// <summary>The head skin in its mesh space: positions, skin triangles, weights, speech shape deltas.</summary>
        sealed class SkinData
        {
            public NVector3[] positions;
            public int[] triangles;
            public BoneWeights4[] weights;
            public Matrix4x4[] bindposes;
            public string[] shapeNames;      // per kit shape (null = none)
            public float[] shapeFrameWeight; // the head's last frame weight per kit shape
            public NVector3[][] shapeDeltas; // per kit shape (null = none)

            public static SkinData Read(SkinnedMeshRenderer skin, int[] skinShapes)
            {
                var mesh = skin.sharedMesh;
                var d = new SkinData();
                var verts = mesh.vertices;
                d.positions = new NVector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) d.positions[i] = N(verts[i]);
                var tris = new List<int>();
                var mats = skin.sharedMaterials;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (FacialHairSkin.IsSkinMaterial(s < mats.Length && mats[s] != null ? mats[s].name : null)) tris.AddRange(mesh.GetTriangles(s));
                if (tris.Count == 0) for (int s = 0; s < mesh.subMeshCount; s++) tris.AddRange(mesh.GetTriangles(s));
                d.triangles = tris.ToArray();
                var bw = mesh.boneWeights;
                d.weights = new BoneWeights4[verts.Length];
                for (int i = 0; i < d.weights.Length && i < bw.Length; i++)
                    d.weights[i] = new BoneWeights4(bw[i].boneIndex0, bw[i].weight0, bw[i].boneIndex1, bw[i].weight1,
                                                    bw[i].boneIndex2, bw[i].weight2, bw[i].boneIndex3, bw[i].weight3);
                d.bindposes = mesh.bindposes;
                if (skinShapes != null)
                {
                    int n = skinShapes.Length;
                    d.shapeNames = new string[n];
                    d.shapeFrameWeight = new float[n];
                    d.shapeDeltas = new NVector3[n][];
                    var delta = new Vector3[verts.Length];
                    for (int k = 0; k < n; k++)
                    {
                        int si = skinShapes[k];
                        if (si < 0 || si >= mesh.blendShapeCount) continue;
                        int frame = mesh.GetBlendShapeFrameCount(si) - 1;
                        if (frame < 0) continue;
                        try { mesh.GetBlendShapeFrameVertices(si, frame, delta, null, null); }
                        catch (System.Exception) { continue; }
                        var nd = new NVector3[delta.Length];
                        for (int i = 0; i < nd.Length; i++) nd[i] = N(delta[i]);
                        d.shapeNames[k] = mesh.GetBlendShapeName(si);
                        d.shapeFrameWeight[k] = mesh.GetBlendShapeFrameWeight(si, frame);
                        d.shapeDeltas[k] = nd;
                    }
                }
                return d;
            }
        }

        static NVector3 N(Vector3 v) => new NVector3(v.x, v.y, v.z);
        static Vector3 U(NVector3 v) => new Vector3(v.X, v.Y, v.Z);

        Part Build(MeshRenderer source, SkinnedMeshRenderer skin, SkinData data)
        {
            var src = source.GetComponent<MeshFilter>().sharedMesh;
            if (!src.isReadable)
            {
                if (_warned.Add(src.name)) Debug.LogWarning($"[G3] facial hair: {src.name} is not readable, it stays rigid");
                return null;
            }
            // the skin bone the hair hangs under (Slot_Beard -> *_Head_Head_1): beard and that bone are rigid to each other,
            // so the hair's place in the head mesh's bind space is exact whatever pose the body is in now
            var bones = skin.bones;
            int anchor = -1;
            for (var t = source.transform.parent; t != null && anchor < 0; t = t.parent)
                anchor = System.Array.IndexOf(bones, t);
            if (anchor < 0)
            {
                if (_warned.Add("anchor/" + src.name)) Debug.LogWarning($"[G3] facial hair: {src.name} hangs on none of {skin.sharedMesh.name}'s bones");
                return null;
            }
            var hairToSkin = data.bindposes[anchor].inverse * bones[anchor].worldToLocalMatrix * source.transform.localToWorldMatrix;
            var skinToHair = hairToSkin.inverse;

            var verts = src.vertices;
            var hairPos = new NVector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) hairPos[i] = N(hairToSkin.MultiplyPoint3x4(verts[i]));
            var map = FacialHairSkin.Map(data.positions, data.triangles, hairPos);
            var neighbours = FacialHairSkin.Neighbours(hairPos, src.triangles, 1e-4f, SmoothEdge);
            // only the anchor and the face bones under it (never neck / spine: the hair hangs on the head in G3)
            var allowed = new bool[bones.Length];
            for (int b = 0; b < bones.Length; b++) allowed[b] = bones[b] != null && bones[b].IsChildOf(bones[anchor]);
            var weights = FacialHairSkin.TransferWeights(map, data.weights, bones.Length, neighbours, anchor, allowed: allowed);

            var mesh = Object.Instantiate(src);
            mesh.name = src.name + "_Talk";
            var bw = new BoneWeight[weights.Length];
            for (int i = 0; i < bw.Length; i++)
            {
                var w = weights[i];
                bw[i] = new BoneWeight
                {
                    boneIndex0 = w.B0, weight0 = w.W0, boneIndex1 = w.B1, weight1 = w.W1,
                    boneIndex2 = w.B2, weight2 = w.W2, boneIndex3 = w.B3, weight3 = w.W3,
                };
            }
            mesh.boneWeights = bw;
            var bind = new Matrix4x4[bones.Length];
            for (int b = 0; b < bind.Length; b++) bind[b] = data.bindposes[b] * hairToSkin;
            mesh.bindposes = bind;

            int[] shapes = null;
            if (data.shapeDeltas != null)
            {
                shapes = new int[data.shapeDeltas.Length];
                var names = new HashSet<string>();
                for (int k = 0; k < shapes.Length; k++)
                {
                    shapes[k] = -1;
                    if (data.shapeDeltas[k] == null || !names.Add(data.shapeNames[k])) continue;
                    var d = FacialHairSkin.TransferDeltas(map, data.shapeDeltas[k], neighbours);
                    var local = new Vector3[d.Length];
                    for (int i = 0; i < d.Length; i++) local[i] = skinToHair.MultiplyVector(U(d[i]));
                    mesh.AddBlendShapeFrame(data.shapeNames[k], data.shapeFrameWeight[k] > 0 ? data.shapeFrameWeight[k] : 100f, local, null, null);
                    shapes[k] = mesh.blendShapeCount - 1;
                }
            }

            var go = new GameObject(mesh.name);
            go.layer = source.gameObject.layer;
            go.transform.SetParent(source.transform, false); // under Slot_Beard: first-person / helmet rules see it as head
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = bones[anchor];
            smr.sharedMaterials = source.sharedMaterials;
            smr.shadowCastingMode = source.shadowCastingMode;
            smr.receiveShadows = source.receiveShadows;
            smr.lightProbeUsage = source.lightProbeUsage;
            smr.reflectionProbeUsage = source.reflectionProbeUsage;
            smr.renderingLayerMask = source.renderingLayerMask;
            smr.updateWhenOffscreen = false;
            var toRoot = bones[anchor].worldToLocalMatrix * source.transform.localToWorldMatrix;
            var b0 = src.bounds;
            var bounds = new Bounds(toRoot.MultiplyPoint3x4(b0.center), Vector3.zero);
            for (int c = 0; c < 8; c++)
                bounds.Encapsulate(toRoot.MultiplyPoint3x4(b0.center + Vector3.Scale(b0.extents,
                    new Vector3((c & 1) != 0 ? 1 : -1, (c & 2) != 0 ? 1 : -1, (c & 4) != 0 ? 1 : -1))));
            bounds.Expand(0.05f); // the jaw opens a few centimetres
            smr.localBounds = bounds;

            var part = new Part { source = source, wasForcedOff = source.forceRenderingOff, go = go, smr = smr, mesh = mesh, shapes = shapes };
            source.forceRenderingOff = true;
            return part;
        }

        /// <summary>Blend shape kit: the same weights (0…1 per kit shape) the head shows.</summary>
        public void SetShapes(float[] weights)
        {
            foreach (var p in _parts)
            {
                if (p.shapes == null || p.smr == null) continue;
                for (int k = 0; k < p.shapes.Length && k < weights.Length; k++)
                    if (p.shapes[k] >= 0) p.smr.SetBlendShapeWeight(p.shapes[k], weights[k] * 100f);
            }
        }

        /// <summary>Back to the static facial hair.</summary>
        public void Remove()
        {
            foreach (var p in _parts)
            {
                if (p.source != null) p.source.forceRenderingOff = p.wasForcedOff;
                if (p.go != null) Object.Destroy(p.go);
                if (p.mesh != null) Object.Destroy(p.mesh);
            }
            _parts.Clear();
        }
    }
}
