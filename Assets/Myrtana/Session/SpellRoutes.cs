using System.Collections.Generic;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>Which part of the game carries out a released spell.</summary>
    public enum SpellRoute
    {
        /// <summary>Nothing does: the spell has no effect in the port.</summary>
        None,
        /// <summary>A flying missile (CombatPresenter.CastMissile, the hit through MagicService.ToSpellHit).</summary>
        Missile,
        /// <summary>An area spell object (heat/frost wave, ice bomb, meteor, summon lightning, fire rain, hail storm).</summary>
        Area,
        /// <summary>Creatures appear (SummonService).</summary>
        Summon,
        /// <summary>An NPC, world or hero-form spell (SpellEffectServices: sleep, control, mist, time bubble, forms …).</summary>
        World,
        /// <summary>An effect on the target at once (MagicService.ApplyInstant: heal, refresh, cures, poison, disease).</summary>
        Instant,
        /// <summary>Fear: the target flees or attacks (ControlSpells.Fear).</summary>
        Fear,
        /// <summary>A teleport (the Teleports service answers SpellCasted).</summary>
        Teleport,
        /// <summary>Spl_Light: the light ball follows the caster (the presentation answers SpellCasted).</summary>
        Light,
        /// <summary>Used on a weapon from the inventory (Bless Weapon: WeaponTreatments), not cast from the hand.</summary>
        Inventory,
    }

    /// <summary>
    /// The route of every spell of the content (fix-110 audit: each spell an item casts must do something and show
    /// something). Pure; SpellCasterBody delivers in the same order.
    /// </summary>
    public static class SpellRoutes
    {
        public static SpellRoute Of(SpellDef spell, MagicRules rules)
        {
            if (spell == null) return SpellRoute.None;
            if (spell.Delivery == SpellDelivery.Projectile) return spell.Missile != null || spell.Spawn != null ? SpellRoute.Missile : SpellRoute.None;
            if (rules?.AreaSpellFor(spell) != null) return SpellRoute.Area;
            if (spell.Effect != null && rules?.Summons.For(spell.Effect) != null) return SpellRoute.Summon;
            if (SpellEffectServices.Handles(spell)) return SpellRoute.World;
            if (spell.Effect == "MagicFear") return SpellRoute.Fear;
            if (spell.Effect == Teleports.Effect) return SpellRoute.Teleport;
            if (MagicService.HasInstantEffect(spell.Effect)) return SpellRoute.Instant;
            if (spell.Id == SpellEffectRules.LightSpell) return SpellRoute.Light;
            if (spell.Id == SpellEffectRules.BlessSpell) return SpellRoute.Inventory;
            return SpellRoute.None;
        }

        /// <summary>
        /// The caster's cast effect (G3 EffectCasterCast: fear, charm, blood cloud, cures, summon circles …) is shown on the
        /// caster at release, unless something else shows it: the target effect when it is the same (transformations), the
        /// area spell object (the heat/frost wave and ice burst are its looks), the teleport view, the soul travel form.
        /// </summary>
        public static bool ShowsCasterCast(SpellDef spell, SpellRoute route)
        {
            var effect = spell?.Visuals.CasterCast;
            if (effect == null || effect == spell.Visuals.TargetCast) return false;
            if (route == SpellRoute.Area || route == SpellRoute.Teleport || route == SpellRoute.None) return false;
            return spell.Effect != "MagicSoulTravel";
        }

        /// <summary>
        /// The G3 effects (g3.efm names) a spell can show: its phase effects, its missile's, and the effects of what it
        /// leaves in the world (the mist, the time bubble, the light ball, the blessed weapon).
        /// </summary>
        public static IEnumerable<string> Visuals(SpellDef spell)
        {
            if (spell == null) yield break;
            var v = spell.Visuals;
            foreach (var e in new[] { v.ItemHold, v.ItemLoad, v.ItemPower, v.ItemCast, v.CasterLoad, v.CasterCast, v.TargetCast })
                if (e != null) yield return e;
            if (spell.Missile?.Effect != null) yield return spell.Missile.Effect;
            if (spell.Missile?.ImpactEffect != null) yield return spell.Missile.ImpactEffect;
            switch (spell.Effect)
            {
                case "MagicSummonMist": yield return SpellEffectRules.MistEffect; break;
                case "MagicTimeBubble": yield return SpellEffectRules.TimeBubbleEffect; break;
            }
            if (spell.Id == SpellEffectRules.LightSpell) yield return SpellEffectRules.LightSourceEffect;
            if (spell.Id == SpellEffectRules.BlessSpell) yield return SpellEffectRules.BlessEffect;
        }
    }
}
