using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Bows and crossbows (Docs/combat.md §2, gameplay-data/ranged.json [data]): the draw multiplier, the shot velocity,
    /// the projectile's damage, the ammunition choice and the NPC refill. Formula constants of CP 1.75, like the
    /// resolver's; the projectile template values (ShootVelocity 100 km/h, DecayDuration 10 s) are those of every G3 arrow
    /// and bolt template.
    /// </summary>
    public static class RangedRules
    {
        /// <summary>HitMultiplier = 0.2 + 0.8·min(1, draw / 1.95 s) (OnPowerAim_Loop).</summary>
        public const float BowMinMultiplier = 0.2f, BowMultiplierRange = 0.8f, BowFullDrawSeconds = 1.95f;
        public const float CrossbowMultiplier = 1f;
        /// <summary>
        /// The player's ShootVelocity = HitMultiplier · 200 (Shoot), in km/h: gCProjectile_PS::GetDirection scales it by
        /// 0.277778 · 100 to cm/s [Game.dll 0x20329632], so a full draw flies at 55.6 m/s (fix-56).
        /// </summary>
        public const float PlayerVelocityFactor = 200f;
        /// <summary>NPC shots keep the template's ShootVelocity (100 km/h = 27.8 m/s), aimed once at release (<see cref="ProjectileFlight"/>).</summary>
        public const float NpcVelocityKmh = 100f;
        /// <summary>km/h → m/s (GetDirection: ·0.277778 to m/s, ·100 to cm).</summary>
        public const float KmhToMetresPerSecond = 1f / 3.6f;
        public const float NpcShotVelocity = NpcVelocityKmh * KmhToMetresPerSecond;
        /// <summary>
        /// Gravity on the player's arrows and bolts (PathStyle 2 Physics: the velocity is set once, then the rigid body flies
        /// on; G3's physics gravity read as 9.81 m/s² [interp]). NPC shots (PathStyle 0 Line: aimed once at release, never
        /// re-aimed — ShootIteration re-aims only Missile, fix-93) do not drop.
        /// </summary>
        public const float Gravity = 9.81f;
        /// <summary>A stuck arrow or bolt fades out after the template's DecayDuration (FadeOnDecay).</summary>
        public const float DecaySeconds = 10f;
        /// <summary>ZS_Attack_Shoot: AssureItems(ammo, 10 + Random(10)).</summary>
        public const int NpcRefillMin = 10, NpcRefillRandom = 10;
        /// <summary>NPC crossbowmen play the reload (Cock) at this speed (CP speed table).</summary>
        public const float NpcCockSpeed = 1.5f;

        /// <summary>When the equipped ammunition is used up, the first stack in this order is equipped (CP adds SharpArrow).</summary>
        public static readonly IReadOnlyList<string> BowAmmoOrder = new[] { "Arrow", "PoisonArrow", "FireArrow", "ExplosiveArrow", "BluntArrow", "SharpArrow" };
        public static readonly IReadOnlyList<string> CrossbowAmmoOrder = new[] { "Bolt", "Bolt_Sharp" };

        /// <summary>The bow's HitMultiplier after drawing for <paramref name="drawSeconds"/>; a crossbow always shoots with 1.</summary>
        public static float HitMultiplier(WeaponClass weapon, float drawSeconds)
        {
            if (weapon == WeaponClass.Crossbow) return CrossbowMultiplier;
            float t = Math.Max(0f, drawSeconds) / BowFullDrawSeconds;
            return BowMinMultiplier + BowMultiplierRange * Math.Min(1f, t);
        }

        /// <summary>Velocity of a shot: the player's grows with the draw, NPC shots use the template's.</summary>
        public static float ShotVelocity(bool player, float hitMultiplier) =>
            player ? hitMultiplier * PlayerVelocityFactor * KmhToMetresPerSecond : NpcShotVelocity;

        /// <summary>
        /// The low launch elevation (radians above horizontal) that carries a shot of <paramref name="speed"/> m/s
        /// <paramref name="horizontal"/> m ahead and <paramref name="rise"/> m up under <see cref="Gravity"/>; false when it
        /// is out of reach (then the 45° elevation, the farthest). A G3 player holds the crosshair this much above a far
        /// foe; the dev runner aims with it.
        /// </summary>
        public static bool LaunchElevation(float horizontal, float rise, float speed, out float elevation)
        {
            double x = Math.Max(1e-3, horizontal), y = rise, v2 = (double)speed * speed, g = Gravity;
            double disc = v2 * v2 - g * (g * x * x + 2 * y * v2);
            if (disc < 0 || speed <= 0)
            {
                elevation = (float)(Math.PI / 4);
                return false;
            }
            elevation = (float)Math.Atan((v2 - Math.Sqrt(disc)) / (g * x));
            return true;
        }

        /// <summary>Projectile damage = ammunition DamageAmount + weapon DamageAmount.</summary>
        public static int ProjectileDamage(ItemDef ammo, ItemDef weapon) => (ammo?.Damage?.Amount ?? 0) + (weapon?.Damage?.Amount ?? 0);

        /// <summary>The ammunition's damage type (blunt arrows hit with Impact); Missile by default.</summary>
        public static DamageType ProjectileDamageType(ItemDef ammo) =>
            ammo?.Damage != null && Enum.TryParse(ammo.Damage.Type, out DamageType type) && type != DamageType.None ? type : DamageType.Missile;

        /// <summary>The projectile's name contains "Fire" or "Explosive" (beats the missile immunity of skeletons and ice golems).</summary>
        public static bool IsFireOrExplosive(string ammo) =>
            ammo != null && (ammo.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0 || ammo.IndexOf("Explosive", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>The ammunition kind a ranged weapon shoots ("Arrow" / "Bolt" item kinds).</summary>
        public static string AmmoKind(WeaponClass ranged) => ranged == WeaponClass.Crossbow ? "Bolt" : "Arrow";

        /// <summary>
        /// The ammunition to shoot: the equipped stack while it lasts and fits the weapon, else the first carried stack
        /// of the auto-select order; null when there is none ("MissingItem").
        /// </summary>
        public static string SelectAmmo(WeaponClass ranged, string equipped, Func<string, ItemDef> definition, Func<string, int> carried)
        {
            if (equipped != null && carried(equipped) > 0 && definition(equipped)?.Kind == AmmoKind(ranged)) return equipped;
            foreach (var id in ranged == WeaponClass.Crossbow ? CrossbowAmmoOrder : BowAmmoOrder)
                if (carried(id) > 0) return id;
            return null;
        }

        /// <summary>
        /// fix-184: the ammunition to put on the string instead of the nocked one (<paramref name="nocked"/>) when the
        /// player's choice (<paramref name="selected"/>, <see cref="SelectAmmo"/>) changed before the draw; null keeps it
        /// (same stack, or nothing left to shoot: the release then lowers the bow).
        /// </summary>
        public static string Renock(string nocked, string selected) =>
            selected != null && !string.Equals(nocked, selected, StringComparison.Ordinal) ? selected : null;

        /// <summary>The item NPC archers are refilled with (ZS_Attack_Shoot AssureItems template).</summary>
        public static string NpcAmmoItem(WeaponClass ranged) => ranged == WeaponClass.Crossbow ? "Bolt" : "Arrow";

        /// <summary>The number of arrows or bolts an NPC archer is given when it starts shooting (10–19).</summary>
        public static int NpcRefill(IRandom random) => NpcRefillMin + random.Next(NpcRefillRandom);
    }
}
