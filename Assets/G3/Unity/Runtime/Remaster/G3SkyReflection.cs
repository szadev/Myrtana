using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Sky reflection cubemap of the world remaster (Docs/remaster-world.md 11.9, Lighting part, JSON <c>v2SkyReflection</c>).
    /// G3's sky (<c>G3/Sky</c>: day colours, sun, the v2 cloud slab) changes every frame, so the scene's baked skybox
    /// reflection never matched it and URP's flat glossy colour stood in. Here the skybox material is drawn into a small
    /// HDR cube render texture, one face per frame every <c>v2SkyReflectionInterval</c> seconds (box-filtered mips for
    /// rough surfaces), and set as the custom environment reflection (URP's <c>_GlossyEnvironmentCubeMap</c> fallback:
    /// water, armour, every PBR material without a probe) and as the global <c>_G3SkyCube</c> of the v2 surfaces (wet
    /// stone, metal). Off restores the scene's reflection mode and texture; nothing else changes.
    /// </summary>
    public static class G3SkyReflection
    {
        static readonly int CubeId = Shader.PropertyToID("_G3SkyCube");
        static readonly int ParamsId = Shader.PropertyToID("_G3SkyReflection");

        static bool _on;
        static RenderTexture _cube;
        static CommandBuffer _cb;
        static Mesh _mesh;
        static int _face;
        static float _next;
        static bool _complete;
        static bool _saved;
        static DefaultReflectionMode _prevMode;
        static Texture _prevTexture;

        /// <summary>Flips the face projection (render-into-texture convention); runner switch for the orientation check.</summary>
        public static bool FlipY = true;

        /// <summary>True while the cube is rendered and used.</summary>
        public static bool Active => _on;

        /// <summary>The cube (null while off).</summary>
        public static RenderTexture Cube => _cube;

        /// <summary>Turns the sky reflection on or off (G3WorldV2.Apply: Lighting part and the JSON toggle).</summary>
        public static void Apply(bool on)
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            var c = WorldRemasterLook.Current;
            on &= c.v2 && c.v2SkyReflection && Application.isPlaying;
            if (!on)
            {
                if (_on) Restore();
                _on = false;
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                return;
            }
            int size = Mathf.ClosestPowerOfTwo(c.v2SkyReflectionSize);
            if (_cube == null || _cube.width != size)
            {
                Release();
                _cube = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "G3 Sky Reflection", dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = false,
                    filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
                };
                _cube.Create();
                _complete = false;
                _face = 0;
            }
            _cb ??= new CommandBuffer { name = "G3 Sky Reflection" };
            if (_mesh == null) _mesh = BuildCube();
            _on = true;
            _next = 0;
            RenderPipelineManager.beginContextRendering += OnBeginContext;
        }

        /// <summary>One line for the runner.</summary>
        public static string Describe() => _on ? $"sky reflection {(_cube != null ? _cube.width : 0)}² ({(_complete ? "set" : "filling")})" : "sky reflection off";

        static readonly global::Unity.Profiling.ProfilerMarker PmFace = new global::Unity.Profiling.ProfilerMarker("G3SkyReflection.Face");   // fix-171

        static void OnBeginContext(ScriptableRenderContext ctx, System.Collections.Generic.List<Camera> cams)
        {
            if (!_on || _cube == null) return;
            var sky = RenderSettings.skybox;
            if (sky == null) return;
            if (_face == 0 && _complete && Time.unscaledTime < _next) return;
            using var _pm = PmFace.Auto();
            var c = WorldRemasterLook.Current;
            // the cloud slab marches 16 steps per pixel: at 128² a face costs ~1/60 of a 1080p sky; the sky must keep its
            // camera-relative height (G3/Sky reads _WorldSpaceCameraPos): the main camera's position, as the frame sees it
            var cam = Camera.main;
            var eye = cam != null ? cam.transform.position : Vector3.zero;
            _cb.Clear();
            RenderFace(_cb, sky, _face, eye);
            G3GpuDiag.Count("skyrefl.face");
            _face++;
            if (_face == 6)
            {
                _face = 0;
                _cb.GenerateMips(_cube);
                _next = Time.unscaledTime + c.v2SkyReflectionInterval;
                if (!_complete) { _complete = true; Use(); }
            }
            ctx.ExecuteCommandBuffer(_cb);
            _cb.Clear();
        }

        static readonly Vector3[] LookAt = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static readonly Vector3[] Up = { Vector3.up, Vector3.up, Vector3.back, Vector3.forward, Vector3.up, Vector3.up };

        static void RenderFace(CommandBuffer cb, Material sky, int face, Vector3 eye)
        {
            cb.SetRenderTarget(new RenderTargetIdentifier(_cube, 0, (CubemapFace)face, 0));
            cb.ClearRenderTarget(false, true, Color.black);
            // DirectX cube face convention (CoreUtils.lookAtList / upVectorList); Unity views look down -z
            var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(eye, Quaternion.LookRotation(LookAt[face], Up[face]), Vector3.one).inverse;
            var proj = GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(90, 1, 0.1f, 100f), FlipY);
            cb.SetViewProjectionMatrices(view, proj);
            cb.DrawMesh(_mesh, Matrix4x4.TRS(eye, Quaternion.identity, Vector3.one * 10f), sky, 0, 0);
        }

        static void Use()
        {
            if (!_saved)
            {
                _prevMode = RenderSettings.defaultReflectionMode;
                _prevTexture = RenderSettings.customReflectionTexture;
                _saved = true;
            }
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = _cube;
            Shader.SetGlobalTexture(CubeId, _cube);
            Shader.SetGlobalVector(ParamsId, new Vector4(1, Mathf.Log(_cube.width, 2), 0, 0));
        }

        static void Restore()
        {
            if (_saved)
            {
                RenderSettings.customReflectionTexture = _prevTexture;
                RenderSettings.defaultReflectionMode = _prevMode;
                _saved = false;
            }
            _complete = false;
            _face = 0;
        }

        static void Release()
        {
            if (_cube != null) { _cube.Release(); Object.Destroy(_cube); _cube = null; }
        }

        /// <summary>A unit cube seen from inside (the skybox shader takes the object-space position as the view direction).</summary>
        static Mesh BuildCube()
        {
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1);
            int[] quads = { 0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5 };
            var t = new int[36];
            for (int q = 0; q < 6; q++)
            {
                int a = quads[q * 4], b = quads[q * 4 + 1], cc = quads[q * 4 + 2], d = quads[q * 4 + 3];
                t[q * 6] = a; t[q * 6 + 1] = b; t[q * 6 + 2] = cc; t[q * 6 + 3] = a; t[q * 6 + 4] = cc; t[q * 6 + 5] = d;
            }
            var m = new Mesh { name = "G3 Sky Reflection Cube", vertices = v, triangles = t, hideFlags = HideFlags.DontSave };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return m;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            _on = false; _saved = false; _complete = false; _face = 0;
            _cube = null; _cb = null; _mesh = null;
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
        }
    }
}
