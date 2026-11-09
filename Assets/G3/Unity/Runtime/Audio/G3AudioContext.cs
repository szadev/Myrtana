using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Shared inputs of the audio directors on the "G3 Audio" object: data assets, the listener focus (player, else
    /// main camera), the music zone under it, the game hour and the region. Re-evaluated every
    /// <see cref="updateInterval"/> seconds.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/Audio/G3 Audio Context")]
    public sealed class G3AudioContext : MonoBehaviour
    {
        public G3AudioLibrary library;
        public G3AudioWorldAsset world;
        public G3Environment environment;
        [Tooltip("Whose position selects zones/ambience. Empty: the G3Player, else the main camera.")]
        public Transform focus;
        public float updateInterval = 0.5f;

        [Header("Current (read only)")]
        [SerializeField] string zoneName;
        [SerializeField] string region;

        MusicZoneMap _zones;
        float _next;

        public MusicZoneData Zone { get; private set; }
        public Vector3 FocusPosition { get; private set; }
        public string Region => region;
        public float Hour => environment != null ? environment.timeOfDay : 12f;

        /// <summary>Raised after every re-evaluation (zone may or may not have changed).</summary>
        public event System.Action Refreshed;

        void Awake()
        {
            if (environment == null) environment = FindAnyObjectByType<G3Environment>();
            _zones = new MusicZoneMap(world != null ? world.zones : null);
        }

        void Start() => Refresh();

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + updateInterval;
            Refresh();
        }

        public void Refresh()
        {
            var t = ResolveFocus();
            if (t == null) return;
            FocusPosition = t.position;
            if (_zones == null) _zones = new MusicZoneMap(world != null ? world.zones : null);
            Zone = _zones.ZoneAt(new V3(FocusPosition.x, FocusPosition.y, FocusPosition.z));
            zoneName = Zone != null ? Zone.ToString() : "";
            region = Zone?.weatherEnvironment ?? "";
            Refreshed?.Invoke();
        }

        Transform ResolveFocus()
        {
            if (focus != null) return focus;
            var player = FindAnyObjectByType<G3Player>();
            if (player != null) return focus = player.transform;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }
    }
}
