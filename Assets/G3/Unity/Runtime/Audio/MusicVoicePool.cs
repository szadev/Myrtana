using System.Collections.Generic;
using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Mirrors a <see cref="MusicSequencer"/>'s cue list onto 2D AudioSources: binds new cues (PlayScheduled at their
    /// DSP start, sample offset preset), keeps scheduled end times in sync, applies the gain envelopes every frame and
    /// frees the sources of cues that are gone.
    /// </summary>
    sealed class MusicVoicePool
    {
        sealed class Voice
        {
            public AudioSource Source;
            public MusicCue Cue;
            public double BoundEnd;
        }

        readonly Transform _parent;
        readonly G3AudioLibrary _library;
        readonly List<Voice> _voices = new List<Voice>();
        readonly HashSet<MusicCue> _live = new HashSet<MusicCue>();
        readonly HashSet<string> _missingReported = new HashSet<string>();

        public MusicVoicePool(Transform parent, G3AudioLibrary library)
        {
            _parent = parent;
            _library = library;
        }

        public void Sync(IReadOnlyList<MusicCue> cues, double now, float masterVolume)
        {
            _live.Clear();
            foreach (var c in cues) _live.Add(c);
            foreach (var v in _voices)
                if (v.Cue != null && !_live.Contains(v.Cue)) Release(v);

            foreach (var c in cues)
            {
                var v = Find(c) ?? Bind(c, now);
                if (v == null) continue;
                if (v.BoundEnd != c.End)
                {
                    v.BoundEnd = c.End;
                    if (c.End <= now) { Release(v); continue; }
                    v.Source.SetScheduledEndTime(c.End);
                }
                v.Source.volume = c.Volume * c.Gain.Evaluate(now) * masterVolume;
            }
        }

        /// <summary>Stops and destroys every voice object.</summary>
        public void Dispose()
        {
            foreach (var v in _voices)
                if (v.Source != null) Object.Destroy(v.Source.gameObject);
            _voices.Clear();
        }

        Voice Find(MusicCue c)
        {
            foreach (var v in _voices) if (v.Cue == c) return v;
            return null;
        }

        Voice Bind(MusicCue c, double now)
        {
            if (c.End <= now) return null;
            var clip = _library != null ? _library.GetClip(c.Clip) : null;
            if (clip == null)
            {
                if (_missingReported.Add(c.Clip)) Debug.LogWarning($"G3 music: clip '{c.Clip}' is not imported (G3/Audio/Import All).");
                return null;
            }
            var v = FreeVoice();
            v.Cue = c;
            v.BoundEnd = c.End;
            var src = v.Source;
            src.clip = clip;
            double late = now - c.Start;
            double pos = c.Offset + (late > 0 ? late : 0);
            src.timeSamples = Mathf.Clamp((int)(pos * clip.frequency), 0, Mathf.Max(0, clip.samples - 1));
            src.volume = c.Volume * c.Gain.Evaluate(now);
            if (late > 0) src.Play();
            else src.PlayScheduled(c.Start);
            if (!double.IsInfinity(c.End)) src.SetScheduledEndTime(c.End);
            return v;
        }

        Voice FreeVoice()
        {
            foreach (var v in _voices) if (v.Cue == null) return v;
            var go = new GameObject($"Music Voice {_voices.Count}");
            go.transform.SetParent(_parent, false);
            var src = go.AddComponent<AudioSource>();
            G3AudioMixer.Route(src, G3AudioBus.Music);
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            src.priority = G3.Audio.SoundPriority.Music;
            src.ignoreListenerPause = true;   // music goes on while the loading screen pauses the world's sounds
            src.bypassReverbZones = true;
            var voice = new Voice { Source = src };
            _voices.Add(voice);
            return voice;
        }

        static void Release(Voice v)
        {
            if (v.Source != null) { v.Source.Stop(); v.Source.clip = null; }
            v.Cue = null;
        }
    }
}
