using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Eye blinks on a head with ARKit blend shapes (the MetaHuman hero head, Docs/remaster-metahuman.md §5.3): drives
    /// <c>eyeBlinkLeft</c> / <c>eyeBlinkRight</c> from a <see cref="BlinkSchedule"/>. G3 heads have no blink; this is
    /// added by <see cref="G3CharacterRemaster"/> to a swapped head whose mesh has the shapes and removed when the swap is
    /// undone. Does nothing while the renderer is hidden (first person) or the mesh lacks the shapes.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(310)] // after G3TalkingFace (300)
    public sealed class G3FaceBlink : MonoBehaviour
    {
        SkinnedMeshRenderer _smr;
        Mesh _mesh;
        int _left = -1, _right = -1;
        BlinkSchedule _schedule;

        /// <summary>Adds the blinker to <paramref name="head"/> when its mesh has blink shapes; returns it or null.</summary>
        public static G3FaceBlink Ensure(SkinnedMeshRenderer head)
        {
            if (head == null || head.sharedMesh == null || head.sharedMesh.blendShapeCount == 0) return null;
            if (Index(head.sharedMesh, "eyeBlinkLeft") < 0 && Index(head.sharedMesh, "eyeBlinkRight") < 0) return null;
            var b = head.GetComponent<G3FaceBlink>();
            return b != null ? b : head.gameObject.AddComponent<G3FaceBlink>();
        }

        public static void Remove(Renderer head)
        {
            if (head == null) return;
            var b = head.GetComponent<G3FaceBlink>();
            if (b != null) Destroy(b);
        }

        static int Index(Mesh mesh, string shape)
        {
            var names = new string[mesh.blendShapeCount];
            for (int i = 0; i < names.Length; i++) names[i] = mesh.GetBlendShapeName(i);
            return ArkitShapes.Find(names, shape);
        }

        void OnEnable()
        {
            _smr = GetComponent<SkinnedMeshRenderer>();
            _schedule = new BlinkSchedule((uint)(Random.Range(1, int.MaxValue)), Time.time);
            _mesh = null;
        }

        void LateUpdate()
        {
            if (_smr == null || !_smr.enabled) return;
            if (_smr.sharedMesh != _mesh)
            {
                _mesh = _smr.sharedMesh;
                _left = _mesh != null ? Index(_mesh, "eyeBlinkLeft") : -1;
                _right = _mesh != null ? Index(_mesh, "eyeBlinkRight") : -1;
            }
            if (_left < 0 && _right < 0) return;
            float w = _schedule.Sample(Time.time) * 100f;
            if (_left >= 0) _smr.SetBlendShapeWeight(_left, w);
            if (_right >= 0) _smr.SetBlendShapeWeight(_right, w);
        }

        void OnDisable()
        {
            if (_smr == null || _smr.sharedMesh == null) return;
            if (_left >= 0 && _left < _smr.sharedMesh.blendShapeCount) _smr.SetBlendShapeWeight(_left, 0);
            if (_right >= 0 && _right < _smr.sharedMesh.blendShapeCount) _smr.SetBlendShapeWeight(_right, 0);
        }
    }
}
