using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Save section "rules.quickslots": the item of each quick slot (read after "rules.items", which fills the inventory).</summary>
    public sealed class QuickSlotsSaveSection : ISaveParticipant
    {
        readonly QuickSlots _slots;

        public QuickSlotsSaveSection(QuickSlots slots) => _slots = slots;

        public string Section => "rules.quickslots";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            w.WriteInt32(QuickSlots.Count);
            for (int i = 0; i < QuickSlots.Count; i++) w.WriteString(_slots.ItemAt(i));
        }

        public void Read(ISaveReader r, int version)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string item = r.ReadString();
                if (item != null) _slots.Assign(i, item);
            }
        }
    }
}
