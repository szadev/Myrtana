using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Parties led by an NPC (CP 1.75, Docs/party.md §5): the info command JoinNPC (63) makes Entity1 a Controlled
    /// member of Entity2's party (a player-party member is dismissed first; the alignment follows the leader's). G3
    /// SelectRoutineTask then gives
    /// <list type="bullet">
    /// <item>the leader ZS_TravelToDestination: it goes to its routine destination, waits 5 s there and stops leading
    /// (SetPartyLeader None);</item>
    /// <item>each member ZS_FollowNPC: every 1 s, farther than 5 m (FUN_10012d40: 500 cm for NPC followers) from the leader,
    /// it goes to him; HandleControlledReturnHome releases a Controlled member once its leader stopped leading
    /// [interp: G3 releases it when its goto ends while the leader is not the player].</item>
    /// </list>
    /// A member whose leader dies or leaves the game is released. Saved by <see cref="NpcPartiesSaveSection"/>.
    /// </summary>
    public sealed class NpcParties : IActorStore
    {
        /// <summary>A follower keeps this close to its NPC leader (m).</summary>
        public const float FollowDistance = 5f;
        /// <summary>The leader waits this long at its destination before the party ends (real s).</summary>
        public const float LeaderWaitSeconds = 5f;
        /// <summary>ZS_FollowNPC_Loop re-checks the distance this often (real s).</summary>
        public const float RecheckSeconds = 1f;

        readonly Dictionary<ActorId, ActorId> _leaderOf = new Dictionary<ActorId, ActorId>();
        readonly Dictionary<ActorId, double> _arrivedAt = new Dictionary<ActorId, double>();

        public int Count => _leaderOf.Count;

        /// <summary>JoinNPC: <paramref name="member"/> follows <paramref name="leader"/> (false for the player or itself).</summary>
        public bool Join(ActorId member, ActorId leader)
        {
            if (member.IsEmpty || leader.IsEmpty || member.IsPlayer || leader.IsPlayer || member == leader) return false;
            if (_leaderOf.ContainsKey(leader)) _leaderOf.Remove(leader); // a leader is nobody's member
            foreach (var m in MembersOf(member).ToList()) _leaderOf.Remove(m); // nor is a member a leader
            _leaderOf[member] = leader;
            _arrivedAt.Remove(leader);
            return true;
        }

        /// <summary>The NPC leading <paramref name="member"/> (false when it follows nobody).</summary>
        public bool TryGetLeader(ActorId member, out ActorId leader) => _leaderOf.TryGetValue(member, out leader);

        public bool Leads(ActorId npc) => _leaderOf.ContainsValue(npc);

        public IEnumerable<ActorId> MembersOf(ActorId leader) =>
            _leaderOf.Where(kv => kv.Value == leader).Select(kv => kv.Key).OrderBy(id => id.Name, StringComparer.Ordinal);

        /// <summary>The member leaves its NPC leader (back to its routine).</summary>
        public void Release(ActorId member) => _leaderOf.Remove(member);

        /// <summary>The leader stops leading (SetPartyLeader None): every member is released.</summary>
        public void Disband(ActorId leader)
        {
            foreach (var m in MembersOf(leader).ToList()) _leaderOf.Remove(m);
            _arrivedAt.Remove(leader);
        }

        /// <summary>
        /// ZS_TravelToDestination_Loop: the leader stands at its destination since <paramref name="now"/> (game s) or earlier;
        /// true once it waited <see cref="LeaderWaitSeconds"/> (× <paramref name="timeScale"/>) — the party then ends.
        /// </summary>
        public bool LeaderWaited(ActorId leader, double now, float timeScale)
        {
            if (!_arrivedAt.TryGetValue(leader, out var since)) _arrivedAt[leader] = since = now;
            return now - since >= LeaderWaitSeconds * Math.Max(1f, timeScale);
        }

        /// <summary>The leader is on its way again (not at its destination).</summary>
        public void LeaderMoved(ActorId leader) => _arrivedAt.Remove(leader);

        public void Forget(ActorId actor)
        {
            _leaderOf.Remove(actor);
            Disband(actor);
        }

        public bool Holds(ActorId actor) => _leaderOf.ContainsKey(actor) || Leads(actor);

        internal IEnumerable<KeyValuePair<ActorId, ActorId>> Pairs => _leaderOf.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal);

        internal void Clear()
        {
            _leaderOf.Clear();
            _arrivedAt.Clear();
        }
    }

    /// <summary>Save section "npcParties": member → NPC leader.</summary>
    public sealed class NpcPartiesSaveSection : ISaveParticipant
    {
        readonly NpcParties _parties;

        public NpcPartiesSaveSection(NpcParties parties) => _parties = parties ?? throw new ArgumentNullException(nameof(parties));

        public string Section => "npcParties";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var pairs = _parties.Pairs.ToList();
            w.WriteInt32(pairs.Count);
            foreach (var kv in pairs)
            {
                w.WriteString(kv.Key.Name);
                w.WriteString(kv.Value.Name);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            _parties.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var member = new ActorId(r.ReadString());
                var leader = new ActorId(r.ReadString());
                _parties.Join(member, leader);
            }
        }
    }

    /// <summary>
    /// CP OnFollowPlayer: a Mob member of the hero's party (MobEnabled) farther than 620 m from the "Fireclan" enclave
    /// leaves the party (leader None, not waiting, member type None), says "TooFarFromMeetingPoint" and goes back to its
    /// routine. Checked while it follows (not while it waits, and not while the hero swims) [the check sits in the
    /// follow callback; a global session flag (+0x290) that suppresses it is not decoded, assumed off].
    /// </summary>
    public static class MobMeetingPoint
    {
        public const string Enclave = "Fireclan";
        public const float MaxMetres = 620f;
        public const string Line = "TooFarFromMeetingPoint";

        public static bool TooFar(WorldPose member, WorldPose fireclan) => member.DistanceTo(fireclan) > MaxMetres;
    }
}
