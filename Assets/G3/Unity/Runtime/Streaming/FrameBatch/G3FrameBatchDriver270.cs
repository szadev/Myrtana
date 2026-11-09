using UnityEngine;

namespace G3.Unity
{
    /// <summary>Steps the <see cref="G3FrameBatch"/> members of execution order 270 (perf-3).</summary>
    [DefaultExecutionOrder(270)]
    [AddComponentMenu("")]
    public sealed class G3FrameBatchDriver270 : MonoBehaviour
    {
        void LateUpdate() => G3FrameBatch.Run(G3FrameBatch.Slot.LateUpdate270);
    }
}
