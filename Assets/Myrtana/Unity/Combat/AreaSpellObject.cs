using System.Collections.Generic;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// A living area spell in the world (G3 Smn_Heatwave, Smn_Firerain …; Docs/combat.md §12.1): ticks its
    /// <see cref="AreaSpellRun"/> with the shown fighters around it, resolves each hit as a spell-object hit of the caster
    /// (<see cref="MagicService.ToAreaHit"/>) and shows the object's G3 effect while it lives. Decides no damage itself.
    /// </summary>
    public sealed class AreaSpellObject : MonoBehaviour
    {
        /// <summary>Effects of the Smn_ templates (gCEffect_PS) that the spell's own visuals do not carry [data: G3Cli fx dump].</summary>
        static readonly Dictionary<string, string> ObjectEffects = new Dictionary<string, string>
        {
            ["Firerain"] = "eff_magic_firerain_cast_01",
            ["Hailstorm"] = "eff_magic_heavenzap_cast_01",
        };

        readonly List<AreaCandidate> _candidates = new List<AreaCandidate>();
        readonly Dictionary<ActorId, CombatFighter> _byId = new Dictionary<ActorId, CombatFighter>();
        CombatPresenter _presenter;
        CombatFighter _caster;
        SpellDef _spell;
        AreaSpellRun _run;
        int _fx;
        GameObject _placeholder;

        /// <summary>Starts the spell object at <paramref name="centre"/>.</summary>
        public static AreaSpellObject Spawn(CombatPresenter presenter, CombatFighter caster, SpellDef spell, AreaSpellDef def, Vector3 centre)
        {
            var session = presenter.Session;
            bool player = caster.Actor.IsPlayer;
            float duration = def.Mode == AreaSpellMode.Rain
                ? AreaSpellRun.RainSeconds(session.Random, spell.Deity, player, p => player && session.Hero.Knows(p))
                : 0f;
            var leader = session.Party != null && session.Party.Members.IsLedByPlayer(caster.Actor) ? ActorId.Player
                       : session.Summoning != null ? session.Summoning.SummonerOf(caster.Actor) : default;
            var go = new GameObject("Area " + def.Name);
            go.transform.position = centre;
            var obj = go.AddComponent<AreaSpellObject>();
            obj._presenter = presenter;
            obj._caster = caster;
            obj._spell = spell;
            obj._run = new AreaSpellRun(def, caster.Actor, leader, duration);
            obj.ShowLooks(def, centre);
            if (presenter.logHits) Debug.Log($"[Myrtana] area {def.Name} by {caster.Actor} at {centre} ({(duration > 0 ? duration.ToString("0.0") + " s" : "wave")})");
            return obj;
        }

        void ShowLooks(AreaSpellDef def, Vector3 centre)
        {
            // Heat wave, frost wave and ice bomb have no target effect: their caster cast effect is the wave (fix-110).
            string effect = ObjectEffects.TryGetValue(def.Name, out var e) ? e : _spell.Visuals.TargetCast ?? _spell.Visuals.CasterCast;
            if (G3EffectPlayer.Has(effect))
            {
                if (_run.IsRain) _fx = G3EffectPlayer.Start(effect, centre, Quaternion.identity, transform);
                else G3EffectPlayer.Play(effect, centre);
            }
            else if (_run.IsRain) _placeholder = SpellFx.Aura(transform, Vector3.up * 3f, SpellFx.ColorOf(def.DamageType), 6f);
            else
            {
                SpellFx.Burst(centre + Vector3.up, SpellFx.ColorOf(def.DamageType), def.Radius / 100f * 0.5f);
                if (effect != null) G3EffectSounds.Play(effect, centre);
            }
        }

        void Update()
        {
            var session = _presenter != null ? _presenter.Session : null;
            if (session == null || _run == null || _run.Over)
            {
                Destroy(gameObject);
                return;
            }
            Collect(session);
            if (!_run.Tick(Time.deltaTime, _candidates, session.Random, out var hit)) return;
            if (!_byId.TryGetValue(hit.Target, out var victim) || _caster == null) return;
            var context = MagicService.ToAreaHit(_presenter.BuildHit(_caster, victim), _spell, hit);
            var outcome = _presenter.StrikeArea(_caster, context);
            if (_presenter.logHits) Debug.Log($"[Myrtana] area {_run.Def.Name} {_caster.Actor} → {hit}: {outcome}");
        }

        void Collect(Myrtana.Session.GameSession session)
        {
            _candidates.Clear();
            _byId.Clear();
            var centre = transform.position;
            float reach = _run.Def.Radius / 100f + 1f;
            var vitals = session.Combat.Vitals;
            var party = session.Party?.Members;
            foreach (var f in _presenter.Fighters)
            {
                if (f == null || !f.isActiveAndEnabled) continue;
                var at = f.Body != null ? f.Body.Center : f.transform.position;
                float d = Vector3.Distance(at, centre);
                if (d > reach) continue;
                var leader = party != null && party.IsLedByPlayer(f.Actor) ? ActorId.Player
                           : session.Summoning != null ? session.Summoning.SummonerOf(f.Actor) : default;
                _candidates.Add(new AreaCandidate(f.Actor, d * 100f, leader, vitals.StateOf(f.Actor) == VitalState.Dead));
                _byId[f.Actor] = f;
            }
        }

        void OnDestroy()
        {
            G3EffectPlayer.Stop(_fx);
            if (_placeholder != null) Destroy(_placeholder);
        }
    }
}
