using System;

namespace G3.Audio
{
    /// <summary>eEAudioEmitterMode</summary>
    public enum EmitterMode { Once = 0, Loop = 1, Repeat = 2 }

    /// <summary>
    /// A placed 3D sound: an eCAudioEmitter_PS on a world entity, or the emitter of a template spawned by a
    /// gCEffect_PS (campfires, fireplaces). Distances in metres. Docs/formats-audio.md §5.2.
    /// </summary>
    [Serializable]
    public sealed class SoundEmitterData
    {
        public string name;
        /// <summary>World manifest cell index the emitter streams with (-1 = always).</summary>
        public int cell = -1;
        public V3 position;
        public string clip;
        public EmitterMode mode;
        public float volume = 1f, minDistance = 1f, maxDistance = 50f;
        public float repeatDelayMin, repeatDelayMax, repeatProbabilityMin = 1f, repeatProbabilityMax = 1f;
        public int maxRepeats;
        public bool useMaxRepeats, stream;
    }

    /// <summary>Repeat-mode decisions of an emitter (SecondsBetweenRepeats / RepeatProbability / MaxNumRepeats).</summary>
    public static class EmitterRules
    {
        /// <summary>
        /// Listener distance within which the emitter runs: its MaxDistance, but no farther than where logarithmic
        /// roll-off has reached −40 dB (100 × MinDistance, at least 30 m) — campfires carry MaxDistance 250 m with
        /// MinDistance 0.25 m and would otherwise keep dozens of inaudible voices busy.
        /// </summary>
        public static float ActivationRadius(SoundEmitterData e) => Math.Min(e.maxDistance, Math.Max(30f, e.minDistance * 100f));

        public static double NextDelay(SoundEmitterData e, Random rng)
        {
            double lo = Math.Min(e.repeatDelayMin, e.repeatDelayMax), hi = Math.Max(e.repeatDelayMin, e.repeatDelayMax);
            return Math.Max(0.1, lo + (hi - lo) * rng.NextDouble());
        }

        public static bool ShouldPlay(SoundEmitterData e, int playedSoFar, Random rng)
        {
            if (e.useMaxRepeats && e.maxRepeats > 0 && playedSoFar >= e.maxRepeats) return false;
            double lo = Math.Min(e.repeatProbabilityMin, e.repeatProbabilityMax), hi = Math.Max(e.repeatProbabilityMin, e.repeatProbabilityMax);
            double p = lo + (hi - lo) * rng.NextDouble();
            return p >= 1 || rng.NextDouble() < p;
        }
    }

    /// <summary>The audio part of a g3.efm effect: pick one of the clips and play it 3D. Distances in metres.</summary>
    [Serializable]
    public sealed class EffectSoundData
    {
        public string name;
        public float volume = 1f, minDistance = 1f, maxDistance = 30f;
        public string[] clips = new string[0];

        public string PickClip(Random rng) => clips == null || clips.Length == 0 ? null : clips[rng.Next(clips.Length)];
    }
}
