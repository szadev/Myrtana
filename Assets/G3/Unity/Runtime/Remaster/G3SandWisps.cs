using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// remaster-sand (Docs/remaster-world.md 22.6, fx-1 22.11): wind-blown sand streamers near the ground - one procedural draw
    /// of up to <c>sandWispCount</c> ground-hugging torn sheets (8 x 2 quads each, built in the vertex shader from SV_VertexID:
    /// Resources/G3SandWisps.shader) on open loose sand only (the relief's sand mask), drifting along G3Wind's live direction
    /// in a camera-centred toroidal box, lying on the drawn ground (the relief ground map, else the ground under the camera),
    /// faded by distance (<c>sandWispRange</c>), near the camera (<c>sandWispNear</c>) and outside the desert. No buffers, no particles, no CPU per wisp. Amount from
    /// <see cref="SandV2.WispAmount"/> (none in a breeze); nothing is drawn at 0 or outside Varant.
    /// </summary>
    public static class G3SandWisps
    {
        static readonly int P0 = Shader.PropertyToID("_G3SandWisp0");
        static readonly int P1 = Shader.PropertyToID("_G3SandWisp1");
        static readonly int P2 = Shader.PropertyToID("_G3SandWisp2");
        static readonly int P3 = Shader.PropertyToID("_G3SandWisp3");

        /// <summary>fx-1: vertices per streamer sheet (8 x 2 quads lying on the ground; Resources/G3SandWisps.shader).</summary>
        public const int SheetVertices = 8 * 2 * 6;

        static Material _mat;
        static bool _missing;
        static float _amount, _shown;
        static float _groundY;
        static int _groundFrame = -1000;
        static readonly RaycastHit[] _hits = new RaycastHit[4];

        public static string Describe() => _shown > 0.001f ? $"wisps {_shown:0.00} x {WorldRemasterLook.Current.sandWispCount}" : "wisps off";

        /// <summary>Per frame from <see cref="G3SandV2.Tick"/>: eases the amount (gusts come and go over ~1 s) and draws.</summary>
        internal static void Tick(Camera cam, float amount)
        {
            var c = WorldRemasterLook.Current;
            if (cam == null || c.sandWispCount <= 0) { _shown = 0; return; }
            var eye = cam.transform.position;
            amount *= G3SnowV2.DesertAt(eye.x, eye.z);
            float dt = Mathf.Max(Time.deltaTime, 0);
            _amount = amount;
            _shown = Mathf.MoveTowards(_shown, _amount, dt * (amount > _shown ? 0.8f : 0.4f));
            if (_shown < 0.01f) return;
            if (_mat == null && !_missing)
            {
                var sh = Resources.Load<Shader>("G3SandWisps");
                if (sh == null || !sh.isSupported) { _missing = true; Debug.LogWarning("[G3SandWisps] shader G3SandWisps missing/unsupported: no wisps"); return; }
                _mat = new Material(sh) { name = "G3 Sand Wisps", hideFlags = HideFlags.DontSave };
            }
            if (_mat == null) return;
            // the ground under the camera (fallback where the relief's ground map is off): one ray every 10 frames
            if (Time.frameCount - _groundFrame > 10)
            {
                _groundFrame = Time.frameCount;
                int n = Physics.RaycastNonAlloc(eye + Vector3.up * 2f, Vector3.down, _hits, 60f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                    if (_hits[i].distance < best && _hits[i].collider is not CharacterController) { best = _hits[i].distance; _groundY = _hits[i].point.y; }
                if (best == float.MaxValue) _groundY = eye.y - 1.7f;
            }
            var w = G3Wind.Active;
            Vector3 dir = Vector3.forward;
            if (w != null) dir = Quaternion.Euler(0, w.azimuth, 0) * Vector3.forward;
            else { var (x, z) = SandV2.WindDir(c.sandWindAngle); dir = new Vector3(x, 0, z); }
            float speed = 2.5f + 5f * Mathf.Clamp01(G3SandV2.Wind) + 5f * G3SandV2.Storm;   // m/s of the grains along the ground
            int count = Mathf.Min(c.sandWispCount, 512);
            float range = c.sandWispRange;
            // x amount, y peak opacity, z range (m), w count; _1: wind x z, speed, time; _2: ground y fallback, -, -, -
            _mat.SetVector(P0, new Vector4(_shown, c.sandWispOpacity, range, count));
            _mat.SetVector(P1, new Vector4(dir.x, dir.z, speed, Time.time));
            // fx-1: low sheets (storm: a little taller), crest plumes, near fade, loose-sand mask
            _mat.SetVector(P2, new Vector4(_groundY, c.sandWispHeight * (1 + 0.6f * G3SandV2.Storm), c.sandWispCrest, c.sandWispNear));
            _mat.SetVector(P3, new Vector4(c.sandWispMask, 0, 0, 0));
            G3SandFx.Draw(_mat, count * SheetVertices);   // sand-2: G3SandFx's camera pass (Graphics.RenderPrimitives never reached the image)
        }

        internal static void Release()
        {
            if (_mat != null) Object.Destroy(_mat);
            _mat = null;
            _shown = _amount = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _mat = null; _missing = false; _shown = _amount = 0; _groundFrame = -1000; }
    }
}
