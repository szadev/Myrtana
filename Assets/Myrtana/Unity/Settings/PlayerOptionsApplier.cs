using System.Collections.Generic;
using G3.Unity;
using Myrtana.Session;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Applies the menu's <see cref="PlayerOptions"/>: the master volume to the listener, music/ambient/FX/voice to the
    /// <see cref="G3AudioMixer"/> groups (without the mixer asset: to the dialog voice source and the G3 audio components,
    /// whose inspector volume is the 100 % level), the camera mode to the hero camera and the GUI size to the G3 panels.
    /// Applied at start and after every change.
    /// </summary>
    public sealed class PlayerOptionsApplier
    {
        readonly PlayerOptions _options;
        readonly AudioSource _voice;
        readonly Dictionary<Object, float> _baseVolumes = new Dictionary<Object, float>();

        public PlayerOptionsApplier(PlayerOptions options, AudioSource voice)
        {
            _options = options;
            _voice = voice;
            ApplySpeakerMode(options.SpeakerMode);
            options.Changed += Apply;
            Apply();
        }

        /// <summary>The audio system's speaker mode (eEAudioSpeakerMode order = Unity's AudioSpeakerMode).</summary>
        public static int RunningSpeakerMode => (int)AudioSettings.GetConfiguration().speakerMode;

        /// <summary>
        /// G3 applies the speaker setup with a restart; the port sets it once at start-up, before anything plays
        /// (AudioSettings.Reset stops every playing source).
        /// </summary>
        static void ApplySpeakerMode(int mode)
        {
            if (mode < PlayerOptions.SpeakerModeMin || mode > PlayerOptions.SpeakerModeMax || mode == RunningSpeakerMode) return;
            var config = AudioSettings.GetConfiguration();
            config.speakerMode = (AudioSpeakerMode)mode;
            if (!AudioSettings.Reset(config)) Debug.LogWarning($"Speaker mode {(AudioSpeakerMode)mode} not accepted by the audio system.");
        }

        public void Apply()
        {
            AudioListener.volume = _options.Gain(VolumeChannel.Master);
            float music = _options.Gain(VolumeChannel.Music), ambient = _options.Gain(VolumeChannel.Ambient),
                  fx = _options.Gain(VolumeChannel.Effects), voice = _options.Gain(VolumeChannel.Voice);
            if (G3AudioMixer.Available)
            {
                // The mixer groups carry the option volumes for every routed source, playing or not (streamed emitters,
                // the hero's voice); the components stay at their own 100 % level.
                G3AudioMixer.SetVolume(G3AudioBus.Music, music);
                G3AudioMixer.SetVolume(G3AudioBus.Ambience, ambient);
                G3AudioMixer.SetVolume(G3AudioBus.Effects, fx);
                G3AudioMixer.SetVolume(G3AudioBus.Voice, voice);
                music = ambient = fx = voice = 1f;
            }
            if (_voice != null) _voice.volume = voice;

            var director = Object.FindAnyObjectByType<G3MusicDirector>();
            if (director != null) director.volume = Base(director, director.volume) * music;
            var ambience = Object.FindAnyObjectByType<G3AmbientDirector>();
            if (ambience != null) ambience.volume = Base(ambience, ambience.volume) * ambient;
            G3SoundPool.EffectsVolume = fx;                                   // effect one-shots, motion sounds (combat)
            G3SoundPool.VoiceVolume = voice;                                  // combat cries
            var emitters = Object.FindAnyObjectByType<G3AudioEmitterStreamer>();
            if (emitters != null) emitters.volume = Base(emitters, emitters.volume) * fx; // without mixer: emitters spawned from now on
            foreach (var steps in Object.FindObjectsByType<G3Footsteps>(FindObjectsSortMode.None))
                steps.volume = Base(steps, steps.volume) * fx;

            var camera = Object.FindAnyObjectByType<G3PlayerCamera>();
            if (camera != null) camera.thirdPerson = !_options.FirstPerson;

            G3UiScale.Percent = _options.UiScale;
        }

        /// <summary>The component's own volume the first time it is seen (the 100 % level).</summary>
        float Base(Object component, float current)
        {
            if (!_baseVolumes.TryGetValue(component, out float v)) _baseVolumes[component] = v = current;
            return v;
        }
    }
}
