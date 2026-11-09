using System.Collections.Generic;
using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Area ambience: a looping background bed per climate and day/night (design choice, <see cref="AmbientBeds"/>)
    /// plus G3's ambient one-shots (g3.ams) chosen by climate (zone weather environment), the dominant SpeedTree
    /// environment around the listener and the daytime, spawned at nearby tree areas by <see cref="AmbientSpawner"/>.
    /// </summary>
    [RequireComponent(typeof(G3AudioContext))]
    [AddComponentMenu("G3/Audio/G3 Ambient Director")]
    public sealed class G3AmbientDirector : MonoBehaviour
    {
        [Range(0, 1)] public float volume = 1f;
        [Range(0, 1)] public float bedVolume = 1f;
        [Range(0, 1)] public float oneShotVolume = 1f;
        public float bedFadeSeconds = 4f;
        [Tooltip("Ambient-grid cells (25 m) around the listener that count for the environment and spawn positions.")]
        [Range(0, 4)] public int searchRadiusCells = 1;
        [Tooltip("One-shots spawn this high above the listener (tree crowns), metres.")]
        public Vector2 spawnHeight = new Vector2(3f, 9f);
        public int maxOneShotVoices = 6;

        [Header("Current (read only)")]
        [SerializeField] AmbientClimate climate;
        [SerializeField] string environmentName;
        [SerializeField] AmbientDayTime dayTime;
        [SerializeField] string bed;
        [SerializeField] string lastOneShot;

        G3AudioContext _context;
        AmbientSpawner _spawner;
        AmbientBedPlayer _bed;
        AmbientEnvironment? _environment;
        readonly List<AudioSource> _oneShots = new List<AudioSource>();
        readonly System.Random _rng = new System.Random();

        // Built in OnEnable, torn down in OnDisable (not Start/OnDestroy): a script recompile during Play reloads the
        // domain, which drops these non-serialized fields and calls OnDisable/OnEnable again but never Start.
        void OnEnable()
        {
            _context = GetComponent<G3AudioContext>();
            if (_context.library == null)
            {
                Debug.LogWarning("G3AmbientDirector: no imported audio library (G3/Audio/Import All). Ambience disabled.");
                enabled = false;
                return;
            }
            _spawner = new AmbientSpawner(_context.library.ambient, _rng);
            _bed = new AmbientBedPlayer(transform);
            _context.Refreshed += Evaluate;
            _context.Refresh();   // raises Refreshed → Evaluate once the focus is known (else the context's Start does)
        }

        void OnDisable()
        {
            if (_context != null) _context.Refreshed -= Evaluate;
            _bed?.Dispose();
            _bed = null;
            foreach (var s in _oneShots)
                if (s != null) Destroy(s.gameObject);
            _oneShots.Clear();
        }

        void Evaluate()
        {
            climate = AmbientRules.ClimateOf(_context.Zone);
            dayTime = AmbientDayTimes.FromHour(_context.Hour);
            var p = _context.FocusPosition;
            var grid = _context.world != null ? _context.world.ambientGrid : null;
            _environment = grid != null ? grid.DominantAround(p.x, p.z, searchRadiusCells) : null;
            environmentName = _environment?.ToString() ?? "none";

            var bedData = AmbientBeds.Find(_context.library.beds, climate, dayTime);
            var clip = bedData != null ? _context.library.GetClip(bedData.clip) : null;
            _bed.Play(clip, bedData?.volume ?? 0f);
            bed = clip != null ? clip.name : "";
        }

        void Update()
        {
            _bed.Tick(Time.deltaTime, bedFadeSeconds, volume * bedVolume);
            var sound = _spawner.Update(Time.timeAsDouble, climate, _environment, dayTime);
            if (sound != null) Spawn(sound);
        }

        void Spawn(AmbientSoundData sound)
        {
            var clip = _context.library.GetClip(sound.clip);
            if (clip == null || _environment == null) return;
            var p = _context.FocusPosition;
            var grid = _context.world.ambientGrid;
            if (!grid.TryRandomPoint(_environment.Value, p.x, p.z, searchRadiusCells, _rng, out float x, out float z)) return;
            var src = FreeOneShot();
            if (src == null) return;
            src.transform.position = new Vector3(x, p.y + Random.Range(spawnHeight.x, spawnHeight.y), z);
            src.minDistance = Mathf.Max(1f, sound.minDistance);
            src.maxDistance = Mathf.Clamp(sound.maxDistance, src.minDistance + 1f, 500f);
            src.clip = clip;
            src.volume = sound.volume * oneShotVolume * volume;
            src.Play();
            _spawner.NotifyStarted(Time.timeAsDouble, clip.length);
            lastOneShot = clip.name;
        }

        AudioSource FreeOneShot()
        {
            foreach (var s in _oneShots) if (!s.isPlaying) return s;
            if (_oneShots.Count >= maxOneShotVoices) return null;
            var go = new GameObject($"Ambient One-shot {_oneShots.Count}");
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            G3AudioMixer.Route(src, G3AudioBus.Ambience);
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.dopplerLevel = 0f;
            src.priority = G3.Audio.SoundPriority.AmbientOneShot;
            _oneShots.Add(src);
            return src;
        }
    }
}
