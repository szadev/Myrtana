using System;

namespace G3.Audio
{
    /// <summary>Music situations used by g3.mul (Docs/formats-audio.md §2.2, gameplay-logic.md §11.3).</summary>
    public static class MusicSituations
    {
        public const string Explore = "explore";
        public const string Fight = "fight";
        public const string Showdown = "showdown";
        public const string Dead = "dead";
        public const string SoulTravel = "soultravel";
        public const string Revolution = "revolution";
        public const string RevolutionWon = "revolutionwon";

        public static bool IsExplore(string s) => string.IsNullOrEmpty(s) || MusicCatalog.Eq(s, Explore);
    }

    /// <summary>Music only knows day and night; gCClock night = 22–6 h.</summary>
    public static class MusicDayTimes
    {
        public const string Day = "day";
        public const string Night = "night";

        public static string FromHour(float hour)
        {
            float h = ((hour % 24f) + 24f) % 24f;
            return h >= 22f || h < 6f ? Night : Day;
        }
    }

    /// <summary>What the music should currently express: (location, situation, daytime), all lower case.</summary>
    public readonly struct MusicState : IEquatable<MusicState>
    {
        public readonly string Location, Situation, DayTime;

        public MusicState(string location, string situation, string dayTime)
        {
            Location = (location ?? "").ToLowerInvariant();
            Situation = (situation ?? MusicSituations.Explore).ToLowerInvariant();
            DayTime = (dayTime ?? MusicDayTimes.Day).ToLowerInvariant();
        }

        public bool IsEmpty => string.IsNullOrEmpty(Location);

        public bool Equals(MusicState o) => Location == o.Location && Situation == o.Situation && DayTime == o.DayTime;
        public override bool Equals(object obj) => obj is MusicState o && Equals(o);
        public override int GetHashCode() => (Location, Situation, DayTime).GetHashCode();
        public override string ToString() => $"{Location}_{Situation}_{DayTime}";
    }

    /// <summary>
    /// Supplies the music situation (fight, showdown, …). Return null/empty for "no opinion"; the director takes the
    /// first non-empty answer of its sources in registration order and defaults to explore. The game implements
    /// it with G3's per-second rule (Myrtana.Rules.MusicSituationRule).
    /// </summary>
    public interface IMusicSituationSource
    {
        string MusicSituation { get; }
    }
}
