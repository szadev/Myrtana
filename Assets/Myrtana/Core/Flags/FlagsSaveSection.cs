namespace Myrtana.Core
{
    /// <summary>Save section "core.flags": the story flags that are set.</summary>
    public sealed class FlagsSaveSection : ISaveParticipant
    {
        readonly GameEventFlags _flags;

        public FlagsSaveSection(GameEventFlags flags) => _flags = flags;

        public string Section => "core.flags";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteSortedStrings(_flags.All);

        public void Read(ISaveReader r, int version)
        {
            foreach (var flag in r.ReadStrings()) _flags.Set(flag);
        }
    }
}
