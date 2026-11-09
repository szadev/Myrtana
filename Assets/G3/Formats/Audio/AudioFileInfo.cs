using System;
using System.Collections.Generic;
using System.Text;

namespace G3.Formats.Audio
{
    /// <summary>
    /// Header facts of a shipped audio file (Ogg Vorbis or RIFF WAVE), enough to check Unity compatibility and
    /// durations. Pure C#, no decoding.
    /// </summary>
    public sealed class AudioFileInfo
    {
        public string Container;        // "ogg", "wav", or "unknown"
        public string Codec;            // "vorbis", "pcm", "ms-adpcm", "ima-adpcm", "mp3", "0x....", …
        public int Channels, SampleRate, BitsPerSample, NominalBitrate;
        public long Samples;            // per channel; -1 when unknown
        public double Seconds => Samples >= 0 && SampleRate > 0 ? (double)Samples / SampleRate : -1;
        public int CuePoints;           // WAV 'cue ' chunk entries
        public readonly List<string> Comments = new List<string>();   // Vorbis comments / WAV LIST INFO
        public string Vendor;

        public static AudioFileInfo Read(byte[] d)
        {
            if (d.Length >= 4 && d[0] == 'O' && d[1] == 'g' && d[2] == 'g' && d[3] == 'S') return ReadOgg(d);
            if (d.Length >= 12 && Encoding.ASCII.GetString(d, 0, 4) == "RIFF" && Encoding.ASCII.GetString(d, 8, 4) == "WAVE") return ReadWav(d);
            return new AudioFileInfo { Container = "unknown", Codec = "", Samples = -1 };
        }

        static AudioFileInfo ReadOgg(byte[] d)
        {
            var info = new AudioFileInfo { Container = "ogg", Codec = "?", Samples = -1 };
            // Reassemble the first packets (identification, comment) from the page segment tables.
            var packets = new List<byte[]>();
            var cur = new List<byte>();
            int pos = 0;
            while (pos + 27 <= d.Length && packets.Count < 2 && d[pos] == 'O' && d[pos + 1] == 'g')
            {
                int nseg = d[pos + 26];
                int data = pos + 27 + nseg;
                for (int s = 0; s < nseg && packets.Count < 2; s++)
                {
                    int len = d[pos + 27 + s];
                    for (int k = 0; k < len && data + k < d.Length; k++) cur.Add(d[data + k]);
                    data += len;
                    if (len < 255) { packets.Add(cur.ToArray()); cur.Clear(); }
                }
                int total = 0;
                for (int s = 0; s < nseg; s++) total += d[pos + 27 + s];
                pos += 27 + nseg + total;
            }
            if (packets.Count > 0 && packets[0].Length >= 30 && packets[0][0] == 1 && Encoding.ASCII.GetString(packets[0], 1, 6) == "vorbis")
            {
                var p = packets[0];
                info.Codec = "vorbis";
                info.Channels = p[11];
                info.SampleRate = BitConverter.ToInt32(p, 12);
                info.NominalBitrate = BitConverter.ToInt32(p, 20);
            }
            if (packets.Count > 1 && packets[1].Length > 11 && packets[1][0] == 3)
            {
                var p = packets[1];
                int o = 7;
                int vl = BitConverter.ToInt32(p, o); o += 4;
                if (vl >= 0 && o + vl <= p.Length) { info.Vendor = Encoding.UTF8.GetString(p, o, vl); o += vl; }
                if (o + 4 <= p.Length)
                {
                    int n = BitConverter.ToInt32(p, o); o += 4;
                    for (int i = 0; i < n && o + 4 <= p.Length; i++)
                    {
                        int l = BitConverter.ToInt32(p, o); o += 4;
                        if (l < 0 || o + l > p.Length) break;
                        info.Comments.Add(Encoding.UTF8.GetString(p, o, l)); o += l;
                    }
                }
            }
            // Duration: granule position of the last page.
            for (int i = d.Length - 27; i >= 0; i--)
            {
                if (d[i] == 'O' && d[i + 1] == 'g' && d[i + 2] == 'g' && d[i + 3] == 'S' && d[i + 4] == 0)
                {
                    info.Samples = BitConverter.ToInt64(d, i + 6);
                    break;
                }
            }
            return info;
        }

        static AudioFileInfo ReadWav(byte[] d)
        {
            var info = new AudioFileInfo { Container = "wav", Codec = "?", Samples = -1 };
            int pos = 12, tag = 0, blockAlign = 0, byteRate = 0;
            long dataSize = -1, factSamples = -1;
            while (pos + 8 <= d.Length)
            {
                string id = Encoding.ASCII.GetString(d, pos, 4);
                int size = BitConverter.ToInt32(d, pos + 4);
                int body = pos + 8;
                if (size < 0) break;
                switch (id)
                {
                    case "fmt ":
                        tag = BitConverter.ToUInt16(d, body);
                        info.Channels = BitConverter.ToUInt16(d, body + 2);
                        info.SampleRate = BitConverter.ToInt32(d, body + 4);
                        byteRate = BitConverter.ToInt32(d, body + 8);
                        blockAlign = BitConverter.ToUInt16(d, body + 12);
                        info.BitsPerSample = BitConverter.ToUInt16(d, body + 14);
                        break;
                    case "data": dataSize = Math.Min(size, d.Length - body); break;
                    case "fact": factSamples = BitConverter.ToUInt32(d, body); break;
                    case "cue ": info.CuePoints = BitConverter.ToInt32(d, body); break;
                    case "LIST":
                        if (body + 4 <= d.Length && Encoding.ASCII.GetString(d, body, 4) == "INFO")
                            for (int p = body + 4; p + 8 <= Math.Min(d.Length, body + size);)
                            {
                                int l = BitConverter.ToInt32(d, p + 4);
                                if (l < 0 || p + 8 + l > d.Length) break;
                                info.Comments.Add(Encoding.ASCII.GetString(d, p, 4) + "=" + Encoding.ASCII.GetString(d, p + 8, l).TrimEnd('\0'));
                                p += 8 + l + (l & 1);
                            }
                        break;
                }
                pos = body + size + (size & 1);
            }
            switch (tag)
            {
                case 1: info.Codec = "pcm"; break;
                case 2: info.Codec = "ms-adpcm"; break;
                case 3: info.Codec = "float"; break;
                case 0x11: info.Codec = "ima-adpcm"; break;
                case 0x55: info.Codec = "mp3"; break;
                case 0xFFFE: info.Codec = "extensible"; break;
                default: info.Codec = "0x" + tag.ToString("X4"); break;
            }
            info.NominalBitrate = byteRate * 8;
            if (factSamples >= 0) info.Samples = factSamples;
            else if (dataSize >= 0 && tag == 1 && blockAlign > 0) info.Samples = dataSize / blockAlign;
            else if (dataSize >= 0 && byteRate > 0 && info.SampleRate > 0) info.Samples = dataSize * info.SampleRate / byteRate;
            return info;
        }
    }
}
