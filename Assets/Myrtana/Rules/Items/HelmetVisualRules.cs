using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// How a worn helmet is drawn (fix-220). G3: an It_Helmet_* item is a static mesh (<c>eCVisualMeshDynamic_PS</c>,
    /// <c>G3_Armor_Helm_*.xcmsh</c>) that the engine hangs on the head actor's <c>gESlot_Helmet</c> node
    /// (<c>Slot_Helmet</c> under <c>Hero_Head_Head_1</c>); like hair and beard it is modelled in that slot's frame, so it
    /// attaches with an identity local transform and follows the head bone.
    /// G3 has no "hides hair" flag (none in the item templates, none in Game.dll); the G3 hero's hair is part of his head
    /// mesh and no NPC of CP 1.75 wears a helmet, so in G3 nothing is ever hidden. The remaster adds a separate hair shell on
    /// <c>Slot_Hair</c> that would poke through: static meshes under <c>Slot_Hair</c> are hidden while a helmet that covers
    /// the skull is worn [interp]. A crown (G3_Armor_Helmet_Crone_01, a 7.7 cm ring on top of the head) keeps the hair; every
    /// real helmet of the game is 23-41 cm tall. The beard always stays (no helmet covers the chin).
    /// fix-232: the light tail hanging down the back under the horned helmet (It_Helmet_Horn_01, G3_Armor_Helm_Myrtana_06) is
    /// the helmet's own horse-hair plume from its crest spike, part of the helmet mesh, not the hero's hair (his G3 hair is short
    /// and baked into the head mesh, Slot_Hair empty: "hair hidden (0)" with the remaster off is right).
    /// </summary>
    public static class HelmetVisualRules
    {
        public const string HelmetSlot = "Slot_Helmet";
        public const string HairSlot = "Slot_Hair";

        /// <summary>A helmet mesh at least this tall (m, along the slot's up axis) covers the hair [interp: crown 0.077, helmets ≥ 0.23].</summary>
        public const float HairCoverHeight = 0.15f;

        /// <summary>The static mesh of a worn helmet item; null when the item is not a helmet or has no mesh.</summary>
        public static string MeshKey(ItemDef item) =>
            item != null && item.Slot == EquipSlot.Helmet && !string.IsNullOrEmpty(item.Visual?.Mesh) ? item.Visual.Mesh : null;

        /// <summary>
        /// fix-232 [owner] "Show helmet": the helmet mesh drawn on the hero, null = none (off: the item stays worn with its
        /// protection, only its mesh is not drawn, and the hair it would hide shows).
        /// </summary>
        public static string ShownMeshKey(ItemDef item, bool showHelmet) => showHelmet ? MeshKey(item) : null;

        /// <summary>A helmet whose mesh is <paramref name="height"/> m tall hides the hair under it.</summary>
        public static bool HidesHair(float height) => height >= HairCoverHeight;

        /// <summary>A rig node is <paramref name="slot"/> (the importers may suffix duplicates: <c>Slot_Hair.001</c>).</summary>
        public static bool IsSlot(string node, string slot) =>
            node != null && (string.Equals(node, slot, StringComparison.OrdinalIgnoreCase) ||
                             node.StartsWith(slot + ".", StringComparison.OrdinalIgnoreCase));
    }
}
