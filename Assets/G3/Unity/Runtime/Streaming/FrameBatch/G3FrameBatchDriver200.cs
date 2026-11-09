using UnityEngine;

namespace G3.Unity
{
    /// <summary>Steps the <see cref="G3FrameBatch"/> members of execution order 200 (perf-3).</summary>
    [DefaultExecutionOrder(200)]
    [AddComponentMenu("")]
    public sealed class G3FrameBatchDriver200 : MonoBehaviour
    {
        void Update() => G3FrameBatch.Run(G3FrameBatch.Slot.Update200);
    }
}
