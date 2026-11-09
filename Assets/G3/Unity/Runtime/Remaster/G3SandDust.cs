using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// remaster-sand (sand-2, Docs/remaster-world.md 22.6): small dust puffs kicked up by footsteps on desert sand - each sand
    /// print (<see cref="G3SnowV2"/>'s SandPrint) adds a puff to a ring of <see cref="Max"/>; one procedural draw (G3SandFx's camera pass) of three soft
    /// billboards per live puff (Resources/G3SandDust.shader, built from SV_VertexID: no particles, no meshes), growing and
    /// drifting downwind with G3Wind for ~1.4 s while they fade, soft against the depth. Colour = G3's loose sand image (its
    /// mean), lit like the wisps. Amount json <c>sandDust</c> (0 = off).
    /// </summary>
    public static class G3SandDust
    {
        /// <summary>Puffs alive at most (ring; the oldest is replaced).</summary>
        public const int Max = 64;

        /// <summary>Seconds a puff lives (fx-1: json sandDustLife, 0.8 s - quick settle; sand-3 had 1.8 s).</summary>
        public static float Life => WorldRemasterLook.Current.sandDustLife;

        static readonly int PuffId = Shader.PropertyToID("_G3SandPuff");
        static readonly int Puff2Id = Shader.PropertyToID("_G3SandPuff2");
        static readonly int DustId = Shader.PropertyToID("_G3SandDust0"), Dust1Id = Shader.PropertyToID("_G3SandDust1");

        static readonly Vector4[] _puff = new Vector4[Max], _puff2 = new Vector4[Max];
        static int _next;
        static float _lastBirth = -100;
        static Material _mat;
        static bool _missing;

        /// <summary>
        /// A puff at a footprint (<paramref name="pos"/> on the ground); <paramref name="size"/> 1 = a walking boot;
        /// <paramref name="speed"/> = the body's speed (m/s): fx-1 - a walk barely shows, a sprint a little more
        /// (<see cref="SandV2.DustBySpeed"/>).
        /// </summary>
        public static void Puff(Vector3 pos, float size, float speed = 4f)
        {
            var look = WorldRemasterLook.Current;
            float amount = look.sandDust * SandV2.DustBySpeed(speed, look.sandDustWalk);   // 0 standing / dancing in place
            if (amount <= 0 || !Application.isPlaying) return;
            // only near the camera: the print system steps every character in its 64 m window, and far puffs would push
            // the hero's own out of the ring (sand-2 session: the camp NPCs' steps filled it within a second)
            var cam = Camera.main;
            if (cam == null || (cam.transform.position - pos).sqrMagnitude > 35f * 35f) return;
            var w = G3Wind.Active;
            Vector3 dir = w != null ? Quaternion.Euler(0, w.azimuth, 0) * Vector3.forward : Vector3.forward;
            if (amount < 0.02f) return;
            float drift = 0.25f + 0.6f * Mathf.Clamp01(G3SandV2.Wind) + 1.5f * G3SandV2.Storm;   // m/s drift of the dust
            _puff[_next] = new Vector4(pos.x, pos.y, pos.z, Time.time);
            _puff2[_next] = new Vector4(dir.x * drift, dir.z * drift, Mathf.Clamp(size, 0.3f, 2f) * (0.7f + 0.3f * Mathf.Clamp01(amount)), Mathf.Min(amount, 2f));
            _next = (_next + 1) % Max;
            _lastBirth = Time.time;
        }

        /// <summary>Per frame from <see cref="G3SandV2.Tick"/>: one draw while any puff lives.</summary>
        internal static void Tick(Camera cam)
        {
            if (cam == null || Time.time - _lastBirth > Life + 0.1f) return;
            if (_mat == null && !_missing)
            {
                var sh = Resources.Load<Shader>("G3SandDust");
                if (sh == null || !sh.isSupported) { _missing = true; Debug.LogWarning("[G3SandDust] shader G3SandDust missing/unsupported: no dust puffs"); return; }
                _mat = new Material(sh) { name = "G3 Sand Dust", hideFlags = HideFlags.DontSave };
            }
            if (_mat == null) return;
            _mat.SetVectorArray(PuffId, _puff);
            _mat.SetVectorArray(Puff2Id, _puff2);
            var look = WorldRemasterLook.Current;
            // fx-1: w = top of a puff (m), opacity in the strength
            _mat.SetVector(DustId, new Vector4(Time.time, Life, Max, look.sandDustHeight));
            _mat.SetVector(Dust1Id, new Vector4(look.sandDustOpacity, 0, 0, 0));
            G3SandFx.Draw(_mat, Max * 18);
        }

        internal static void Release()
        {
            if (_mat != null) Object.Destroy(_mat);
            _mat = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _mat = null; _missing = false; _next = 0; _lastBirth = -100;
            for (int i = 0; i < Max; i++) { _puff[i] = new Vector4(0, 0, 0, -100); _puff2[i] = Vector4.zero; }
        }
    }
}
