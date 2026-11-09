using System;
using System.IO;
using System.Text;

namespace G3.Formats.Audio
{
    /// <summary>
    /// Converts an IMA-ADPCM RIFF WAVE (format tag 0x11, as in Sound.pak voice/sfx_voi_fight_stumble_*) to 16-bit PCM
    /// WAVE, which Unity imports reliably. Standard Microsoft IMA ADPCM block layout: per block and channel a 4-byte
    /// header (i16 predictor, u8 step index, u8 0), then 4-byte groups per channel, 8 nibbles each, low nibble first.
    /// </summary>
    public static class ImaAdpcm
    {
        static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
            107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724,
            796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
            4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500,
            20350, 22385, 24623, 27086, 29794, 32767,
        };

        public static bool IsImaAdpcmWav(byte[] wav) => FindChunk(wav, "fmt ", out int fmt, out _) && BitConverter.ToUInt16(wav, fmt) == 0x11;

        public static byte[] ToPcm16(byte[] wav)
        {
            if (!FindChunk(wav, "fmt ", out int fmt, out _)) throw new InvalidDataException("WAV: no fmt chunk");
            if (BitConverter.ToUInt16(wav, fmt) != 0x11) throw new InvalidDataException("WAV: not IMA ADPCM");
            int channels = BitConverter.ToUInt16(wav, fmt + 2);
            int rate = BitConverter.ToInt32(wav, fmt + 4);
            int blockAlign = BitConverter.ToUInt16(wav, fmt + 12);
            if (!FindChunk(wav, "data", out int data, out int dataLen)) throw new InvalidDataException("WAV: no data chunk");
            dataLen = Math.Min(dataLen, wav.Length - data);

            var pcm = new MemoryStream();
            var w = new BinaryWriter(pcm);
            var pred = new int[channels];
            var index = new int[channels];
            var block = new short[channels][];
            for (int pos = data; pos + 4 * channels <= data + dataLen; pos += blockAlign)
            {
                int end = Math.Min(pos + blockAlign, data + dataLen);
                int p = pos;
                for (int c = 0; c < channels; c++, p += 4)
                {
                    pred[c] = BitConverter.ToInt16(wav, p);
                    index[c] = Math.Min(88, Math.Max(0, (int)wav[p + 2]));
                }
                int perChannel = 1 + (end - p) / channels * 2;
                for (int c = 0; c < channels; c++) { block[c] = new short[perChannel]; block[c][0] = (short)pred[c]; }
                int n = 1;
                while (p + 4 * channels <= end)
                {
                    for (int c = 0; c < channels; c++, p += 4)
                        for (int b = 0; b < 4; b++)
                        {
                            byte v = wav[p + b];
                            block[c][n + b * 2] = Decode(v & 0xF, ref pred[c], ref index[c]);
                            block[c][n + b * 2 + 1] = Decode(v >> 4, ref pred[c], ref index[c]);
                        }
                    n += 8;
                }
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < channels; c++) w.Write(block[c][i]);
            }
            return BuildPcmWav(pcm.ToArray(), channels, rate);
        }

        static short Decode(int nibble, ref int predictor, ref int index)
        {
            int step = StepTable[index];
            int diff = step >> 3;
            if ((nibble & 4) != 0) diff += step;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 1) != 0) diff += step >> 2;
            predictor += (nibble & 8) != 0 ? -diff : diff;
            predictor = Math.Max(short.MinValue, Math.Min(short.MaxValue, predictor));
            index = Math.Max(0, Math.Min(88, index + IndexTable[nibble]));
            return (short)predictor;
        }

        static byte[] BuildPcmWav(byte[] samples, int channels, int rate)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + samples.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((ushort)(channels * 2));
            w.Write((ushort)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(samples.Length);
            w.Write(samples);
            return ms.ToArray();
        }

        static bool FindChunk(byte[] wav, string id, out int offset, out int length)
        {
            offset = length = 0;
            if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return false;
            int p = 12;
            while (p + 8 <= wav.Length)
            {
                string cid = Encoding.ASCII.GetString(wav, p, 4);
                int len = BitConverter.ToInt32(wav, p + 4);
                if (cid == id) { offset = p + 8; length = len; return true; }
                if (len < 0) return false;
                p += 8 + len + (len & 1);
            }
            return false;
        }
    }
}
