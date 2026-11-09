using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>G3 gEProjectileTouchBehavior (values of the Game.dll enum registration, fix-176).</summary>
    public enum ProjectileTouch { KillSelf = 0, Reflect = 1, Stop = 2, MaterialDependant = 3 }

    /// <summary>What an arrow or bolt does once it touched something.</summary>
    public enum ProjectileRest
    {
        /// <summary>Stays where it hit (in the level, or in the body when it has no bone to link to).</summary>
        Stick,
        /// <summary>Links to the touched bone of a character (LinkToBones).</summary>
        StickToBone,
        /// <summary>Gone at once (KillSelf: the explosive arrow).</summary>
        Vanish,
        /// <summary>Physics on: it bounces off and falls (Reflect; MaterialDependant on hard surfaces; a blunt arrow off a body).</summary>
        Drop,
    }

    /// <summary>
    /// How an arrow or bolt ends its flight (Docs/combat.md §2, fix-176) [Game.dll gCProjectile_PS::OnTouch 0x10152650]:
    /// the template's TouchBehavior and LinkToBones decide. KillSelf deletes the projectile (explosive arrow); Reflect turns
    /// physics on (bolts bounce off the level and fall); Stop freezes it where it hit; MaterialDependant sticks in wood,
    /// earth, ice, leather, flesh and snow and bounces off everything else (stone, metal, water, clay, glass, debris, foliage,
    /// grass, sand …). A character touched by a projectile with LinkToBones gets it attached to the touched bone, one
    /// without (blunt arrow) lets it fall. G3 never lets a projectile be picked up again (EnablePicking false; we do, [owner] fix-195: <see cref="SpentAmmoRules"/>). The impact effect is
    /// the template's EffectTargetHit on a character, else EffectPointImpact; the explosive arrow's blast has no damage of
    /// its own (VFX_Magic_Weapons_ExplosiveArrow_Explosion_01 is particles only, FuncOnTargetHit is empty): only the
    /// touched victim takes the arrow's hit (no area damage, no knock-back), its burning quality sets him on fire.
    /// </summary>
    public static class ProjectileTouchRules
    {
        /// <summary>eEShapeMaterial values an arrow sticks in (Wood 1, Earth 5, Ice 6, Leather 7, Flesh 10, Snow 11).</summary>
        public static readonly HashSet<int> StickMaterials = new HashSet<int> { 1, 5, 6, 7, 10, 11 };

        /// <summary>
        /// The CP 1.75 templates' values, for content packs imported before <see cref="ItemDef.Projectile"/> existed
        /// (and the default of an unknown arrow: MaterialDependant + LinkToBones, as Arrow).
        /// </summary>
        static readonly Dictionary<string, ItemProjectile> Known = new Dictionary<string, ItemProjectile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Arrow"] = new ItemProjectile(nameof(ProjectileTouch.MaterialDependant), true),
            ["SharpArrow"] = new ItemProjectile(nameof(ProjectileTouch.MaterialDependant), true),
            ["FireArrow"] = new ItemProjectile(nameof(ProjectileTouch.MaterialDependant), true),
            ["PoisonArrow"] = new ItemProjectile(nameof(ProjectileTouch.MaterialDependant), true),
            ["BluntArrow"] = new ItemProjectile(nameof(ProjectileTouch.MaterialDependant), false),
            ["ExplosiveArrow"] = new ItemProjectile(nameof(ProjectileTouch.KillSelf), false),
            ["Bolt"] = new ItemProjectile(nameof(ProjectileTouch.Reflect), true),
            ["Bolt_Sharp"] = new ItemProjectile(nameof(ProjectileTouch.Reflect), true),
        };

        static readonly ItemProjectile Default = new ItemProjectile(nameof(ProjectileTouch.MaterialDependant), true);

        /// <summary>The projectile data of an ammunition item: its own, else the known template's, else Arrow's.</summary>
        public static ItemProjectile Of(ItemDef ammo)
        {
            if (ammo?.Projectile != null) return ammo.Projectile;
            return ammo != null && Known.TryGetValue(ammo.Id, out var known) ? known : Default;
        }

        public static ProjectileTouch TouchOf(ItemProjectile p) =>
            p != null && Enum.TryParse(p.Touch, out ProjectileTouch t) ? t : ProjectileTouch.MaterialDependant;

        /// <summary>
        /// The projectile touched the level; <paramref name="shapeMaterial"/> is the eEShapeMaterial there (null: unknown,
        /// e.g. a collider without G3 material: the arrow sticks [interp]).
        /// </summary>
        public static ProjectileRest OnLevel(ProjectileTouch touch, int? shapeMaterial)
        {
            switch (touch)
            {
                case ProjectileTouch.KillSelf: return ProjectileRest.Vanish;
                case ProjectileTouch.Reflect: return ProjectileRest.Drop;
                case ProjectileTouch.Stop: return ProjectileRest.Stick;
                default:
                    return shapeMaterial == null || StickMaterials.Contains(shapeMaterial.Value) ? ProjectileRest.Stick : ProjectileRest.Drop;
            }
        }

        /// <summary>The projectile touched a character (the hit is resolved either way).</summary>
        public static ProjectileRest OnCharacter(ProjectileTouch touch, bool linkToBones)
        {
            if (touch == ProjectileTouch.KillSelf) return ProjectileRest.Vanish;
            if (linkToBones) return ProjectileRest.StickToBone;
            return touch == ProjectileTouch.Stop ? ProjectileRest.Stick : ProjectileRest.Drop;
        }

        /// <summary>The impact effect to play: EffectTargetHit on a character (when set), else EffectPointImpact.</summary>
        public static string ImpactEffect(string pointImpact, string targetHit, bool onCharacter) =>
            onCharacter && !string.IsNullOrEmpty(targetHit) ? targetHit : string.IsNullOrEmpty(pointImpact) ? null : pointImpact;
    }
}
