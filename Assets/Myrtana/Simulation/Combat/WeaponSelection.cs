namespace Myrtana.Simulation
{
    /// <summary>
    /// How the brain's wanted weapon and the body's held weapon fit together (pure). G3's ZS_Attack_Loop runs the blocking
    /// <c>_AI_SelectWeapon</c> before every move choice: when the wanted category (combat_ai.json weaponChoice) differs from
    /// the hand, the NPC puts its weapon away and draws the other one (_AI_HoldInventoryItems_Base: put-away motion,
    /// items change hands, draw motion) and only then picks a move with the weapon in its hand. Here the brain keeps
    /// choosing the wanted weapon each tick, but its moves always follow the held one: while they differ it only waits
    /// (the body switches), so it never orders a shot with a sword in the hand or a swing with a crossbow.
    /// </summary>
    public static class WeaponSelection
    {
        /// <summary>
        /// The category in the hand: nothing (or a draw/sheathe running) → None; the spell hand → Magic; a bow or crossbow
        /// → Ranged; any other weapon, fists included → Melee.
        /// </summary>
        public static HeldWeapon HeldOf(bool inHand, bool switching, bool ranged, bool spell) =>
            !inHand || switching ? HeldWeapon.None : spell ? HeldWeapon.Magic : ranged ? HeldWeapon.Ranged : HeldWeapon.Melee;

        /// <summary>
        /// The "keep the current weapon" band (700–1000): the held category; empty hands → melee (CP 1.75
        /// keepCurrentBelow.emptyHands); not reported → the brain's previous choice.
        /// </summary>
        public static WeaponMode KeepCurrent(HeldWeapon held, WeaponMode previous)
        {
            switch (held)
            {
                case HeldWeapon.Unknown: return previous;
                case HeldWeapon.Ranged: return WeaponMode.Ranged;
                case HeldWeapon.Magic: return WeaponMode.Magic;
                default: return WeaponMode.Melee;
            }
        }

        /// <summary>The wanted weapon is in the hand (or the hand is not reported): the brain may choose moves for it.</summary>
        public static bool Ready(HeldWeapon held, WeaponMode wanted)
        {
            switch (held)
            {
                case HeldWeapon.Unknown: return true;
                case HeldWeapon.Melee: return wanted == WeaponMode.Melee;
                case HeldWeapon.Ranged: return wanted == WeaponMode.Ranged;
                case HeldWeapon.Magic: return wanted == WeaponMode.Magic;
                default: return false;
            }
        }
    }
}
