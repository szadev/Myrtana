using UnityEngine;
using UnityEngine.Audio;

namespace G3.Unity
{
    /// <summary>The mixer groups (G3's audio channel groups / the option menu's volumes).</summary>
    public enum G3AudioBus { Music, Effects, Voice, Ambience }

    /// <summary>
    /// The project's AudioMixer (Resources/G3AudioMixer.mixer): Master with Music, Effects, Voice and Ambience groups,
    /// each with an exposed volume "&lt;Group&gt;Volume" in dB. Every G3 audio source is routed into its group, so the
    /// option volumes reach all sounds at once — also those already playing (streamed emitters, the hero's voice).
    /// <see cref="SetVolume"/> sets a group's gain (0–1), <see cref="SetMuted"/> silences it on top (menu/title).
    /// The master volume stays on <see cref="AudioListener.volume"/> (it also covers the video screen).
    /// Without the asset (or with a broken one) <see cref="Available"/> is false and sources play unrouted.
    /// Loading-screen pauses stay on <see cref="AudioListener.pause"/> (music voices ignore it); the mixer does not pause.
    /// </summary>
    public static class G3AudioMixer
    {
        public const string ResourceName = "G3AudioMixer";
        const float SilentDb = -80f;

        static bool s_loaded;
        static AudioMixer s_mixer;
        static readonly AudioMixerGroup[] s_groups = new AudioMixerGroup[4];
        static readonly float[] s_gain = { 1f, 1f, 1f, 1f };
        static readonly bool[] s_muted = new bool[4];

        public static AudioMixer Mixer { get { Load(); return s_mixer; } }

        /// <summary>True when the mixer asset loaded and has all four groups.</summary>
        public static bool Available
        {
            get
            {
                Load();
                if (s_mixer == null) return false;
                foreach (var g in s_groups) if (g == null) return false;
                return true;
            }
        }

        public static AudioMixerGroup Group(G3AudioBus bus)
        {
            Load();
            return s_groups[(int)bus];
        }

        /// <summary>Sends <paramref name="source"/> to the group of <paramref name="bus"/> (no-op without the mixer).</summary>
        public static void Route(AudioSource source, G3AudioBus bus)
        {
            if (source == null) return;
            var g = Group(bus);
            if (g != null) source.outputAudioMixerGroup = g;
        }

        public static void SetVolume(G3AudioBus bus, float gain)
        {
            s_gain[(int)bus] = Mathf.Clamp01(gain);
            Apply(bus);
        }

        public static float Volume(G3AudioBus bus) => s_gain[(int)bus];

        public static void SetMuted(G3AudioBus bus, bool muted)
        {
            if (s_muted[(int)bus] == muted) return;
            s_muted[(int)bus] = muted;
            Apply(bus);
        }

        public static bool IsMuted(G3AudioBus bus) => s_muted[(int)bus];

        /// <summary>Linear gain → mixer dB (0 → −80 dB = silent).</summary>
        public static float ToDb(float gain) => gain <= 0.0001f ? SilentDb : Mathf.Max(SilentDb, 20f * Mathf.Log10(gain));

        static void Apply(G3AudioBus bus)
        {
            if (Mixer == null) return;
            int i = (int)bus;
            s_mixer.SetFloat(bus + "Volume", s_muted[i] ? SilentDb : ToDb(s_gain[i]));
        }

        static void Load()
        {
            if (s_loaded) return;
            s_loaded = true;
            s_mixer = Resources.Load<AudioMixer>(ResourceName);
            if (s_mixer == null)
            {
                Debug.LogWarning($"[G3AudioMixer] Resources/{ResourceName}.mixer not found: sounds play without mixer groups.");
                return;
            }
            for (int i = 0; i < s_groups.Length; i++)
            {
                var name = ((G3AudioBus)i).ToString();
                var found = s_mixer.FindMatchingGroups("Master/" + name);
                s_groups[i] = found != null && found.Length > 0 ? found[0] : null;
                if (s_groups[i] == null) Debug.LogWarning($"[G3AudioMixer] group Master/{name} missing in {ResourceName}.");
                else if (!s_mixer.GetFloat(name + "Volume", out _)) Debug.LogWarning($"[G3AudioMixer] exposed parameter {name}Volume missing.");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_loaded = false;
            s_mixer = null;
            for (int i = 0; i < s_groups.Length; i++) { s_groups[i] = null; s_gain[i] = 1f; s_muted[i] = false; }
        }
    }
}
