using System;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>How closely an NPC is simulated (gameplay-architecture.md §6.2). Decided by the presentation layer.</summary>
    public enum DetailLevel
    {
        /// <summary>No view: schedule only, travel is a timed straight line.</summary>
        Abstract,
        /// <summary>A view is shown but not driven: it follows the abstract travel.</summary>
        Near,
        /// <summary>A view walks and animates; its body reports position and arrival back.</summary>
        Active,
    }

    /// <summary>What an NPC is doing in its routine.</summary>
    public enum NpcActivity
    {
        /// <summary>No station (no routine, or nothing free): stands where it is.</summary>
        Idle,
        /// <summary>On the way to <see cref="NpcAgent.Station"/>.</summary>
        Travelling,
        /// <summary>At its station, doing the station's interaction.</summary>
        AtStation,
        /// <summary>In a conversation: stands still until it ends.</summary>
        Talking,
        /// <summary>Escorts the hero (party member or guide, <see cref="IEscorts"/>): no routine station.</summary>
        Escorting,
    }

    /// <summary>
    /// Routine state of one NPC: plain data, saved as is (<see cref="NpcSimulationSave"/>). Changed only by
    /// <see cref="NpcSimulation"/>; views read it.
    /// </summary>
    public sealed class NpcAgent
    {
        public NpcAgent(NpcDef definition, WorldPose pose)
        {
            Definition = definition;
            _pose = pose;
        }

        public NpcDef Definition { get; }
        public ActorId Id => Definition.Id;

        /// <summary>Last settled pose: the spawn, the station after arrival, or what the active body reported.</summary>
        public WorldPose Pose { get => _pose; internal set { _pose = value; Touch(); } }
        public NpcActivity Activity { get => _activity; internal set { _activity = value; Touch(); } }
        /// <summary>The claimed station (None while idle).</summary>
        public Station Station { get => _station; internal set { _station = value; Touch(); } }
        /// <summary>
        /// The day phase the current station was chosen for. Setting it alone does not mark the NPC as changed: an idle NPC
        /// without a station (the spawn state) replans whatever its phase.
        /// </summary>
        public DayPhase Phase { get => _phase; internal set => _phase = value; }
        public DetailLevel Detail { get; internal set; }

        /// <summary>Abstract travel: start pose and game-time window (the position is interpolated on demand).</summary>
        public WorldPose TravelFrom { get => _travelFrom; internal set { _travelFrom = value; Touch(); } }
        public double TravelStart { get => _travelStart; internal set { _travelStart = value; Touch(); } }
        public double TravelEnd { get => _travelEnd; internal set { _travelEnd = value; Touch(); } }
        /// <summary>Game time of arrival at the station.</summary>
        public double ArrivedAt { get => _arrivedAt; internal set { _arrivedAt = value; Touch(); } }
        /// <summary>No new plan before this game time (after nothing was free).</summary>
        public double RetryAt { get => _retryAt; internal set { _retryAt = value; Touch(); } }
        /// <summary>Activity to return to when a conversation ends.</summary>
        public NpcActivity ResumeActivity { get => _resumeActivity; internal set { _resumeActivity = value; Touch(); } }
        /// <summary>Game time of the last off-screen escort step.</summary>
        internal double EscortedAt { get; set; }
        /// <summary>The routine the story switched to (G3 SetRoutine); null = the NPC's own routine.</summary>
        public string RoutineName { get => _routineName; internal set { _routineName = value; Touch(); } }

        // ------------------------------------------------------------------ saved state and its change mark (fix-157)

        WorldPose _pose, _travelFrom;
        NpcActivity _activity, _resumeActivity;
        Station _station;
        DayPhase _phase;
        double _travelStart, _travelEnd, _arrivedAt, _retryAt;
        string _routineName;

        /// <summary>
        /// A saved field was set since the NPC was created at its spawn (or put back there, <see cref="ResetToSpawn"/>).
        /// Sticky: an NPC that comes back to its spawn state stays marked. The save writes only marked NPCs
        /// (<see cref="NpcSimulationSave"/>); an unmarked one is in its spawn state.
        /// </summary>
        internal bool ChangedSinceSpawn { get; private set; }
        /// <summary>Told once, when <see cref="ChangedSinceSpawn"/> becomes true (the simulation's list of changed NPCs).</summary>
        internal Action<NpcAgent> FirstChange { get; set; }
        /// <summary>The order the simulation created its agents in (the save writes the changed ones in this order).</summary>
        internal int Order { get; set; }
        /// <summary>
        /// A content NPC placed at its <see cref="NpcDef.Spawn"/> when the game was created, so a fresh game has the same spawn
        /// state to come back to. Not one that appeared during play (a summon: its definition and pose are made when it
        /// appears, and differ when it is re-created on load).
        /// </summary>
        internal bool HasSpawnState { get; set; }

        void Touch()
        {
            if (ChangedSinceSpawn) return;
            ChangedSinceSpawn = true;
            FirstChange?.Invoke(this);
        }

        /// <summary>
        /// The spawn state: idle at the spawn pose, nothing planned, the NPC's own routine (the phase is kept: it is not
        /// used while idle without a station). Clears the change mark; the caller rebuilds its list. Needs a spawn.
        /// </summary>
        internal void ResetToSpawn()
        {
            if (!HasSpawnState) throw new InvalidOperationException($"{Id} has no spawn state.");
            _pose = Definition.Spawn ?? _pose;
            _activity = NpcActivity.Idle;
            _station = Station.None;
            _travelFrom = default;
            _travelStart = _travelEnd = _arrivedAt = _retryAt = 0;
            _resumeActivity = NpcActivity.Idle;
            _routineName = null;
            ChangedSinceSpawn = false;
        }

        /// <summary>Clears the change mark of an NPC found in its spawn state (after loading); the caller rebuilds its list.</summary>
        internal void MarkUnchanged() => ChangedSinceSpawn = false;

        /// <summary>
        /// ZS_Ambient (creatures without routine points): the point it walks to now (travelling without a station); null
        /// otherwise. Not saved: a wanderer is loaded standing and picks a new point.
        /// </summary>
        public WorldPose? WanderTarget { get; internal set; }
        /// <summary>The ambient action a wanderer plays while idle ("EatGround", "Ambient", "SleepGround"); null = none.</summary>
        public string AmbientAction { get; internal set; }

        /// <summary>
        /// The multi-spot work task in progress (<see cref="WorkCycles"/>), its 1-based step (TaskPosition) and the game time the
        /// action at the current spot ends (0 = not started). Not saved: a loaded NPC starts the cycle afresh.
        /// </summary>
        public WorkTask Task { get; internal set; }
        public int TaskStep { get; internal set; }
        internal double StepEndsAt { get; set; }

        /// <summary>The preacher this NPC listens to at a Listener point (ZS_Listen); empty otherwise. Not saved.</summary>
        public ActorId ListeningTo { get; internal set; }

        /// <summary>The last smalltalk turn whose line was said (the leader keeps it); −1 = none yet.</summary>
        internal long SmalltalkTurn { get; set; } = -1;

        /// <summary>
        /// The points the NPC follows now: its own routine, or the one the story named. A named routine the NPC has no
        /// points for leaves it without points (G3 clears Working/Relaxing/SleepingPoints): it stays and idles where it is.
        /// </summary>
        public NpcRoutineDef Routine => RoutineName == null ? Definition.Routine : Definition.RoutineNamed(RoutineName) ?? NoPoints;

        /// <summary>A routine without any point (see <see cref="Routine"/>).</summary>
        public static readonly NpcRoutineDef NoPoints = new NpcRoutineDef();

        public override string ToString() => $"{Id} {Activity} {Station} ({Phase}, {Detail})";
    }
}
