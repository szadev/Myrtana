using G3.Unity;
using Myrtana.Core;

namespace Myrtana.Unity
{
    /// <summary>
    /// Save section "remaster.snowTracks" (fix-178): the persistent snow tracks (<see cref="G3SnowV2.Tracks"/>, deflated,
    /// <see cref="SnowTrackStore.Write"/>), so a wolf's trail found before a save is still there after the load. Written as
    /// a length and 32-bit words (no base64). A save without the section (older, or tracks off) loads with no tracks:
    /// <see cref="SnowTrackSampler.Bind"/> applies <see cref="Pending"/> or clears them.
    /// perf-2: an <see cref="ISaveCapture"/>: the crash-recovery snapshot only copies the prints on the main thread
    /// (<see cref="SnowTrackStore.Capture"/>), the deflate runs on its worker (was 16-37 ms of main thread every 2 min).
    /// </summary>
    public sealed class SnowTracksSaveSection : ISaveParticipant, ISaveCapture
    {
        public string Section => "remaster.snowTracks";
        public int Version => 1;

        /// <summary>The tracks read by <see cref="Read"/>, applied by the next session bind (<see cref="SnowTrackSampler"/>: the
        /// new session's clock is set then, the prints' ages are relative to it); null = none read (a new game).</summary>
        public byte[] Pending { get; set; }

        /// <summary>The snapshot's copy (reused: the next recovery snapshot waits for the previous one's worker).</summary>
        readonly SnowTrackStore.Snapshot _captured = new SnowTrackStore.Snapshot();
        static readonly object None = new object();

        public object Capture() => (object)G3SnowV2.CaptureTracks(_captured) ?? None;

        public void WriteCaptured(object captured, ISaveWriter w) =>
            WriteBytes(w, captured is SnowTrackStore.Snapshot s ? SnowTrackStore.Encode(s) : null);

        public void Write(ISaveWriter w) => WriteBytes(w, G3SnowV2.SaveTracks());

        static void WriteBytes(ISaveWriter w, byte[] data)
        {
            int n = data?.Length ?? 0;
            w.WriteInt32(n);
            for (int i = 0; i < n; i += 4)
            {
                int v = data[i] | (i + 1 < n ? data[i + 1] << 8 : 0) | (i + 2 < n ? data[i + 2] << 16 : 0) | (i + 3 < n ? data[i + 3] << 24 : 0);
                w.WriteInt32(v);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int n = r.ReadInt32();
            var data = new byte[System.Math.Max(0, n)];
            for (int i = 0; i < n; i += 4)
            {
                int v = r.ReadInt32();
                for (int k = 0; k < 4 && i + k < n; k++) data[i + k] = (byte)(v >> (8 * k));
            }
            Pending = data;
        }
    }
}
