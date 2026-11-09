using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Save section "npcs": the routine state of every NPC. Station claims and detail levels are derived and rebuilt on
    /// load (views are created again from the state, like after a streaming change). NPCs that no longer exist in the
    /// content are skipped.
    /// <para>
    /// Version 3 (fix-157, the 2-minute crash-recovery snapshot): an NPC still in its spawn state (idle at its spawn
    /// pose, nothing planned — most creatures of the world) is not written, and is put back into that state on load;
    /// every other NPC is written as a header word plus only the fields that differ from the derivable or default value
    /// (pose = spawn, station = the routine point of the phase, zero times, no station …), poses
    /// as floats (they are floats in <see cref="WorldPose"/>, so nothing is lost). The phase of an NPC in its spawn state
    /// is not kept: an idle NPC without a station replans on its next step whatever the phase
    /// (<see cref="NpcSimulation"/>.Process). Only the NPCs marked as changed since their spawn are looked at
    /// (<see cref="NpcSimulation.ChangedAgents"/>), so the cost follows what the game touched, not the size of the world.
    /// </para>
    /// </summary>
    public sealed class NpcSimulationSave : ISaveParticipant, ISaveCapture
    {
        // v3 header word: flags in bits 0-15, activity in 16-23, phase in 24-27, resume activity in 28-31
        // (a station's pose is written, not looked up: the routine point lookup cost more than the 16 bytes save)
        const int PoseAtSpawn = 1, HasStation = 2,
                  StationOnRoutinePoint = 4, // the station is on the NPC's routine point for its phase: only the slot is written
                  HasTravelFrom = 8, HasTravelTimes = 16, HasArrived = 32, HasRetry = 64, HasRoutine = 128;

        readonly NpcSimulation _simulation;
        readonly HashSet<NpcAgent> _loaded = new HashSet<NpcAgent>();

        public NpcSimulationSave(NpcSimulation simulation) => _simulation = simulation;

        public string Section => "npcs";
        /// <summary>2: the routine name set by the story (SetRoutine). 3: NPCs in their spawn state skipped, only changed fields.</summary>
        public int Version => 3;

        public void Write(ISaveWriter w) => WriteCaptured(CaptureInto(_forWrite), w);   // the same code as the snapshot

        /// <summary>fix-171: one written NPC as the section writes it (the copy the recovery snapshot encodes on a worker).</summary>
        struct Captured
        {
            public string Name, Routine, StationPoint;
            public int Header, Slot;
            public WorldPose Pose, From;
            public double Start, End, Arrived, Retry;
        }

        readonly List<Captured> _captured = new List<Captured>(), _forWrite = new List<Captured>();

        /// <summary>
        /// fix-171 (<see cref="ISaveCapture"/>): the changed NPCs that are not in their spawn state, copied with their header
        /// words (main thread: no encoding); <see cref="WriteCaptured"/> writes them. The list is reused by the next capture.
        /// </summary>
        public object Capture() => CaptureInto(_captured);   // the snapshot's list (a normal save meanwhile uses its own)

        List<Captured> CaptureInto(List<Captured> into)
        {
            var agents = _simulation.ChangedAgents; // the others are in their spawn state
            into.Clear();
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                int header = Header(a);
                if (header == PoseAtSpawn) continue;
                into.Add(new Captured
                {
                    Name = a.Id.Name, Header = header, Routine = a.RoutineName, Pose = a.Pose,
                    StationPoint = (header & HasStation) != 0 && (header & StationOnRoutinePoint) == 0 ? a.Station.Point.Name : null,
                    Slot = (header & HasStation) != 0 ? a.Station.Slot : 0,
                    From = a.TravelFrom, Start = a.TravelStart, End = a.TravelEnd, Arrived = a.ArrivedAt, Retry = a.RetryAt,
                });
            }
            return into;
        }

        public void WriteCaptured(object captured, ISaveWriter w)
        {
            var list = (List<Captured>)captured;
            w.WriteInt32(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                int header = c.Header;
                w.WriteString(c.Name);
                w.WriteInt32(header);
                if ((header & HasRoutine) != 0) w.WriteString(c.Routine);
                if ((header & PoseAtSpawn) == 0) WritePose(w, c.Pose);
                if ((header & HasStation) != 0)
                {
                    if ((header & StationOnRoutinePoint) == 0) w.WriteString(c.StationPoint);
                    w.WriteInt32(c.Slot);
                }
                if ((header & HasTravelFrom) != 0) WritePose(w, c.From);
                if ((header & HasTravelTimes) != 0)
                {
                    w.WriteDouble(c.Start);
                    w.WriteDouble(c.End);
                }
                if ((header & HasArrived) != 0) w.WriteDouble(c.Arrived);
                if ((header & HasRetry) != 0) w.WriteDouble(c.Retry);
            }
        }

        /// <summary>
        /// The header word of <paramref name="a"/>; exactly <see cref="PoseAtSpawn"/> means the spawn state (idle, Idle to
        /// resume, nothing else set) — not written. Only a content NPC has one (<see cref="NpcAgent.HasSpawnState"/>): a
        /// summon is always written with its pose.
        /// </summary>
        static int Header(NpcAgent a)
        {
            int flags = 0;
            var spawn = a.Definition.Spawn;
            bool hasStation = !a.Station.IsNone;
            if (a.HasSpawnState && spawn.HasValue && a.Pose.Equals(spawn.Value)) flags |= PoseAtSpawn;
            if (hasStation)
            {
                flags |= HasStation;
                var routine = a.Routine;
                if (routine != null && routine.PointFor(a.Phase) == a.Station.Point) flags |= StationOnRoutinePoint;
            }
            if (!a.TravelFrom.Equals(default(WorldPose))) flags |= HasTravelFrom;
            if (a.TravelStart != 0 || a.TravelEnd != 0) flags |= HasTravelTimes;
            if (a.ArrivedAt != 0) flags |= HasArrived;
            if (a.RetryAt != 0) flags |= HasRetry;
            if (a.RoutineName != null) flags |= HasRoutine;
            if (flags == PoseAtSpawn && a.Activity == NpcActivity.Idle && a.ResumeActivity == NpcActivity.Idle) return PoseAtSpawn;
            return flags | ((int)a.Activity & 0xFF) << 16 | ((int)a.Phase & 0xF) << 24 | ((int)a.ResumeActivity & 0xF) << 28;
        }

        public void Read(ISaveReader r, int version)
        {
            if (version >= 3) ReadCompact(r);
            else ReadFull(r, version);
            _simulation.RebuildChanged(a => Header(a) == PoseAtSpawn);
            _simulation.RebuildClaims();
        }

        void ReadCompact(ISaveReader r)
        {
            _loaded.Clear();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var id = new ActorId(r.ReadString());
                int header = r.ReadInt32();
                string routine = (header & HasRoutine) != 0 ? r.ReadString() : null;
                var pose = (header & PoseAtSpawn) == 0 ? ReadPose(r) : default;
                string point = null;
                int slot = 0;
                if ((header & HasStation) != 0)
                {
                    if ((header & StationOnRoutinePoint) == 0) point = r.ReadString();
                    slot = r.ReadInt32();
                }
                var from = (header & HasTravelFrom) != 0 ? ReadPose(r) : default;
                double start = 0, end = 0;
                if ((header & HasTravelTimes) != 0)
                {
                    start = r.ReadDouble();
                    end = r.ReadDouble();
                }
                double arrived = (header & HasArrived) != 0 ? r.ReadDouble() : 0;
                double retry = (header & HasRetry) != 0 ? r.ReadDouble() : 0;
                if (!_simulation.TryGet(id, out var a)) continue;
                _loaded.Add(a);
                a.RoutineName = string.IsNullOrEmpty(routine) ? null : routine;
                a.Phase = (DayPhase)((header >> 24) & 0xF);
                var station = Station.None;
                if ((header & HasStation) != 0)
                {
                    var pointId = (header & StationOnRoutinePoint) != 0 ? a.Routine?.PointFor(a.Phase) ?? default
                                  : string.IsNullOrEmpty(point) ? default : new Id<RoutinePointDef>(point);
                    if (!pointId.IsEmpty) station = new Station(pointId, slot);
                }
                if ((header & PoseAtSpawn) != 0) pose = a.Definition.Spawn ?? a.Pose;
                a.Pose = pose;
                a.Activity = (NpcActivity)((header >> 16) & 0xFF);
                a.Station = station;
                a.TravelFrom = from;
                a.TravelStart = start;
                a.TravelEnd = end;
                a.ArrivedAt = arrived;
                a.RetryAt = retry;
                a.ResumeActivity = (NpcActivity)((header >> 28) & 0xF);
            }
            // not in the save = in its spawn state (the fresh session may have put it at its routine start)
            foreach (var a in _simulation.Agents)
                if (!_loaded.Contains(a) && a.HasSpawnState && a.Definition.Spawn.HasValue) a.ResetToSpawn();
            _loaded.Clear();
        }

        /// <summary>Versions 1 and 2: every NPC with every field, poses as doubles.</summary>
        void ReadFull(ISaveReader r, int version)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var id = new ActorId(r.ReadString());
                var pose = ReadDoublePose(r);
                var activity = (NpcActivity)r.ReadInt32();
                var point = r.ReadString();
                int slot = r.ReadInt32();
                var phase = (DayPhase)r.ReadInt32();
                var from = ReadDoublePose(r);
                double start = r.ReadDouble(), end = r.ReadDouble(), arrived = r.ReadDouble(), retry = r.ReadDouble();
                var resume = (NpcActivity)r.ReadInt32();
                string routine = version >= 2 ? r.ReadString() : "";
                if (!_simulation.TryGet(id, out var a)) continue;
                a.Pose = pose;
                a.Activity = activity;
                a.Station = string.IsNullOrEmpty(point) ? Station.None : new Station(new Id<RoutinePointDef>(point), slot);
                a.Phase = phase;
                a.TravelFrom = from;
                a.TravelStart = start;
                a.TravelEnd = end;
                a.ArrivedAt = arrived;
                a.RetryAt = retry;
                a.ResumeActivity = resume;
                a.RoutineName = string.IsNullOrEmpty(routine) ? null : routine;
            }
        }

        static void WritePose(ISaveWriter w, WorldPose p)
        {
            w.WriteSingle(p.X);
            w.WriteSingle(p.Y);
            w.WriteSingle(p.Z);
            w.WriteSingle(p.Yaw);
        }

        static WorldPose ReadPose(ISaveReader r) => new WorldPose(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        static WorldPose ReadDoublePose(ISaveReader r) =>
            new WorldPose((float)r.ReadDouble(), (float)r.ReadDouble(), (float)r.ReadDouble(), (float)r.ReadDouble());
    }
}
