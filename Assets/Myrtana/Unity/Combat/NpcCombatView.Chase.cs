using Myrtana.Simulation;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// The combat chase (fix-142): a fighter out of reach runs at its target along the NavMesh, as G3's ZS_Attack_Goto
    /// walks with the engine goto (FindWay over the nav zones and paths) instead of in a straight line that the ledge
    /// refusal (<see cref="Displace"/>, fix-60) stops at the first edge. Straight when the NavMesh line to the target is
    /// clear (or there is no NavMesh to judge by: the old run); else along the path of an own <see cref="NpcWalker"/>
    /// (cross-cell NavMesh, one-way drop links down steps a G3 body falls off); when no way gets into reach
    /// (<see cref="CombatChaseRules"/>) the body stands and the brain hears <see cref="CombatSituation.TargetUnreachable"/>
    /// (it retreats). A retreat walks to a NavMesh point away from the target the same way.
    /// </summary>
    public sealed partial class NpcCombatView
    {
        readonly NpcWalker _chaseWalker = new NpcWalker();
        readonly ChaseReachTracker _reachTracker = new ChaseReachTracker();
        Vector3 _chaseGoal;
        float _chasePlanAge;
        ChaseWay _chaseWay = ChaseWay.Straight;
        float _reach = 1.2f;
        bool _retreatPlanned, _retreatDone;

        /// <summary>The way of the running chase (runner probes).</summary>
        public ChaseWay ChaseWay => _chaseWay;
        /// <summary>The chase found no way into reach (handed to the brain).</summary>
        public bool ChaseUnreachable => _reachTracker.Unreachable;
        /// <summary>How the chase walker's last plan goes (logs, probes).</summary>
        public string ChasePlanText => _chaseWalker.Plan;
        /// <summary>
        /// The longest the body stood still while it chased (s, since the fight started): the owner's "free target at the
        /// edge" (runner probe <c>chasestand</c>).
        /// </summary>
        public float ChaseStandMax { get; private set; }
        float _chaseStandFor;

        void ResetChase()
        {
            _chaseWalker.Clear();
            _reachTracker.Reset();
            _chaseWay = ChaseWay.Straight;
            _chasePlanAge = 0;
            _retreatPlanned = _retreatDone = false;
            _chaseStandFor = 0;
            ChaseStandMax = 0;
        }

        /// <summary>One frame of running at <paramref name="target"/> (Fwd / Move with the gap still open).</summary>
        void Chase(CombatFighter target, float speed, float gap)
        {
            var self = transform.position;
            var goal = target.transform.position;
            float dt = Time.deltaTime;
            _chasePlanAge += dt;
            ChaseFrame(target, speed, gap, self, goal, dt);
            float moved = new Vector2(transform.position.x - self.x, transform.position.z - self.z).magnitude;
            _chaseStandFor = moved < 0.2f * speed * dt ? _chaseStandFor + dt : 0;
            if (_chaseStandFor > ChaseStandMax) ChaseStandMax = _chaseStandFor;
        }

        void ChaseFrame(CombatFighter target, float speed, float gap, Vector3 self, Vector3 goal, float dt)
        {
            // fliers / water walkers (bloodflies over water) keep the straight run: the NavMesh avoids deep water
            if (_view != null && _view.OverWater)
            {
                _chaseWay = ChaseWay.Straight;
                _reachTracker.Reset();
                RunAt(goal, speed, gap);
                return;
            }
            if (CombatChaseRules.Replan(_chaseWalker.HasDestination, _chasePlanAge, Vector3.Distance(goal, _chaseGoal)))
                PlanChase(self, goal, target);
            _reachTracker.Update(_chaseWay, dt);
            switch (_chaseWay)
            {
                case ChaseWay.Straight:
                    RunAt(goal, speed, gap);
                    break;
                case ChaseWay.Path:
                    var r = _chaseWalker.Step(transform, speed, dt, target.transform);
                    if (r == NpcWalker.Result.Stuck)
                    {
                        Debug.Log($"[combat] {_view.Actor} chase stuck at {transform.position} ({_chaseWalker.Plan}): target unreachable");
                        _reachTracker.Stuck();
                    }
                    break;
                default: // unreachable: stand and watch (the brain retreats once the verdict holds)
                    Face(goal);
                    break;
            }
        }

        void RunAt(Vector3 goal, float speed, float gap)
        {
            var to = goal - transform.position;
            to.y = 0;
            Run(to, speed, Mathf.Min(speed * Time.deltaTime, gap - MinGap));
        }

        void PlanChase(Vector3 self, Vector3 goal, CombatFighter target)
        {
            _chaseGoal = goal;
            _chasePlanAge = 0;
            var was = _chaseWay;
            _chaseWalker.OverWater = _view != null && _view.Walker.OverWater;
            _chaseWalker.SwimDepth = _view != null ? _view.Walker.SwimDepth : _chaseWalker.SwimDepth;
            _chaseWalker.SetDestination(self, goal, keepProgress: _chaseWalker.HasDestination && !_chaseWalker.Direct);
            ChasePlan plan = !_chaseWalker.StartOnNavMesh ? ChasePlan.NoNavMesh
                : !_chaseWalker.OnNavMesh ? ChasePlan.None
                : _chaseWalker.PathComplete ? ChasePlan.Complete : ChasePlan.Partial;
            var end = _chaseWalker.TryNavEnd(out var e) ? e : self;
            float endLeft = new Vector2(goal.x - end.x, goal.z - end.z).magnitude;
            float reach = _reach + _fighter.Body.Footprint + target.Body.Footprint;
            _chaseWay = CombatChaseRules.Decide(plan, StraightClear(self, goal), endLeft, goal.y - end.y, reach);
            if (_chaseWay != was)
                Debug.Log($"[combat] {_view.Actor} chase {was} -> {_chaseWay} at {self} to {goal}: {plan}, {_chaseWalker.Plan}");
        }

        /// <summary>The NavMesh line from self to the target crosses no edge and stays on one level.</summary>
        static bool StraightClear(Vector3 self, Vector3 goal)
        {
            if (!NavMesh.SamplePosition(self, out var a, 1f, NavMesh.AllAreas)) return false;
            if (!NavMesh.SamplePosition(goal, out var b, 1f, NavMesh.AllAreas)) return false;
            if (Mathf.Abs(a.position.y - b.position.y) > CombatChaseRules.SameLevel) return false;
            return !NavMesh.Raycast(a.position, b.position, out _, NavMesh.AllAreas);
        }

        /// <summary>
        /// One frame of a retreat (fix-142, <see cref="NpcCombatBrain.Retreating"/>): along the NavMesh to a point
        /// <see cref="FleePointDistance"/> away from the target; arriving or getting stuck ends it (the flee point).
        /// </summary>
        void Retreat(CombatFighter target)
        {
            if (_retreatDone) return;
            if (!_retreatPlanned)
            {
                _retreatPlanned = true;
                var away = transform.position - target.transform.position;
                away.y = 0;
                if (away.sqrMagnitude < 1e-4f) away = -transform.forward;
                var to = transform.position + away.normalized * FleePointDistance;
                _chaseWalker.SetDestination(transform.position, to);
                Debug.Log($"[combat] {_view.Actor} retreats from {target.Actor} (unreachable) to {to}: {_chaseWalker.Plan}");
            }
            var r = _chaseWalker.Step(transform, _runSpeed, Time.deltaTime);
            if (r != NpcWalker.Result.Walking) _retreatDone = true;
        }
    }
}
