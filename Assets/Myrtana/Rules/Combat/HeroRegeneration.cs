using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>What the per-second HP/MP tick needs to know about the hero (gameplay-data/status_tick.json).</summary>
    public struct HeroRegenState
    {
        public bool Dead;
        public bool MasterGladiator;
        public bool MasterMage;
        /// <summary>Sitting on the ground, a stool, a bench or a throne (AniState SitGround/SitStool/SitBench/SitThrone).</summary>
        public bool Sitting;
        /// <summary>An NPC fights the hero (AIMode Combat): AB gives no Master Gladiator regeneration then.</summary>
        public bool InCombat;
        /// <summary>A GUI page is open: AB gives no positive regeneration then.</summary>
        public bool PageOpen;
        /// <summary>Inside a magic barrier (status bit 256): −10 HP a second.</summary>
        public bool InMagicBarrier;
        public int HitPointsMax;
        public int ManaMax;
    }

    /// <summary>
    /// The hero's HP and MP change of one OnTick second, CP 1.75 (Script_Game OnTick FUN_100b0360 / FUN_100b0790,
    /// gameplay-logic.md §2.3). HP, positive part only for a living hero and (AB) with no page open: Perk_MasterGladiator
    /// +5 (AB: min(5, HPmax/80), nothing in combat); without the perk, sitting gives min(5, HPmax/100). The magic barrier takes
    /// 10 a second. Poison and burning (−5 each) are <see cref="StatusEffects"/>'. MP: Perk_MasterMage +5 (AB:
    /// clamp(MPmax/100, 1, 5)), same gate.
    /// </summary>
    public static class RegenRules
    {
        public const string MasterGladiatorPerk = "Perk_MasterGladiator", MasterMagePerk = "Perk_MasterMage";
        public const int BarrierPenalty = 10;

        public static int HitPointsPerSecond(in HeroRegenState s, RuleOptions options)
        {
            bool ab = options.AlternativeBalancing;
            int gain = 0;
            if (!s.Dead && !(ab && s.PageOpen))
            {
                if (s.MasterGladiator) gain = ab ? (s.InCombat ? 0 : Math.Min(5, s.HitPointsMax / 80)) : 5;
                else if (s.Sitting) gain = Math.Min(5, s.HitPointsMax / 100);
            }
            return gain - (s.InMagicBarrier ? BarrierPenalty : 0);
        }

        public static int ManaPerSecond(in HeroRegenState s, RuleOptions options)
        {
            bool ab = options.AlternativeBalancing;
            if (s.Dead || (ab && s.PageOpen) || !s.MasterMage) return 0;
            return ab ? Math.Max(1, Math.Min(5, s.ManaMax / 100)) : 5;
        }
    }

    /// <summary>
    /// Applies <see cref="RegenRules"/> to the hero once per real second. The session knows the perks, the vitals and the
    /// fight; the presentation tells it what only it sees (<see cref="Sitting"/>, <see cref="PageOpen"/>,
    /// <see cref="InMagicBarrier"/>).
    /// </summary>
    public sealed class HeroRegeneration
    {
        readonly Hero _hero;
        readonly ActorVitals _vitals;
        readonly RuleOptions _options;
        readonly Func<bool> _inCombat;
        float _carry;

        public HeroRegeneration(Hero hero, ActorVitals vitals, RuleOptions options, Func<bool> inCombat)
        {
            _hero = hero;
            _vitals = vitals;
            _options = options ?? RuleOptions.Default;
            _inCombat = inCombat ?? (() => false);
        }

        /// <summary>The hero sits on a seat (the object use sets it).</summary>
        public bool Sitting { get; set; }

        /// <summary>A GUI page is open (set by the presentation; null = never).</summary>
        public Func<bool> PageOpen { get; set; }

        /// <summary>The hero stands inside a magic barrier (set by the presentation; null = never).</summary>
        public Func<bool> InMagicBarrier { get; set; }

        public HeroRegenState State => new HeroRegenState
        {
            Dead = _vitals.IsDead(ActorId.Player),
            MasterGladiator = _hero.Knows(RegenRules.MasterGladiatorPerk),
            MasterMage = _hero.Knows(RegenRules.MasterMagePerk),
            Sitting = Sitting,
            InCombat = _inCombat(),
            PageOpen = PageOpen?.Invoke() ?? false,
            InMagicBarrier = InMagicBarrier?.Invoke() ?? false,
            HitPointsMax = _hero.Stats.Maximum(ActorVitals.HitPointsId),
            ManaMax = _hero.Stats.Maximum(ActorVitals.ManaId),
        };

        public void Tick(float realSeconds)
        {
            if (realSeconds <= 0) return;
            _carry += realSeconds;
            while (_carry >= 1f)
            {
                _carry -= 1f;
                var s = State;
                _vitals.AddHitPoints(ActorId.Player, RegenRules.HitPointsPerSecond(s, _options));
                _vitals.AddMana(ActorId.Player, RegenRules.ManaPerSecond(s, _options));
            }
        }
    }
}
