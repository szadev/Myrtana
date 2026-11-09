using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// The staff in the magic stance (G3 Script_Game PS_Magic.cpp, Docs/combat.md §12.7): which hand holds what, and when the
    /// right mouse button blocks. G3 keeps two stacks in the hands of PS_Magic (HoldStacks left, right):
    /// <list type="bullet">
    /// <item>A spell's quick-slot key (FUN_10002550, use type Cast): the spell goes into the right hand; the left hand takes
    /// the staff that is drawn in the right one, or keeps the staff it already holds; otherwise it stays empty. The same
    /// spell's key again puts the spell away.</item>
    /// <item>A staff's key while a spell is in the right hand (use type Staff): the staff goes into the left hand beside it.</item>
    /// <item>OnMagicHandleInput (0x1007d360): the secondary key (Use2) pressed with a staff in the left hand and not in
    /// PS_Magic_Summon queues the parade (<c>_AI_Parade</c> against the focus, PS_Magic_StaffParade); without a staff it
    /// does nothing — it never aborts the spell. Its release ends the block (PS_Magic_StaffParade_Loop → PS_Magic_Loop); a
    /// cast queued during the block goes straight to the cast states. CP extended AI: the block ends by itself once the
    /// key has been held 1501 ms.</item>
    /// <item>HasStaffMagicParade (CanParade.cpp): staff and spell in the hands; the hero needs Perk_StaffMagic, an NPC the
    /// mage class. Only then does the block stop spells (CanParadeMagic: in parade, the caster in view; mana pays) and
    /// <c>_AI_Parade</c> start <see cref="ShieldEffect"/> (PS_Magic_Loop stops it once the parade is down).</item>
    /// </list>
    /// </summary>
    public static class StaffParade
    {
        public const string ShieldEffect = "eff_magic_parade_shield_01";
        public const string Perk = "Perk_StaffMagic";
        /// <summary>CP extended AI: PSCharacterControl.DurationPressedMSecs ≥ 0x5dd ends the staff block.</summary>
        public const int ExtendedAiHoldMilliseconds = 1501;

        /// <summary>Readying a spell: the left hand holds a staff afterwards (the drawn staff moves over, or it stays).</summary>
        public static bool StaffInLeftHand(WeaponClass? drawnInRightHand, bool staffInLeftHand) =>
            drawnInRightHand == WeaponClass.Staff || staffInLeftHand;

        /// <summary>A staff's quick-slot key while a spell is in the right hand: the staff goes into the left hand.</summary>
        public static bool StaffKeyJoinsSpell(WeaponClass? keyWeapon, bool spellInRightHand) =>
            keyWeapon == WeaponClass.Staff && spellInRightHand;

        /// <summary>The secondary key in the magic stance raises the staff block (else it does nothing).</summary>
        public static bool RaisesBlock(bool staffInLeftHand, bool summoning) => staffInLeftHand && !summoning;

        /// <summary>CP extended AI: the block held this long ends by itself.</summary>
        public static bool EndsByItself(bool extendedAI, double heldSeconds) =>
            extendedAI && heldSeconds * 1000.0 >= ExtendedAiHoldMilliseconds;

        /// <summary>HasStaffMagicParade: the block stops spells and shows <see cref="ShieldEffect"/>.</summary>
        public static bool BlocksMagic(bool staffAndSpellInHands, bool isPlayer, Func<string, bool> knows, bool mage) =>
            staffAndSpellInHands && (isPlayer ? knows != null && knows(Perk) : mage);
    }
}
