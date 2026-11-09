using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Items
{
    /// <summary>One gCInventoryStack (a sub-object in gCInventory_PS post data). Docs/formats-items.md §3.</summary>
    public sealed class InventoryStack
    {
        public Guid Template;            // "Template" eCTemplateEntityProxy → item template GUID (item header)
        public int Amount, Quality;
        public int Type;                 // world: "Generated"/"Type" gEStackType (0 normal, 1 treasure, 2 merchandise); see Raw
        public int QuickSlot = -1, SortIndex = -1;
        public bool Learned;
        public int ActivationCount;
        public PropertyObject Raw;
    }

    /// <summary>One equipped slot (gCInventorySlot): which stack sits in which gESlot.</summary>
    public sealed class InventorySlot
    {
        public int Index;                // position in the slot array (= gESlot)
        public EquipSlot Slot;           // "Slot" property
        public int Stack = -1;           // "Stack" property: index into Stacks
        public PropertyObject Raw;
    }

    /// <summary>
    /// gCInventory_PS: properties TreasureSet1..5 (names of gCTreasureSet_PS templates) + post data
    /// (g3dit gCInventory_PS.readPostClassVersion):
    /// u16 1, u32 n × sub-object gCInventoryStack; u16 1, u32 slotCount (19) × (sub-object gCInventorySlot | 01 00 00 empty);
    /// 5 × eCEntityProxy (cached treasure-set entities, u16 1 + bool + [bCGuid]).
    /// </summary>
    public sealed class Inventory
    {
        public readonly string[] TreasureSets = new string[5];
        public bool GeneratedPlunder, GeneratedTrade;
        public readonly List<InventoryStack> Stacks = new List<InventoryStack>();
        public readonly List<InventorySlot> Slots = new List<InventorySlot>();
        /// <summary>True when the post data was consumed to exactly the object end.</summary>
        public bool ParsedExactly;

        /// <param name="postStart">Stream offset right after the PS class version (RawPostDataStart / PostDataStart).</param>
        public static Inventory Read(GenomeReader g, PropertyObject ps, long postStart)
        {
            var inv = new Inventory
            {
                GeneratedPlunder = Props.Bool(ps, "GeneratedPlunder"),
                GeneratedTrade = Props.Bool(ps, "GeneratedTrade"),
            };
            for (int i = 0; i < 5; i++) inv.TreasureSets[i] = Props.Str(g, ps, $"TreasureSet{i + 1}") ?? "";
            long save = g.Position;
            try
            {
                g.Position = postStart;
                var r = g.R;
                r.ReadUInt16();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var o = g.ReadObject();
                    if (o == null) continue;
                    g.Position = o.End;
                    inv.Stacks.Add(new InventoryStack
                    {
                        Raw = o,
                        Template = Props.Proxy(o, "Template"),
                        Amount = Props.Int(o, "Amount"),
                        Quality = Props.Int(o, "Quality"),
                        Type = o.Get("Type") != null ? Props.Enum(o, "Type") : Props.Enum(o, "Generated"),
                        QuickSlot = Props.Int(o, "QuickSlot", -1),
                        SortIndex = Props.Int(o, "SortIndex", -1),
                        Learned = Props.Bool(o, "Learned"),
                        ActivationCount = Props.Int(o, "ActivationCount"),
                    });
                }
                r.ReadUInt16();
                int slots = r.ReadInt32();
                for (int i = 0; i < slots; i++)
                {
                    var o = g.ReadObject();
                    if (o == null) continue;
                    g.Position = o.End;
                    inv.Slots.Add(new InventorySlot { Index = i, Raw = o, Slot = (EquipSlot)Props.Enum(o, "Slot"), Stack = Props.Int(o, "Stack", -1) });
                }
                for (int i = 0; i < 5; i++)
                {
                    r.ReadUInt16();
                    if (r.ReadByte() != 0) g.Skip(20);
                }
                inv.ParsedExactly = g.Position == ps.End;
            }
            catch (Exception) { inv.ParsedExactly = false; }
            finally { g.Position = save; }
            return inv;
        }
    }

    /// <summary>gCLock_PS: Status (gELockStatus 0 locked, 1 unlocked), Key (template proxy), Difficulty (-1 none, 0..3+ lockpick level).</summary>
    public sealed class LockInfo
    {
        public bool Locked;
        public Guid Key;
        public int Difficulty;

        public static LockInfo From(PropertyObject ps) => ps == null ? null : new LockInfo
        {
            Locked = Props.Enum(ps, "Status") == 0,
            Key = Props.Proxy(ps, "Key"),
            Difficulty = Props.Int(ps, "Difficulty", -1),
        };
    }

    /// <summary>gCDoor_PS (mover): Status (gEDoorStatus 0 open, 1 closed), open/close animations (.FXM).</summary>
    public sealed class DoorInfo
    {
        public bool Closed;
        public string OpenAni, CloseAni;
        public bool Enabled;

        public static DoorInfo From(GenomeReader g, PropertyObject ps) => ps == null ? null : new DoorInfo
        {
            Closed = Props.Enum(ps, "Status") == 1,
            OpenAni = Props.Str(g, ps, "OpenAni"),
            CloseAni = Props.Str(g, ps, "CloseAni"),
            Enabled = Props.Bool(ps, "IsEnabled"),
        };
    }

    /// <summary>gCInteraction_PS: what the hero/NPCs can do with the entity (UseType: Chest 106, Door 101, Bed 74, Bench 84 …).</summary>
    public sealed class InteractionInfo
    {
        public UseType UseType;
        public string ScriptUseFunc;
        public int FocusNameType;        // gEFocusNameType 0 skeleton, 1 entity, 2 bone, 3 disabled
        public Guid Owner;               // eCEntityProxy (owning NPC entity GUID), Empty when none

        public static InteractionInfo From(GenomeReader g, PropertyObject ps) => ps == null ? null : new InteractionInfo
        {
            UseType = (UseType)Props.Enum(ps, "UseType"),
            ScriptUseFunc = Props.Str(g, ps, "ScriptUseFunc"),
            FocusNameType = Props.Enum(ps, "FocusNameType"),
            Owner = Props.Proxy(ps, "Owner"),
        };
    }
}

namespace G3.Formats.Items
{
    /// <summary>
    /// A treasure set template (Treasure/**: gCTreasureSet_PS + gCInventory_PS). Entities name up to five sets in
    /// gCInventory_PS.TreasureSet1..5; the engine fills the container from them at runtime (gETreasureDistribution,
    /// Min/MaxTransferStacks). The candidate stacks are the gCInventory_PS stacks of the set template.
    /// </summary>
    public sealed class TreasureSet
    {
        public string Name;
        public int Distribution;         // gETreasureDistribution: 0 Plunder, 1 Unique, 2 Trophy, 3 Weaponry, 4 Trade_Generate, 5 Trade_Refresh, 6 Mining, 7 Pickpocket, 8 NotRandom, 9 Guard, 10 Ammunition
        public int MinStacks, MaxStacks;
        public Inventory Inventory;

        public static TreasureSet From(Templates.TemplateFile t)
        {
            var h = t.Main;
            if (h == null) return null;
            PropertyObject ts = null, inv = null; long invPost = -1;
            for (int i = 0; i < h.PropertySets.Count; i++)
            {
                var ps = h.PropertySets[i];
                if (ps.ClassName == "gCTreasureSet_PS") ts = ps;
                else if (ps.ClassName == "gCInventory_PS") { inv = ps; invPost = h.PostDataStart[i]; }
            }
            if (ts == null) return null;
            return new TreasureSet
            {
                Name = t.Name,
                Distribution = Props.Enum(ts, "TreasureDistribution"),
                MinStacks = Props.Int(ts, "MinTransferStacks"),
                MaxStacks = Props.Int(ts, "MaxTransferStacks"),
                Inventory = inv != null ? Inventory.Read(h.Source, inv, invPost) : null,
            };
        }
    }
}
