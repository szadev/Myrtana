using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>gCMusicLink.Type</summary>
    public enum MusicLinkKind { Exit = 0, Entrance = 1, Loop = 2, Generic = 3 }

    /// <summary>A sync point inside a fragment (seconds from the start of the sample).</summary>
    [Serializable]
    public sealed class MusicLinkData
    {
        public float time;
        public int connectIn, connectOut;
        public MusicLinkKind kind;
    }

    /// <summary>One piece of music (gCMusicFragment) for a location/situation/daytime/variation. Times in seconds.</summary>
    [Serializable]
    public sealed class MusicFragmentData
    {
        public string location, situation, dayTime, variation, nextVariation;
        /// <summary>Sample base name (lower case, no extension), e.g. "faring_explore_day_01".</summary>
        public string clip;
        public float volume = 1f;
        public float duration;
        public List<MusicLinkData> links = new List<MusicLinkData>();

        /// <summary>NextVariation "-1": play once, then stop (beliar, dead, innos, revolutionwon) [inferred].</summary>
        public bool IsOneShot => nextVariation == "-1";

        public bool Is(string loc, string sit, string day) =>
            MusicCatalog.Eq(location, loc) && MusicCatalog.Eq(situation, sit) && MusicCatalog.Eq(dayTime, day);

        public bool SameFamily(MusicFragmentData o) => o != null && Is(o.location, o.situation, o.dayTime);

        public MusicLinkData FirstLink(MusicLinkKind kind)
        {
            foreach (var l in links) if (l.kind == kind) return l;
            return null;
        }

        public override string ToString() => clip;
    }

    /// <summary>A short bridge (gCMusicTransition) between an exit connector and an entrance connector. Times in seconds.</summary>
    [Serializable]
    public sealed class MusicTransitionData
    {
        public string id, clip;
        public int connectIn, connectOut;
        public float fadeOutBegin, fadeOutEnd, fadeInBegin, fadeInEnd;
        /// <summary>Point of the transition sample that is aligned with the old fragment's exit link (Timing = SpecifiedOffset).</summary>
        public float offset;
        public float volume = 1f;
        public float duration;

        public override string ToString() => clip;
    }

    /// <summary>
    /// The music library (g3.mul) in engine-free form: fragments with their sync links and the transitions.
    /// Lookups follow Docs/formats-audio.md §2.4–2.5.
    /// </summary>
    [Serializable]
    public sealed class MusicCatalog
    {
        public List<MusicFragmentData> fragments = new List<MusicFragmentData>();
        public List<MusicTransitionData> transitions = new List<MusicTransitionData>();

        /// <summary>
        /// Fragments for a state, mimicking gCMusicSequencer::FindFragment + fallback [inferred]: exact match, then
        /// daytime "day", then situation "explore" (same location).
        /// </summary>
        public List<MusicFragmentData> FindFragments(string location, string situation, string dayTime)
        {
            var res = new List<MusicFragmentData>();
            var tries = new[] { (situation, dayTime), (situation, MusicDayTimes.Day), (MusicSituations.Explore, dayTime), (MusicSituations.Explore, MusicDayTimes.Day) };
            foreach (var (s, d) in tries)
            {
                foreach (var f in fragments)
                    if (f.Is(location, s, d)) res.Add(f);
                if (res.Count > 0) break;
            }
            return res;
        }

        public bool HasLocation(string location)
        {
            foreach (var f in fragments) if (Eq(f.location, location)) return true;
            return false;
        }

        public bool HasSituation(string location, string situation)
        {
            foreach (var f in fragments) if (Eq(f.location, location) && Eq(f.situation, situation)) return true;
            return false;
        }

        /// <summary>
        /// Transition candidates for leaving through an exit with <paramref name="connectOut"/> into an entrance with
        /// <paramref name="connectIn"/> [inferred from the data]: exact pair; for plain exits (0) the lead-ins NN→NN;
        /// an out-specific one with ConnectIn 0 (transition_30 after fight music); the 666 default (transition_00).
        /// </summary>
        public List<MusicTransitionData> FindTransitions(int connectOut, int connectIn)
        {
            var res = new List<MusicTransitionData>();
            foreach (var t in transitions) if (t.connectOut == connectOut && t.connectIn == connectIn) res.Add(t);
            if (res.Count == 0 && connectOut == 0)
                foreach (var t in transitions) if (t.connectIn == connectIn && t.connectOut == connectIn) res.Add(t);
            if (res.Count == 0)
                foreach (var t in transitions) if (t.connectOut == connectOut && t.connectIn == 0) res.Add(t);
            if (res.Count == 0)
                foreach (var t in transitions) if (t.connectIn == 666) res.Add(t);
            return res;
        }

        /// <summary>The fragment that follows <paramref name="f"/> when it ends: its NextVariation (same family), else itself; null for one-shots.</summary>
        public MusicFragmentData Successor(MusicFragmentData f)
        {
            if (f.IsOneShot) return null;
            if (!string.IsNullOrEmpty(f.nextVariation))
                foreach (var o in fragments)
                    if (o.SameFamily(f) && SameVariation(o.variation, f.nextVariation)) return o;
            return f;
        }

        /// <summary>"0" and "00" both denote the first variation.</summary>
        public static bool SameVariation(string a, string b)
        {
            int ia, ib;
            if (int.TryParse(a, out ia) && int.TryParse(b, out ib)) return ia == ib;
            return Eq(a, b);
        }

        public static bool Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
