using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// A fixed set of 3D one-shot voices for short sounds (effects, combat cries): no objects are created per sound.
    /// A voice can follow a transform; when all are busy the one that started first is taken. Created on first use in
    /// the active scene.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class G3SoundPool : MonoBehaviour
    {
        const int Size = 32;

        /// <summary>Gain of effect sounds (the menu's FX volume; 1 when the mixer's Effects group carries it).</summary>
        public static float EffectsVolume = 1f;
        /// <summary>Gain of combat voice lines (the menu's voice volume; 1 when the mixer's Voice group carries it).</summary>
        public static float VoiceVolume = 1f;

        static G3SoundPool s_instance;
        static AudioListener s_listener;

        sealed class Voice
        {
            public AudioSource source;
            public Transform follow;
            public Vector3 offset;
            public int handle;
            public float started;
        }

        Voice[] _voices;
        int _nextHandle = 1;

        public static G3SoundPool Instance
        {
            get
            {
                if (s_instance == null) s_instance = new GameObject("G3 Sound Pool").AddComponent<G3SoundPool>();
                return s_instance;
            }
        }

        void Awake()
        {
            _voices = new Voice[Size];
            for (int i = 0; i < Size; i++)
            {
                var go = new GameObject("Voice " + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.dopplerLevel = 0f;
                src.priority = G3.Audio.SoundPriority.Effect;
                _voices[i] = new Voice { source = src };
            }
        }

        /// <summary>Whether a sound at <paramref name="position"/> can be heard within <paramref name="distance"/> metres of the listener.</summary>
        public static bool Audible(Vector3 position, float distance)
        {
            if (s_listener == null || !s_listener.isActiveAndEnabled) s_listener = FindAnyObjectByType<AudioListener>();
            return s_listener == null || (s_listener.transform.position - position).sqrMagnitude <= distance * distance;
        }

        /// <summary>Plays <paramref name="clip"/> once; returns a handle for <see cref="Stop"/> (0 when nothing plays).</summary>
        public int Play(AudioClip clip, Vector3 position, Transform follow, float volume, float minDistance, float maxDistance,
            G3AudioBus bus = G3AudioBus.Effects)
        {
            if (clip == null || volume <= 0f) return 0;
            var v = FreeVoice();
            var src = v.source;
            src.Stop();
            src.transform.position = position;
            v.follow = follow;
            v.offset = follow != null ? position - follow.position : Vector3.zero;
            v.handle = _nextHandle++;
            v.started = Time.time;
            src.minDistance = Mathf.Max(0.3f, minDistance);
            src.maxDistance = Mathf.Max(src.minDistance + 1f, maxDistance);
            src.clip = clip;
            src.outputAudioMixerGroup = G3AudioMixer.Group(bus);
            src.volume = Mathf.Clamp01(volume);
            src.Play();
            return v.handle;
        }

        /// <summary>Stops the sound of <paramref name="handle"/> if it still plays.</summary>
        public void Stop(int handle)
        {
            if (handle == 0 || _voices == null) return;
            foreach (var v in _voices)
                if (v.handle == handle)
                {
                    v.source.Stop();
                    v.follow = null;
                    v.handle = 0;
                }
        }

        Voice FreeVoice()
        {
            Voice oldest = _voices[0];
            foreach (var v in _voices)
            {
                if (!v.source.isPlaying) return v;
                if (v.started < oldest.started) oldest = v;
            }
            return oldest;
        }

        void LateUpdate()
        {
            foreach (var v in _voices)
            {
                if (v.follow == null) continue;
                if (!v.source.isPlaying) { v.follow = null; continue; }
                // fix-199: a pooled NPC view goes inactive and is reused for another NPC: the sound stays where its
                // speaker was last seen instead of jumping to the next owner of the view
                if (!v.follow.gameObject.activeInHierarchy) { v.follow = null; continue; }
                v.source.transform.position = v.follow.position + v.offset;
            }
        }

        void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }
    }
}
