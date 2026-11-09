using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// Items the hero holds that have no worn slot to go back to: G3 links them to gESlot_LeftHand / gESlot_RightHand
    /// only while held (gCInventory_PS::GetLeftEquipSlot / GetRightEquipSlot return None for them, so HoldStacks puts them
    /// back to no slot). That is the burning torch in the left hand and a drawn spell or scroll in the right hand.
    /// Weapons, shields and ammunition keep their worn slot in or out of the hand (<see cref="HeroEquipment"/>).
    /// The views that animate the hands set it; it is not saved (the torch has its own save section and a load
    /// starts with the spell put away).
    /// </summary>
    public sealed class HeroHands
    {
        public string Left { get; private set; }
        public string Right { get; private set; }

        /// <summary>
        /// The kind of weapon the hero has drawn (set by the hero's combat each frame; fists count as melee). Not an item:
        /// a drawn weapon keeps its worn slot (<see cref="HeroEquipment"/>); the quick bar uses it to show the active one.
        /// </summary>
        public HandWeapon Weapon { get; private set; }

        public void SetWeapon(HandWeapon weapon)
        {
            if (Weapon == weapon) return;
            Weapon = weapon;
            Changed?.Invoke();
        }

        public event Action Changed;

        public void SetLeft(string item)
        {
            if (string.IsNullOrEmpty(item)) item = null;
            if (Left == item) return;
            Left = item;
            Changed?.Invoke();
        }

        public void SetRight(string item)
        {
            if (string.IsNullOrEmpty(item)) item = null;
            if (Right == item) return;
            Right = item;
            Changed?.Invoke();
        }

        public void Clear()
        {
            if (Left == null && Right == null && Weapon == HandWeapon.None) return;
            Left = Right = null;
            Weapon = HandWeapon.None;
            Changed?.Invoke();
        }

        public bool Holds(string item) => item != null && (item == Left || item == Right);
    }

    /// <summary>What the hero has drawn: nothing, a melee weapon (or fists), a bow or crossbow.</summary>
    public enum HandWeapon { None, Melee, Ranged }

    /// <summary>
    /// G3's "equipped" tint of an item tile (inventory lists and the HUD quick bar share one function, Game.dll 100a2d40:
    /// a stack whose gESlot is not None gets the blue item colour #12496B, focused #0C7097). It is not "the active slot":
    /// every stack that sits in a slot is blue at once, e.g. the sword on the back while the bow and its arrows are in the
    /// hands. What is in a slot (Script_Game OnPlayerUseQuickSlot → FUN_10002550 → HoldStacks, gCInventory_PS::EquipStack):
    /// <list type="bullet">
    /// <item>worn weapons, shield, ammunition, armour, helmet, jewellery, drawn or not (holstering returns a weapon to its
    /// back/bow slot, so a sheathed sword stays blue while a bow, torch or spell is selected);</item>
    /// <item>a bow chosen on the quick bar is equipped to the bow slot and stays blue after holstering; drawing it equips
    /// the arrows it shoots (EquipStackToSlot(ammo, gESlot_Ammo));</item>
    /// <item>a torch only while it burns in the left hand; a spell or scroll only while drawn in the right hand;</item>
    /// <item>pressing the key of the drawn weapon holsters it (still blue); of the held torch, puts it away (not blue).</item>
    /// </list>
    /// </summary>
    public static class EquippedMarker
    {
        public static bool IsMarked(string item, Func<string, bool> worn, HeroHands hands) =>
            !string.IsNullOrEmpty(item) && ((worn != null && worn(item)) || (hands != null && hands.Holds(item)));

        public static bool IsMarked(string item, HeroEquipment equipment, HeroHands hands) =>
            IsMarked(item, equipment != null ? equipment.IsWorn : (Func<string, bool>)null, hands);

        /// <summary>
        /// The quick bar's blue cell (fix-56, owner: "selecting the bow or torch still shows the sword as active"). With
        /// nothing in the hands it is G3's tint (<see cref="IsMarked(string, HeroEquipment, HeroHands)"/>: every worn stack).
        /// Once something is in the hands, only what is active is blue: the held torch or spell, the drawn melee weapon
        /// with its shield / off-hand blade, the drawn bow or crossbow with the ammunition it shoots (G3 equips that to
        /// gESlot_Ammo on the draw, so the arrows are blue beside the bow). A sheathed weapon of the other kind is not:
        /// with the bow drawn or the torch or a spell held, the sword's cell is plain [owner; G3's own bar would keep the
        /// sheathed sword blue, as its inventory tint does].
        /// </summary>
        public static bool IsQuickMarked(string item, HeroEquipment equipment, HeroHands hands)
        {
            if (string.IsNullOrEmpty(item)) return false;
            if (hands != null && hands.Holds(item)) return true;
            if (equipment == null || !equipment.IsWorn(item)) return false;
            var weapon = hands?.Weapon ?? HandWeapon.None;
            bool active = weapon != HandWeapon.None || hands != null && (hands.Left != null || hands.Right != null);
            if (!active) return true;
            foreach (var worn in equipment.Worn)
            {
                if (worn.Value != item) continue;
                if (QuickMarkedWhileActive(worn.Key, weapon)) return true;
            }
            return false;
        }

        /// <summary>Whether a stack worn in <paramref name="slot"/> stays blue while something is in the hands.</summary>
        public static bool QuickMarkedWhileActive(WornSlot slot, HandWeapon drawn)
        {
            switch (slot)
            {
                case WornSlot.MeleeWeapon:
                case WornSlot.Shield:
                case WornSlot.OffHand:
                    return drawn == HandWeapon.Melee;
                case WornSlot.RangedWeapon:
                case WornSlot.Ammo:
                    return drawn == HandWeapon.Ranged;
                default:
                    return true; // armour, rings …: not weapons, never "inactive"
            }
        }

        /// <summary>Bit <c>i</c> set = quick-bar cell <c>i</c> shows a marked item.</summary>
        public static int QuickBarMask(QuickSlots slots, Func<string, bool> marked)
        {
            int mask = 0;
            if (slots == null || marked == null) return 0;
            for (int i = 0; i < QuickSlots.Count; i++)
            {
                string item = slots.ItemAt(i);
                if (item != null && marked(item)) mask |= 1 << i;
            }
            return mask;
        }
    }
}
