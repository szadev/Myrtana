namespace Myrtana.Rules
{
    /// <summary>
    /// The natural weapon of a creature that holds no weapon item (fix-127) [CP 1.75 Script_Game 0x1000b200, used by the
    /// weapon choice 0x10049e50, MagicAnimalForm and MagicTransform]: G3 gives it the <c>Fist</c> (the troll its
    /// <c>TrollFist</c>) with a quality by species, and that quality does what a treated weapon does (CanBePoisoned,
    /// CanBeDiseased, CanBurn, CanFreeze):
    /// <list type="bullet">
    /// <item>poison (bit 4): Bloodfly (the "hornet"), SwampLurker, ScorpionKing; CP 1.75 OnTick re-assures the bloodfly's
    /// poison fist while it hovers over water;</item>
    /// <item>disease (bit 2): Zombie, Ripper, Varan, Troll;</item>
    /// <item>burning (bit 8): FireVaran, FireGolem;</item>
    /// <item>frost (bit 16): IceGolem;</item>
    /// <item>every other species: a plain fist.</item>
    /// </list>
    /// The same table hands Goblins a worn club, Ogres a worn morning star, Stalkers a worn spiked club, Demons the demon
    /// sword and the Dragon the fireball: <see cref="NaturalWeapon"/> (fix-237; the creature rigs of these species only have
    /// attack motions with that weapon, the Stalker's "Zombie" with 2H ones, so with fists they never swung).
    /// The hero in an animal form strikes with his form's fist.
    /// </summary>
    public static class SpeciesWeaponry
    {
        /// <summary>
        /// The melee weapon item G3 assures a creature of <paramref name="species"/> (0x1000b200: Demon <c>It_2H_DemonSword_01</c>;
        /// Goblin <c>It_1H_Club_01</c>, Ogre <c>It_Axe_OgreMorningStar_01</c>, Stalker <c>It_Axe_SpikedClub_01</c>, each with
        /// quality 0x100 worn) and that stack quality; null for the others (fists; the Dragon's fireball is its spell).
        /// </summary>
        public static (string item, WeaponQuality quality)? NaturalWeapon(string species)
        {
            switch (species)
            {
                case "Demon": return ("It_2H_DemonSword_01", WeaponQuality.None);
                case "Goblin": return ("It_1H_Club_01", WeaponQuality.Worn);
                case "Ogre": return ("It_Axe_OgreMorningStar_01", WeaponQuality.Worn);
                case "Stalker": return ("It_Axe_SpikedClub_01", WeaponQuality.Worn);
                default: return null;
            }
        }

        /// <summary>The quality bits of the fist of <paramref name="species"/> (None for an unknown or plain species).</summary>
        public static WeaponQuality FistQuality(string species)
        {
            switch (species)
            {
                case "Bloodfly":
                case "SwampLurker":
                case "ScorpionKing":
                    return WeaponQuality.Poisoned;
                case "Zombie":
                case "Ripper":
                case "Varan":
                case "Troll":
                    return WeaponQuality.Diseased;
                case "FireVaran":
                case "FireGolem":
                    return WeaponQuality.Burning;
                case "IceGolem":
                    return WeaponQuality.Frozen;
                default:
                    return WeaponQuality.None;
            }
        }
    }
}
