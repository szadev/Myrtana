using System;
using System.Collections.Generic;
using G3.Effects;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The session side of G3's effects and weather (Docs/formats-effects.md §8): the weather runs on the game clock, a level-up
    /// plays <c>eff_event_levelup_01</c> on the hero (Script_Game: after "GO_LevelUp"), a teleport plays
    /// <c>eff_magic_teleport_cast_01</c> where the hero leaves and where he arrives [interp]. Hits, spells, arrows and motion
    /// effect tracks get their visuals through <see cref="G3EffectPlayer"/> without this view.
    /// </summary>
    public sealed class EffectsView : MonoBehaviour, ISessionView
    {
        public const string LevelUpEffect = "eff_event_levelup_01";
        public const string TeleportEffect = "eff_magic_teleport_cast_01";
        /// <summary>Spl_Light: the light ball that follows the caster (g3.efm eff_magic_light_source_01: glow, point light, sphere).</summary>
        public const string LightSourceEffect = SpellEffectRules.LightSourceEffect;

        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        Transform _hero;
        G3WeatherDirector _weather;
        Vector3 _teleportFrom;
        float _teleportUntil = -1;
        int _lightFx;
        float _lightUntil = -1;

        public void Init(Transform hero, G3WeatherDirector weather)
        {
            _hero = hero;
            _weather = weather;
        }

        public void Bind(GameSession session)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            if (session == null) return;
            if (_weather != null && session.Clock is GameClock clock) _weather.DayTimeSource = () => clock.Day + clock.HourOfDay / 24.0;
            _subscriptions.Add(session.Events.Subscribe<LevelReached>(_ => OnHero(LevelUpEffect)));
            _subscriptions.Add(session.Events.Subscribe<SpellCasted>(e =>
            {
                // [interp] Spl_Light lasts (8 + random(0..3)) · 10 s (Docs/gameplay-data/magic.json, deity bonus not applied), one at a time.
                if (e.Caster != ActorId.Player || e.Spell != SpellEffectRules.LightSpell || _hero == null) return;
                G3EffectPlayer.Stop(_lightFx);
                // fix-196: a small orb above the head (G3.Effects.LightOrbRules), not the HDR blob at the camera line; the
                // light casts shadows only where item lights may (moving shadowed point lights are costly)
                var at = _hero.TransformPoint(new Vector3(LightOrbRules.OffsetRight, LightOrbRules.OffsetUp, LightOrbRules.OffsetForward));
                bool shadows = G3ItemEffect.AllowShadows;
                _lightFx = G3EffectPlayer.Start(LightSourceEffect, at, _hero.rotation, _hero, false, t => LightOrbRules.For(t, shadows));
                _lightUntil = Time.time + (8 + UnityEngine.Random.Range(0, 4)) * 10f;
            }));
            _subscriptions.Add(session.Events.Subscribe<TeleportRequested>(_ =>
            {
                if (_hero == null) return;
                OnHero(TeleportEffect);
                _teleportFrom = _hero.position;
                _teleportUntil = Time.unscaledTime + 15f;
            }));
        }

        void OnHero(string effect)
        {
            if (_hero == null) return;
            G3EffectPlayer.Play(effect, _hero.position + Vector3.up, _hero);
        }

        void LateUpdate()
        {
            if (_lightUntil >= 0 && Time.time > _lightUntil) { G3EffectPlayer.Stop(_lightFx); _lightFx = 0; _lightUntil = -1; }
            if (_teleportUntil < 0 || _hero == null) return;
            if (Time.unscaledTime > _teleportUntil) { _teleportUntil = -1; return; }
            if ((_hero.position - _teleportFrom).sqrMagnitude < 20f * 20f) return;
            _teleportUntil = -1;
            OnHero(TeleportEffect); // arrived
        }

        void OnDestroy()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
