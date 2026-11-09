using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>eEAmbientClimate</summary>
    public enum AmbientClimate { Mediterranean = 0, Arctic = 1, Desert = 2, Cave = 3 }

    /// <summary>eEAmbientEnvironment (eCSpeedTree_PS.AmbientEnvironment)</summary>
    public enum AmbientEnvironment { Forest = 0, Meadow = 1, Plain = 2, Mountain = 3, Coast = 4, Town = 5 }

    /// <summary>eEAmbientDayTime</summary>
    public enum AmbientDayTime { Morning = 0, Day = 1, Evening = 2, Night = 3 }

    public static class AmbientDayTimes
    {
        /// <summary>gCClock_PS: 6–8 morning, 8–20 day, 20–22 evening, 22–6 night (Docs/formats-weather.md §3).</summary>
        public static AmbientDayTime FromHour(float hour)
        {
            float h = ((hour % 24f) + 24f) % 24f;
            if (h >= 6f && h < 8f) return AmbientDayTime.Morning;
            if (h >= 8f && h < 20f) return AmbientDayTime.Day;
            if (h >= 20f && h < 22f) return AmbientDayTime.Evening;
            return AmbientDayTime.Night;
        }
    }

    /// <summary>gCAmbientSound: a one-shot that may spawn for (climate, environment, daytime). Distances in metres.</summary>
    [Serializable]
    public sealed class AmbientSoundData
    {
        public AmbientClimate climate;
        public AmbientEnvironment environment;
        public AmbientDayTime dayTime;
        public string clip;
        public float volume = 1f, minDistance, maxDistance;
        public int weight = 1;
    }

    /// <summary>gCAmbientEnvironment: spawn parameters of one (climate, environment) cell.</summary>
    [Serializable]
    public sealed class AmbientCellParams
    {
        public AmbientClimate climate;
        public AmbientEnvironment environment;
        public int minSounds, maxSounds;
        public float minDelay, maxDelay;   // seconds; the data has min > max in places → unordered range
        public float probability;
    }

    /// <summary>The ambient library (g3.ams) in engine-free form. Docs/formats-audio.md §3.</summary>
    [Serializable]
    public sealed class AmbientCatalog
    {
        public List<AmbientSoundData> sounds = new List<AmbientSoundData>();
        public List<AmbientCellParams> cells = new List<AmbientCellParams>();

        public AmbientCellParams Params(AmbientClimate c, AmbientEnvironment e)
        {
            foreach (var p in cells) if (p.climate == c && p.environment == e) return p;
            return null;
        }

        public List<AmbientSoundData> Find(AmbientClimate c, AmbientEnvironment e, AmbientDayTime t)
        {
            var res = new List<AmbientSoundData>();
            foreach (var s in sounds) if (s.climate == c && s.environment == e && s.dayTime == t) res.Add(s);
            return res;
        }
    }

    /// <summary>Region/zone → climate rules for the ambience [design choice where noted].</summary>
    public static class AmbientRules
    {
        /// <summary>Music locations that are underground; G3 has a Cave climate but no cave sounds in g3.ams [design choice].</summary>
        static readonly HashSet<string> CaveLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dungeon", "orcmine01", "orcmine02", "crystalcave", "thedig", "beliartemple",
        };

        /// <summary>Weather environment Myrtana → Mediterranean, Varant → Desert, Nordmar → Arctic; cave music zones → Cave.</summary>
        public static AmbientClimate ClimateOf(MusicZoneData zone)
        {
            if (zone == null) return AmbientClimate.Mediterranean;
            if (CaveLocations.Contains(MusicStateResolver.Normalize(zone.musicLocation))) return AmbientClimate.Cave;
            var w = (zone.weatherEnvironment ?? "").ToLowerInvariant();
            if (w.Contains("varant")) return AmbientClimate.Desert;
            if (w.Contains("nordmar")) return AmbientClimate.Arctic;
            return AmbientClimate.Mediterranean;
        }
    }
}
