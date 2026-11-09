using UnityEngine;

namespace G3.Unity
{
    /// <summary>Steps the <see cref="G3FrameBatch"/> members of execution order 0 (perf-3).</summary>
    [DefaultExecutionOrder(0)]
    [AddComponentMenu("")]
    public sealed class G3FrameBatchDriver0 : MonoBehaviour
    {
        void Update() => G3FrameBatch.Run(G3FrameBatch.Slot.Update0);
        void LateUpdate() => G3FrameBatch.Run(G3FrameBatch.Slot.LateUpdate0);
    }
}
