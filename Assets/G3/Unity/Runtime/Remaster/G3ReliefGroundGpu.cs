using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The relief's ground maps on the GPU (Docs/remaster-world.md section 14): visual minus collision height round the
    /// camera as two RG16 float textures (160 m at 0.31 m, 511 m at 1 m; G = collision height minus a base), for shaders that
    /// place things G3 put on its collision surface (grass) onto the drawn relief: <c>G3ReliefGround.hlsl</c>,
    /// <c>G3ReliefGroundDelta(positionWS)</c>. Globals: <c>_G3ReliefGround0/1</c>, <c>_G3ReliefGroundA/B</c> =
    /// (x0, z0, 1 / texel, N; N = 0: off), <c>_G3ReliefGroundC</c> = (base0, base1).
    /// </summary>
    public static class G3ReliefGroundGpu
    {
        static readonly int Tex0 = Shader.PropertyToID("_G3ReliefGround0"), Tex1 = Shader.PropertyToID("_G3ReliefGround1");
        static readonly int ParA = Shader.PropertyToID("_G3ReliefGroundA"), ParB = Shader.PropertyToID("_G3ReliefGroundB"), ParC = Shader.PropertyToID("_G3ReliefGroundC");
        static readonly int SandTex = Shader.PropertyToID("_G3SandMask0"), SandP = Shader.PropertyToID("_G3SandMaskP");
        static Texture2D _t0, _t1, _sand;

        public static void Upload(ReliefGroundMap g0, ushort[] d0, float base0, ReliefGroundMap g1, ushort[] d1, float base1, byte[] sand0 = null)
        {
            if (sand0 != null && sand0.Length == g0.N * g0.N)
            {
                // fx-1: loose-sand mask on map 0's grid (R8; the sand streamers spawn only on open loose sand)
                if (_sand == null || _sand.width != g0.N)
                {
                    if (_sand != null) Object.Destroy(_sand);
                    _sand = new Texture2D(g0.N, g0.N, TextureFormat.R8, false, true)
                    {
                        name = "G3SandMask0", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave,
                    };
                }
                _sand.SetPixelData(sand0, 0);
                _sand.Apply(false, false);
                Shader.SetGlobalTexture(SandTex, _sand);
                Shader.SetGlobalVector(SandP, new Vector4(1, 0, 0, 0));
            }
            Shader.SetGlobalVector(ParC, new Vector4(base0, base1, 0, 0));
            _t0 = Fill(_t0, g0.N, d0, "G3ReliefGround0");
            _t1 = Fill(_t1, g1.N, d1, "G3ReliefGround1");
            Shader.SetGlobalTexture(Tex0, _t0);
            Shader.SetGlobalTexture(Tex1, _t1);
            Shader.SetGlobalVector(ParA, new Vector4(g0.X0, g0.Z0, 1f / g0.Texel, g0.N));
            Shader.SetGlobalVector(ParB, new Vector4(g1.X0, g1.Z0, 1f / g1.Texel, g1.N));
        }

        public static void Clear()
        {
            Shader.SetGlobalVector(ParA, Vector4.zero);
            Shader.SetGlobalVector(ParB, Vector4.zero);
            Shader.SetGlobalVector(SandP, Vector4.zero);
        }

        static Texture2D Fill(Texture2D t, int n, ushort[] data, string name)
        {
            if (t == null || t.width != n)
            {
                if (t != null) Object.Destroy(t);
                t = new Texture2D(n, n, TextureFormat.RGHalf, false, true)
                {
                    name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave,
                };
            }
            t.SetPixelData(data, 0);
            t.Apply(false, false);
            return t;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _t0 = _t1 = _sand = null;
            Clear();
        }
    }
}
