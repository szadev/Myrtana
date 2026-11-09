#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-59 <c>touchwatch</c>: counts every touch of a weapon, missile or spell missile on one victim that reached the
    /// rules (<see cref="CombatPresenter.TouchResolved"/>), per attacker: touches, the gates that dropped them, hits and
    /// damage. Conditions <c>touches:X</c> and <c>hurtby:X</c> read the tally (a companion fighting beside the hero:
    /// <c>expect hurtby:Gorn=0</c>).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        sealed class TouchTally
        {
            public int Touches, Hits, Damage;
            public readonly Dictionary<string, int> Gates = new Dictionary<string, int>();
        }

        readonly Dictionary<ActorId, TouchTally> _touchTally = new Dictionary<ActorId, TouchTally>();
        ActorId _touchVictim;
        bool _touchWatching;

        void StartTouchWatch(ActorId victim)
        {
            StopTouchWatch(log: false);
            _touchTally.Clear();
            _touchVictim = victim;
            _touchWatching = true;
            CombatPresenter.TouchResolved += OnTouchResolved;
        }

        void StopTouchWatch(bool log)
        {
            if (!_touchWatching) return;
            CombatPresenter.TouchResolved -= OnTouchResolved;
            _touchWatching = false;
            if (log) Log("touchwatch " + _touchVictim + ": " + TouchSummary());
        }

        void OnTouchResolved(ActorId attacker, ActorId victim, HitDelivery delivery, HitOutcome outcome)
        {
            if (victim != _touchVictim || outcome == null) return;
            if (!_touchTally.TryGetValue(attacker, out var t)) _touchTally[attacker] = t = new TouchTally();
            t.Touches++;
            if (outcome.Result == HitResult.Ignored)
            {
                string gate = outcome.Gate.ToString();
                t.Gates[gate] = t.Gates.TryGetValue(gate, out int n) ? n + 1 : 1;
                return;
            }
            t.Hits++;
            t.Damage += outcome.Damage;
            // a hit on the watched body is worth a line at once (who, how, what it did)
            Log($"touchwatch: {attacker} -> {victim} ({delivery}) {outcome}");
        }

        string TouchSummary()
        {
            if (_touchTally.Count == 0) return "no touches";
            return string.Join("; ", _touchTally.Select(kv =>
                $"{kv.Key} touches={kv.Value.Touches} hits={kv.Value.Hits} damage={kv.Value.Damage}" +
                (kv.Value.Gates.Count > 0 ? " spared[" + string.Join(",", kv.Value.Gates.Select(g => g.Key + "x" + g.Value)) + "]" : "")));
        }

        /// <summary>Condition values: touches (<c>touches</c>) and damage (<c>hurtby</c>) of <paramref name="attacker"/> on the watched victim.</summary>
        string TouchValue(string kind, ActorId attacker)
        {
            _touchTally.TryGetValue(attacker, out var t);
            return kind == "touches" ? (t?.Touches ?? 0).ToString(Inv) : (t?.Damage ?? 0).ToString(Inv);
        }
    }
}
#endif
