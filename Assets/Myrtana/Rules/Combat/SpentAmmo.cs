namespace Myrtana.Rules
{
    /// <summary>What becomes of a shot arrow or bolt once its flight ended (<see cref="SpentAmmoRules"/>).</summary>
    public enum SpentAmmoFate
    {
        /// <summary>Nothing is left (the explosive arrow is gone in its blast).</summary>
        Gone,
        /// <summary>It went into the touched character's inventory (lootable from the body).</summary>
        IntoVictim,
        /// <summary>It lies (or sticks) in the world as an item the hero can pick up.</summary>
        Lies,
    }

    /// <summary>
    /// Shot arrows and bolts can be had again [owner, fix-195]. G3 CP 1.75 does not allow it [Game.dll
    /// gCProjectile_PS::OnTouch 0x10152650: EnablePicking(false) on every touch; gCProjectile_PS::Decay: the projectile
    /// fades after its DecayDuration of 10 s]: there is no break chance and nothing goes into an inventory. The owner
    /// wants them back (Gothic 1/2 style):
    /// <list type="bullet">
    /// <item>a projectile that sticks in (or falls off) a character goes into that character's inventory: it is looted
    /// from the body; the arrow shown in the body still fades after 10 s;</item>
    /// <item>one that sticks in the level, or bounces off and comes to rest, becomes a dropped world item of the same
    /// item (fire, sharp, poison, blunt arrows and bolts keep their type and the shot's quality), focused and picked up
    /// like any other dropped item and saved with them, in the pose it stuck in;</item>
    /// <item>KillSelf (the explosive arrow) leaves nothing;</item>
    /// <item>no break chance (G3 has none to copy); at most <see cref="MaxLying"/> spent projectiles lie in the world,
    /// the oldest goes first [interp: bounds the save; G3's own lifetime is the 10 s fade].</item>
    /// </list>
    /// </summary>
    public static class SpentAmmoRules
    {
        /// <summary>Spent arrows and bolts lying in the world at most; a further one removes the oldest [interp].</summary>
        public const int MaxLying = 100;

        /// <summary>
        /// The fate of a projectile that came to <paramref name="rest"/>; <paramref name="onCharacter"/>: it touched a
        /// character (a blunt arrow that falls off is still the victim's).
        /// </summary>
        public static SpentAmmoFate Fate(ProjectileRest rest, bool onCharacter)
        {
            if (rest == ProjectileRest.Vanish) return SpentAmmoFate.Gone;
            return onCharacter ? SpentAmmoFate.IntoVictim : SpentAmmoFate.Lies;
        }
    }
}
