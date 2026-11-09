using System;
using G3.Unity.Characters;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Plays the sounds of a character's motions: every effect event of the current motion (<see cref="G3MotionPlayer.EffectEvent"/>,
    /// the .xmot effect track: <c>EFF_Ani_Anvil_01</c>, <c>EFF_Ani_Drink_01</c> …) is looked up in g3.efm
    /// (<see cref="G3AudioLibrary.GetEffect"/>) and one of its samples is played at the character, 3D with logarithmic
    /// falloff from the effect's MinDistance (Docs/formats-animation.md §8.7): tool sounds, weapon whooshes, creature cries and
    /// creature steps. Human footsteps are left to <see cref="G3Footsteps"/>.
    /// </summary>
    [RequireComponent(typeof(G3MotionPlayer))]
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/Audio/G3 Motion Sounds")]
    public sealed class G3MotionSounds : MonoBehaviour
    {
        [Tooltip("Imported audio; found through the scene's G3AudioContext when empty")] public G3AudioLibrary library;
        [Range(0, 1)] public float volume = 1f;
        [Tooltip("Beyond this distance (m) to the listener no sound is started; G3 effects have no real maximum")]
        public float audibleDistance = 40f;
        [Tooltip("Log every played effect (verification)")] public bool log;
        [Tooltip("AudioSource priority (G3.Audio.SoundPriority): NPCs and creatures CharacterMotion, the hero HeroMotion")]
        public int priority = G3.Audio.SoundPriority.CharacterMotion;

        static readonly System.Random s_rng = new System.Random();
        static G3AudioLibrary s_sceneLibrary;
        static AudioListener s_listener;
        G3MotionPlayer _player;
        AudioSource _src;

        /// <summary>Raised for every effect sound started here: effect name, clip name.</summary>
        public static event Action<G3MotionSounds, string, string> Played;

        static readonly System.Collections.Generic.Dictionary<string, int> s_playedCount =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How often an effect sound was started from a motion track since the domain loaded (dev probe <c>sfx</c>).</summary>
        public static int PlayedCount(string effect) => effect != null && s_playedCount.TryGetValue(effect, out int n) ? n : 0;

        void Awake() => _player = GetComponent<G3MotionPlayer>();

        /// <summary>The player hero's motions: his tool and action sounds rank above the world's loops (SoundPriority).</summary>
        public G3MotionSounds AsHero()
        {
            priority = G3.Audio.SoundPriority.HeroMotion;
            return this;
        }

        void OnEnable() => _player.EffectEvent += OnEffect;

        void OnDisable() => _player.EffectEvent -= OnEffect;

        void OnEffect(string effect)
        {
            // Human steps come from G3Footsteps; creature steps (eff_step_creature01/giant/minecrawler) only from the tracks.
            if (!G3.Audio.MotionEventWindow.PlaysFromTrack(effect)) return;
            var lib = Library();
            var fx = lib != null ? lib.GetEffect(effect) : null;
            if (fx == null || !Audible()) return;
            string sample = fx.PickClip(s_rng);
            var clip = lib.GetClip(sample);
            if (clip == null) return;
            var src = Source();
            src.priority = priority;
            src.minDistance = Mathf.Max(0.5f, fx.minDistance);
            src.maxDistance = Mathf.Max(src.minDistance + 1f, Mathf.Min(fx.maxDistance, audibleDistance));
            src.PlayOneShot(clip, fx.volume * volume * G3SoundPool.EffectsVolume);
            if (log) Debug.Log($"[G3MotionSounds] {name}: {effect} -> {sample} ({_player.CurrentKey})");
            s_playedCount[effect] = PlayedCount(effect) + 1;
            Played?.Invoke(this, effect, sample);
        }

        G3AudioLibrary Library()
        {
            if (library != null) return library;
            if (s_sceneLibrary == null)
            {
                var context = FindAnyObjectByType<G3AudioContext>();
                s_sceneLibrary = context != null ? context.library : null;
            }
            return s_sceneLibrary;
        }

        bool Audible()
        {
            if (s_listener == null || !s_listener.isActiveAndEnabled) s_listener = FindAnyObjectByType<AudioListener>();
            return s_listener == null || (s_listener.transform.position - transform.position).sqrMagnitude <= audibleDistance * audibleDistance;
        }

        AudioSource Source()
        {
            if (_src != null) return _src;
            _src = gameObject.AddComponent<AudioSource>();
            G3AudioMixer.Route(_src, G3AudioBus.Effects);
            _src.playOnAwake = false;
            _src.spatialBlend = 1f;
            _src.rolloffMode = AudioRolloffMode.Logarithmic;
            _src.dopplerLevel = 0f;
            return _src;
        }
    }
}
