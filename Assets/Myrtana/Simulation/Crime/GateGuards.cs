using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>G3 gEGuardStatus (same order).</summary>
    public enum GateGuardStatus
    {
        /// <summary>Watches the gate: the next player who comes close gets the first warning.</summary>
        Active,
        FirstWarnGiven,
        SecondWarnGiven,
        /// <summary>Lets everybody pass (the story's SetGuardStatus, anything but "Active").</summary>
        Inactive,
        /// <summary>The player got past the guard (nearer the guard point than the guard) without a warning.</summary>
        Behind,
    }

    /// <summary>
    /// An NPC that guards a gate (G3 gCNPC_PS.GuardPoint): the point it keeps the player away from (world metres) and
    /// whether it watches at the start of a new game (GuardStatus Inactive in the world data = no).
    /// </summary>
    public sealed class GateGuardDef : IDefinition
    {
        [System.Text.Json.Serialization.JsonConstructor]
        public GateGuardDef(ActorId npc, WorldPose point, bool activeAtStart = true)
        {
            Npc = npc;
            Point = point;
            ActiveAtStart = activeAtStart;
        }

        public ActorId Npc { get; }
        public WorldPose Point { get; }
        public bool ActiveAtStart { get; }

        string IDefinition.Key => Npc.Name;
    }

    /// <summary>What a gate guard's perception step decides (G3 AssessActivePerception, gate-guard branch).</summary>
    public enum GateGuardVerdict
    {
        /// <summary>Nothing for the gate (the player is far, or the guard is inactive).</summary>
        None,
        /// <summary>The player is within 500: the guard talks to him if it has something important to say (a warning).</summary>
        MayTalk,
        /// <summary>The player came 80 closer to the guard point after the second warning: attack (reason GateGuard).</summary>
        Attack,
    }

    /// <summary>
    /// The gate guards' state and rules (Docs/perception-crime.md §4.7, Docs/gameplay-data/perception.json "gateGuard",
    /// CP 1.75 AssessActivePerception / IsGateGuard / gCInfo_PS FirstWarn-SecondWarn):
    /// <list type="bullet">
    /// <item>A gate guard has a guard point and is not Inactive. It ignores drawn weapons (AssessPlayerWeapon).</item>
    /// <item>Player 500 or farther from the guard: the status goes back to Active (unless Inactive).</item>
    /// <item>Within 500: an Active guard whose player stands nearer the guard point than the guard itself lets him be
    /// (Behind); then the guard talks if it has an important info - the FirstWarn info needs Active, the SecondWarn info
    /// FirstWarnGiven and the player 80 closer to the point than at the first warning. Each warning info records the
    /// status and the player's distance to the point.</item>
    /// <item>After the second warning, 80 closer again: attack.</item>
    /// </list>
    /// Distances are centimetres (3D), as in G3. Saved by <see cref="GateGuardsSaveSection"/>.
    /// </summary>
    public sealed class GateGuards
    {
        /// <summary>The player must come this much closer to the guard point than at the last warning (cm) [data].</summary>
        public const float CloserBy = 80f;
        /// <summary>At this distance from the guard (cm) the guard forgets its warnings [data].</summary>
        public const float ResetDistance = 500f;

        sealed class Guard
        {
            public WorldPose Point;
            public GateGuardStatus Status;
            public GateGuardStatus StartStatus;
            /// <summary>The player's distance to the guard point at the last warning (cm).</summary>
            public float LastDistance;
        }

        readonly Dictionary<ActorId, Guard> _guards = new Dictionary<ActorId, Guard>();
        /// <summary>SetGuardStatus for NPCs without a guard point (G3 stores the status; it has no effect).</summary>
        readonly Dictionary<ActorId, GateGuardStatus> _pointless = new Dictionary<ActorId, GateGuardStatus>();

        public GateGuards(IEnumerable<GateGuardDef> guards)
        {
            foreach (var g in guards ?? Array.Empty<GateGuardDef>())
            {
                if (g == null || g.Npc.IsEmpty) continue;
                var status = g.ActiveAtStart ? GateGuardStatus.Active : GateGuardStatus.Inactive;
                _guards[g.Npc] = new Guard { Point = g.Point, Status = status, StartStatus = status };
            }
        }

        public int Count => _guards.Count;

        /// <summary>The NPC has a guard point (G3 GetGuardPoint != None).</summary>
        public bool HasGuardPoint(ActorId npc) => _guards.ContainsKey(npc);

        public bool TryGetPoint(ActorId npc, out WorldPose point)
        {
            point = default;
            if (!_guards.TryGetValue(npc, out var g)) return false;
            point = g.Point;
            return true;
        }

        public GateGuardStatus StatusOf(ActorId npc) =>
            _guards.TryGetValue(npc, out var g) ? g.Status : _pointless.TryGetValue(npc, out var s) ? s : GateGuardStatus.Active;

        /// <summary>G3 IsGateGuard: a guard point and not Inactive.</summary>
        public bool IsGateGuard(ActorId npc) => _guards.TryGetValue(npc, out var g) && g.Status != GateGuardStatus.Inactive;

        /// <summary>G3 SetGuardStatus: "Active" → Active, anything else → Inactive.</summary>
        public void SetActive(ActorId npc, bool active)
        {
            if (npc.IsEmpty || npc.IsPlayer) return;
            var status = active ? GateGuardStatus.Active : GateGuardStatus.Inactive;
            if (_guards.TryGetValue(npc, out var g)) g.Status = status;
            else _pointless[npc] = status;
        }

        /// <summary>The player's distance to the guard point (cm).</summary>
        public float DistanceToPoint(ActorId npc, WorldPose player) =>
            _guards.TryGetValue(npc, out var g) ? player.DistanceTo(g.Point) * PerceptionRules.CentimetresPerUnit : float.MaxValue;

        /// <summary>G3 condition type FirstWarn: a guard point and status Active.</summary>
        public bool MayWarnFirst(ActorId npc) => _guards.TryGetValue(npc, out var g) && g.Status == GateGuardStatus.Active;

        /// <summary>G3 condition type SecondWarn: status FirstWarnGiven and the player 80 closer to the point than at the first warning.</summary>
        public bool MayWarnSecond(ActorId npc, WorldPose player) =>
            _guards.TryGetValue(npc, out var g) && g.Status == GateGuardStatus.FirstWarnGiven &&
            DistanceToPoint(npc, player) < g.LastDistance - CloserBy;

        /// <summary>A warning info ended (G3 OnEndInfo FirstWarn/SecondWarn): status and the player's distance are recorded.</summary>
        public void Warned(ActorId npc, bool second, WorldPose player)
        {
            if (!_guards.TryGetValue(npc, out var g)) return;
            g.Status = second ? GateGuardStatus.SecondWarnGiven : GateGuardStatus.FirstWarnGiven;
            g.LastDistance = DistanceToPoint(npc, player);
        }

        /// <summary>
        /// The gate-guard step of one active perception of <paramref name="npc"/> (standing at <paramref name="guard"/>)
        /// that noticed the player at <paramref name="player"/>. Not a gate guard → <see cref="GateGuardVerdict.None"/>.
        /// </summary>
        public GateGuardVerdict Assess(ActorId npc, WorldPose guard, WorldPose player)
        {
            if (!_guards.TryGetValue(npc, out var g) || g.Status == GateGuardStatus.Inactive) return GateGuardVerdict.None;
            float toPoint = DistanceToPoint(npc, player);
            if (g.Status == GateGuardStatus.SecondWarnGiven && toPoint < g.LastDistance - CloserBy) return GateGuardVerdict.Attack;
            float toGuard = guard.DistanceTo(player) * PerceptionRules.CentimetresPerUnit;
            if (toGuard >= ResetDistance)
            {
                g.Status = GateGuardStatus.Active;
                return GateGuardVerdict.None;
            }
            if (g.Status == GateGuardStatus.Active && toPoint < guard.DistanceTo(g.Point) * PerceptionRules.CentimetresPerUnit)
                g.Status = GateGuardStatus.Behind;
            return GateGuardVerdict.MayTalk;
        }

        internal IEnumerable<KeyValuePair<ActorId, (GateGuardStatus Status, float LastDistance)>> Changed()
        {
            foreach (var kv in _guards.OrderBy(k => k.Key.Name, StringComparer.Ordinal))
                if (kv.Value.Status != kv.Value.StartStatus || kv.Value.LastDistance != 0)
                    yield return new KeyValuePair<ActorId, (GateGuardStatus, float)>(kv.Key, (kv.Value.Status, kv.Value.LastDistance));
            foreach (var kv in _pointless.OrderBy(k => k.Key.Name, StringComparer.Ordinal))
                yield return new KeyValuePair<ActorId, (GateGuardStatus, float)>(kv.Key, (kv.Value, 0f));
        }

        internal void Restore(ActorId npc, GateGuardStatus status, float lastDistance)
        {
            if (_guards.TryGetValue(npc, out var g))
            {
                g.Status = status;
                g.LastDistance = lastDistance;
            }
            else if (!npc.IsEmpty) _pointless[npc] = status;
        }
    }

    /// <summary>Save section "world.gateGuards": every guard whose status or warning distance differs from the new game.</summary>
    public sealed class GateGuardsSaveSection : ISaveParticipant
    {
        readonly GateGuards _guards;

        public GateGuardsSaveSection(GateGuards guards) => _guards = guards ?? throw new ArgumentNullException(nameof(guards));

        public string Section => "world.gateGuards";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var list = _guards.Changed().ToList();
            w.WriteInt32(list.Count);
            foreach (var kv in list)
            {
                w.WriteString(kv.Key.Name);
                w.WriteInt32((int)kv.Value.Status);
                w.WriteDouble(kv.Value.LastDistance);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var id = new ActorId(r.ReadString());
                var status = (GateGuardStatus)r.ReadInt32();
                float last = (float)r.ReadDouble();
                _guards.Restore(id, status, last);
            }
        }
    }
}
