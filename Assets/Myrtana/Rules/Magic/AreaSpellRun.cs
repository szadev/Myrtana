using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Someone the area spell might hit this tick: its distance from the spell object and its party bond.</summary>
    public readonly struct AreaCandidate
    {
        public AreaCandidate(ActorId id, float distance, ActorId partyLeader = default, bool dead = false)
        {
            Id = id;
            Distance = distance;
            PartyLeader = partyLeader;
            Dead = dead;
        }

        public ActorId Id { get; }
        /// <summary>Distance from the spell object (cm).</summary>
        public float Distance { get; }
        /// <summary>The candidate's party leader (the player for his companions and summons, a summoner for its creatures).</summary>
        public ActorId PartyLeader { get; }
        public bool Dead { get; }
    }

    /// <summary>One hit of an area spell: who, how much and what kind of damage (the resolver applies species and protection).</summary>
    public readonly struct AreaHit
    {
        public AreaHit(ActorId target, int damage, DamageType type, float distance)
        {
            Target = target;
            Damage = damage;
            Type = type;
            Distance = distance;
        }

        public ActorId Target { get; }
        public int Damage { get; }
        public DamageType Type { get; }
        public float Distance { get; }

        public override string ToString() => $"{Target} {Damage} {Type} at {Distance:0} cm";
    }

    /// <summary>
    /// One living area spell object (G3 Smn_Heatwave, Smn_Firerain … whose script routine calls MagicAreaDamageTick;
    /// Docs/combat.md §12.1). Pure: the caller ticks it with the candidates around the object and resolves the hits.
    /// <list type="bullet">
    /// <item>Wave (Heat/Frost wave, Ice Bomb, Meteor, Lightning): every tick the nearest candidate beyond the inner radius
    /// and within the radius is hit with round(near + d/r·(far − near)); the inner radius becomes d + 5 cm. When none is
    /// left the object decays. Heat/Frost wave wait 1 ms between ticks, the others none (one hit per frame); Meteor and
    /// Lightning start after 0.5 s (ZS_Meteor / ZS_Lightning).
    /// Each victim is hit once per wave, even when the hit throws it back beyond the inner radius.</item>
    /// <item>Rain (Fire rain, Hail storm): every 0.4 s, with n candidates within 1000 cm, rand(100) &gt; 33 + min(5n, 70)
    /// misses; otherwise a random candidate takes 50 + rand(50). The rain lasts 8 + rand(4) s (+50 % with the caster's
    /// deity perk).</item>
    /// <item>CP 1.75: candidates led by the caster (its party, summons, controlled) and the caster's own leader are
    /// skipped; the caster itself and the dead are never candidates [interp].</item>
    /// </list>
    /// </summary>
    public sealed class AreaSpellRun
    {
        /// <summary>The wave's inner radius grows by this much past each victim (cm).</summary>
        public const float WaveStep = 5f;
        /// <summary>Rain duration: 8 + rand(4) s before the deity bonus.</summary>
        public const int RainBaseSeconds = 8, RainRandomSeconds = 4;

        readonly List<AreaCandidate> _eligible = new List<AreaCandidate>();
        /// <summary>A wave's victims so far: a victim knocked back past the inner radius is not hit again.</summary>
        readonly HashSet<ActorId> _waveHit = new HashSet<ActorId>();
        float _inner = -1f;
        float _nextTick;

        public AreaSpellRun(AreaSpellDef def, ActorId caster, ActorId casterLeader = default, float durationSeconds = 0)
        {
            Def = def ?? throw new ArgumentNullException(nameof(def));
            Caster = caster;
            CasterLeader = casterLeader;
            Duration = durationSeconds;
            _nextTick = def.StartDelaySeconds;
        }

        public AreaSpellDef Def { get; }
        public ActorId Caster { get; }
        public ActorId CasterLeader { get; }
        /// <summary>A rain's lifetime (s); waves end when nobody is left.</summary>
        public float Duration { get; }
        /// <summary>Seconds since the object appeared.</summary>
        public float Elapsed { get; private set; }
        /// <summary>The object decays (the wave found nobody left, the rain's time is up).</summary>
        public bool Over { get; private set; }
        public bool IsRain => Def.Mode == AreaSpellMode.Rain;

        /// <summary>A rain's duration for this caster: 8 + rand(4) s, ×1.5 with the player's matching deity perk.</summary>
        public static float RainSeconds(IRandom random, SpellDeity deity, bool isPlayer, Func<string, bool> knows) =>
            SpellCasting.DurationWithBonus(RainBaseSeconds + random.Next(RainRandomSeconds), deity, isPlayer, knows);

        /// <summary>CP friendly-fire rule of MagicAreaDamageTick (plus the caster and the dead).</summary>
        public static bool Skipped(in AreaCandidate c, ActorId caster, ActorId casterLeader) =>
            c.Dead || c.Id == caster || (!caster.IsEmpty && c.PartyLeader == caster) || (!casterLeader.IsEmpty && c.Id == casterLeader);

        /// <summary>
        /// Advances the object by <paramref name="dt"/> real seconds; at most one hit per tick. Returns true with the hit when
        /// one lands (a rain's miss and a wave with nobody left return false).
        /// </summary>
        public bool Tick(float dt, IReadOnlyList<AreaCandidate> candidates, IRandom random, out AreaHit hit)
        {
            hit = default;
            if (Over) return false;
            Elapsed += Math.Max(0, dt);
            if (IsRain && Elapsed >= Duration)
            {
                Over = true;
                return false;
            }
            if (Elapsed < _nextTick) return false;
            _nextTick = Elapsed + Def.TickSeconds;

            _eligible.Clear();
            if (candidates != null)
                foreach (var c in candidates)
                    if (!Skipped(c, Caster, CasterLeader) && c.Distance <= Def.Radius) _eligible.Add(c);
            return IsRain ? Rain(random, out hit) : Wave(out hit);
        }

        bool Wave(out AreaHit hit)
        {
            hit = default;
            int best = -1;
            for (int i = 0; i < _eligible.Count; i++)
                if (_eligible[i].Distance > _inner && !_waveHit.Contains(_eligible[i].Id) && (best < 0 || _eligible[i].Distance < _eligible[best].Distance)) best = i;
            if (best < 0)
            {
                Over = true;
                return false;
            }
            var c = _eligible[best];
            _inner = c.Distance + WaveStep;
            _waveHit.Add(c.Id);
            hit = new AreaHit(c.Id, Def.WaveDamage(c.Distance), Def.DamageType, c.Distance);
            return true;
        }

        bool Rain(IRandom random, out AreaHit hit)
        {
            hit = default;
            int n = _eligible.Count;
            if (n == 0) return false;
            if (!Def.RainHits(random.Next(100), n)) return false;
            var c = _eligible[random.Next(n)];
            int damage = Def.BaseDamage + (Def.RandomDamage > 0 ? random.Next(Def.RandomDamage) : 0);
            hit = new AreaHit(c.Id, damage, Def.DamageType, c.Distance);
            return true;
        }
    }
}
