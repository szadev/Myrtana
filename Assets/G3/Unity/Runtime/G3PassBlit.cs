using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// dx12-2: full-screen draw of a shared material whose per-pass values travel with the draw. A render-graph render
    /// function runs while the frame is recorded, the draws it records execute later: a <c>Material.SetVector</c> there
    /// changes the material at once, so every draw of that material in the frame (or, with split graphics jobs, the
    /// previous frame's draws still in flight) saw the value set last (exposure chain sizes, bounce texels, ping-pong
    /// history textures). The values go into a property block that the command buffer copies at record time instead.
    /// Same draw as <see cref="Blitter.BlitTexture(RasterCommandBuffer, RTHandle, Vector4, Material, int)"/> (full-screen
    /// triangle, <c>_BlitTexture</c>, <c>_BlitScaleBias</c> 1,1,0,0, mip 0).
    /// </summary>
    public static class G3PassBlit
    {
        static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
        static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        static readonly int BlitMipLevelId = Shader.PropertyToID("_BlitMipLevel");
        static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();

        /// <summary>Starts the values of one draw (cleared block with the source bound); set the pass's values on it, then <see cref="Draw"/>.</summary>
        public static MaterialPropertyBlock Begin(RTHandle source)
        {
            Block.Clear();
            Block.SetTexture(BlitTextureId, source);
            Block.SetVector(BlitScaleBiasId, new Vector4(1, 1, 0, 0));
            Block.SetFloat(BlitMipLevelId, 0);
            return Block;
        }

        /// <summary>Records the full-screen triangle with the values set since <see cref="Begin"/> (copied now).</summary>
        public static void Draw(RasterCommandBuffer cmd, Material material, int pass) =>
            cmd.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, 3, 1, Block);
    }
}
