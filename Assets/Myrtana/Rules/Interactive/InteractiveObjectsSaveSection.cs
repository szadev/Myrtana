using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Save section "world.objects": locks whose state differs from the content (opened, so far), doors and switches that
    /// were toggled, and the chests already filled (looted or not; their contents are saved with the inventories).
    /// </summary>
    public sealed class InteractiveObjectsSaveSection : ISaveParticipant
    {
        readonly InteractiveObjects _objects;

        public InteractiveObjectsSaveSection(InteractiveObjects objects) => _objects = objects;

        public string Section => "world.objects";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            w.WriteSortedStrings(_objects.LockToggled);
            w.WriteSortedStrings(_objects.MoverToggled);
            w.WriteSortedStrings(_objects.Filled);
        }

        public void Read(ISaveReader r, int version)
        {
            var locks = r.ReadStrings();
            var movers = r.ReadStrings();
            var filled = r.ReadStrings();
            _objects.Restore(locks, movers, filled);
        }
    }
}
