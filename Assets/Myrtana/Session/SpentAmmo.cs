using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// Gives a shot arrow or bolt back to the world once its flight ended [owner, fix-195; <see cref="SpentAmmoRules"/>]:
    /// into the touched character's inventory (looted from the body), or as a pickable world item where it came to rest.
    /// The item keeps its type (fire, sharp, blunt arrow …) and the shot's quality.
    /// </summary>
    public static class SpentAmmo
    {
        /// <summary>
        /// The projectile of <paramref name="shooter"/> shot with <paramref name="ammo"/> came to <paramref name="rest"/>;
        /// <paramref name="victim"/> is the character it touched (default: the level), <paramref name="position"/> (x, y, z)
        /// and <paramref name="rotation"/> (quaternion x, y, z, w) its pose there. Returns what became of it.
        /// </summary>
        public static SpentAmmoFate Recover(GameSession session, ActorId shooter, string ammo, int quality, ProjectileRest rest,
                                            ActorId victim, float[] position, float[] rotation)
        {
            if (session == null || string.IsNullOrEmpty(ammo)) return SpentAmmoFate.Gone;
            var fate = SpentAmmoRules.Fate(rest, onCharacter: !victim.IsEmpty);
            switch (fate)
            {
                case SpentAmmoFate.IntoVictim:
                    session.Items.Inventories.Give(victim, ammo, 1, quality);
                    break;
                case SpentAmmoFate.Lies:
                    if (session.Items.World.DropSpent(shooter, ammo, quality, position, rotation) == null) return SpentAmmoFate.Gone;
                    break;
            }
            return fate;
        }
    }
}
