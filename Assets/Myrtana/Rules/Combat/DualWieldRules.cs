using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>Which weapon of a fighter strikes in a move (G3 enables the "strip" and touch damage per hand item).</summary>
    [Flags]
    public enum MeleeBlades { None = 0, Right = 1, Left = 2, Both = Right | Left }

    /// <summary>Where a one-handed weapon the hero uses (inventory or quick slot) goes, and which hands hold what afterwards.</summary>
    public enum OneHandedUse
    {
        /// <summary>Not a dual-wield case: the ordinary equip (main weapon slot) and draw.</summary>
        Main,
        /// <summary>Equipped to the off-hand slot (G3 gESlot_BackLeft); both are drawn when the main one is drawn or drawn now.</summary>
        OffHand,
        /// <summary>The off-hand weapon's key while it is in the left hand: only the left weapon is put away (the right one stays).</summary>
        PutAwayLeft,
        /// <summary>The off-hand weapon's key while only the main weapon is drawn: the left weapon is drawn beside it.</summary>
        DrawLeft,
        /// <summary>The main weapon's key while it is in the hand: everything is put away.</summary>
        Holster,
        /// <summary>The main weapon's key while sheathed: both weapons are drawn.</summary>
        DrawBoth,
    }

    /// <summary>
    /// G3 dual wielding (two one-handed weapons, the <c>1H_1H</c> stance; Docs/combat.md §1.7). Sources: CP 1.75 Script_Game
    /// FUN_10002550 (hand choice of OnPlayerUseQuickSlot and OnPlayerUseStack), _AI_TakeItem, OnPlayerSelectWeapon,
    /// EquipWeaponry, the _AI_ attack commands and AssessHit / FUN_1002b3a0.
    /// <list type="bullet">
    /// <item>The second weapon sits in gESlot_BackLeft (the slot of the shield: one or the other), carried on
    /// <c>Slot_MeleeBackLeft</c>, in the hand on <c>Slot_LeftHand_Weapon</c>; the main one in gESlot_BackRight.</item>
    /// <item>The hero needs <see cref="Perk"/> (Perk_1H1H_1) and a one-handed main weapon; NPCs get it from their weaponry
    /// (a one-handed stack of two or more).</item>
    /// </list>
    /// </summary>
    public static class DualWieldRules
    {
        /// <summary>The perk that lets the hero take a second one-handed weapon into the left hand.</summary>
        public const string Perk = "Perk_1H1H_1";

        /// <summary>G3 item use type of one-handed weapons.</summary>
        public const string OneHandedKind = "OneHanded";

        /// <summary>
        /// The slot a one-handed weapon goes to when the hero uses it while nothing is drawn (G3 OnPlayerUseStack →
        /// FUN_10002550 case 2, _AI_TakeItem): with the perk and a one-handed main weapon in BackRight that is another stack,
        /// the off-hand slot (BackLeft; a shield there is taken off); otherwise the main slot.
        /// </summary>
        /// <param name="knowsPerk">The hero has <see cref="Perk"/>.</param>
        /// <param name="mainKind">Use type of the worn main melee weapon (null = none).</param>
        /// <param name="sameAsMainStack">The used item is the worn main weapon and no second unit is carried (the same stack).</param>
        public static OneHandedUse EquipSlot(bool knowsPerk, string mainKind, bool sameAsMainStack) =>
            knowsPerk && mainKind == OneHandedKind && !sameAsMainStack ? OneHandedUse.OffHand : OneHandedUse.Main;

        /// <summary>
        /// A one-handed weapon's quick-slot key (OnPlayerUseQuickSlot → FUN_10002550 case 2) for the hero.
        /// </summary>
        /// <param name="isOffHand">The used item is the worn off-hand weapon (BackLeft).</param>
        /// <param name="isMain">The used item is the worn main weapon (BackRight).</param>
        /// <param name="leftHeld">The off-hand weapon is in the left hand now.</param>
        /// <param name="mainDrawn">The main weapon is drawn (a one-handed one, in the right hand).</param>
        /// <param name="knowsPerk">The hero has <see cref="Perk"/>.</param>
        /// <param name="mainKind">Use type of the worn main melee weapon (null = none).</param>
        /// <param name="offHandWorn">An off-hand weapon is worn (BackLeft holds a one-handed weapon).</param>
        public static OneHandedUse QuickSlot(bool isOffHand, bool isMain, bool leftHeld, bool mainDrawn, bool knowsPerk, string mainKind,
                                             bool offHandWorn)
        {
            bool mainOneHanded = mainKind == OneHandedKind;
            if (isOffHand && leftHeld) return OneHandedUse.PutAwayLeft; // stack in the left hand: left = -1, right kept
            if (isMain && mainDrawn) return OneHandedUse.Holster;        // stack in the right hand: both put away
            if (isOffHand && mainOneHanded) return mainDrawn ? OneHandedUse.DrawLeft : OneHandedUse.DrawBoth; // BackLeft → left, BackRight → right
            if (isMain) return offHandWorn && mainOneHanded ? OneHandedUse.DrawBoth : OneHandedUse.Main;
            // Another one-handed weapon: with the perk and a one-handed main it becomes the left one (drawn at once).
            if (knowsPerk && mainOneHanded) return mainDrawn ? OneHandedUse.DrawLeft : OneHandedUse.DrawBoth;
            return OneHandedUse.Main;
        }

        /// <summary>
        /// The off-hand weapon is drawn with the main one only beside a one-handed main weapon (G3 OnPlayerSelectWeapon takes
        /// BackLeft and BackRight; the (1H, 1H) pair is the melee config) and not while the left hand holds the torch.
        /// </summary>
        public static bool DrawsOffHand(WeaponClass main, bool offHandWorn, bool torchInLeftHand) =>
            offHandWorn && main == WeaponClass.OneHanded && !torchInLeftHand;

        /// <summary>
        /// The blades that deal damage in a move (G3 _AI_ commands): Attack, SimpleWhirl, PowerAttack (and the sprint attack)
        /// enable both hand items of a dual wielder; QuickAttack and PierceAttack the left one from pose P1 (GetPrimaryPoseExt
        /// == 2), else the right one; every other move the right one.
        /// </summary>
        public static MeleeBlades Blades(CombatAction action, int pose, bool dual)
        {
            if (!dual) return MeleeBlades.Right;
            switch (action)
            {
                case CombatAction.Attack:
                case CombatAction.SimpleWhirl:
                case CombatAction.PowerAttack:
                case CombatAction.SprintAttack:
                    return MeleeBlades.Both;
                case CombatAction.QuickAttack:
                case CombatAction.QuickAttackLeft:
                case CombatAction.QuickAttackRight:
                case CombatAction.PierceAttack:
                    return pose == 1 ? MeleeBlades.Left : MeleeBlades.Right;
                default:
                    return MeleeBlades.Right;
            }
        }

        /// <summary>
        /// The power attack's G3 state position at its hit: the move runs as _AI_PowerAttack inside the state
        /// (PS_Melee_PowerAttack / the NPC attack state), whose own position is 1 then (FUN_1002b3a0: dual power level
        /// 2 when &lt; 2, else 3; AssessHit ×2 for a dual power attack only at position 2) [interp: the state's frame, not the
        /// command's]. So a dual power attack is level 2 and ×1 per blade; both blades may hit.
        /// </summary>
        public const int PowerAttackStatePosition = 1;

        /// <summary>The state position counts as G3's "second" position (<see cref="Combatant.SecondPose"/>).</summary>
        public static bool SecondPose(int statePosition) => statePosition == 2;

        /// <summary>
        /// G3 EquipWeaponry: a one-handed weapon whose weaponry units (merged per item) number two or more is split into the
        /// main slot and the off-hand slot. Returns that item, or null.
        /// </summary>
        public static string DualWeaponOf(IEnumerable<(string item, string kind, int amount)> weaponry)
        {
            var units = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var (item, kind, amount) in weaponry)
            {
                if (item == null || kind != OneHandedKind) continue;
                if (!units.ContainsKey(item)) order.Add(item);
                units[item] = (units.TryGetValue(item, out var n) ? n : 0) + Math.Max(1, amount);
            }
            foreach (var item in order)
                if (units[item] >= 2) return item;
            return null;
        }
    }
}
