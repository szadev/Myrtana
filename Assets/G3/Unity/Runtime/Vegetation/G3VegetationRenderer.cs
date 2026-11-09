using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Draws a <see cref="G3VegetationData"/> region with GPU instancing (Graphics.RenderMeshInstanced),
    /// per camera, with per-chunk distance and frustum culling. The G3/Vegetation shader fades plants out
    /// (dithered) over the last part of the view range and applies the per-instance colour.
    /// Works in Edit mode (Scene and Game view) and in Play mode.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class G3VegetationRenderer : MonoBehaviour
    {
        public G3VegetationData data;

        [Tooltip("Multiplier on the original eCVegetation_PS ViewRange (50 m in the whole game).")]
        [Min(0.05f)] public float viewDistanceScale = 1f;

        /// <summary>
        /// Performance options (G3 "Vegetation view range" / "Vegetation quality", set by the game's options): a factor on
        /// every renderer's view range, plants drawn at all, and whether tall plants may cast shadows.
        /// </summary>
        public static float OptionViewScale = 1f;
        public static bool OptionEnabled = true;
        public static bool OptionShadows = true;
        /// <summary>The options' vegetation quality 0..3 (remaster-veg: scatter density low/medium/high).</summary>
        public static int OptionQuality
        {
            get => G3GroundScatter.VegetationQuality;
            set => G3GroundScatter.VegetationQuality = value;
        }

        float Scale => viewDistanceScale * OptionViewScale;
        [Tooltip("Fraction of the view range over which plants shrink into the ground (Genome: FadeOutStart 25 m of ViewRange 50 m).")]
        [Range(0, 1)] public float fadeFraction = 0.5f;
        [Tooltip("Cast shadows for meshes at least shadowMinHeight tall (bushes). Small grass never casts.")]
        public bool castShadows = false;
        [Min(0)] public float shadowMinHeight = 1.0f;
        public bool receiveShadows = true;
        [Tooltip("Also draw into Scene view cameras.")]
        public bool drawInSceneView = true;

        // Statistics of the last rendered Game camera (for profiling from the editor).
        [NonSerialized] public int lastVisibleChunks, lastDrawCalls, lastInstances;
        [NonSerialized] public float lastCpuMs;
        readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        struct RtBatch
        {
            public int mesh;
            public int count;
            public Matrix4x4[] matrices;
            public MaterialPropertyBlock props;
        }

        RtBatch[] _batches;
        G3VegetationData _builtFrom;
        bool _dirty = true;
        readonly Plane[] _planes = new Plane[6];

        static readonly int InstColorId = Shader.PropertyToID("_InstColor");
        static readonly int FadeId = Shader.PropertyToID("_G3VegFade");

        void OnEnable()
        {
            _dirty = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            G3GrassV2.Forget(this);   // dx12-1: an unloaded region's grass v2 buffers go with it
            _batches = null;
            _builtFrom = null;
        }

        void OnValidate() => _dirty = true;

        /// <summary>crash-2: grass v2 makes, swaps and releases its GPU buffers here, outside the camera callbacks.</summary>
        void LateUpdate()
        {
            if (G3GrassV2.Enabled) G3GrassV2.Prepare(this);
        }

        /// <summary>Total instances in the data asset.</summary>
        public int InstanceCount => data != null ? data.instanceCount : 0;

        public void Rebuild()
        {
            _dirty = false;
            _builtFrom = data;
            _batches = null;
            if (data == null || data.instances == null || data.batches == null) return;

            var bytes = data.instances.bytes;
            int n = bytes.Length / G3VegetationData.InstanceStride;
            var f = new float[n * 10];
            Buffer.BlockCopy(bytes, 0, f, 0, n * G3VegetationData.InstanceStride);

            _batches = new RtBatch[data.batches.Count];
            for (int bi = 0; bi < _batches.Length; bi++)
            {
                var b = data.batches[bi];
                var rb = new RtBatch { mesh = b.mesh, count = b.count, matrices = new Matrix4x4[b.count], props = new MaterialPropertyBlock() };
                var colors = new Vector4[b.count];
                for (int i = 0; i < b.count; i++)
                {
                    int o = (b.start + i) * 10;
                    var pos = new Vector3(f[o], f[o + 1], f[o + 2]);
                    var rot = new Quaternion(f[o + 3], f[o + 4], f[o + 5], f[o + 6]);
                    float w = f[o + 7], h = f[o + 8];
                    float lenSq = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                    if (lenSq < 1e-6f) rot = Quaternion.identity; else if (Mathf.Abs(lenSq - 1) > 1e-3f) rot = rot.normalized;
                    rb.matrices[i] = Matrix4x4.TRS(pos, rot, new Vector3(w, h, w));
                    uint c = BitConverter.ToUInt32(bytes, (b.start + i) * G3VegetationData.InstanceStride + 36);
                    colors[i] = new Vector4(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f, (c >> 24) / 255f);
                }
                rb.props.SetVectorArray(InstColorId, colors);
                _batches[bi] = rb;
            }
            ApplyFadeParams();
        }

        void ApplyFadeParams()
        {
            if (_batches == null) return;
            for (int bi = 0; bi < _batches.Length; bi++)
            {
                var m = data.meshes[_batches[bi].mesh];
                float end = Mathf.Max(1, m.viewRange) * Scale;
                float start = end * (1 - fadeFraction);
                _batches[bi].props.SetVector(FadeId, new Vector4(start, 1f / Mathf.Max(0.01f, end - start), end, 0));
            }
        }

        float _appliedScale = -1, _appliedFade = -1;

        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!isActiveAndEnabled || data == null || !OptionEnabled) return;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
            if (cam.cameraType == CameraType.SceneView && !drawInSceneView) return;
            if ((cam.cullingMask & (1 << gameObject.layer)) == 0) return;
            if (G3GrassV2.TryDraw(this, cam, ctx)) return;   // world remaster v2: GPU-indirect, denser grass (off by default)

            if (_dirty || _builtFrom != data || _batches == null) Rebuild();
            if (_batches == null) return;
            if (_appliedScale != Scale || _appliedFade != fadeFraction)
            {
                ApplyFadeParams();
                _appliedScale = Scale; _appliedFade = fadeFraction;
            }

            _sw.Restart();
            GeometryUtility.CalculateFrustumPlanes(cam, _planes);
            var camPos = cam.transform.position;
            int chunks = 0, draws = 0, inst = 0;

            // The material constructor fills in the defaults (renderingLayerMask etc.); a default(RenderParams) has
            // renderingLayerMask 0, which URP filters out entirely.
            var rp = new RenderParams(data.meshes.Count > 0 ? data.meshes[0].material : null)
            {
                camera = cam,
                layer = gameObject.layer,
                receiveShadows = receiveShadows,
                lightProbeUsage = LightProbeUsage.BlendProbes,
                motionVectorMode = G3WorldV2.PlantMotionMode,   // remaster TAA: wind motion vectors (11.7)
            };

            var meshes = data.meshes;
            foreach (var ch in data.chunks)
            {
                float maxRange = ch.viewRange * Scale;
                float distSq = ch.bounds.SqrDistance(camPos);
                if (distSq > maxRange * maxRange) continue;
                if (!GeometryUtility.TestPlanesAABB(_planes, ch.bounds)) continue;
                chunks++;
                rp.worldBounds = ch.bounds;
                for (int bi = ch.firstBatch, end = ch.firstBatch + ch.batchCount; bi < end; bi++)
                {
                    ref var b = ref _batches[bi];
                    var me = meshes[b.mesh];
                    if (me.mesh == null || me.material == null) continue;
                    float r = me.viewRange * Scale;
                    if (distSq > r * r) continue;
                    rp.material = G3WorldRemaster.Resolve(me.material);   // world remaster override (off by default)
                    rp.matProps = b.props;
                    rp.shadowCastingMode = castShadows && OptionShadows && me.maxHeight >= shadowMinHeight ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    for (int sm = 0; sm < me.mesh.subMeshCount; sm++)
                        Graphics.RenderMeshInstanced(rp, me.mesh, sm, b.matrices, b.count);
                    draws++;
                    inst += b.count;
                }
            }
            if (cam.cameraType == CameraType.Game)
            {
                lastVisibleChunks = chunks; lastDrawCalls = draws; lastInstances = inst;
                lastCpuMs = (float)_sw.Elapsed.TotalMilliseconds;
            }
        }

        void OnDrawGizmosSelected()
        {
            if (data == null) return;
            Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.35f);
            foreach (var ch in data.chunks) Gizmos.DrawWireCube(ch.bounds.center, ch.bounds.size);
        }
    }
}
