using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>How a spell is cast (G3 gECastType): at once, with an optional power cast, or as a slow regular cast.</summary>
    public enum SpellCastType { Immediate, Power, Regular }

    /// <summary>Whom a spell needs (G3 gESpellTarget): nobody but the caster, or a focus target.</summary>
    public enum SpellTarget { Caster, Focus }

    /// <summary>What a spell does as G3 classifies it (gESpellType).</summary>
    public enum SpellKind { Projectile, Target, Summon }

    /// <summary>The god a spell belongs to (G3 gESpellDeity); the matching mage perk strengthens it.</summary>
    public enum SpellDeity { None, Adanos, Beliar, Innos }

    /// <summary>How the cast arrives (G3 FuncOnCast): a flying missile, an effect on the target, or a spawned object.</summary>
    public enum SpellDelivery { None, Projectile, Instant, Summon }

    /// <summary>
    /// A spell of the content (G3 <c>gCMagic_PS</c> template, Docs/gameplay-logic.md §4): its costs, how it is cast and
    /// delivered, the effect it has on its target, and the items that carry it (the learned spell and the scroll).
    /// </summary>
    public sealed class SpellDef : IDefinition
    {
        [JsonConstructor]
        public SpellDef(string id, IReadOnlyList<string> items, IReadOnlyList<string> scrolls, SpellCastType castType, SpellTarget target, SpellKind kind,
                        SpellDeity deity, int minMana, int maxMana, SpellDelivery delivery, string effect, string animation,
                        int requiredIntelligence, string spawn, SpellMissile missile, SpellVisuals visuals)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Items = items ?? Array.Empty<string>();
            Scrolls = scrolls ?? Array.Empty<string>();
            CastType = castType;
            Target = target;
            Kind = kind;
            Deity = deity;
            MinMana = minMana;
            MaxMana = Math.Max(minMana, maxMana);
            Delivery = delivery;
            Effect = string.IsNullOrEmpty(effect) ? null : effect;
            Animation = string.IsNullOrEmpty(animation) ? null : animation;
            RequiredIntelligence = requiredIntelligence;
            Spawn = string.IsNullOrEmpty(spawn) ? null : spawn;
            Missile = missile;
            Visuals = visuals ?? SpellVisuals.None;
        }

        /// <summary>The G3 spell template name ("Spl_Fireball").</summary>
        public string Id { get; }
        /// <summary>Permanent items that cast this spell (the learned spell, It_Spell_…).</summary>
        public IReadOnlyList<string> Items { get; }
        /// <summary>Items used up by casting (G3 non-permanent: the scroll, It_Scroll_…).</summary>
        public IReadOnlyList<string> Scrolls { get; }
        public SpellCastType CastType { get; }
        public SpellTarget Target { get; }
        public SpellKind Kind { get; }
        public SpellDeity Deity { get; }
        /// <summary>Mana paid when the spell is loaded.</summary>
        public int MinMana { get; }
        /// <summary>Mana of a power cast (the rest, Max − Min, is paid when powering up).</summary>
        public int MaxMana { get; }
        public SpellDelivery Delivery { get; }
        /// <summary>The target effect (G3 FuncOnTargetHit: "MagicHeal", "MagicSummonSkeleton", "MagicHeatWave" …); null = none.</summary>
        public string Effect { get; }
        /// <summary>The cast motion's action when it is not the cast action itself ("Summon", "Heal", "Manipulate" …); null = default.</summary>
        public string Animation { get; }
        /// <summary>INT needed to learn the spell (G3 ReqAttrib1 INT).</summary>
        public int RequiredIntelligence { get; }
        /// <summary>The spawned template: the missile, the spell object or the summoned creature (null = none).</summary>
        public string Spawn { get; }
        /// <summary>The flying missile of a projectile spell; null for other spells.</summary>
        public SpellMissile Missile { get; }
        public SpellVisuals Visuals { get; }

        /// <summary>The deity's name as the damage rules use it ("Innos", "Adanos", "Beliar"), or null.</summary>
        [JsonIgnore] public string DeityName => Deity == SpellDeity.None ? null : Deity.ToString();
        /// <summary>The spell needs a focus target (projectiles home on it; instant spells act on it).</summary>
        [JsonIgnore] public bool NeedsFocus => Target == SpellTarget.Focus;

        /// <summary>Casting from this item uses it up (a scroll).</summary>
        public bool UsesUp(string item)
        {
            foreach (var s in Scrolls)
                if (string.Equals(s, item, StringComparison.Ordinal)) return true;
            return false;
        }

        string IDefinition.Key => Id;

        public override string ToString() => $"{Id} ({CastType}, {Delivery}{(Effect != null ? " " + Effect : "")}, mana {MinMana}/{MaxMana})";
    }

    /// <summary>
    /// The missile of a projectile spell (G3: the Spawn template's <c>gCDamage_PS</c> and <c>gCProjectile_PS</c>): its damage,
    /// its speed, and its looks.
    /// </summary>
    public sealed class SpellMissile
    {
        [JsonConstructor]
        public SpellMissile(string template, DamageType damageType, int damage, float manaMultiplier, float speed, string effect,
                            string impactEffect)
        {
            Template = template;
            DamageType = damageType;
            Damage = damage;
            ManaMultiplier = manaMultiplier;
            Speed = speed;
            Effect = effect;
            ImpactEffect = impactEffect;
        }

        /// <summary>G3 template name ("Mis_Fireball"); names with "Fire" or "Explosive" beat missile immunities.</summary>
        public string Template { get; }
        public DamageType DamageType { get; }
        /// <summary>G3 DamageAmount (the base of the spell damage formula, Docs/combat.md §2).</summary>
        public int Damage { get; }
        /// <summary>G3 DamageManaMultiplier (kept as data; the CP damage formula scales with INT instead).</summary>
        public float ManaMultiplier { get; }
        /// <summary>G3 ShootVelocity (100 = the engine's standard speed).</summary>
        public float Speed { get; }
        /// <summary>Effect of the flying missile ("eff_magic_fireball_projectile_01").</summary>
        public string Effect { get; }
        /// <summary>Effect where it hits ("eff_magic_fireball_impact_01").</summary>
        public string ImpactEffect { get; }
    }

    /// <summary>
    /// The G3 effect names (g3.efm) of a spell's phases: on the casting hand (hold, load, power, cast), on the caster and on
    /// the target. The presentation shows and sounds them; the rules do not use them.
    /// </summary>
    public sealed class SpellVisuals
    {
        public static readonly SpellVisuals None = new SpellVisuals(null, null, null, null, null, null, null);

        [JsonConstructor]
        public SpellVisuals(string itemHold, string itemLoad, string itemPower, string itemCast, string casterLoad, string casterCast,
                            string targetCast)
        {
            ItemHold = Clean(itemHold);
            ItemLoad = Clean(itemLoad);
            ItemPower = Clean(itemPower);
            ItemCast = Clean(itemCast);
            CasterLoad = Clean(casterLoad);
            CasterCast = Clean(casterCast);
            TargetCast = Clean(targetCast);
        }

        public string ItemHold { get; }
        public string ItemLoad { get; }
        public string ItemPower { get; }
        public string ItemCast { get; }
        public string CasterLoad { get; }
        public string CasterCast { get; }
        public string TargetCast { get; }

        static string Clean(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
