using System;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Plays the sound of a G3 effect (g3.efm, e.g. <c>eff_magic_fireball_cast_01</c>, <c>eff_col_wh_02_me_fl</c>) once at a
    /// point or on a moving object: one of the effect's samples, 3D with logarithmic falloff from its MinDistance, like
    /// <see cref="G3MotionSounds"/> does for motion events, through the pooled voices of <see cref="G3SoundPool"/>.
    /// Effects without sound (or not imported) are skipped.
    /// </summary>
    public static class G3EffectSounds
    {
        const float AudibleDistance = 40f;

        static readonly System.Random s_rng = new System.Random();
        static G3AudioLibrary s_library;

        /// <summary>Log every effect asked for (verification): <c>[G3EffectSounds] effect -> sample</c>.</summary>
        public static bool LogPlays;

        /// <summary>Raised for every sound started: effect, sample, position.</summary>
        public static event Action<string, string, Vector3> Played;

        /// <summary>Raised for every effect asked for, with or without sound (G3EffectPlayer shows its visuals).</summary>
        public static event Action<string, Vector3, Transform> Requested;

        /// <summary>Whether the imported audio knows a sound for <paramref name="effect"/>.</summary>
        public static bool Has(string effect) => Library()?.GetEffect(effect) is { } fx && fx.clips != null && fx.clips.Length > 0;

        /// <summary>Plays the effect's sound at <paramref name="position"/>, following <paramref name="follow"/> when given; false when it has none.</summary>
        public static bool Play(string effect, Vector3 position, Transform follow = null, float volume = 1f) =>
            Start(effect, position, follow, volume) != 0;

        /// <summary>As <see cref="Play"/>, returning a handle for <see cref="Stop"/> (0 = nothing started).</summary>
        public static int Start(string effect, Vector3 position, Transform follow = null, float volume = 1f)
        {
            if (string.IsNullOrEmpty(effect)) return 0;
            Requested?.Invoke(effect, position, follow);
            var lib = Library();
            var fx = lib != null ? lib.GetEffect(effect) : null;
            string sample = fx?.PickClip(s_rng);
            var clip = sample != null ? lib.GetClip(sample) : null;
            if (LogPlays) Debug.Log($"[G3EffectSounds] {effect} -> {(clip != null ? sample : fx == null ? "(no effect)" : "(clip not imported)")}");
            if (clip == null) return 0;
            float max = Mathf.Min(fx.maxDistance, AudibleDistance);
            if (!G3SoundPool.Audible(position, max)) return 0;
            int handle = G3SoundPool.Instance.Play(clip, position, follow, fx.volume * volume * G3SoundPool.EffectsVolume, fx.minDistance, max);
            if (handle != 0) Played?.Invoke(effect, sample, position);
            return handle;
        }

        /// <summary>Stops a sound started with <see cref="Start"/> (the bow string when the arrow leaves).</summary>
        public static void Stop(int handle)
        {
            if (handle != 0) G3SoundPool.Instance.Stop(handle);
        }

        static G3AudioLibrary Library()
        {
            if (s_library == null)
            {
                var context = UnityEngine.Object.FindAnyObjectByType<G3AudioContext>();
                s_library = context != null ? context.library : null;
            }
            return s_library;
        }
    }
}
