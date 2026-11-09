namespace G3.Player
{
    /// <summary>
    /// fix-191: at most one safety-net relocation of a body per frame, for all bodies together (no engine types, tested in
    /// Tools/G3Tests). Build 22c: the fix-181 buried guard moved several bodies in one frame right before a native
    /// UnityPlayer crash (the editor crashed in the GPU Resident Drawer's MeshRendererProcessor the same way). A guard that
    /// finds a body to move asks <see cref="TryTake"/>; refused, it keeps its check due and asks again next frame, so the
    /// bodies are served one per frame (a relocated body's guard rests for its period, the next one in line moves).
    /// </summary>
    public sealed class RelocationGate
    {
        long _frame = long.MinValue;

        /// <summary>True for the first caller in <paramref name="frame"/>, false for every later one in the same frame.</summary>
        public bool TryTake(long frame)
        {
            if (frame == _frame) return false;
            _frame = frame;
            return true;
        }

        /// <summary>The gate of the NPC bodies (buried guard, cave guard, deferred sink lift).</summary>
        public static readonly RelocationGate Bodies = new RelocationGate();
    }
}
