using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// fx-1 (Docs/remaster-world.md 22.11): the covered-ground mask round the camera (<see cref="CoveredMap"/>): 128 x 128 points at
    /// 1 m, one batched ray straight down per point (RaycastCommand on the job workers) compared with the relief's terrain
    /// collision; rebuilt when the camera moved 8 m or every 4 s while the desert sand is on. Globals <c>_G3CoveredMask</c> (R8,
    /// 255 = under a roof / tent / awning / overhang / indoors) and <c>_G3CoveredP</c> = (x0, z0, 1 / texel, N; N = 0: none) -
    /// shared with the arch shaders (arch-4: no sand indoors). CPU: <see cref="At"/> (footstep dust gate).
    /// </summary>
    public static class G3CoveredMap
    {
        public const int N = 128;
        public const float Texel = 1f;

        static readonly int MaskId = Shader.PropertyToID("_G3CoveredMask"), ParId = Shader.PropertyToID("_G3CoveredP");
        static Texture2D _tex;
        static byte[] _map;
        static float _x0, _z0;
        static Vector3 _centre = new Vector3(float.NaN, 0, float.NaN);
        static float _next;
        static NativeArray<RaycastCommand> _cmds;
        static NativeArray<RaycastHit> _hits;
        static JobHandle _job;
        static bool _pending;
        static float _px0, _pz0;
        static readonly float[] _top = new float[N * N], _ground = new float[N * N];

        /// <summary>Covered (0..1) at (x, z); 0 where the mask was not built.</summary>
        public static float At(float x, float z) => CoveredMap.At(_map, N, _x0, _z0, Texel, x, z);

        public static string Describe()
        {
            if (_map == null) return "covered map none";
            int c = 0;
            foreach (var b in _map) if (b != 0) c++;
            return $"covered map {N}x{N} @ ({_x0:0},{_z0:0}): {100f * c / _map.Length:0.0} % covered";
        }

        /// <summary>Per frame from <see cref="G3SandV2.Tick"/>: finishes a batch, schedules the next when due.</summary>
        internal static void Tick(Camera cam)
        {
            if (cam == null) return;
            if (_pending)
            {
                if (!_job.IsCompleted) return;
                _job.Complete();
                Finish();
            }
            var p = cam.transform.position;
            bool moved = float.IsNaN(_centre.x) || new Vector2(p.x - _centre.x, p.z - _centre.z).sqrMagnitude > 64f;
            if (!moved && Time.unscaledTime < _next) return;
            if (!G3TerrainRelief.TryGround(p, out _, out _)) return;   // needs the relief's terrain collision
            Schedule(p);
        }

        static void Schedule(Vector3 p)
        {
            _centre = p;
            _next = Time.unscaledTime + 4f;
            float half = (N - 1) * Texel * 0.5f;
            _px0 = Mathf.Round((p.x - half) / Texel) * Texel;
            _pz0 = Mathf.Round((p.z - half) / Texel) * Texel;
            if (!_cmds.IsCreated) { _cmds = new NativeArray<RaycastCommand>(N * N, Allocator.Persistent); _hits = new NativeArray<RaycastHit>(N * N, Allocator.Persistent); }
            var qp = new QueryParameters(Physics.DefaultRaycastLayers & ~(1 << 4) & ~(1 << 2), false, QueryTriggerInteraction.Ignore, false);
            float y = p.y + 120f;
            for (int j = 0, k = 0; j < N; j++)
                for (int i = 0; i < N; i++, k++)
                    _cmds[k] = new RaycastCommand(new Vector3(_px0 + i * Texel, y, _pz0 + j * Texel), Vector3.down, qp, 400f);
            _job = RaycastCommand.ScheduleBatch(_cmds, _hits, 64, 1);
            _pending = true;
        }

        static void Finish()
        {
            _pending = false;
            for (int k = 0; k < N * N; k++)
            {
                var h = _hits[k];
                if (h.collider == null) { _top[k] = _ground[k] = float.NaN; continue; }
                float top = h.point.y;
                _top[k] = top;
                var q = new Vector3(_px0 + (k % N) * Texel, top, _pz0 + (k / N) * Texel);
                _ground[k] = G3TerrainRelief.TryGround(q, out float col, out _) ? col : float.NaN;
            }
            _map ??= new byte[N * N];
            CoveredMap.Build(_top, _ground, N, _map);
            _x0 = _px0; _z0 = _pz0;
            if (_tex == null)
                _tex = new Texture2D(N, N, TextureFormat.R8, false, true)
                {
                    name = "G3CoveredMask", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave,
                };
            _tex.SetPixelData(_map, 0);
            _tex.Apply(false, false);
            Shader.SetGlobalTexture(MaskId, _tex);
            Shader.SetGlobalVector(ParId, new Vector4(_x0, _z0, 1f / Texel, N));
        }

        internal static void Release()
        {
            if (_pending) { _job.Complete(); _pending = false; }
            if (_cmds.IsCreated) _cmds.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            if (_tex != null) Object.Destroy(_tex);
            _tex = null; _map = null;
            _centre = new Vector3(float.NaN, 0, float.NaN);
            Shader.SetGlobalVector(ParId, Vector4.zero);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _pending = false; _tex = null; _map = null; _centre = new Vector3(float.NaN, 0, float.NaN); _next = 0;
            Shader.SetGlobalVector(ParId, Vector4.zero);
        }
    }
}
