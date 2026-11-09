using UnityEngine;

namespace G3.Unity
{
    /// <summary>Two looping 2D AudioSources that crossfade when the bed clip changes.</summary>
    sealed class AmbientBedPlayer
    {
        readonly AudioSource[] _src = new AudioSource[2];
        readonly float[] _target = new float[2];
        int _active;

        public AmbientBedPlayer(Transform parent)
        {
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject($"Ambient Bed {i}");
                go.transform.SetParent(parent, false);
                var s = go.AddComponent<AudioSource>();
                G3AudioMixer.Route(s, G3AudioBus.Ambience);
                s.playOnAwake = false;
                s.loop = true;
                s.spatialBlend = 0f;
                s.volume = 0f;
                s.priority = G3.Audio.SoundPriority.AmbientBed;
                _src[i] = s;
            }
        }

        public AudioClip Clip => _src[_active].clip;

        /// <summary>Destroys both bed sources.</summary>
        public void Dispose()
        {
            foreach (var s in _src)
                if (s != null) Object.Destroy(s.gameObject);
        }

        /// <summary>Starts crossfading to <paramref name="clip"/> (null = silence) at <paramref name="volume"/>.</summary>
        public void Play(AudioClip clip, float volume)
        {
            if (clip == _src[_active].clip)
            {
                _target[_active] = clip != null ? volume : 0f;
                return;
            }
            _target[_active] = 0f;
            _active = 1 - _active;
            var s = _src[_active];
            s.clip = clip;
            _target[_active] = clip != null ? volume : 0f;
            if (clip != null)
            {
                s.volume = 0f;
                s.time = Random.Range(0f, clip.length * 0.9f);
                s.Play();
            }
        }

        public void Tick(float dt, float fadeSeconds, float master)
        {
            float step = dt / Mathf.Max(0.01f, fadeSeconds);
            for (int i = 0; i < 2; i++)
            {
                var s = _src[i];
                float want = _target[i] * master;
                s.volume = Mathf.MoveTowards(s.volume, want, step * Mathf.Max(0.05f, Mathf.Max(want, s.volume)));
                if (s.volume <= 0f && _target[i] <= 0f && s.isPlaying) s.Stop();
            }
        }
    }
}
