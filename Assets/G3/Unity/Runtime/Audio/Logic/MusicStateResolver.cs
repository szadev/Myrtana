using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>
    /// Turns (zone, situation, hour) into a playable <see cref="MusicState"/>: normalises zone location names (data
    /// typos), falls back to the region's default location when a zone names music that does not exist, and plays a
    /// situation the location has no piece for (e.g. fight in Faring) with the region's piece (myrtana/varant fight)
    /// [design choice; G3's FindFallback is not decoded].
    /// </summary>
    public sealed class MusicStateResolver
    {
        public const string DefaultLocation = "myrtana";

        /// <summary>Zone MusicLocation values without a fragment of that name (Docs/formats-audio.md §2.6).</summary>
        static readonly Dictionary<string, string> Fixups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "vistapointharp", "vistaharp" },
            { "ocmine01", "orcmine01" },
        };

        readonly MusicCatalog _catalog;

        public MusicStateResolver(MusicCatalog catalog) { _catalog = catalog; }

        /// <summary>Region default music: Myrtana → myrtana, Varant → varant, Nordmar → northmarlow (the region circles' values).</summary>
        public static string RegionLocation(string weatherEnvironment)
        {
            var w = (weatherEnvironment ?? "").ToLowerInvariant();
            if (w.Contains("varant")) return "varant";
            if (w.Contains("nordmar")) return "northmarlow";
            return DefaultLocation;
        }

        public static string Normalize(string location)
        {
            var l = (location ?? "").Trim().ToLowerInvariant();
            return Fixups.TryGetValue(l, out var f) ? f : l;
        }

        public MusicState Resolve(MusicZoneData zone, string situation, float hour)
        {
            string region = RegionLocation(zone?.weatherEnvironment);
            string loc = Normalize(zone?.musicLocation);
            if (!_catalog.HasLocation(loc)) loc = _catalog.HasLocation(region) ? region : DefaultLocation;

            string sit = MusicSituations.IsExplore(situation) ? MusicSituations.Explore : situation.ToLowerInvariant();
            if (sit != MusicSituations.Explore && !_catalog.HasSituation(loc, sit))
            {
                if (_catalog.HasSituation(region, sit)) loc = region;
                else if (_catalog.HasSituation(DefaultLocation, sit)) loc = DefaultLocation;
                else sit = MusicSituations.Explore;
            }
            return new MusicState(loc, sit, MusicDayTimes.FromHour(hour));
        }
    }
}
