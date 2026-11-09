using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>
    /// The language-neutral title stored in a save header: game day and time ("Day 1, 08:00"), since fix-107 followed by
    /// the text id of the place the hero saved at ("Day 1, 08:00 @ MAP_World_Myrtana_Faring_01", <see
    /// cref="Myrtana.Rules.WorldMaps.PlaceNear"/>), so the slot lists show it in the current language without reading the
    /// save's body. Older titles have no place. Slot lists parse it back (<see cref="TryParse(string, out int, out int, out
    /// int, out TextId)"/>) and show it in G3's localized format.
    /// </summary>
    public static class SaveTitle
    {
        const string PlaceSeparator = " @ ";
        static readonly Regex Pattern = new Regex(@"^Day (\d+), (\d{1,2}):(\d{2})(?: @ (\S+))?$", RegexOptions.CultureInvariant);

        public static string Of(GameSession session)
        {
            float hour = session.Clock.HourOfDay;
            int h = (int)Math.Floor(hour), m = (int)Math.Floor((hour - h) * 60f);
            return Format(session.Clock.Day + 1, h, m, PlaceOf(session));
        }

        /// <summary>The marker label nearest to the hero (empty when his position or a map is unknown).</summary>
        public static TextId PlaceOf(GameSession session)
        {
            if (session?.Maps == null || session.Crime?.World == null) return default;
            if (!session.Crime.World.TryGetPose(ActorId.Player, out var pose)) return default;
            return session.Maps.PlaceNear(pose.X, pose.Z, id => session.Localizer.TryText(id, out var t) && !string.IsNullOrWhiteSpace(t));
        }

        /// <summary>
        /// fix-166: runs <see cref="PlaceOf"/>'s lookup once at the middle of every map (its markers' labels through the
        /// localizer), so the first save in play does not pay for it: behind the loading screen the hero has no pose yet
        /// and the title has no place, and the first recovery snapshot in play spent ~40 ms here (build-1019d spikes
        /// 30-39 ms). Returns how many maps were looked at.
        /// </summary>
        public static int WarmUp(GameSession session)
        {
            if (session?.Maps == null) return 0;
            int maps = 0;
            foreach (var map in session.Maps.All)
            {
                var p = map.Projection;
                if (!p.IsValid) continue;
                session.Maps.PlaceNear((p.Left + p.Right) * 0.5f, (p.Top + p.Bottom) * 0.5f,
                                       id => session.Localizer.TryText(id, out var t) && !string.IsNullOrWhiteSpace(t));
                maps++;
            }
            return maps;
        }

        public static string Format(int day, int hour, int minute, TextId place = default) =>
            $"Day {day}, {hour:00}:{minute:00}" + (place.IsEmpty || place.Value.IndexOf(' ') >= 0 ? "" : PlaceSeparator + place.Value);

        /// <summary>Day (from 1), hour and minute of a title written by <see cref="Of"/>; false for anything else.</summary>
        public static bool TryParse(string title, out int day, out int hour, out int minute) =>
            TryParse(title, out day, out hour, out minute, out _);

        /// <summary>As <see cref="TryParse(string, out int, out int, out int)"/>, with the place (empty in older saves).</summary>
        public static bool TryParse(string title, out int day, out int hour, out int minute, out TextId place)
        {
            day = hour = minute = 0;
            place = default;
            var m = title == null ? null : Pattern.Match(title);
            if (m == null || !m.Success) return false;
            day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            hour = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            minute = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (m.Groups[4].Success) place = new TextId(m.Groups[4].Value);
            return true;
        }
    }
}
