namespace Myrtana.Rules
{
    /// <summary>
    /// How G3 holds and carries a weapon class (Docs/combat.md §9, "Weapon hold"): the hand field it puts into the motion
    /// names, and the skeleton slots it hangs on. The item mesh is modelled in the slot frame (identity attach); the slot
    /// frame itself comes from the playing motion (every G3 motion carries a pose for the hand slots), so the hand field
    /// decides how the weapon sits in the hand.
    /// </summary>
    public static class WeaponHold
    {
        public const string Fist = "Fist", OneHanded = "1H", TwoHanded = "2H", Staff = "Staff", Bow = "Bow", CrossBow = "CrossBow";

        public const string RightHand = "Slot_RightHand_Weapon", LeftHand = "Slot_LeftHand_Weapon", LeftHandShield = "Slot_LeftHand_Shield";

        /// <summary>
        /// Hand field of a weapon class in motion names. G3 groups by use type: 2H and Axe are the heavy two-hand grip
        /// (orcs only have <c>None_2H</c>), Staff and Halberd the polearm grip (<c>None_Staff</c>, hands mid-shaft) —
        /// the same pairs gCInventorySlot::GetBone puts on Slot_AxeBack / Slot_StaffBack. Tools used as weapons (broom,
        /// rake, shovel) are staves (their use types play <c>Staff</c> motions, Docs/formats-animation.md §8.7).
        /// </summary>
        public static string MotionField(WeaponClass weapon)
        {
            switch (weapon)
            {
                case WeaponClass.OneHanded: return OneHanded;
                case WeaponClass.TwoHanded:
                case WeaponClass.Axe: return TwoHanded;
                case WeaponClass.Halberd:
                case WeaponClass.Staff:
                case WeaponClass.Tool: return Staff;
                case WeaponClass.Bow: return Bow;
                case WeaponClass.Crossbow: return CrossBow;
                default: return Fist;
            }
        }

        /// <summary>The hand slot a drawn weapon sits in: bows and crossbows in the left hand, everything else in the right.</summary>
        public static string HandSlot(WeaponClass weapon) =>
            weapon == WeaponClass.Bow || weapon == WeaponClass.Crossbow ? LeftHand : RightHand;

        /// <summary>
        /// The slot a sheathed weapon hangs on (gCInventorySlot::GetBone, carry slots): 2H and axes on <c>Slot_AxeBack</c>,
        /// staves and halberds on <c>Slot_StaffBack</c>, other melee weapons on <c>Slot_MeleeBackRight</c>, bows on
        /// <c>Slot_Bow</c>, crossbows on <c>Slot_CrossBow</c>; null for fists.
        /// </summary>
        public static string CarrySlot(WeaponClass weapon)
        {
            switch (weapon)
            {
                case WeaponClass.TwoHanded:
                case WeaponClass.Axe: return "Slot_AxeBack";
                case WeaponClass.Staff:
                case WeaponClass.Halberd: return "Slot_StaffBack";
                case WeaponClass.OneHanded:
                case WeaponClass.Tool: return "Slot_MeleeBackRight";
                case WeaponClass.Bow: return "Slot_Bow";
                case WeaponClass.Crossbow: return "Slot_CrossBow";
                default: return null;
            }
        }

        public const string ShieldBack = "Slot_ShieldBack";

        /// <summary>The off-hand weapon of a dual wielder while sheathed (gCInventorySlot::GetBone: gESlot_BackLeft, not a shield).</summary>
        public const string MeleeBackLeft = "Slot_MeleeBackLeft";

        /// <summary>
        /// Where a dual wielder's second one-handed weapon hangs: in the left hand (<c>Slot_LeftHand_Weapon</c>, gESlot_LeftHand)
        /// while it is held beside the drawn main weapon, else on <c>Slot_MeleeBackLeft</c> (gESlot_BackLeft).
        /// </summary>
        public static string OffHandSlot(bool held) => held ? LeftHand : MeleeBackLeft;

        /// <summary>An item shown on a body slot (not the drawn weapon, which the hand code shows).</summary>
        public readonly struct Worn
        {
            public readonly ItemDef Item;
            public readonly string Slot;
            public Worn(ItemDef item, string slot) { Item = item; Slot = slot; }
            public override string ToString() => $"{Item?.Id}@{Slot}";
        }

        /// <summary>
        /// What a fighter shows on its body besides the drawn weapon (G3: every equipped weapon is always visible — in the
        /// hand or on its carry slot): the melee and ranged weapons that are not <paramref name="drawn"/> on their
        /// <see cref="CarrySlot"/>, the shield on <c>Slot_ShieldBack</c>, or in the left hand (<c>Slot_LeftHand_Shield</c>)
        /// while a one-handed weapon is drawn (G3 fights with a shield only in the 1H stance; <see cref="ShieldSlot"/>: none with a
        /// two-handed main weapon or while a two-handed or ranged weapon is drawn, fix-192). <paramref name="meleeLost"/>:
        /// the melee weapon lies on the ground (dropped when knocked out). Fists and items without a slot show nothing.
        /// </summary>
        public static void WornItems(CombatLoadout loadout, ItemDef drawn, bool meleeLost, System.Collections.Generic.List<Worn> result) =>
            WornItems(loadout, drawn, meleeLost ? (System.Func<ItemDef, bool>)(i => loadout != null && Same(i, loadout.MeleeItem)) : null, result);

        /// <summary>
        /// <see cref="WornItems(CombatLoadout, ItemDef, bool, System.Collections.Generic.List{Worn})"/> with a per-item test
        /// (fix-149): <paramref name="lying"/> = the item lies on the ground (G3 DropHandItems dropped it at the knock-out or
        /// death: the hand weapon and the shield or second blade in the left hand) and is not shown on the body; a lying melee
        /// weapon also takes the second blade and the shield out of the hands. Null = nothing lies.
        /// </summary>
        public static void WornItems(CombatLoadout loadout, ItemDef drawn, System.Func<ItemDef, bool> lying, System.Collections.Generic.List<Worn> result)
        {
            result.Clear();
            if (loadout == null) return;
            bool Lies(ItemDef item) => item != null && lying != null && lying(item);
            bool meleeLost = Lies(loadout.MeleeItem);
            if (loadout.MeleeItem != null && !meleeLost && !Same(loadout.MeleeItem, drawn))
            {
                string slot = CarrySlot(loadout.Melee);
                if (slot != null) result.Add(new Worn(loadout.MeleeItem, slot));
            }
            if (loadout.RangedItem != null && loadout.Ranged != null && !Same(loadout.RangedItem, drawn) && !Lies(loadout.RangedItem))
                result.Add(new Worn(loadout.RangedItem, CarrySlot(loadout.Ranged.Value)));
            if (loadout.DualWield && !meleeLost && !Lies(loadout.OffHandItem))
            {
                // G3 HoldStacks draws BackLeft and BackRight together: the second blade is in the left hand while the main
                // one is drawn (DualWieldRules).
                bool held = Same(loadout.MeleeItem, drawn);
                result.Add(new Worn(loadout.OffHandItem, OffHandSlot(held)));
            }
            if (loadout.ShieldItem != null && !Lies(loadout.ShieldItem))
            {
                WeaponClass? inHand = drawn == null ? (WeaponClass?)null
                    : Same(loadout.RangedItem, drawn) && loadout.Ranged != null ? loadout.Ranged
                    : Same(loadout.MeleeItem, drawn) ? (meleeLost ? (WeaponClass?)null : loadout.Melee)
                    : CombatLoadout.WeaponClassOf(drawn.Kind);
                string slot = ShieldSlot(meleeLost || loadout.MeleeItem == null ? WeaponClass.Fist : loadout.Melee, inHand);
                if (slot != null) result.Add(new Worn(loadout.ShieldItem, slot));
            }
        }

        /// <summary>
        /// The weapon takes both hands (fix-192): two-handed swords, axes, staves, halberds, tools, bows and crossbows. The
        /// left hand is not free for a shield beside it.
        /// </summary>
        public static bool BothHands(WeaponClass weapon) => weapon != WeaponClass.Fist && weapon != WeaponClass.OneHanded;

        /// <summary>A melee weapon of this use type ("TwoHanded", "Axe", "Staff" …) cannot be worn together with a shield (fix-192).</summary>
        public static bool MeleeExcludesShield(string kind) =>
            CombatLoadout.WeaponClassOf(kind) is WeaponClass w && w != WeaponClass.Bow && w != WeaponClass.Crossbow && BothHands(w);

        /// <summary>
        /// Where the worn shield is shown (fix-192, [owner] build 22d): null = not shown. With a two-handed main melee weapon
        /// there is no shield (the hero's equipment takes it off, <see cref="HeroEquipment"/>); while a two-handed weapon,
        /// staff, bow or crossbow is in the hands it is hidden, not on the back (G3 shows only the 1H stance with a shield);
        /// in the left hand (<c>Slot_LeftHand_Shield</c>) beside a drawn one-handed weapon; else on <c>Slot_ShieldBack</c>
        /// (sheathed, fists, a spell). <paramref name="drawn"/>: the class of the weapon in the hands, null = none.
        /// </summary>
        public static string ShieldSlot(WeaponClass mainMelee, WeaponClass? drawn)
        {
            if (mainMelee != WeaponClass.Bow && mainMelee != WeaponClass.Crossbow && BothHands(mainMelee)) return null; // not worn together
            if (drawn == null || drawn == WeaponClass.Fist) return ShieldBack;
            if (drawn == WeaponClass.OneHanded) return LeftHandShield;
            return null;
        }

        /// <summary>
        /// G3 DropHandItems (Script_Game 0x10024d10, Kill and Defeat) drops both hand slots when they hold a droppable weapon
        /// (IsDroppableWeapon 0x10010970: use types 1H, 2H, bow, crossbow, shield, staff, …). The right hand is the drawn
        /// weapon; this is what is in the <b>left</b> hand beside <paramref name="drawn"/> (fix-149): the shield while a
        /// one-handed weapon is drawn, a dual wielder's second blade while the main one is drawn; null otherwise (sheathed,
        /// ranged or magic: the bow is <paramref name="drawn"/> itself).
        /// </summary>
        public static ItemDef LeftHandDrop(CombatLoadout loadout, ItemDef drawn)
        {
            if (loadout == null || drawn == null || !Same(loadout.MeleeItem, drawn) || loadout.Melee != WeaponClass.OneHanded) return null;
            if (loadout.DualWield) return loadout.OffHandItem;
            return loadout.ShieldItem;
        }

        static bool Same(ItemDef a, ItemDef b) => a != null && b != null && (ReferenceEquals(a, b) || a.Id == b.Id);
    }
}
