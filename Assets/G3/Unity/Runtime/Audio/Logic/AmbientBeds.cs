using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>A looping 2D background bed for a climate by day or night.</summary>
    [Serializable]
    public sealed class AmbientBedData
    {
        public AmbientClimate climate;
        public bool night;
        public string clip;
        public float volume = 0.35f;
    }

    /// <summary>
    /// Background beds. Not part of G3's data (g3.ams only has one-shots and nothing for Arctic/Cave): a design choice
    /// using the shipped but unreferenced Sound.pak environment/ambience|wind loops (Docs/formats-audio.md §5.2, §7).
    /// </summary>
    public static class AmbientBeds
    {
        public static List<AmbientBedData> Defaults() => new List<AmbientBedData>
        {
            Bed(AmbientClimate.Mediterranean, false, "wood_gentleambience_01", 0.3f),
            Bed(AmbientClimate.Mediterranean, true, "nature_insects_06", 0.3f),
            Bed(AmbientClimate.Desert, false, "wind_varant_01", 0.3f),
            Bed(AmbientClimate.Desert, true, "nature_insects_06", 0.25f),
            Bed(AmbientClimate.Arctic, false, "wind_highplaces_01", 0.3f),
            Bed(AmbientClimate.Arctic, true, "wind_northmarvillage_01", 0.3f),
            Bed(AmbientClimate.Cave, false, "wind_caves_01", 0.35f),
            Bed(AmbientClimate.Cave, true, "wind_caves_01", 0.35f),
        };

        public static AmbientBedData Find(IEnumerable<AmbientBedData> beds, AmbientClimate climate, AmbientDayTime time)
        {
            if (beds == null) return null;
            bool night = time == AmbientDayTime.Night;
            foreach (var b in beds) if (b.climate == climate && b.night == night) return b;
            return null;
        }

        static AmbientBedData Bed(AmbientClimate c, bool night, string clip, float volume) =>
            new AmbientBedData { climate = c, night = night, clip = clip, volume = volume };
    }
}
