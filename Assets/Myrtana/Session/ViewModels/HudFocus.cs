using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>
    /// How the focused thing stands to the hero; the HUD colours its focus name by this (G3 focus-name colours). Objects
    /// (fix-230, <see cref="HudFocus.ObjectRelation"/>): <see cref="Item"/> without a lock, <see cref="Neutral"/> with an open
    /// lock, <see cref="Locked"/> (G3 draws it as hostile red) and <see cref="Unlooted"/> (G3 draws it as panic orange).
    /// </summary>
    public enum FocusRelation { Neutral, Friendly, Party, Dead, Angry, Hostile, Panic, Item, Locked, Unlooted }

    /// <summary>The entity in the hero's focus as the HUD shows it: its name and relation.</summary>
    public readonly struct FocusTarget
    {
        public FocusTarget(string name, FocusRelation relation)
        {
            Name = name;
            Relation = relation;
        }

        public string Name { get; }
        public FocusRelation Relation { get; }
    }

    /// <summary>Names and relations of focus targets (NPCs and world items) for the HUD.</summary>
    public static class HudFocus
    {
        /// <param name="locked">The hero's combat target: shown as hostile like G3's "locked" focus.</param>
        public static FocusTarget Npc(GameSession session, ActorId actor, bool locked = false) =>
            new FocusTarget(NpcName(session, actor), Relation(session, actor, locked));

        /// <summary>
        /// An interactive object's focus name: the text <c>FO_&lt;entity name&gt;</c> (G3 gCGUIEntityRenderer), or nothing
        /// when the string table has none (fix-88: G3 draws the localized string only; never the raw entity name or the
        /// localizer's "[FO_…]" marker). The 23 treasure spots without an FO_ string show no name; the 3 doors without one
        /// fall back to a door text (<see cref="FocusNameIds"/>, fix-250).
        /// </summary>
        public static string ObjectName(GameSession session, InteractiveObjectDef def)
        {
            if (def == null) return string.Empty;
            foreach (var id in FocusNameIds(def))
                if (session.Localizer.TryText(id, out var text)) return text;
            return string.Empty;
        }

        /// <summary>A generic door name of the game's string table («Дверь» / "Door" / "Tür" in every language).</summary>
        public const string GenericDoorText = "FO_G3_Object_Interact_Animated_Door_01";

        /// <summary>
        /// The texts tried for an object's focus name, in order: G3's <c>FO_&lt;entity name&gt;</c>; for a door
        /// (fix-250 [owner]: Fabio's locked door in Lago showed no name) also the door's text without the placement
        /// suffix (<c>MoraSul_TempleDoor_Tester_01</c> → <c>FO_MoraSul_TempleDoor_Tester</c> «Дверь храма», the string
        /// CP 1.75 ships for it) and then <see cref="GenericDoorText"/>. CP 1.75 itself (Game.dll
        /// gCGUIEntityRenderer::DrawFocusName → eCLocAdmin::GetString, empty for a missing id,) draws nothing for the
        /// three doors whose <c>FO_</c> id is missing (G3_Object_Interact_Animated_Door_Fabio and the two temple door
        /// testers): a port fix of missing data, like <see cref="PatchStrings"/>. Other objects keep G3's rule.
        /// </summary>
        public static IEnumerable<TextId> FocusNameIds(InteractiveObjectDef def)
        {
            yield return def.FocusName;
            if (def.Kind != InteractiveKind.Door || string.IsNullOrEmpty(def.Name)) yield break;
            string stem = WithoutPlacementSuffix(def.Name);
            if (stem != def.Name) yield return new TextId("FO_" + stem);
            yield return new TextId(GenericDoorText);
        }

        /// <summary>"Name_01" → "Name"; names without a numeric suffix stay.</summary>
        public static string WithoutPlacementSuffix(string name)
        {
            int us = name.LastIndexOf('_');
            if (us <= 0 || us == name.Length - 1) return name;
            for (int i = us + 1; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return name;
            return name.Substring(0, us);
        }

        /// <summary>An interactive object's focus: its name (<see cref="ObjectName"/>) and relation (<see cref="ObjectRelation"/>).</summary>
        public static FocusTarget Object(GameSession session, InteractiveObjectDef def) =>
            new FocusTarget(ObjectName(session, def), ObjectRelation(session, def));

        /// <summary>
        /// The colour state of an object's focus name (fix-230; RE CP 1.75 Game.dll <c>gCGUIEntityRenderer::GetAttitude</c>
        /// 0x1007a0c0): no gCLock_PS → the item colour (cream); a shut lock (Status 0) → red, chests and doors
        /// alike; an open lock on a UseType Chest → bright orange while its plunder was not generated yet (never opened;
        /// G3's GeneratedPlunder flag) or while it still holds a stack whose item category is not None (0) or Spellbook (9)
        /// and whose stack type is not Merchandise, else tan; any other object with an open lock (a door) → tan. The port has
        /// no None-category items or merchandise stacks in chests, so an unknown item id is the only stack skipped besides
        /// spellbooks.
        /// </summary>
        public static FocusRelation ObjectRelation(GameSession session, InteractiveObjectDef def)
        {
            if (def?.Lock == null) return FocusRelation.Item;
            var objects = session.Interaction.Objects;
            if (objects.IsLocked(def.Id)) return FocusRelation.Locked;
            if (def.UseType != "Chest") return FocusRelation.Neutral;
            if (!objects.IsFilled(def.Id)) return FocusRelation.Unlooted;
            var db = session.Content.Database;
            var inventories = session.Items.Inventories;
            var container = InteractiveObjects.ContainerOf(def.Id);
            if (!inventories.Holds(container)) return FocusRelation.Neutral; // read only: Of would add an empty inventory
            foreach (var stack in inventories.Of(container).Stacks)
                if (stack.Amount > 0 && db.TryGet(stack.Item, out ItemDef item) && item.Category != ItemCategory.Spellbook)
                    return FocusRelation.Unlooted;
            return FocusRelation.Neutral;
        }

        /// <summary>
        /// The loot page title: G3 gCHUDViewLootPool2 labels the pool with the looted entity's localized "FO_&lt;name&gt;"
        /// (empty for none), the same text as its focus name: the NPC's name for a body, the object's FO_ text or nothing
        /// for a container (fix-94; was the localizer's "[FO_…]" marker for the 23 treasure spots without an FO_ string).
        /// </summary>
        public static string LootTitle(GameSession session, ActorId body, string container) =>
            container == null ? NpcName(session, body)
                              : ObjectName(session, session.Interaction.Objects.TryGet(container, out var def) ? def : null);

        /// <summary>A world item: "Name" or "Name (amount)".</summary>
        public static FocusTarget WorldItem(GameSession session, string worldItem)
        {
            var db = session.Content.Database;
            string id;
            int amount;
            if (db.TryGet(worldItem, out WorldItemDef def)) (id, amount) = (def.Item, def.Amount);
            else if (session.Items.World.TryGetDropped(worldItem, out var dropped)) (id, amount) = (dropped.Item, dropped.Amount);
            else return new FocusTarget(worldItem, FocusRelation.Item);
            string name = db.TryGet(id, out ItemDef item) ? ItemLists.Name(session, item) : id;
            return new FocusTarget(amount > 1 ? $"{name} ({amount})" : name, FocusRelation.Item);
        }

        public static string NpcName(GameSession session, ActorId actor) =>
            session.Content.Database.TryGet(actor.Name, out NpcDef def) && !def.DisplayName.IsEmpty
                ? session.Localizer.Text(def.DisplayName)
                : actor.Name;

        static FocusRelation Relation(GameSession session, ActorId actor, bool locked)
        {
            if (session.Combat.Vitals.IsDead(actor)) return FocusRelation.Dead;
            var brain = session.NpcCombat.Get(actor);
            if (brain != null && brain.State.Phase == CombatPhase.Flee) return FocusRelation.Panic;
            if (locked || (brain != null && brain.IsAttacking && brain.State.Target.IsPlayer)) return FocusRelation.Hostile;
            switch (session.Crime.Attitudes.Of(actor, ActorId.Player))
            {
                case Attitude.Friendly: return FocusRelation.Friendly;
                case Attitude.Angry: return FocusRelation.Angry;
                case Attitude.Hostile:
                case Attitude.Predator: return FocusRelation.Hostile;
                case Attitude.Panic: return FocusRelation.Panic;
                default: return FocusRelation.Neutral;
            }
        }
    }
}
