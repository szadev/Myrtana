using System;
using System.Collections.Generic;
using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Imported G3 audio data (built by G3.Unity.Editor.G3AudioImporter into Assets/G3Imported/Audio): the music library
    /// (g3.mul), ambient library (g3.ams), background beds, effect sounds (footsteps) and the AudioClips they name.
    /// </summary>
    public sealed class G3AudioLibrary : ScriptableObject
    {
        [Serializable]
        public struct NamedClip
        {
            public string name;
            public AudioClip clip;
        }

        public MusicCatalog music = new MusicCatalog();
        public AmbientCatalog ambient = new AmbientCatalog();
        public List<AmbientBedData> beds = new List<AmbientBedData>();
        public List<EffectSoundData> effects = new List<EffectSoundData>();
        [Tooltip("Lower-case sample base name → clip.")]
        public List<NamedClip> clips = new List<NamedClip>();

        Dictionary<string, AudioClip> _clips;
        Dictionary<string, EffectSoundData> _effects;

        void OnEnable() => ResetCaches();

        /// <summary>Drops the name lookups (after the lists were rebuilt).</summary>
        public void ResetCaches() { _clips = null; _effects = null; }

        /// <summary>Clip by G3 sample name (any case, with or without extension); null if not imported.</summary>
        public AudioClip GetClip(string sample)
        {
            if (string.IsNullOrEmpty(sample)) return null;
            if (_clips == null)
            {
                _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in clips) if (c.clip != null && !string.IsNullOrEmpty(c.name)) _clips[c.name] = c.clip;
            }
            _clips.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(sample), out var clip);
            return clip;
        }

        /// <summary>Clip length in seconds (sample-exact), or -1.</summary>
        public double ClipLength(string sample)
        {
            var c = GetClip(sample);
            return c != null && c.frequency > 0 ? (double)c.samples / c.frequency : -1;
        }

        public EffectSoundData GetEffect(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_effects == null)
            {
                _effects = new Dictionary<string, EffectSoundData>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in effects) if (e != null && !string.IsNullOrEmpty(e.name)) _effects[e.name] = e;
            }
            _effects.TryGetValue(name, out var fx);
            return fx;
        }
    }
}
