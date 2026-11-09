using System;

namespace Myrtana.Simulation
{
    /// <summary>How a fighter runs at its target (<see cref="CombatChaseRules.Decide"/>).</summary>
    public enum ChaseWay
    {
        /// <summary>Straight at the target: nothing in the way on the NavMesh, or no NavMesh to judge by (the old run).</summary>
        Straight,
        /// <summary>Along the NavMesh path (round a ledge, down a drop link, through the next cell).</summary>
        Path,
        /// <summary>No way gets into reach of the target (it stands on a rock, below a cliff the body cannot drop).</summary>
        Unreachable,
    }

    /// <summary>What the NavMesh says about the way to the target (one plan of the chase).</summary>
    public enum ChasePlan
    {
        /// <summary>The fighter is not on the NavMesh (its cell is not built yet): nothing to judge by.</summary>
        NoNavMesh,
        /// <summary>A complete path to the target (drop links included).</summary>
        Complete,
        /// <summary>The path ends short of the target (the connected NavMesh's closest point).</summary>
        Partial,
        /// <summary>The fighter is on the NavMesh but no path leads anywhere towards the target.</summary>
        None,
    }

    /// <summary>
    /// The way of a combat chase (fix-142, owner: a Trelis ogre ran straight at the hero in the ravine below, the ledge
    /// refusal held it at the edge, and it stood there as a free target). G3's ZS_Attack_Goto / ZS_Attack_Sprint walk to
    /// the target with the engine goto (sAIGotoInstr → gCNavigationMap::FindWay over the nav zones and paths), so a
    /// G3 NPC goes round; our NavMesh plays that part (<c>NpcWalker</c>, cross-cell NavMesh, one-way drop links of at
    /// most <c>NavDropRules.MaxDrop</c> where G3 bodies fall off a step). When FindWay finds no way G3 clears the route
    /// and the NPC stands (Game.dll RunNPCNavigation 0x10133696) until the chase time sends it into
    /// ZS_Attack_WaitForFlee, whose 0.5 s check sends it back to the attack as long as the target does not get farther:
    /// it stands forever under the hero's arrows. [interp] We call such a target unreachable and let the brain retreat
    /// (<see cref="NpcCombatBrain"/>). Pure; metres.
    /// </summary>
    public static class CombatChaseRules
    {
        /// <summary>Height difference still on one level (m; the view's VerticalReach).</summary>
        public const float SameLevel = 2f;
        /// <summary>A partial path that ends this much beyond the own reach from the target still gets there (m) [interp].</summary>
        public const float EndSlack = 0.75f;
        /// <summary>An unreachable verdict must hold this long before the brain hears of it (s): a re-plan or a drop link may still find a way [interp].</summary>
        public const float UnreachableSeconds = 1.5f;
        /// <summary>The chase plans again when the target moved this far (m) or this long after the last plan (s).</summary>
        public const float ReplanMove = 1f, ReplanSeconds = 0.5f;

        /// <summary>
        /// The way after a plan. <paramref name="straightClear"/>: the NavMesh line from self to the target has no edge on
        /// it and both ends are on one level. <paramref name="endLeft"/> / <paramref name="endRise"/>: flat distance and
        /// height from the path's last NavMesh point to the target (target minus end; Partial only).
        /// <paramref name="reach"/>: the own attack reach (m, the body's footprints included).
        /// </summary>
        public static ChaseWay Decide(ChasePlan plan, bool straightClear, float endLeft, float endRise, float reach)
        {
            if (plan == ChasePlan.NoNavMesh) return ChaseWay.Straight;
            if (straightClear) return ChaseWay.Straight;
            if (plan == ChasePlan.Complete) return ChaseWay.Path;
            if (plan == ChasePlan.Partial && endLeft <= reach + EndSlack && Math.Abs(endRise) <= SameLevel) return ChaseWay.Path;
            return ChaseWay.Unreachable;
        }

        /// <summary>Whether the chase plans again (the target moved, the plan is old, or there is none).</summary>
        public static bool Replan(bool hasPlan, float sincePlan, float targetMoved) =>
            !hasPlan || sincePlan >= ReplanSeconds || targetMoved >= ReplanMove;
    }

    /// <summary>
    /// Debounces <see cref="ChaseWay.Unreachable"/> verdicts (<see cref="CombatChaseRules.UnreachableSeconds"/>); a walk
    /// along the path that got stuck (no progress for the walker's stuck time) counts as unreachable at once and stays so
    /// until <see cref="Reset"/> (a new plan would only lead into the same wall). Pure.
    /// </summary>
    public sealed class ChaseReachTracker
    {
        float _unreachableFor;
        bool _stuck;

        /// <summary>The brain should hear that the target cannot be reached.</summary>
        public bool Unreachable { get; private set; }

        public void Update(ChaseWay way, float dt)
        {
            if (_stuck) return;
            if (way != ChaseWay.Unreachable)
            {
                _unreachableFor = 0;
                Unreachable = false;
                return;
            }
            _unreachableFor += Math.Max(0, dt);
            if (_unreachableFor >= CombatChaseRules.UnreachableSeconds) Unreachable = true;
        }

        /// <summary>The walk along the path made no progress (NpcWalker.Result.Stuck).</summary>
        public void Stuck()
        {
            _stuck = true;
            _unreachableFor = CombatChaseRules.UnreachableSeconds;
            Unreachable = true;
        }

        public void Reset()
        {
            _stuck = false;
            _unreachableFor = 0;
            Unreachable = false;
        }
    }
}
