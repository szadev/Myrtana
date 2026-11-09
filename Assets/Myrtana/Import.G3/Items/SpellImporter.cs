using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Items;
using G3.Formats.Templates;
using Myrtana.Rules;
using DamageType = Myrtana.Rules.DamageType;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// G3 spell templates (<c>gCMagic_PS</c>, Magic/Magic_Spells) → <see cref="SpellDef"/>s (Docs/combat.md §12): costs,
    /// cast and delivery types, the target effect, the missile of projectile spells (the Spawn template's
    /// <c>gCDamage_PS</c>/<c>gCProjectile_PS</c>/<c>gCEffect_PS</c>), the effect names, and the items that cast them
    /// (permanent spells and used-up scrolls, through the items' <c>Spell</c> proxy). Every G3 enum is translated here.
    /// </summary>
    public static class SpellImporter
    {
        public static List<SpellDef> Import(TemplateArchive templates, ItemCatalog catalog, Func<ItemInfo, bool> inventoriable, ImportReport report)
        {
            var spells = new List<SpellDef>();
            foreach (var t in templates.Files.Where(f => !f.IsDeleted && f.Main != null))
            {
                var ps = t.Main.PropertySets.FirstOrDefault(p => p.ClassName == "gCMagic_PS");
                if (ps == null) continue;
                var g = t.Main.Source;
                var guids = new[] { t.Guid, t.Main.Guid };
                var carriers = catalog.Items.Where(i => inventoriable(i) && guids.Contains(i.Spell)).ToList();
                var spawn = templates.Get(Props.Proxy(ps, "Spawn"));
                string req = Props.Str(g, ps, "ReqAttrib1Tag");
                spells.Add(new SpellDef(
                    t.Name,
                    carriers.Where(i => i.Permanent).Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    carriers.Where(i => !i.Permanent).Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    (SpellCastType)Clamp(Props.Enum(ps, "CastType"), 2),
                    (SpellTarget)Clamp(Props.Enum(ps, "SpellTarget"), 1),
                    (SpellKind)Clamp(Props.Enum(ps, "SpellType"), 2),
                    Deity(Props.Enum(ps, "SpellDeity")),
                    Props.Int(ps, "MinManaCost"),
                    Props.Int(ps, "MaxManaCost"),
                    Delivery(Props.Str(g, ps, "FuncOnCast"), Props.Enum(ps, "SpellType")),
                    Props.Str(g, ps, "FuncOnTargetHit"),
                    Props.Str(g, ps, "AnimActionCast"),
                    string.Equals(req, "INT", StringComparison.OrdinalIgnoreCase) ? Props.Int(ps, "ReqAttrib1Value") : 0,
                    spawn?.Name,
                    Missile(spawn),
                    new SpellVisuals(Props.Str(g, ps, "EffectItemHold"), Props.Str(g, ps, "EffectItemLoad"), Props.Str(g, ps, "EffectItemPower"),
                                     Props.Str(g, ps, "EffectItemCast"), Props.Str(g, ps, "EffectCasterLoad"),
                                     Props.Str(g, ps, "EffectCasterCast"), Props.Str(g, ps, "EffectTargetCast"))));
                if (carriers.Count == 0) report.Count("spell without an item (only NPCs or scripts cast it)", t.Name);
            }
            return spells.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>A projectile template's damage, speed and effects; null when the spawn is no damaging projectile.</summary>
        static SpellMissile Missile(TemplateFile spawn)
        {
            var e = spawn?.Main;
            var projectile = e?.Get("gCProjectile_PS");
            var damage = e?.Get("gCDamage_PS");
            if (projectile == null || damage == null) return null;
            var g = e.Source;
            return new SpellMissile(spawn.Name, DamageTypeOf(Props.Enum(damage, "DamageType")), Props.Int(damage, "DamageAmount"),
                                    Props.Float(damage, "DamageManaMultiplier", 1), Props.Float(projectile, "ShootVelocity", 100),
                                    Props.Str(g, e.Get("gCEffect_PS"), "Effect"), Props.Str(g, projectile, "EffectPointImpact"));
        }

        /// <summary>gESpellDeity: 0 Adanos, 1 Beliar, 2 Innos, 3 none (teleports, transformations).</summary>
        static SpellDeity Deity(int value)
        {
            switch (value)
            {
                case 0: return SpellDeity.Adanos;
                case 1: return SpellDeity.Beliar;
                case 2: return SpellDeity.Innos;
                default: return SpellDeity.None;
            }
        }

        /// <summary>FuncOnCast, or by SpellType when empty (Projectile → MagicProjectile, Target → MagicInstant, Summon → MagicSummon).</summary>
        static SpellDelivery Delivery(string funcOnCast, int spellType)
        {
            switch (funcOnCast)
            {
                case "MagicProjectile": return SpellDelivery.Projectile;
                case "MagicInstant": return SpellDelivery.Instant;
                case "MagicSummon": return SpellDelivery.Summon;
            }
            return spellType == 0 ? SpellDelivery.Projectile : spellType == 1 ? SpellDelivery.Instant : SpellDelivery.Summon;
        }

        /// <summary>gEDamageType: 1 Impact, 2 Blade, 3 Missile, 4 Fire, 5 Ice, 6 Lightning.</summary>
        static DamageType DamageTypeOf(int value) => value >= 1 && value <= 6 ? (DamageType)value : DamageType.None;

        static int Clamp(int value, int max) => value < 0 ? 0 : value > max ? max : value;
    }
}
