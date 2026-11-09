using System;
using System.Collections.Generic;
using G3.Formats.Genome;
using G3.Formats.World;

namespace G3.Formats.Items
{
    public enum WorldItemKind
    {
        Loose,          // item lying in the world (root-level gCItem_PS entity), placed by its world matrix
        Plant,          // loose item with UseType Plant (herbs, mushrooms … picked up like items)
        NpcPart,        // Body_/Head_/Hair_/Beard_ child of an NPC (visual parts, slot Body/Head/Hair/Beard)
        NpcEquipment,   // other gCItem_PS child of an NPC (weapons etc.; slot != body parts)
        // (items under furniture / other items / nested RootEntity are Loose or Plant; see WorldItem.ParentName)
    }

    /// <summary>A gCItem_PS entity of an entity archive, classified. Docs/formats-items.md §4.</summary>
    public sealed class WorldItem
    {
        public WorldItemKind Kind;
        public WorldEntity Entity;
        public ItemInfo Item;            // decoded from the entity's own (inline) property sets
        public string ParentName;        // name of the parent entity (null at root)
        public int Depth;                // 0 = RootEntity, 1 = directly under root, …
        public Guid Template => Entity.Creator;
        public float X => Entity.X;
        public float Y => Entity.Y;
        public float Z => Entity.Z;
    }

    public enum ContainerKind { Chest, NpcInventory, ItemInventory, Other }

    /// <summary>An entity carrying gCInventory_PS (chest, NPC, …) with its decoded stacks and lock.</summary>
    public sealed class WorldContainer
    {
        public ContainerKind Kind;
        public WorldEntity Entity;
        public Inventory Inventory;
        public LockInfo Lock;
        public InteractionInfo Interaction;
    }

    /// <summary>An entity with gCInteraction_PS / gCDoor_PS / gCLock_PS that is not an item or NPC (doors, beds, benches, anvils…).</summary>
    public sealed class WorldInteractable
    {
        public WorldEntity Entity;
        public InteractionInfo Interaction;
        public DoorInfo Door;
        public LockInfo Lock;
        public string Visual;            // mesh or actor resource
    }

    /// <summary>Classifies the entities of one archive (read with keepRaw: true).</summary>
    public static class WorldItemScanner
    {
        public static void Scan(List<WorldEntity> list, List<WorldItem> items, List<WorldContainer> containers, List<WorldInteractable> interactables)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var sets = e.RawPropertySets;
                if (sets == null) continue;
                PropertyObject item = null, inv = null, npc = null, inter = null, door = null, lck = null;
                long invPost = -1;
                string visual = null;
                for (int k = 0; k < sets.Count; k++)
                {
                    var ps = sets[k];
                    switch (ps.ClassName)
                    {
                        case "gCItem_PS": item = ps; break;
                        case "gCInventory_PS": inv = ps; invPost = e.RawPostDataStart != null ? e.RawPostDataStart[k] : -1; break;
                        case "gCNPC_PS": npc = ps; break;
                        case "gCInteraction_PS": inter = ps; break;
                        case "gCDoor_PS": door = ps; break;
                        case "gCLock_PS": lck = ps; break;
                        case "eCVisualAnimation_PS": visual = Props.Str(e.Source, ps, "ResourceFilePath"); break;
                        case "eCVisualMeshStatic_PS":
                        case "eCVisualMeshDynamic_PS": visual = visual ?? Props.Str(e.Source, ps, "ResourceFileName"); break;
                    }
                }
                var g = e.Source;
                if (item != null)
                {
                    var info = ItemInfo.From(g, sets, e.Name, e.Creator);
                    var w = new WorldItem { Entity = e, Item = info };
                    int p = e.Parent;
                    if (p >= 0) w.ParentName = list[p].Name;
                    for (int q = p; q >= 0; q = list[q].Parent) w.Depth++;
                    bool npcParent = p >= 0 && HasClass(list[p], "gCNPC_PS");
                    if (npcParent)
                        w.Kind = info.UseType == UseType.Body || info.UseType == UseType.Head || info.UseType == UseType.Hair ||
                                 info.UseType == UseType.Beard || info.Slot == EquipSlot.Body || info.Slot == EquipSlot.Head ||
                                 info.Slot == EquipSlot.Hair || info.Slot == EquipSlot.Beard ? WorldItemKind.NpcPart : WorldItemKind.NpcEquipment;
                    // Children of RootEntity (SysDyn nests several RootEntity levels) and of furniture/other items are
                    // ordinary loose items placed by their absolute world matrix; ParentName records the support.
                    else w.Kind = info.UseType == UseType.Plant ? WorldItemKind.Plant : WorldItemKind.Loose;
                    items?.Add(w);
                }
                if (inv != null && containers != null)
                {
                    var c = new WorldContainer
                    {
                        Entity = e,
                        Inventory = invPost >= 0 ? Inventory.Read(g, inv, invPost) : null,
                        Lock = LockInfo.From(lck),
                        Interaction = InteractionInfo.From(g, inter),
                    };
                    c.Kind = npc != null ? ContainerKind.NpcInventory : item != null ? ContainerKind.ItemInventory
                           : c.Interaction?.UseType == UseType.Chest ? ContainerKind.Chest : ContainerKind.Other;
                    containers.Add(c);
                }
                if (item == null && npc == null && inv == null && (inter != null || door != null || lck != null) && interactables != null)
                    interactables.Add(new WorldInteractable
                    {
                        Entity = e,
                        Interaction = InteractionInfo.From(g, inter),
                        Door = DoorInfo.From(g, door),
                        Lock = LockInfo.From(lck),
                        Visual = visual,
                    });
            }
        }

        static bool HasClass(WorldEntity e, string cls)
        {
            if (e.RawPropertySets == null) return false;
            foreach (var ps in e.RawPropertySets) if (ps.ClassName == cls) return true;
            return false;
        }
    }
}
