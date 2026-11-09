using System;
using G3.Player;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero's fall damage (G3 CP 1.75 OnMovementModeChanged; <see cref="FallRules"/>, Docs/hero.md "Falling"):
    /// on landing the fall time decides the damage, Perk_Acrobatic halves it and rolls, a heavy landing cries AARGH_03,
    /// HP below 1 kills. Also tells the hero animator which landing to show and holds the controls while the heavy
    /// landing or the roll plays (the ZS_HeavyLand / ZS_Acrobat AI states take the controls in G3).
    /// </summary>
    public sealed class HeroFallDamage : IDisposable
    {
        public const string AcrobaticPerk = "Perk_Acrobatic";
        /// <summary>Logs every damaging landing ("[fall] …") for the editor checks.</summary>
        public static bool Log = true;

        readonly G3Player _player;
        readonly G3HeroAnimator _hero;
        readonly Func<GameSession> _session;
        readonly Func<bool> _dead;

        public HeroFallDamage(G3Player player, G3HeroAnimator hero, Func<GameSession> session, Func<bool> dead)
        {
            _player = player;
            _hero = hero;
            _session = session;
            _dead = dead;
            _player.Landed += OnLanded;
            if (_hero != null) _hero.LandingKindOf = KindOf;
        }

        /// <summary>The heavy landing or the acrobatic roll is playing: no walking.</summary>
        public bool Busy => _hero != null && (_hero.Current == G3HeroAnimator.State.HardLand || _hero.Current == G3HeroAnimator.State.Roll);

        float _rollPhase = -1f;
        G3MotionPlayer _motion;

        /// <summary>
        /// Carries the hero along the acrobatic roll (ZS_Acrobat's combat move "Fall": the motion name's 430 cm over the
        /// motion, straight ahead of the body) through <paramref name="displace"/> (the CharacterController stops at walls).
        /// </summary>
        public void Tick(Action<Vector3> displace)
        {
            if (_hero == null || _hero.Current != G3HeroAnimator.State.Roll)
            {
                _rollPhase = -1f;
                return;
            }
            if (_motion == null) _motion = _hero.GetComponent<G3MotionPlayer>();
            if (_motion == null) return;
            float phase = _motion.CurrentPhase;
            if (_rollPhase < 0f) _rollPhase = 0f;
            float distance = FallRules.RollDistance;
            float step = FallRules.RollStep(_rollPhase, phase, distance);
            _rollPhase = Mathf.Max(_rollPhase, phase);
            if (step <= 0f) return;
            var forward = _hero.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return;
            displace(forward.normalized * step + Vector3.down * 0.05f); // keep it on the ground over small bumps
        }

        /// <summary>The last landing that hurt (tests and editor checks).</summary>
        public FallOutcome Last { get; private set; }

        public void Dispose()
        {
            _player.Landed -= OnLanded;
            if (_hero != null && _hero.LandingKindOf == (Func<float, LandingKind>)KindOf) _hero.LandingKindOf = null;
        }

        FallOutcome Outcome(float fallSeconds, GameSession s)
        {
            var vitals = s.Combat.Vitals;
            return FallRules.Land(fallSeconds, _player.Water, s.Hero.Knows(AcrobaticPerk), vitals.IsDown(ActorId.Player),
                s.Settings.Rules.AlternativeBalancing, vitals.HitPoints(ActorId.Player));
        }

        LandingKind KindOf(float fallSeconds)
        {
            var s = _session();
            return s == null ? FallRules.DefaultKind(fallSeconds) : Outcome(fallSeconds, s).Kind;
        }

        void OnLanded(float airTime, float fallSeconds)
        {
            var s = _session();
            if (s == null || _dead()) return;
            var o = Outcome(fallSeconds, s);
            if (o.Damage <= 0) return;
            Last = o;
            var vitals = s.Combat.Vitals;
            int hp = vitals.HitPoints(ActorId.Player);
            // With the perk and AB off G3 skips the death check; the hero keeps at least 1 HP here [interp].
            int loss = o.Killed ? o.Damage : Math.Min(o.Damage, Math.Max(0, hp - 1));
            if (o.Killed) vitals.Kill(ActorId.Player, byPlayerParty: false);
            else vitals.AddHitPoints(ActorId.Player, -loss);
            if (o.PainCry) s.Events.Publish(new CombatShout(ActorId.Player, "Aargh_03"));
            if (Log)
                Debug.Log($"[fall] t={fallSeconds:F2}s air={airTime:F2}s damage={o.Damage} kind={o.Kind} killed={o.Killed} " +
                          $"hp {hp}->{vitals.HitPoints(ActorId.Player)} at {_player.transform.position}");
        }
    }
}
