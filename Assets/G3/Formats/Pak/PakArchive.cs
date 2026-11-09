using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace G3.Formats.Pak
{
    public sealed class PakEntry
    {
        public string Path;          // relative, forward slashes, e.g. "G3_Myrtana_Landscape_01/foo.xcmsh"
        public long Offset;
        public long PackedSize;
        public long Size;
        public uint Compression;     // 0 = stored, 2 = zlib
        public uint Encryption;
        public PakArchive Archive;

        public byte[] Read() => Archive.Read(this);
    }

    /// <summary>
    /// Genome engine "G3V0" volume (.pak, .p00, .p01 ...).
    /// Header (48 bytes): u32 version, char[4] "G3V0", u32 revision, u32 encryption,
    /// u32 compression, u32 reserved, u64 offsetFiles, u64 offsetDirectories, u64 volumeSize.
    /// Entry: FILETIME created/accessed/modified, u64 reserved, u32 attributes, then
    ///   dir  (attr &amp; 0x10): string name, u32 subdirCount, subdirs..., u32 fileCount, files...
    ///   file: u64 offset, u64 packedSize, u64 size, u32 encryption, u32 compression, string name, string sourcePath
    /// String: u32 length, chars, '\0' (no terminator when length == 0).
    /// </summary>
    public sealed class PakArchive : IDisposable
    {
        public readonly string FilePath;
        public readonly List<PakEntry> Entries = new List<PakEntry>();
        readonly FileStream _stream;
        readonly object _lock = new object();

        public PakArchive(string path)
        {
            FilePath = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var r = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);
            r.ReadUInt32();
            var magic = Encoding.ASCII.GetString(r.ReadBytes(4));
            if (magic != "G3V0") throw new InvalidDataException($"{path}: not a G3V0 volume ({magic})");
            r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
            r.ReadUInt64();
            long dirOffset = (long)r.ReadUInt64();
            _stream.Position = dirOffset;
            ReadEntry(r);
        }

        void ReadEntry(BinaryReader r)
        {
            _stream.Position += 24 + 8;
            uint attr = r.ReadUInt32();
            if ((attr & 0x10) != 0)
            {
                ReadString(r);
                uint dirs = r.ReadUInt32();
                for (int i = 0; i < dirs; i++) ReadEntry(r);
                uint files = r.ReadUInt32();
                for (int i = 0; i < files; i++) ReadEntry(r);
                return;
            }
            var e = new PakEntry
            {
                Offset = (long)r.ReadUInt64(),
                PackedSize = (long)r.ReadUInt64(),
                Size = (long)r.ReadUInt64(),
                Encryption = r.ReadUInt32(),
                Compression = r.ReadUInt32(),
                Archive = this,
            };
            e.Path = ReadString(r);
            ReadString(r); // original source path on the developers' machine
            Entries.Add(e);
        }

        static string ReadString(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len == 0) return string.Empty;
            var s = Encoding.GetEncoding(28591).GetString(r.ReadBytes(len));
            r.ReadByte();
            return s;
        }

        public byte[] Read(PakEntry e)
        {
            byte[] packed = new byte[e.PackedSize];
            lock (_lock)
            {
                _stream.Position = e.Offset;
                int read = 0;
                while (read < packed.Length)
                {
                    int n = _stream.Read(packed, read, packed.Length - read);
                    if (n <= 0) throw new EndOfStreamException(e.Path);
                    read += n;
                }
            }
            if (e.Compression == 0) return packed;
            if (e.Compression != 2) throw new NotSupportedException($"{e.Path}: compression {e.Compression}");
            var result = new byte[e.Size];
            // zlib stream = 2-byte header + raw deflate + adler32; DeflateStream works on both Mono and .NET
            using (var z = new DeflateStream(new MemoryStream(packed, 2, packed.Length - 2), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < result.Length)
                {
                    int n = z.Read(result, read, result.Length - read);
                    if (n <= 0) throw new EndOfStreamException(e.Path);
                    read += n;
                }
            }
            return result;
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>
    /// All volumes of a Gothic 3 "Data" folder layered like the game does it:
    /// Name.pak first, then Name.p00, Name.p01 ... with later volumes overriding earlier ones.
    /// </summary>
    public sealed class G3FileSystem : IDisposable
    {
        readonly List<PakArchive> _archives = new List<PakArchive>();
        readonly Dictionary<string, PakEntry> _byPath = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PakEntry> _byName = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<PakEntry> Files => _byPath.Values;

        public G3FileSystem(string dataDir, params string[] volumeNames)
        {
            foreach (var name in volumeNames)
            {
                var chain = new List<string>();
                var basePak = System.IO.Path.Combine(dataDir, name + ".pak");
                if (File.Exists(basePak)) chain.Add(basePak);
                for (int i = 0; i < 100; i++)
                {
                    var p = System.IO.Path.Combine(dataDir, $"{name}.p{i:00}");
                    if (!File.Exists(p)) break;
                    chain.Add(p);
                }
                foreach (var file in chain)
                {
                    var a = new PakArchive(file);
                    _archives.Add(a);
                    foreach (var e in a.Entries)
                    {
                        _byPath[name + "/" + e.Path] = e;
                        _byName[System.IO.Path.GetFileName(e.Path)] = e;
                    }
                }
            }
        }

        /// <summary>Lookup by "Volume/relative/path.ext".</summary>
        public PakEntry Get(string path) => _byPath.TryGetValue(path, out var e) ? e : null;

        /// <summary>Lookup by bare file name; the engine references most resources this way.</summary>
        public PakEntry GetByName(string fileName) => _byName.TryGetValue(fileName, out var e) ? e : null;

        public void Dispose()
        {
            foreach (var a in _archives) a.Dispose();
        }
    }
}
