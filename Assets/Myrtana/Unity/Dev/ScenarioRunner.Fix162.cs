#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using Myrtana.Core;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-162 ops (Docs/dev-scenarios.md): the [owner] option "Forgive accidental hits"
    /// (<see cref="Myrtana.Simulation.AccidentalHits"/>) for one scenario, and a stray hit of the hero on an NPC.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        bool? _forgiveHitsBefore; // fix-162: the session's option before a scenario's 'forgivehits'

        /// <summary>'forgivehits on=0|1': the option for this scenario only (the stored value comes back at its end).</summary>
        internal void SetForgiveHits(bool on)
        {
            var port = _session?.Combat.Port;
            if (port == null) { Fail("forgivehits: no session"); return; }
            _forgiveHitsBefore ??= port.ForgiveAccidentalHits;
            port.ForgiveAccidentalHits = on;
            Log($"forgivehits {(on ? "yes" : "no (original)")}");
        }

        void RestoreForgiveHits()
        {
            if (_forgiveHitsBefore == null) return;
            var port = _session?.Combat.Port;
            if (port != null) port.ForgiveAccidentalHits = _forgiveHitsBefore.Value;
            _forgiveHitsBefore = null;
        }

        /// <summary>
        /// 'strayhit npc [count=1] [dmg=1]': the hero's arrow lands on the NPC <c>count</c> times at once (the presenter's
        /// hit snapshot with a missile delivery, through <c>CombatService.Strike</c>: gates, damage, reactions, fight sound),
        /// as a shot past the foe would; logs each outcome and the NPC's fight target after.
        /// </summary>
        internal void StrayHit(ActorId id, int count, int damage)
        {
            var presenter = FindAnyObjectByType<CombatPresenter>();
            var target = FighterOf(id);
            if (presenter == null || target == null || Combat?.Fighter == null) { Fail($"strayhit {id}: no fighter shown"); return; }
            for (int i = 0; i < count; i++)
            {
                var hit = presenter.BuildHit(Combat.Fighter, target);
                hit.Delivery = HitDelivery.Missile;
                hit.Attacker.Action = CombatAction.None;
                hit.DamageType = DamageType.Missile;
                hit.DamageAmount = damage;
                hit.VictimIsTarget = true;
                var outcome = _session.Combat.Strike(hit, byPlayerParty: true);
                var brain = _session.NpcCombat.Get(id);
                Log($"strayhit {id} #{i + 1}: {outcome}; forgiven {_session.NpcCombat.Accidents.IsForgiven(new HitResolved(ActorId.Player, id, outcome))}; " +
                    $"{(brain == null ? "no brain" : brain.State.ToString())}");
            }
        }
    }
}
#endif
