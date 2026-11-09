using System;
using System.Collections.Generic;
using System.IO;

namespace Myrtana.Core
{
    /// <summary>
    /// What a save was made with and what a slot list shows: the content packs (id + hash), the play time, a short
    /// title written by the host (location, level …) and the real-world time of saving.
    /// </summary>
    public sealed class SaveHeader
    {
        public SaveHeader(IReadOnlyList<PackStamp> packs, double playSeconds, string title = null, DateTime savedAtUtc = default)
        {
            Packs = packs ?? Array.Empty<PackStamp>();
            PlaySeconds = playSeconds;
            Title = title ?? string.Empty;
            SavedAtUtc = savedAtUtc;
        }

        public IReadOnlyList<PackStamp> Packs { get; }
        /// <summary>Real seconds played in this game.</summary>
        public double PlaySeconds { get; }
        public string Title { get; }
        public DateTime SavedAtUtc { get; }
    }

    public readonly struct PackStamp
    {
        public PackStamp(string id, string contentHash)
        {
            Id = id;
            ContentHash = contentHash;
        }

        public string Id { get; }
        public string ContentHash { get; }
    }

    /// <summary>
    /// Save file layout: magic, format version, <see cref="SaveHeader"/>, then one length-prefixed section per
    /// <see cref="ISaveParticipant"/>. Unknown sections are skipped on load and sections missing from the file leave the
    /// participant's new-game state untouched, so modules can be added or removed between versions.
    /// </summary>
    public static class SaveFile
    {
        const int Magic = 0x5352594D; // "MYRS"
        /// <summary>2: header with title and save time.</summary>
        const int FormatVersion = 2;

        [ThreadStatic] static MemoryStream s_body;

        public static void Write(Stream output, SaveHeader header, IEnumerable<ISaveParticipant> participants) =>
            Write(output, header, participants, null);

        /// <summary>
        /// As <see cref="Write(Stream, SaveHeader, IEnumerable{ISaveParticipant})"/>; <paramref name="timings"/> (if given)
        /// gets each section's name and its write time in ms (fix-166: the recovery snapshot's per-section log).
        /// </summary>
        public static void Write(Stream output, SaveHeader header, IEnumerable<ISaveParticipant> participants,
                                 IList<KeyValuePair<string, double>> timings)
        {
            var w = new BinarySaveWriter(output, buffered: true);   // fix-171: flushed before every body write and at the end
            w.WriteInt32(Magic);
            w.WriteInt32(FormatVersion);
            WriteHeader(w, header);
            var sections = new List<ISaveParticipant>(participants);
            w.WriteInt32(sections.Count);
            // fix-155: one body buffer for all sections (the recovery snapshot runs every 2 min: no fresh MB-sized buffers);
            // fix-166: and for every save of this thread (it grew 64 KB -> 128 KB -> ... per save: garbage for a later GC)
            var body = s_body ??= new MemoryStream(64 * 1024);
            var bw = new BinarySaveWriter(body, buffered: true);
            foreach (var p in sections)
            {
                body.SetLength(0);
                long start = timings != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                p.Write(bw);
                bw.Flush();
                if (timings != null)
                    timings.Add(new KeyValuePair<string, double>(p.Section,
                        (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
                w.WriteString(p.Section);
                w.WriteInt32(p.Version);
                w.WriteInt32((int)body.Length);
                w.Flush();
                output.Write(body.GetBuffer(), 0, (int)body.Length);
            }
            w.Flush();
        }

        /// <summary>
        /// fix-171: the main-thread half of a save (the crash-recovery snapshot): every section is written into
        /// <paramref name="bodies"/> (reused by the caller), except the <see cref="ISaveCapture"/> ones, which only copy their state;
        /// <see cref="PendingSave.WriteTo"/> then writes the file on any thread, byte for byte what <see cref="Write(Stream, SaveHeader, IEnumerable{ISaveParticipant}, IList{KeyValuePair{string, double}})"/>
        /// writes. <paramref name="timings"/>: each section's main-thread ms.
        /// </summary>
        public static PendingSave Prepare(SaveHeader header, IEnumerable<ISaveParticipant> participants, MemoryStream bodies,
                                          IList<KeyValuePair<string, double>> timings = null)
        {
            var pending = new PendingSave(header, bodies);
            bodies.SetLength(0);
            var bw = new BinarySaveWriter(bodies, buffered: true);
            foreach (var p in participants)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                if (p is ISaveCapture c) pending.Add(p.Section, p.Version, -1, 0, c, c.Capture());
                else
                {
                    long at = bodies.Length;
                    p.Write(bw);
                    bw.Flush();
                    pending.Add(p.Section, p.Version, at, (int)(bodies.Length - at), null, null);
                }
                timings?.Add(new KeyValuePair<string, double>(p.Section,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
            }
            return pending;
        }

        /// <summary>A save prepared on the main thread (<see cref="Prepare"/>), written by <see cref="WriteTo"/> on any thread.</summary>
        public sealed class PendingSave
        {
            readonly SaveHeader _header;
            readonly MemoryStream _bodies;
            readonly List<(string name, int version, long at, int length, ISaveCapture capture, object state)> _sections =
                new List<(string, int, long, int, ISaveCapture, object)>(64);

            internal PendingSave(SaveHeader header, MemoryStream bodies) { _header = header; _bodies = bodies; }

            /// <summary>The save's header (fix-251: the slot lists show a save that is still being written).</summary>
            public SaveHeader Header => _header;

            internal void Add(string name, int version, long at, int length, ISaveCapture capture, object state) =>
                _sections.Add((name, version, at, length, capture, state));

            /// <summary>Writes the whole save file (the captured sections are encoded now).</summary>
            public void WriteTo(Stream output)
            {
                var w = new BinarySaveWriter(output, buffered: true);
                w.WriteInt32(Magic);
                w.WriteInt32(FormatVersion);
                WriteHeader(w, _header);
                w.WriteInt32(_sections.Count);
                var body = new MemoryStream(64 * 1024);
                var bw = new BinarySaveWriter(body, buffered: true);
                var src = _bodies.GetBuffer();
                foreach (var s in _sections)
                {
                    w.WriteString(s.name);
                    w.WriteInt32(s.version);
                    if (s.capture != null)
                    {
                        body.SetLength(0);
                        s.capture.WriteCaptured(s.state, bw);
                        bw.Flush();
                        w.WriteInt32((int)body.Length);
                        w.Flush();
                        output.Write(body.GetBuffer(), 0, (int)body.Length);
                    }
                    else
                    {
                        w.WriteInt32(s.length);
                        w.Flush();
                        output.Write(src, (int)s.at, s.length);
                    }
                }
                w.Flush();
            }
        }

        /// <summary>Reads the header and every section a participant is registered for. Returns the header.</summary>
        public static SaveHeader Read(Stream input, IEnumerable<ISaveParticipant> participants, IGameLog log = null)
        {
            var r = new BinarySaveReader(input);
            var header = ReadPreamble(r);
            var bySection = new Dictionary<string, ISaveParticipant>(StringComparer.Ordinal);
            foreach (var p in participants) bySection.Add(p.Section, p);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                int version = r.ReadInt32();
                int length = r.ReadInt32();
                var body = ReadExactly(input, length, name);
                if (bySection.TryGetValue(name, out var participant))
                    participant.Read(new BinarySaveReader(new MemoryStream(body)), version);
                else
                    (log ?? NullGameLog.Instance).Warning($"Save section '{name}' is not used by this game version and was skipped.");
            }
            return header;
        }

        /// <summary>
        /// fix-155: the file's structure is whole — magic, format, header, and every section's length inside the data, the
        /// last one ending exactly at the end. A file cut short by a crash or power loss (or zero-filled by the file system)
        /// fails; the section bodies are not parsed. <paramref name="data"/> is the whole file.
        /// </summary>
        public static bool IsComplete(byte[] data)
        {
            if (data == null || data.Length < 8) return false;
            try
            {
                var input = new MemoryStream(data, writable: false);
                var r = new BinarySaveReader(input);
                ReadPreamble(r);
                int count = r.ReadInt32();
                if (count < 0) return false;
                for (int i = 0; i < count; i++)
                {
                    r.ReadString();
                    r.ReadInt32();
                    int length = r.ReadInt32();
                    if (length < 0 || length > input.Length - input.Position) return false;
                    input.Position += length;
                }
                return input.Position == input.Length;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException ||
                                       ex is OverflowException || ex is FormatException)
            {
                return false;
            }
        }

        /// <summary>Only the header (for slot lists).</summary>
        public static SaveHeader ReadHeader(Stream input) => ReadPreamble(new BinarySaveReader(input));

        static SaveHeader ReadPreamble(BinarySaveReader r)
        {
            if (r.ReadInt32() != Magic) throw new InvalidDataException("Not a Myrtana save file.");
            int format = r.ReadInt32();
            if (format != FormatVersion) throw new InvalidDataException($"Unsupported save format {format}.");
            return ReadHeader(r);
        }

        static byte[] ReadExactly(Stream input, int length, string section)
        {
            var body = new byte[length];
            for (int read = 0; read < length;)
            {
                int n = input.Read(body, read, length - read);
                if (n <= 0) throw new EndOfStreamException($"Save section {section} is truncated.");
                read += n;
            }
            return body;
        }

        static void WriteHeader(BinarySaveWriter w, SaveHeader header)
        {
            w.WriteDouble(header.PlaySeconds);
            w.WriteString(header.Title);
            w.WriteUInt64((ulong)header.SavedAtUtc.Ticks);
            w.WriteInt32(header.Packs.Count);
            foreach (var pack in header.Packs)
            {
                w.WriteString(pack.Id);
                w.WriteString(pack.ContentHash);
            }
        }

        static SaveHeader ReadHeader(BinarySaveReader r)
        {
            double playSeconds = r.ReadDouble();
            string title = r.ReadString();
            var savedAt = new DateTime((long)r.ReadUInt64(), DateTimeKind.Utc);
            var packs = new PackStamp[r.ReadInt32()];
            for (int i = 0; i < packs.Length; i++) packs[i] = new PackStamp(r.ReadString(), r.ReadString());
            return new SaveHeader(packs, playSeconds, title, savedAt);
        }
    }
}
