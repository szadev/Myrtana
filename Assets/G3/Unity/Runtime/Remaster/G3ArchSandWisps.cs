using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// arch-2 (Docs/remaster-world.md 25.7): sand blown off tent and awning edges after a sandstorm. While the storm sand
    /// deposit (<see cref="ArchSand"/>) is above 0.4 and the wind is up, each cloth arch object near the camera (v2 material
    /// with <c>_V2Arch.z</c> = 6, found by a physics overlap once a second, at most <see cref="MaxEmitters"/>) sheds
    /// <c>archSandWisps</c> short sand ribbons from its lee-side rim, drifting downwind and fading over 1-2.5 s. One procedural
    /// draw through sand-3's <see cref="G3SandFx"/> camera pass (Resources/G3ArchSandWisps.shader, vertices from SV_VertexID):
    /// no buffers, no particles; at most 8 x 32 ribbons.
    /// </summary>
    public static class G3ArchSandWisps
    {
        public const int MaxEmitters = 8;
        const float Radius = 35f;

        static readonly int EmitId = Shader.PropertyToID("_G3ArchWispEmit");
        static readonly int SizeId = Shader.PropertyToID("_G3ArchWispSize");
        static readonly int ParamId = Shader.PropertyToID("_G3ArchWisp0");
        static readonly int V2ArchId = Shader.PropertyToID("_V2Arch");

        static Material _mat;
        static bool _missing;
        static float _shown, _nextScan;
        static readonly Collider[] _cols = new Collider[256];
        static readonly List<(float d, Bounds b)> _found = new List<(float, Bounds)>(32);
        static readonly Vector4[] _emit = new Vector4[MaxEmitters], _size = new Vector4[MaxEmitters];
        static int _count;

        public static string Describe() => _shown > 0.001f ? $"tent wisps {_shown:0.00} x {_count} tents" : "tent wisps off";

        /// <summary>Per environment tick (<see cref="G3WorldV2"/>): eases the amount and draws.</summary>
        internal static void Tick(Camera cam, float amount)
        {
            var c = WorldRemasterLook.Current;
            if (cam == null || c.archSandWisps <= 0) { _shown = 0; return; }
            var eye = cam.transform.position;
            amount *= G3SnowV2.DesertAt(eye.x, eye.z);
            _shown = Mathf.MoveTowards(_shown, amount, Mathf.Max(Time.deltaTime, 0) * 0.5f);
            if (_shown < 0.01f) return;
            if (Time.time >= _nextScan) { _nextScan = Time.time + 1f; Scan(eye); }
            if (_count == 0) return;
            if (_mat == null && !_missing)
            {
                var sh = Resources.Load<Shader>("G3ArchSandWisps");
                if (sh == null || !sh.isSupported) { _missing = true; Debug.LogWarning("[G3ArchSandWisps] shader missing/unsupported: no tent wisps"); return; }
                _mat = new Material(sh) { name = "G3 Arch Sand Wisps", hideFlags = HideFlags.DontSave };
            }
            if (_mat == null) return;
            var w = G3Wind.Active;
            Vector3 dir;
            if (w != null) dir = Quaternion.Euler(0, w.azimuth, 0) * Vector3.forward;
            else { var (x, z) = SandV2.WindDir(c.sandWindAngle); dir = new Vector3(x, 0, z); }
            float speed = 2f + 5f * Mathf.Clamp01(G3SandV2.Wind) + 5f * G3SandV2.Storm;
            int per = Mathf.Min(c.archSandWisps, 32);
            _mat.SetVectorArray(EmitId, _emit);
            _mat.SetVectorArray(SizeId, _size);
            // x amount, y wisps per emitter, z emitters, w time; _1: wind x z, speed, peak opacity
            _mat.SetVector(ParamId, new Vector4(_shown, per, _count, Time.time));
            _mat.SetVector("_G3ArchWisp1", new Vector4(dir.x, dir.z, speed, c.archSandWispOpacity));   // fx-1: own, lower opacity
            G3SandFx.Draw(_mat, _count * per * 6);
        }

        static void Scan(Vector3 eye)
        {
            _found.Clear();
            int n = Physics.OverlapSphereNonAlloc(eye, Radius, _cols, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var r = _cols[i].GetComponent<Renderer>() ?? _cols[i].GetComponentInParent<Renderer>();
                if (r == null || !r.enabled) continue;
                var m = r.sharedMaterial;
                if (m == null || !m.HasProperty(V2ArchId) || Mathf.Abs(m.GetVector(V2ArchId).z - 6f) > 0.5f) continue;
                var b = r.bounds;
                if (b.size.y < 0.8f || b.size.magnitude > 60f) continue;
                bool dup = false;
                foreach (var f in _found) if ((f.b.center - b.center).sqrMagnitude < 0.01f) { dup = true; break; }
                if (!dup) _found.Add(((b.center - eye).sqrMagnitude, b));
            }
            _found.Sort((a, b) => a.d.CompareTo(b.d));
            _count = Mathf.Min(_found.Count, MaxEmitters);
            for (int i = 0; i < MaxEmitters; i++)
            {
                if (i < _count)
                {
                    var b = _found[i].b;
                    _emit[i] = new Vector4(b.center.x, b.max.y, b.center.z, b.min.y);
                    _size[i] = new Vector4(b.extents.x, b.extents.y, b.extents.z, i * 7.31f);
                }
                else _emit[i] = _size[i] = Vector4.zero;
            }
        }

        internal static void Release()
        {
            if (_mat != null) Object.Destroy(_mat);
            _mat = null;
            _shown = 0;
            _count = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _mat = null; _missing = false; _shown = 0; _count = 0; _nextScan = 0; }
    }
}
