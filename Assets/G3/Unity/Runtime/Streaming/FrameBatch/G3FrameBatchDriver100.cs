using UnityEngine;

namespace G3.Unity
{
    /// <summary>Steps the <see cref="G3FrameBatch"/> members of execution order 100 (perf-3).</summary>
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("")]
    public sealed class G3FrameBatchDriver100 : MonoBehaviour
    {
        void Update() => G3FrameBatch.Run(G3FrameBatch.Slot.Update100);
    }
}
