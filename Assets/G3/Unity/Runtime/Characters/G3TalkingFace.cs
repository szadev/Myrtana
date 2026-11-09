using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// A character's face while it speaks (Docs/dialog-presentation.md §4): <see cref="Wear"/> swaps the static head for
    /// the talking head of its <see cref="G3FaceKit"/> — face bones created under the skeleton's head bones, the facial
    /// mesh skinned to them and drawn with the worn head's materials — and <see cref="SetMouth"/> poses the face bones
    /// every frame after the body animation. <see cref="Remove"/> (or destroying the component) restores the static head.
    /// G3 does the same: the head item's FacialAnimFilePath actor plays the viseme motion while a line is spoken. Unlike
    /// G3 (whose beard hangs rigidly on Slot_Beard under the head bone), the beard and moustache follow the jaw and lips
    /// (<see cref="G3FacialHair"/>, fix-246).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(300)] // after G3MotionPlayer (200) and the animators
    public sealed class G3TalkingFace : MonoBehaviour
    {
        G3FaceKit _kit;
        SkinnedMeshRenderer _staticHead, _talkingHead;
        bool _staticWasEnabled;
        readonly List<GameObject> _created = new List<GameObject>();
        Transform[] _trackBones;
        Vector3[] _restPos;
        Quaternion[] _restRot;
        int _from, _to;
        float _weight, _amount;
        // blend shape kit (G3FaceKit.IsMorph): the worn head's shape index per table shape (-1 = missing), weights buffer
        int[] _shapeIndex;
        float[] _shapeWeights;
        G3FacialHair _hair; // beard / moustache skinned to the talking head (fix-246)

        public G3FaceKit Kit => _kit;

        /// <summary>Viseme shown for loud sound when a line has no viseme track.</summary>
        public int OpenViseme => _kit != null ? _kit.openViseme : 1;

        /// <summary>
        /// Puts the talking head on <paramref name="character"/> (or returns the one it already wears); null when its
        /// head has no kit.
        /// </summary>
        public static G3TalkingFace Wear(GameObject character)
        {
            if (character == null) return null;
            var existing = character.GetComponent<G3TalkingFace>();
            if (existing != null) return existing;
            foreach (var smr in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null || smr.sharedMesh.name.IndexOf("Head", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                // a head the runtime character remaster swapped in talks with its remastered kit (none: no lip-sync, rather
                // than the original face over the remastered one)
                var kit = G3CharacterRemaster.IsRemasterMesh(smr.sharedMesh)
                    ? G3CharacterRemaster.FaceKit(smr.sharedMesh.name)
                    : G3FaceKit.Load(smr.sharedMesh.name);
                if (kit == null || (kit.mesh == null && !kit.IsMorph)) continue;
                var face = character.AddComponent<G3TalkingFace>();
                if (face.Build(kit, smr)) return face;
                Destroy(face);
                return null;
            }
            return null;
        }

        /// <summary>Mouth pose: viseme <paramref name="from"/> → <paramref name="to"/> by <paramref name="weight"/>, applied by <paramref name="amount"/>.</summary>
        public void SetMouth(int from, int to, float weight, float amount)
        {
            _from = from;
            _to = to;
            _weight = Mathf.Clamp01(weight);
            _amount = Mathf.Clamp01(amount);
        }

        public void Remove() => Destroy(this);

        bool Build(G3FaceKit kit, SkinnedMeshRenderer staticHead)
        {
            _kit = kit;
            _staticHead = staticHead;
            if (kit.IsMorph) return BuildMorph(kit, staticHead);
            var byName = new Dictionary<string, Transform>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in GetComponentsInChildren<Transform>(true)) byName[t.name] = t;
            if (!CreateNodes(kit, byName)) return false;

            var bones = new Transform[kit.bones.Length];
            for (int i = 0; i < bones.Length; i++)
                if (!byName.TryGetValue(kit.bones[i], out bones[i]))
                {
                    Debug.LogWarning($"[G3] talking head {kit.head}: no bone {kit.bones[i]} on {name}");
                    return false;
                }

            var go = new GameObject(kit.facialActor);
            go.transform.SetParent(staticHead.transform.parent, false);
            go.transform.SetLocalPositionAndRotation(staticHead.transform.localPosition, staticHead.transform.localRotation);
            go.layer = staticHead.gameObject.layer;
            _created.Add(go);
            _talkingHead = go.AddComponent<SkinnedMeshRenderer>();
            _talkingHead.sharedMesh = kit.mesh;
            _talkingHead.bones = bones;
            _talkingHead.rootBone = kit.rootBone != null && byName.TryGetValue(kit.rootBone, out var rb) ? rb : staticHead.rootBone;
            _talkingHead.localBounds = staticHead.localBounds;
            _talkingHead.sharedMaterials = staticHead.sharedMaterials.Length == kit.mesh.subMeshCount ? staticHead.sharedMaterials : kit.materials;
            _talkingHead.shadowCastingMode = staticHead.shadowCastingMode;
            _talkingHead.updateWhenOffscreen = false;

            _staticWasEnabled = staticHead.enabled;
            staticHead.enabled = false;

            _trackBones = new Transform[kit.tracks.Length];
            _restPos = new Vector3[kit.tracks.Length];
            _restRot = new Quaternion[kit.tracks.Length];
            for (int i = 0; i < kit.tracks.Length; i++)
            {
                byName.TryGetValue(kit.tracks[i].bone, out _trackBones[i]);
                if (_trackBones[i] == null) continue;
                _restPos[i] = _trackBones[i].localPosition;
                _restRot[i] = _trackBones[i].localRotation;
            }
            _hair = G3FacialHair.Attach(transform, _talkingHead, null);
            return true;
        }

        /// <summary>
        /// Blend shape kit: the worn head stays and talks itself (no facial mesh, no face bones); shapes the head lacks are
        /// skipped, none at all fails the build.
        /// </summary>
        bool BuildMorph(G3FaceKit kit, SkinnedMeshRenderer head)
        {
            var mesh = head.sharedMesh;
            var names = new string[mesh != null ? mesh.blendShapeCount : 0];
            for (int i = 0; i < names.Length; i++) names[i] = mesh.GetBlendShapeName(i);
            var shapes = kit.morphs.Shapes;
            _shapeIndex = new int[shapes.Length];
            _shapeWeights = new float[shapes.Length];
            int found = 0;
            for (int s = 0; s < shapes.Length; s++)
                if ((_shapeIndex[s] = ArkitShapes.Find(names, shapes[s])) >= 0) found++;
            if (found == 0)
            {
                Debug.LogWarning($"[G3] talking head {kit.head}: none of the {shapes.Length} blend shapes on {mesh?.name}");
                return false;
            }
            _staticWasEnabled = head.enabled;
            _hair = G3FacialHair.Attach(transform, head, _shapeIndex);
            return true;
        }

        /// <summary>Face bones the character lacks, in parent-first order (some heads already carry them).</summary>
        bool CreateNodes(G3FaceKit kit, Dictionary<string, Transform> byName)
        {
            var pending = new List<G3FaceKit.Node>(kit.nodes);
            while (pending.Count > 0)
            {
                int placed = 0;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var n = pending[i];
                    if (byName.ContainsKey(n.name)) { pending.RemoveAt(i); placed++; continue; }
                    if (!byName.TryGetValue(n.parent, out var parent)) continue;
                    var t = new GameObject(n.name).transform;
                    t.SetParent(parent, false);
                    t.SetLocalPositionAndRotation(n.position, n.rotation);
                    byName[n.name] = t;
                    _created.Add(t.gameObject);
                    pending.RemoveAt(i);
                    placed++;
                }
                if (placed == 0)
                {
                    Debug.LogWarning($"[G3] talking head {kit.head}: no parent {pending[0].parent} for {pending[0].name} on {name}");
                    return false;
                }
            }
            return true;
        }

        void LateUpdate()
        {
            if (_kit == null) return;
            if (_shapeIndex != null)
            {
                if (_staticHead == null) return;
                _kit.morphs.Evaluate(_from, _to, _weight, _amount, _shapeWeights);
                for (int s = 0; s < _shapeIndex.Length; s++)
                    if (_shapeIndex[s] >= 0) _staticHead.SetBlendShapeWeight(_shapeIndex[s], _shapeWeights[s] * 100f);
                _hair?.SetShapes(_shapeWeights);
                return;
            }
            if (_trackBones == null) return;
            int last = _kit.visemeCount - 1;
            int from = Mathf.Clamp(_from, 0, last), to = Mathf.Clamp(_to, 0, last);
            for (int i = 0; i < _trackBones.Length; i++)
            {
                var bone = _trackBones[i];
                if (bone == null) continue;
                var track = _kit.tracks[i];
                var rot = Quaternion.Slerp(track.rotations[from], track.rotations[to], _weight);
                var pos = Vector3.Lerp(track.positions[from], track.positions[to], _weight);
                bone.SetLocalPositionAndRotation(Vector3.Lerp(_restPos[i], pos, _amount), Quaternion.Slerp(_restRot[i], rot, _amount));
            }
        }

        void OnDestroy()
        {
            // The facial hair copies, the talking head and the created face bones.
            _hair?.Remove();
            _hair = null;
            foreach (var go in _created)
                if (go != null) Destroy(go);
            _created.Clear();
            if (_staticHead != null && _shapeIndex != null && _staticHead.sharedMesh != null)
                foreach (int i in _shapeIndex)
                    if (i >= 0 && i < _staticHead.sharedMesh.blendShapeCount) _staticHead.SetBlendShapeWeight(i, 0);
            if (_staticHead != null) _staticHead.enabled = _staticWasEnabled;
        }
    }
}
