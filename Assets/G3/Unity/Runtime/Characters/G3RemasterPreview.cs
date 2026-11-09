using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Before/after viewer of a character remaster (Docs/remaster-pipeline.md, built by the editor's
    /// G3RemasterTestScene): the original (A) and the remastered (B) character stand on the same turntable; the talk
    /// variants wear the facial head and cycle the lip-sync visemes. Play mode keys: Tab = A/B, 1–9 = clip,
    /// C = camera (1 m / 5 m / 30 m), Space = pause the turntable. The editor capture poses the same objects directly.
    /// </summary>
    public sealed class G3RemasterPreview : MonoBehaviour
    {
        [Serializable]
        public sealed class TestClip
        {
            public string label, clip;
            [Tooltip("Normalized time of the before/after still")] public float stillTime = 0.5f;
            [Tooltip("Use the talk variants (facial head) and animate the visemes")] public bool talk;
        }

        public GameObject original, remaster, originalTalk, remasterTalk;
        public TestClip[] clips = Array.Empty<TestClip>();
        public Camera[] cameras = Array.Empty<Camera>();
        [Tooltip("Lip-sync poses of the facial head's face bones (index = viseme)")] public G3FaceKit.Track[] visemes = Array.Empty<G3FaceKit.Track>();
        public int visemeCount;
        public float turnDegreesPerSecond = 20;
        public float visemesPerSecond = 8;
        public bool showRemaster;
        public int clipIndex;
        public int cameraIndex = 1;
        public bool spin = true;

        int _playing = -1;
        bool _shownRemaster;

        void Start() => Apply(true);

        void Update()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb != null)
            {
                if (kb.tabKey.wasPressedThisFrame) showRemaster = !showRemaster;
                if (kb.cKey.wasPressedThisFrame && cameras.Length > 0) cameraIndex = (cameraIndex + 1) % cameras.Length;
                if (kb.spaceKey.wasPressedThisFrame) spin = !spin;
                for (int i = 0; i < Math.Min(9, clips.Length); i++)
                    if (kb[Key.Digit1 + i].wasPressedThisFrame) clipIndex = i;
            }
            if (spin) transform.Rotate(0, turnDegreesPerSecond * Time.deltaTime, 0, Space.World);
            Apply(false);
        }

        void LateUpdate()
        {
            var c = Current;
            if (c == null || !c.talk || visemeCount <= 0) return;
            float f = Time.time * visemesPerSecond;
            int a = (int)f % visemeCount, b = (a + 1) % visemeCount;
            PoseViseme(Active, a, b, f - Mathf.Floor(f));
        }

        TestClip Current => clipIndex >= 0 && clipIndex < clips.Length ? clips[clipIndex] : null;

        /// <summary>The character shown for the current clip and A/B choice.</summary>
        public GameObject Active => Pick(showRemaster, Current != null && Current.talk);

        public GameObject Pick(bool remastered, bool talk)
        {
            var go = talk ? (remastered ? remasterTalk : originalTalk) : (remastered ? remaster : original);
            return go != null ? go : (remastered ? remaster : original);
        }

        void Apply(bool force)
        {
            if (!force && _playing == clipIndex && _shownRemaster == showRemaster)
            {
                foreach (var cam in cameras) if (cam != null) cam.enabled = cam == cameras[Mathf.Clamp(cameraIndex, 0, cameras.Length - 1)];
                return;
            }
            var active = Active;
            foreach (var go in new[] { original, remaster, originalTalk, remasterTalk })
                if (go != null) go.SetActive(go == active);
            var c = Current;
            var anim = active != null ? active.GetComponent<Animation>() : null;
            if (anim != null && c != null && anim.GetClip(c.clip) != null)
            {
                anim[c.clip].wrapMode = WrapMode.Loop;
                anim.Play(c.clip);
            }
            _playing = clipIndex;
            _shownRemaster = showRemaster;
            for (int i = 0; i < cameras.Length; i++) if (cameras[i] != null) cameras[i].enabled = i == Mathf.Clamp(cameraIndex, 0, cameras.Length - 1);
        }

        /// <summary>Poses the face bones of <paramref name="character"/> between visemes a and b.</summary>
        public void PoseViseme(GameObject character, int a, int b, float t)
        {
            if (character == null || visemes == null) return;
            var byName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in character.GetComponentsInChildren<Transform>(true)) byName[x.name] = x;
            foreach (var track in visemes)
            {
                if (!byName.TryGetValue(track.bone, out var bone)) continue;
                int ia = Mathf.Clamp(a, 0, track.positions.Length - 1), ib = Mathf.Clamp(b, 0, track.positions.Length - 1);
                bone.SetLocalPositionAndRotation(Vector3.Lerp(track.positions[ia], track.positions[ib], t),
                                                 Quaternion.Slerp(track.rotations[ia], track.rotations[ib], t));
            }
        }
    }
}
