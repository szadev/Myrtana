using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// A placed G3 sound (eCAudioEmitter_PS: campfire crackle, waterfall, wind …). Its AudioSource only runs while the
    /// listener is within MaxDistance; Loop mode loops, Repeat mode replays after SecondsBetweenRepeats with
    /// RepeatProbability, Once plays once per approach (<see cref="EmitterRules"/>).
    /// </summary>
    [AddComponentMenu("G3/Audio/G3 Sound Emitter")]
    public sealed class G3SoundEmitter : MonoBehaviour
    {
        public SoundEmitterData data;
        public G3AudioLibrary library;
        [Range(0, 1)] public float volumeScale = 1f;

        static AudioListener s_listener;
        static readonly System.Random s_rng = new System.Random();
        AudioSource _src;
        float _nextCheck, _nextRepeat;
        bool _inRange;
        int _played;

        public void Init(SoundEmitterData emitter, G3AudioLibrary lib, float volume)
        {
            data = emitter;
            library = lib;
            volumeScale = volume;
            transform.position = new Vector3(emitter.position.x, emitter.position.y, emitter.position.z);
        }

        void Start()
        {
            var clip = library != null && data != null ? library.GetClip(data.clip) : null;
            if (clip == null) { enabled = false; return; }
            _src = gameObject.AddComponent<AudioSource>();
            G3AudioMixer.Route(_src, G3AudioBus.Effects);
            _src.playOnAwake = false;
            _src.clip = clip;
            _src.spatialBlend = 1f;
            _src.rolloffMode = AudioRolloffMode.Logarithmic;
            _src.minDistance = data.minDistance;
            _src.maxDistance = data.maxDistance;
            _src.dopplerLevel = 0f;
            _src.loop = data.mode == EmitterMode.Loop;
            _src.volume = data.volume * volumeScale;
            _src.priority = G3.Audio.SoundPriority.WorldEmitter; // below every gameplay sound (fix-61)
            _nextCheck = Random.value;   // stagger the range checks
        }

        void Update()
        {
            if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + 1f;
                bool inRange = InRange();
                if (inRange != _inRange) { _inRange = inRange; OnRangeChanged(); }
            }
            if (_inRange && data.mode == EmitterMode.Repeat && Time.time >= _nextRepeat && !_src.isPlaying)
            {
                _nextRepeat = Time.time + (float)EmitterRules.NextDelay(data, s_rng);
                if (EmitterRules.ShouldPlay(data, _played, s_rng)) { _src.Play(); _played++; }
            }
        }

        void OnRangeChanged()
        {
            if (!_inRange) { _src.Stop(); return; }
            _played = 0;
            switch (data.mode)
            {
                case EmitterMode.Loop:
                    _src.time = Random.Range(0f, _src.clip.length * 0.95f);
                    _src.Play();
                    break;
                case EmitterMode.Once:
                    _src.Play();
                    break;
                case EmitterMode.Repeat:
                    _nextRepeat = Time.time + (float)EmitterRules.NextDelay(data, s_rng);
                    break;
            }
        }

        bool InRange()
        {
            if (s_listener == null || !s_listener.isActiveAndEnabled) s_listener = FindAnyObjectByType<AudioListener>();
            if (s_listener == null) return false;
            float r = EmitterRules.ActivationRadius(data);
            return (s_listener.transform.position - transform.position).sqrMagnitude <= r * r;
        }
    }
}
