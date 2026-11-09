using System;

namespace G3.Formats.World
{
    /// <summary>
    /// Gothic 3's magic barrier: the dome over Vengard (Docs/formats-world.md §7.6, fix-229). One dynamic entity of the
    /// world's SysDyn archive, <c>G3_Myrtana_Vanguard_Magic_Barrier_01</c>: eCVisualMeshDynamic_PS (a two-shell sphere,
    /// radius 180 m, centre 48 m below ground), eCCollisionShape_PS (its triangle mesh: nobody walks in or out, the city is
    /// reached by teleport) and gCMagicBarrier_PS (Shape 0 sphere, Thickness 500 cm, scripts OnEnter/OnLeave/OnIn
    /// MagicBarrier and OnToucheBarrier). The distant world has a _LOWPOLY copy in G3_World_Lowpoly_01_Levelmesh_01.
    /// <para>Story state: none. No info, quest or script names the entity (Infos.pak / Quests.pak / Script_Game.dll; the
    /// Karrypto_BarrierThere game event is dialogue only), it sits in no story sector, and nothing switches its Enabled flag:
    /// in G3 the barrier stands for the whole game. So it is shown exactly when the entity is enabled and rendered.</para>
    /// </summary>
    public static class MagicBarrier
    {
        /// <summary>Property set that makes an entity a magic barrier.</summary>
        public const string PropertySet = "gCMagicBarrier_PS";

        /// <summary>The Vengard barrier entity (also its mesh stem).</summary>
        public const string VengardEntity = "G3_Myrtana_Vanguard_Magic_Barrier_01";

        /// <summary>The barrier's shipped material.</summary>
        public const string MaterialName = "G3_Special_Magic_Barrier_01";

        /// <summary>Unity shader that renders <see cref="MaterialName"/> (Assets/G3/Unity/Shaders/Effects/G3MagicBarrier.shader).</summary>
        public const string ShaderName = "G3/MagicBarrier";

        /// <summary>
        /// True for the barrier dome's materials (G3_Special_Magic_Barrier_01, the only one any mesh uses). The
        /// G3_Special_Magic_Barrier_0x images alone are textures, not materials.
        /// </summary>
        public static bool IsBarrierMaterial(string materialName) =>
            !string.IsNullOrEmpty(materialName) &&
            materialName.IndexOf("_Magic_Barrier_", StringComparison.OrdinalIgnoreCase) >= 0 &&
            materialName.StartsWith("G3_Special_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The G3 visibility rule: the entity's own Enabled and rendering flags (data), never a story flag (see the class
        /// remarks). The dome is up from the first frame of a new game to the end.
        /// </summary>
        public static bool Shown(bool entityEnabled, bool renderingEnabled) => entityEnabled && renderingEnabled;

        /// <summary><see cref="Shown(bool, bool)"/> for a loaded entity.</summary>
        public static bool Shown(WorldEntity e) => e != null && Shown(e.Enabled, e.RenderingEnabled);
    }
}
