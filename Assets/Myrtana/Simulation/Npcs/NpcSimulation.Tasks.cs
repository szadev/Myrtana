using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The routine tasks that do more than stand at one station (par-npc2): smalltalk lines and the 120 s end
    /// (<see cref="Smalltalk"/>), sermons and their listeners (<see cref="Preaching"/>), the multi-spot work cycles
    /// (<see cref="WorkCycles"/>) and parties led by an NPC (<see cref="NpcParties"/>).
    /// </summary>
    public sealed partial class NpcSimulation
    {
        readonly Dictionary<ActorId, Sermon> _sermons = new Dictionary<ActorId, Sermon>();
        readonly List<RoutinePointDef> _spots = new List<RoutinePointDef>();
        readonly List<NpcAgent> _listeners = new List<NpcAgent>();

        /// <summary>An NPC says an ambient SVM line (smalltalk, a sermon); the session publishes <see cref="NpcAmbientLine"/>.</summary>
        public event Action<ActorId, string> Said;

        /// <summary>
        /// Whether the NPC's voice set has a recorded shared line (G3 HasSVMBlock; "Speech_00" decides between a spoken
        /// sermon and the gesture-only one). Null = every voice has every line (tests).
        /// </summary>
        public Func<ActorId, string, bool> HasLine { get; set; }

        /// <summary>
        /// SendPerceptionPreach's filters the simulation cannot see (alive and up, not a gate guard, not a slave, not fighting);
        /// null = everybody may listen.
        /// </summary>
        public Func<ActorId, bool> MayListen { get; set; }

        /// <summary>Parties led by NPCs (null = none).</summary>
        public NpcParties Parties { get; set; }

        /// <summary>The sermon line the preacher is at (0..9), or −1 when it does not preach now.</summary>
        public int SermonLine(ActorId preacher) => _sermons.TryGetValue(preacher, out var s) && s.RestUntil <= 0 ? s.Line : -1;

        void Say(ActorId speaker, string line) => Said?.Invoke(speaker, line);

        float Scale => Math.Max(1f, _clock.TimeScale);

        /// <summary>
        /// Party handling before the routine: a member of an NPC-led party follows its leader (ZS_FollowNPC); a leader at its
        /// destination ends the party after 5 s (ZS_TravelToDestination). True when the agent was handled.
        /// </summary>
        bool StepParty(NpcAgent agent, double now)
        {
            if (Parties == null) return false;
            if (Parties.TryGetLeader(agent.Id, out var leaderId))
            {
                if (!_byId.TryGetValue(leaderId, out var leader) || !IsInWorld(leader))
                {
                    Parties.Release(agent.Id);
                    return false;
                }
                FollowNpc(agent, leader, now);
                return true;
            }
            if (Parties.Leads(agent.Id))
            {
                // At its destination: its station, or standing without one (no routine point: G3's goto to None ends at once).
                bool arrived = agent.Activity == NpcActivity.AtStation || (agent.Activity == NpcActivity.Idle && agent.Station.IsNone);
                if (!arrived) Parties.LeaderMoved(agent.Id);
                else if (Parties.LeaderWaited(agent.Id, now, _clock.TimeScale)) Parties.Disband(agent.Id);
            }
            return false;
        }

        void FollowNpc(NpcAgent agent, NpcAgent leader, double now)
        {
            if (agent.Activity != NpcActivity.Escorting)
            {
                agent.Pose = PoseOf(agent);
                _claims.Release(agent.Station, agent.Id);
                agent.Station = Station.None;
                agent.Task = WorkTask.None;
                agent.ListeningTo = default;
                agent.Activity = NpcActivity.Escorting;
                agent.EscortedAt = now;
            }
            float realSeconds = (float)((now - agent.EscortedAt) / Scale);
            agent.EscortedAt = now;
            if (agent.Detail == DetailLevel.Active) return; // the body walks after the leader (NpcView)
            agent.Pose = AbstractEscort.Follow(agent.Pose, PoseOf(leader), NpcParties.FollowDistance, agent.Detail, realSeconds);
        }

        /// <summary>The NPC leading this one (ZS_FollowNPC), if any; the view walks it after the leader.</summary>
        public bool TryGetNpcLeader(ActorId member, out NpcAgent leader)
        {
            leader = null;
            return Parties != null && Parties.TryGetLeader(member, out var id) && _byId.TryGetValue(id, out leader);
        }

        /// <summary>
        /// The tasks of an NPC at its station (the phase is unchanged): true when one of them decided what the NPC does
        /// now (the stay-time re-plan is skipped).
        /// </summary>
        bool StepAtStation(NpcAgent agent, double now)
        {
            if (agent.Activity != NpcActivity.AtStation || !Points.TryGetPoint(agent.Station.Point, out var point)) return false;
            if (!agent.ListeningTo.IsEmpty || point.Interaction == Preaching.ListenerInteraction) return StepListener(agent, point, now);
            if (Smalltalk.IsSmalltalk(point.Interaction)) return StepSmalltalk(agent, now);
            if (point.Interaction == Preaching.Interaction) return StepSermon(agent, point, now);
            return StepWork(agent, point, now);
        }

        // ---------------------------------------------------------------------------------------------- smalltalk

        /// <summary>The leader of a pair says the line of each new turn for the turn's speaker; after 120 s both go on.</summary>
        bool StepSmalltalk(NpcAgent agent, double now)
        {
            var partner = SmalltalkPartnerOf(agent);
            if (partner == null) return false;
            double since = Math.Max(agent.ArrivedAt, partner.ArrivedAt);
            double real = (now - since) / Scale;
            if (Smalltalk.Ended(real))
            {
                Replan(agent, agent.Phase, now, moveOn: true);
                if (partner.Activity == NpcActivity.AtStation) Replan(partner, partner.Phase, now, moveOn: true);
                return true;
            }
            if (!Smalltalk.IsLeader(agent.ArrivedAt, agent.Id.Name, partner.ArrivedAt, partner.Id.Name)) return true;
            long turn = Smalltalk.TurnOf(real);
            if (turn != agent.SmalltalkTurn)
            {
                agent.SmalltalkTurn = turn;
                Say(turn % 2 == 0 ? agent.Id : partner.Id, Smalltalk.LineOf(turn));
            }
            return true;
        }

        // ---------------------------------------------------------------------------------------------- sermons

        bool StepSermon(NpcAgent preacher, RoutinePointDef point, double now)
        {
            if (preacher.Detail == DetailLevel.Abstract) return false;
            if (HasLine != null && !HasLine(preacher.Id, Preaching.LineOf(0))) return false; // ZS_Preach_AutoPreach: gestures only
            if (!_sermons.TryGetValue(preacher.Id, out var sermon)) _sermons[preacher.Id] = sermon = new Sermon { NextAt = now };
            if (sermon.RestUntil > 0)
            {
                if (now < sermon.RestUntil) return true;
                sermon.RestUntil = 0;
                sermon.Line = 0;
                sermon.NextAt = now;
            }
            if (now < sermon.NextAt) return true;
            if (sermon.Line >= Preaching.LineCount)
            {
                EndSermon(preacher.Id);
                // HaveABreak: a point of an anchor → ContinueRoutine; else ZS_Break (here: a pause, then the next sermon).
                if (!point.Anchor.IsEmpty)
                {
                    _sermons.Remove(preacher.Id);
                    Replan(preacher, preacher.Phase, now, moveOn: true);
                }
                else sermon.RestUntil = now + Preaching.BreakSeconds * Scale;
                return true;
            }
            if (Preaching.GathersBefore(sermon.Line)) GatherListeners(preacher);
            Say(preacher.Id, Preaching.LineOf(sermon.Line));
            sermon.Line++;
            sermon.NextAt = now + Preaching.LineSeconds * Scale;
            return true;
        }

        /// <summary>SendPerceptionPreach(1): routine NPCs near the preacher go to the free Listener points, nearest first.</summary>
        void GatherListeners(NpcAgent preacher)
        {
            var at = PoseOf(preacher);
            _spots.Clear();
            foreach (var p in Points.PointsWith(Preaching.ListenerInteraction))
                if (p.Slots.Count > 0 && p.Slots[0].DistanceTo(at) <= Preaching.Range) _spots.Add(p);
            if (_spots.Count == 0) return;
            _spots.Sort((a, b) => a.Slots[0].DistanceTo(at).CompareTo(b.Slots[0].DistanceTo(at)));
            _listeners.Clear();
            foreach (var other in _engaged)
            {
                if (other == preacher || !other.ListeningTo.IsEmpty || !IsInWorld(other)) continue;
                if (other.Activity != NpcActivity.AtStation && other.Activity != NpcActivity.Idle && other.Activity != NpcActivity.Travelling) continue;
                if (Sleeps(other) || PoseOf(other).DistanceTo(at) > Preaching.Range) continue;
                if (Points.TryGetPoint(other.Station.Point, out var own) && (own.Interaction == Preaching.Interaction || own.Interaction == Preaching.ListenerInteraction)) continue;
                if (Parties != null && (Parties.TryGetLeader(other.Id, out _) || Parties.Leads(other.Id))) continue;
                if (MayListen != null && !MayListen(other.Id)) continue;
                _listeners.Add(other);
            }
            _listeners.Sort((a, b) => PoseOf(a).DistanceTo(at).CompareTo(PoseOf(b).DistanceTo(at)));
            int next = 0;
            foreach (var listener in _listeners)
            {
                Station free = Station.None;
                for (; next < _spots.Count && free.IsNone; next++)
                    for (int slot = 0; slot < _spots[next].Slots.Count; slot++)
                    {
                        var s = new Station(_spots[next].Id, slot);
                        if (_claims.IsFreeFor(s, listener.Id, listener.Station)) { free = s; break; }
                    }
                if (free.IsNone) break;
                SendTo(listener, free, PhaseOf(listener));
                listener.ListeningTo = preacher.Id;
            }
        }

        /// <summary>SendPerceptionPreach(0): the listeners go on with their routine.</summary>
        void EndSermon(ActorId preacher)
        {
            double now = _clock.GameSeconds;
            foreach (var a in _agents)
                if (a.ListeningTo == preacher)
                {
                    a.ListeningTo = default;
                    if (a.Activity == NpcActivity.AtStation || a.Activity == NpcActivity.Travelling) Replan(a, PhaseOf(a), now, moveOn: false);
                }
        }

        /// <summary>ZS_Listen: stay at the Listener point while the sermon goes on; a listener without one goes back.</summary>
        bool StepListener(NpcAgent agent, RoutinePointDef point, double now)
        {
            bool preaching = !agent.ListeningTo.IsEmpty && _byId.TryGetValue(agent.ListeningTo, out var preacher) &&
                             preacher.Activity == NpcActivity.AtStation && preacher.Detail != DetailLevel.Abstract &&
                             _sermons.ContainsKey(preacher.Id) && IsInWorld(preacher);
            if (preaching) return true;
            agent.ListeningTo = default;
            if (agent.Routine?.PointFor(agent.Phase) == point.Id) return false; // its own routine point (G3 ZS_HangAround there)
            Replan(agent, PhaseOf(agent), now, moveOn: false);
            return true;
        }

        // ---------------------------------------------------------------------------------------------- work cycles

        bool StepWork(NpcAgent agent, RoutinePointDef point, double now)
        {
            if (agent.Task == WorkTask.None)
            {
                var task = WorkCycles.TaskOf(point.Interaction);
                if (task == WorkTask.None) return false;
                agent.Task = task;
                agent.TaskStep = 0;
                agent.StepEndsAt = 0;
                AdvanceWork(agent, point, now);
                return true;
            }
            if (agent.StepEndsAt <= 0)
            {
                agent.StepEndsAt = now + WorkCycles.ActionSeconds(agent.Task, point.Interaction, _random) * Scale;
                return true;
            }
            if (now < agent.StepEndsAt) return true;
            AdvanceWork(agent, point, now);
            return true;
        }

        /// <summary>The next step of the agent's work task: act here, walk to the next spot, or end the task (ContinueRoutine).</summary>
        void AdvanceWork(NpcAgent agent, RoutinePointDef current, double now)
        {
            var task = agent.Task;
            while (true)
            {
                int position = agent.TaskStep + 1;
                string kind = WorkCycles.StepKind(task, position, current.Interaction);
                if (kind == null)
                {
                    EndWork(agent, now);
                    return;
                }
                agent.TaskStep = position;
                bool spots = task == WorkTask.SweepFloor || task == WorkTask.RakeField;
                var station = WorkStation(agent, current, kind, another: task == WorkTask.CarryStones, index: spots ? position - 1 : -1);
                if (station.IsNone)
                {
                    if (task == WorkTask.Smith && !WorkCycles.SmithStepRequired(position)) continue; // no forge / barrel: skipped
                    if (task == WorkTask.CarryStones)
                    {
                        agent.TaskStep = position - 1;
                        agent.StepEndsAt = now + WorkCycles.StoneSeconds * Scale; // nothing free: wait and try again
                        return;
                    }
                    EndWork(agent, now);
                    return;
                }
                agent.StepEndsAt = 0;
                if (station.Equals(agent.Station)) return; // acts here (the action time starts with the next step)
                SendTo(agent, station, agent.Phase);
                agent.Task = task;
                agent.TaskStep = position;
                return;
            }
        }

        void EndWork(NpcAgent agent, double now)
        {
            agent.Task = WorkTask.None;
            agent.TaskStep = 0;
            agent.StepEndsAt = 0;
            Replan(agent, agent.Phase, now, moveOn: true);
            // Nothing else free: the routine keeps the NPC here; it starts the cycle again after the stay.
        }

        /// <summary>
        /// The nearest free station of <paramref name="kind"/>: among the anchor's points, or within 10 m without an anchor
        /// (FUN_10008190 / FUN_10008230); <paramref name="another"/> leaves the current point out. A sweep or field cycle
        /// takes the spot at <paramref name="index"/> of the list sorted by distance (the current spot is the first): its
        /// TaskPosition [interp: FUN_10008490's index argument is not known].
        /// </summary>
        Station WorkStation(NpcAgent agent, RoutinePointDef current, string kind, bool another, int index = -1)
        {
            var at = PoseOf(agent);
            _spots.Clear();
            if (!current.Anchor.IsEmpty)
            {
                foreach (var p in Points.Members(current.Anchor))
                    if (p.Interaction == kind && p.Slots.Count > 0) _spots.Add(p);
            }
            else
            {
                foreach (var p in Points.PointsWith(kind))
                    if (p.Anchor.IsEmpty && p.Slots.Count > 0 && p.Slots[0].DistanceTo(at) <= WorkCycles.SameKindReach) _spots.Add(p);
            }
            if (another) _spots.Remove(current);
            _spots.Sort((a, b) => a.Slots[0].DistanceTo(at).CompareTo(b.Slots[0].DistanceTo(at)));
            if (index >= 0)
            {
                if (index >= _spots.Count) return Station.None;
                var pick = _spots[index];
                _spots.Clear();
                _spots.Add(pick);
            }
            foreach (var p in _spots)
                for (int slot = 0; slot < p.Slots.Count; slot++)
                {
                    var s = new Station(p.Id, slot);
                    if (s.Equals(agent.Station) || _claims.IsFreeFor(s, agent.Id, agent.Station)) return s;
                }
            return Station.None;
        }

        /// <summary>Leaves the current station for <paramref name="station"/> (claimed) and travels there.</summary>
        void SendTo(NpcAgent agent, Station station, DayPhase phase)
        {
            double now = _clock.GameSeconds;
            var here = PoseOf(agent);
            _claims.Release(agent.Station, agent.Id);
            _claims.Claim(station, agent.Id);
            agent.Pose = here;
            agent.Phase = phase;
            agent.Station = station;
            agent.WanderTarget = null;
            agent.AmbientAction = null;
            agent.RetryAt = 0;
            StartTravel(agent, now);
        }

        /// <summary>Task state that a new plan drops.</summary>
        void ClearTasks(NpcAgent agent)
        {
            if (_sermons.Remove(agent.Id)) EndSermon(agent.Id);
            agent.Task = WorkTask.None;
            agent.TaskStep = 0;
            agent.StepEndsAt = 0;
            agent.ListeningTo = default;
            agent.SmalltalkTurn = -1;
        }
    }
}
