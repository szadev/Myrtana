using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Audio
{
    /// <summary>gCMusicTrigger.TransitionMode / gCEffectCommandMusicTrigger.Transition</summary>
    public enum MusicTransitionMode { None = 0, MatchingConnector = 1, SpecifiedConnector = 2, SpecifiedTransition = 3 }

    /// <summary>gCMusicTrigger.Exit: when the current fragment may be left.</summary>
    public enum MusicTriggerTime { Immediately = 0, NextExit = 1, NextPriorityExit = 2, EndOfFragment = 3 }

    /// <summary>gCMusicTrigger.Entrance: where the new fragment starts.</summary>
    public enum MusicFragmentPosition { Begin = 0, Current = 1, LoopEntrance = 2, RandomEntrance = 3 }

    /// <summary>gCMusicLink.Type</summary>
    public enum MusicLinkType { Exit = 0, Entrance = 1, Loop = 2, Generic = 3 }

    /// <summary>gCMusicTransition.Timing</summary>
    public enum MusicTransitionTiming { Centered = 0, OverCurrent = 1, OverNext = 2, SpecifiedOffset = 3 }

    /// <summary>A named state change request (gCMusicTrigger). Empty fields mean "keep the current value".</summary>
    public sealed class MusicTrigger
    {
        public string Id, Location, Situation, DayTime, Variation, Transition;
        public MusicTransitionMode TransitionMode;
        public MusicTriggerTime Exit;
        public MusicFragmentPosition Entrance;
        public int ConnectIn, ConnectOut;
        public bool IsStopTrigger, DisableTriggers;
    }

    /// <summary>gCMusicLink: a sync point inside a fragment (times in ms).</summary>
    public sealed class MusicLink
    {
        public uint OffsetMs;
        public int ConnectIn, ConnectOut;
        public bool IsPriority;
        public MusicLinkType Type;
    }

    /// <summary>gCMusicStream base fields shared by fragments and transitions (times in ms).</summary>
    public abstract class MusicStream
    {
        /// <summary>Sample as stored (".wav"); the shipped file is the .ogg of the same base name.</summary>
        public string Sample;
        public float Volume = 1;
        public uint LeadInEndMs, LeadOutBeginMs, DurationMs;
        public bool Shuffled;
        public string SampleBaseName => Path.GetFileNameWithoutExtension(Sample ?? "");
    }

    /// <summary>gCMusicFragment: one piece of music for (location, situation, daytime, variation).</summary>
    public sealed class MusicFragment : MusicStream
    {
        public string Location, Situation, DayTime, Variation, NextVariation;
        public readonly List<MusicLink> Links = new List<MusicLink>();
        public string Key => $"{Location}_{Situation}_{DayTime}";
    }

    /// <summary>gCMusicTransition: a short bridge played between an exit link (ConnectOut) and an entrance (ConnectIn).</summary>
    public sealed class MusicTransition : MusicStream
    {
        public string Id;
        public int ConnectIn, ConnectOut;
        public uint FadeInBeginMs, FadeInEndMs, FadeOutBeginMs, FadeOutEndMs, OffsetMs;
        public MusicTransitionTiming Timing;
    }

    /// <summary>
    /// Reader for <c>Library/g3.mul</c> (gCMusicLibrary::Load, loaded by gCMusicModule). Docs/formats-audio.md §2.
    /// GENOMFLE container; payload = u16 version (2), trigger list, fragment list, transition list; each list is
    /// u16 version (1) + u32 count + count × bCAccessorPropertyObject. Fragments carry a link sequence after their
    /// properties (u16 version 1, u32 count, count × gCMusicLink object).
    /// </summary>
    public sealed class MusicLibrary
    {
        public ushort Version;
        public readonly List<MusicTrigger> Triggers = new List<MusicTrigger>();
        public readonly List<MusicFragment> Fragments = new List<MusicFragment>();
        public readonly List<MusicTransition> Transitions = new List<MusicTransition>();

        public static MusicLibrary Read(byte[] data)
        {
            var g = new GenomeReader(data);
            var r = g.R;
            var lib = new MusicLibrary { Version = r.ReadUInt16() };
            if (lib.Version != 2) throw new InvalidDataException($"g3.mul: version {lib.Version}");

            foreach (var o in ReadList(g, "gCMusicTrigger"))
            {
                lib.Triggers.Add(new MusicTrigger
                {
                    Id = Str(g, o, "ID"), Location = Str(g, o, "Location"), Situation = Str(g, o, "Situation"),
                    DayTime = Str(g, o, "DayTime"), Variation = Str(g, o, "Variation"), Transition = Str(g, o, "Transition"),
                    TransitionMode = (MusicTransitionMode)(o.Get("TransitionMode")?.AsEnum() ?? 0),
                    Exit = (MusicTriggerTime)(o.Get("Exit")?.AsEnum() ?? 0),
                    Entrance = (MusicFragmentPosition)(o.Get("Entrance")?.AsEnum() ?? 0),
                    ConnectIn = o.Get("ConnectIn")?.AsInt() ?? 0, ConnectOut = o.Get("ConnectOut")?.AsInt() ?? 0,
                    IsStopTrigger = o.Get("IsStopTrigger")?.AsBool() ?? false,
                    DisableTriggers = o.Get("DisableTriggers")?.AsBool() ?? false,
                });
                g.Position = o.End;
            }

            foreach (var o in ReadList(g, "gCMusicFragment"))
            {
                var f = new MusicFragment
                {
                    Location = Str(g, o, "Location"), Situation = Str(g, o, "Situation"), DayTime = Str(g, o, "DayTime"),
                    Variation = Str(g, o, "Variation"), NextVariation = Str(g, o, "NextVariation"),
                };
                ReadStream(g, o, f);
                if (r.ReadUInt16() != 1) throw new InvalidDataException("g3.mul: link sequence version");
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var l = g.ReadObject();
                    f.Links.Add(new MusicLink
                    {
                        OffsetMs = (uint)(l.Get("Offset")?.AsInt() ?? 0),
                        ConnectIn = l.Get("ConnectIn")?.AsInt() ?? 0, ConnectOut = l.Get("ConnectOut")?.AsInt() ?? 0,
                        IsPriority = l.Get("IsPriority")?.AsBool() ?? false,
                        Type = (MusicLinkType)(l.Get("Type")?.AsEnum() ?? 0),
                    });
                    g.Position = l.End;
                }
                if (g.Position != o.End) throw new InvalidDataException($"g3.mul: fragment {f.Sample} ends at {g.Position:X}, expected {o.End:X}");
                lib.Fragments.Add(f);
            }

            foreach (var o in ReadList(g, "gCMusicTransition"))
            {
                var t = new MusicTransition
                {
                    Id = Str(g, o, "ID"),
                    ConnectIn = o.Get("ConnectIn")?.AsInt() ?? 0, ConnectOut = o.Get("ConnectOut")?.AsInt() ?? 0,
                    FadeInBeginMs = U(o, "FadeInBegin"), FadeInEndMs = U(o, "FadeInEnd"),
                    FadeOutBeginMs = U(o, "FadeOutBegin"), FadeOutEndMs = U(o, "FadeOutEnd"), OffsetMs = U(o, "Offset"),
                    Timing = (MusicTransitionTiming)(o.Get("Timing")?.AsEnum() ?? 0),
                };
                ReadStream(g, o, t);
                g.Position = o.End;
                lib.Transitions.Add(t);
            }
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"g3.mul: {g.PayloadEnd - g.Position} bytes left over");
            return lib;
        }

        /// <summary>
        /// Fragments for a state, mimicking gCMusicSequencer::FindFragment + fallback (inferred): exact
        /// location/situation/daytime first, then daytime "day", then situation "explore" (same location).
        /// </summary>
        public List<MusicFragment> FindFragments(string location, string situation, string dayTime)
        {
            var res = new List<MusicFragment>();
            foreach (var (s, d) in new[] { (situation, dayTime), (situation, "day"), ("explore", dayTime), ("explore", "day") })
            {
                foreach (var f in Fragments)
                    if (Eq(f.Location, location) && Eq(f.Situation, s) && Eq(f.DayTime, d)) res.Add(f);
                if (res.Count > 0) break;
            }
            return res;
        }

        /// <summary>
        /// Transition candidates for leaving through an exit link with <paramref name="connectOut"/> into an entrance
        /// with <paramref name="connectIn"/> [inferred from the data, Docs/formats-audio.md §2.4]: exact (out, in) pair;
        /// else for plain exits (connector 0) the "lead-in" transitions with ConnectIn == ConnectOut == in; else an
        /// out-specific transition with ConnectIn 0 (any target, e.g. transition_30 after fight music); else the
        /// 666/666 default (transition_00).
        /// </summary>
        public List<MusicTransition> FindTransitions(int connectOut, int connectIn)
        {
            var res = new List<MusicTransition>();
            foreach (var t in Transitions) if (t.ConnectOut == connectOut && t.ConnectIn == connectIn) res.Add(t);
            if (res.Count == 0 && connectOut == 0)
                foreach (var t in Transitions) if (t.ConnectIn == connectIn && t.ConnectOut == connectIn) res.Add(t);
            if (res.Count == 0)
                foreach (var t in Transitions) if (t.ConnectOut == connectOut && t.ConnectIn == 0) res.Add(t);
            if (res.Count == 0)
                foreach (var t in Transitions) if (t.ConnectIn == 666) res.Add(t);
            return res;
        }

        static bool Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        static IEnumerable<PropertyObject> ReadList(GenomeReader g, string cls)
        {
            ushort v = g.R.ReadUInt16();
            if (v != 1) throw new InvalidDataException($"g3.mul: {cls} list version {v}");
            int n = g.R.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var o = g.ReadObject();
                if (o == null || o.ClassName != cls) throw new InvalidDataException($"g3.mul: expected {cls}, got {o?.ClassName}");
                yield return o;
            }
        }

        static void ReadStream(GenomeReader g, PropertyObject o, MusicStream s)
        {
            s.Sample = Str(g, o, "Sample");
            s.Volume = o.Get("Volume")?.AsFloat() ?? 1f;
            s.LeadInEndMs = U(o, "LeadInEnd");
            s.LeadOutBeginMs = U(o, "LeadOutBegin");
            s.DurationMs = U(o, "Duration");
            s.Shuffled = o.Get("Shuffled")?.AsBool() ?? false;
        }

        static uint U(PropertyObject o, string name) => (uint)(o.Get(name)?.AsInt() ?? 0);

        internal static string Str(GenomeReader g, PropertyObject o, string name)
        {
            var p = o.Get(name);
            return p == null ? "" : g.StrFrom(p.Value);
        }
    }

    /// <summary>One file of Music.pak, classified by its name (fragments/&lt;location&gt;_&lt;situation&gt;_&lt;daytime&gt;_&lt;nn&gt;[_suffix].ogg).</summary>
    public sealed class MusicFile
    {
        public string Path;                       // pak path, e.g. "fragments/faring_explore_day_01.ogg"
        public bool IsTransition;
        public string Location, Situation, DayTime, Variant, Suffix;   // fragments only; Suffix e.g. "rejected"
        public string BaseName => System.IO.Path.GetFileNameWithoutExtension(Path);

        public static MusicFile Parse(string pakPath)
        {
            var f = new MusicFile { Path = pakPath };
            var name = System.IO.Path.GetFileNameWithoutExtension(pakPath);
            if (pakPath.StartsWith("transitions/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("transition_", StringComparison.OrdinalIgnoreCase))
            {
                f.IsTransition = true;
                f.Variant = name.Substring(name.IndexOf('_') + 1);
                return f;
            }
            var parts = name.Split('_');
            if (parts.Length >= 4)
            {
                f.Location = parts[0]; f.Situation = parts[1]; f.DayTime = parts[2]; f.Variant = parts[3];
                if (parts.Length > 4) f.Suffix = string.Join("_", parts, 4, parts.Length - 4);
            }
            return f;
        }
    }
}
