using UnityEngine;

namespace G3.Unity
{
    /// <summary>Steps the <see cref="G3FrameBatch"/> members of execution order 150 (perf-3).</summary>
    [DefaultExecutionOrder(150)]
    [AddComponentMenu("")]
    public sealed class G3FrameBatchDriver150 : MonoBehaviour
    {
        void Update() => G3FrameBatch.Run(G3FrameBatch.Slot.Update150);
    }
}
