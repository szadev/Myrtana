using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>
    /// Decides when an ambient one-shot spawns (gCAmbientSequencer as inferred, Docs/formats-audio.md §3): every
    /// MinSpawnDelay…MaxSpawnDelay seconds; below MinNumSounds always, else with SpawnProbability; never more than
    /// MaxNumSounds at once; the sample is picked by Weight among the (climate, environment, daytime) matches.
    /// </summary>
    public sealed class AmbientSpawner
    {
        readonly AmbientCatalog _catalog;
        readonly Random _rng;
        readonly List<double> _playingUntil = new List<double>();
        double _nextSpawn = double.NaN;

        public AmbientSpawner(AmbientCatalog catalog, Random rng = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _rng = rng ?? new Random();
        }

        public int Playing => _playingUntil.Count;

        /// <summary>Returns the sound to spawn now, or null. <paramref name="environment"/> null = no ambient objects nearby.</summary>
        public AmbientSoundData Update(double now, AmbientClimate climate, AmbientEnvironment? environment, AmbientDayTime dayTime)
        {
            _playingUntil.RemoveAll(t => t <= now);
            if (environment == null) return null;
            var p = _catalog.Params(climate, environment.Value);
            if (p == null) return null;
            if (double.IsNaN(_nextSpawn)) _nextSpawn = now + Delay(p);
            if (now < _nextSpawn) return null;
            _nextSpawn = now + Delay(p);

            int playing = _playingUntil.Count;
            if (playing >= Math.Max(1, p.maxSounds)) return null;
            if (playing >= p.minSounds && _rng.NextDouble() > p.probability) return null;
            return PickWeighted(_catalog.Find(climate, environment.Value, dayTime));
        }

        /// <summary>Tell the spawner that a spawned sound plays for <paramref name="seconds"/>.</summary>
        public void NotifyStarted(double now, double seconds) => _playingUntil.Add(now + Math.Max(0.1, seconds));

        double Delay(AmbientCellParams p)
        {
            double lo = Math.Min(p.minDelay, p.maxDelay), hi = Math.Max(p.minDelay, p.maxDelay);
            return Math.Max(0.25, lo + (hi - lo) * _rng.NextDouble());
        }

        AmbientSoundData PickWeighted(List<AmbientSoundData> list)
        {
            int total = 0;
            foreach (var s in list) total += Math.Max(1, s.weight);
            if (total == 0) return null;
            int r = _rng.Next(total);
            foreach (var s in list)
            {
                r -= Math.Max(1, s.weight);
                if (r < 0) return s;
            }
            return null;
        }
    }
}
