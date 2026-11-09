using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>
    /// The engine-free core of the NPCs' active perception (Docs/perception-crime.md §2.2–§2.4, §4.5), driven once per frame by
    /// the presentation's PerceptionDriver with the poses of the shown NPCs: every Active NPC scans at the G3 rate (1 s; gate
    /// guards 0.1 s) for the player and the NPCs around it. The scan's slots go through G3 AssessActivePerception's steps
    /// (<see cref="ActivePerception"/>): panic flight, food, prey, hostile NPCs or the player, threatening (creatures), and the
    /// player's conduct for humanoids (grudge, trespassing, a drawn weapon, sneaking: the crime system). It also runs the
    /// sleepers' wake checks (OnSleep / OnAnimalSleep), the eaters' drive-off scan and the woken sleepers' observing.
    /// <para>Performance (perf-9): the NPCs around an observer come from a grid of the shown NPCs built once per frame (cells of
    /// the widest slot, 20 m); only the slots the steps read are sense-checked (no raycasts for friends and neutrals); the
    /// observer's attitude profile is read once per scan (not once per candidate); at most <see cref="ScansPerFrame"/> scans
    /// run per frame, nearest first, the rest the next frame; a calm NPC the update LOD (-g3npclod) slows down scans as rarely
    /// as its view updates (×2–×4). Nothing allocates per frame.</para>
    /// </summary>
    public sealed class PerceptionScanner
    {
        /// <summary>Most NPC scans per frame (the rest wait a frame; nearest first) [perf].</summary>
        public const int ScansPerFrame = 8;
        /// <summary>The widest slot (hostile, prey, panic, player: 2000 cm) and the grid's cell size, m.</summary>
        public const float CandidateRange = 20f;
        const float CellSize = CandidateRange;
        /// <summary>AssessPlayerWeapon's perception slot 1: a fight on the player this close (m) replaces the weapon warning [4000].</summary>
        const float FightSlotRange = 40f;
        public const string SneakPerk2 = "Perk_Sneak_2";

        /// <summary>One shown NPC this frame: its body's feet pose and its view's update LOD.</summary>
        public readonly struct ViewInput
        {
            public ViewInput(ActorId id, WorldPose pose, int updateInterval, bool bodyTaken, float height = 0)
            {
                Id = id;
                Pose = pose;
                UpdateInterval = updateInterval;
                BodyTaken = bodyTaken;
                Height = height;
            }

            public readonly ActorId Id;
            public readonly WorldPose Pose;
            /// <summary>The view updates every n-th frame (perf-8 update LOD; 1 = every frame).</summary>
            public readonly int UpdateInterval;
            /// <summary>Combat or a reaction task has the body (the LOD never slows its scans).</summary>
            public readonly bool BodyTaken;
            /// <summary>Top of the body above the feet, m (0 = unknown: a human's); sizes the sight rays (fix-204).</summary>
            public readonly float Height;
        }

        /// <summary>The player as the scans see it this frame.</summary>
        public struct PlayerInput
        {
            public SenseTarget Target;
            /// <summary>Alive and no ghost: only then is the player a candidate.</summary>
            public bool Perceivable;
            /// <summary>A ranged weapon is drawn (OnAnimalSleep, Perk_Sneak_2).</summary>
            public bool RangedState;
            public PlayerConduct Conduct;
        }

        /// <summary>Counters for benchmarks.</summary>
        public struct Counters
        {
            public long Frames, Scans, Candidates;
        }

        struct GridEntry
        {
            public ActorId Id;
            public WorldPose Pose;
            public float Height;
        }

        struct Due
        {
            public int View;
            public float SqrDistance;
        }

        readonly GameSession _session;
        readonly Func<double> _random;
        readonly Dictionary<ActorId, double> _nextScan = new Dictionary<ActorId, double>();
        readonly List<PerceptionCandidate> _candidates = new List<PerceptionCandidate>();
        readonly PerceptionSnapshot _snapshot = new PerceptionSnapshot(); // reused by every scan
        readonly List<WorldPose> _fightsOnPlayer = new List<WorldPose>();
        readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        readonly List<GridEntry> _entries = new List<GridEntry>();
        readonly Stack<List<int>> _spareLists = new Stack<List<int>>();
        readonly Dictionary<ActorId, int> _entryOf = new Dictionary<ActorId, int>();
        readonly List<Due> _due = new List<Due>();
        readonly Comparison<Due> _nearestFirst;
        IReadOnlyList<ViewInput> _views;
        Counters _stats;
        // the EatBody drive-off distance, without a closure per call
        readonly Func<ActorId, float> _distanceToBody;
        WorldPose _body, _playerPose;

        /// <param name="random">0..1 (the first scan of an NPC that comes close is spread over one interval).</param>
        public PerceptionScanner(GameSession session, Func<double> random)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _nearestFirst = NearestFirst;
            _distanceToBody = DistanceToBody;
        }

        /// <summary>The presentation's raycasts (every line is free until set).</summary>
        public ILineOfSight LineOfSight { get; set; } = OpenLineOfSight.Instance;

        /// <summary>A woken sleeper renews its observing of the player: its view faces him (G3 ZS_ObserveSuspect).</summary>
        public Action<ActorId> WatchPlayer { get; set; }

        public Counters Stats => _stats;

        /// <summary>A new session (or the same reloaded): every NPC's next scan is spread anew.</summary>
        public void Reset() => _nextScan.Clear();

        /// <summary>
        /// One frame: the NPCs among <paramref name="views"/> whose scan is due scan, nearest to the player first (then by id): when
        /// several owners notice an intruder at once, the nearest one claims the zone warning and the others observe
        /// (CrimeSystem.ZoneWarner).
        /// </summary>
        public void Frame(in PlayerInput player, IReadOnlyList<ViewInput> views, double now)
        {
            _stats.Frames++;
            _views = views;
            _due.Clear();
            var p = player.Target.Pose;
            for (int i = 0; i < views.Count; i++)
            {
                if (!IsDue(views[i], now)) continue;
                var v = views[i].Pose;
                float dx = v.X - p.X, dy = v.Y - p.Y, dz = v.Z - p.Z;
                _due.Add(new Due { View = i, SqrDistance = dx * dx + dy * dy + dz * dz });
            }
            if (_due.Count == 0) return;
            _due.Sort(_nearestFirst);
            BuildGrid(views);
            CollectFightsOnPlayer();
            for (int i = 0; i < _due.Count; i++)
            {
                var view = views[_due[i].View];
                if (i >= ScansPerFrame)
                {
                    _nextScan[view.Id] = now; // over the frame's budget: next frame
                    continue;
                }
                _stats.Scans++;
                Scan(view.Id, view.Pose, EyeHeightOf(view.Height), player, now);
            }
            _views = null;
        }

        int NearestFirst(Due a, Due b)
        {
            int c = a.SqrDistance.CompareTo(b.SqrDistance);
            return c != 0 ? c : string.CompareOrdinal(_views[a.View].Id.Name, _views[b.View].Id.Name);
        }

        /// <summary>Whether the NPC's scan is due now (and schedules its next one).</summary>
        bool IsDue(in ViewInput view, double now)
        {
            var crime = _session.Crime;
            var npc = view.Id;
            if (!_session.Npcs.TryGet(npc, out var agent)) return false;
            bool gateGuard = crime.Crime.GateGuards != null && crime.Crime.GateGuards.IsGateGuard(npc);
            if (!(crime.Perception.ScanIntervalOf(agent.Detail, gateGuard) is double interval))
            {
                _nextScan.Remove(npc);
                return false;
            }
            if (!_nextScan.TryGetValue(npc, out var due))
            {
                // The first scan of an NPC that comes close is spread over one interval, as G3's OnTick phases are.
                _nextScan[npc] = now + _random() * interval;
                return false;
            }
            if (now < due) return false;
            // Update LOD (perf-8, -g3npclod): a far, calm view that updates every n-th frame also looks around n times less often.
            if (!gateGuard && !view.BodyTaken) interval *= Math.Max(1, view.UpdateInterval);
            _nextScan[npc] = now + interval;
            return true;
        }

        // ------------------------------------------------------------------------------------------- grid

        static long Cell(int x, int z) => ((long)x << 32) ^ (uint)z;

        static int CellIndex(float v) => (int)Math.Floor(v / CellSize);

        /// <summary>The shown NPCs by grid cell.</summary>
        void BuildGrid(IReadOnlyList<ViewInput> views)
        {
            foreach (var list in _cells.Values)
            {
                list.Clear();
                _spareLists.Push(list);
            }
            _cells.Clear();
            _entries.Clear();
            _entryOf.Clear();
            for (int i = 0; i < views.Count; i++)
            {
                var p = views[i].Pose;
                long key = Cell(CellIndex(p.X), CellIndex(p.Z));
                if (!_cells.TryGetValue(key, out var cell))
                {
                    cell = _spareLists.Count > 0 ? _spareLists.Pop() : new List<int>();
                    _cells[key] = cell;
                }
                cell.Add(_entries.Count);
                _entryOf[views[i].Id] = _entries.Count;
                _entries.Add(new GridEntry { Id = views[i].Id, Pose = p, Height = views[i].Height });
            }
        }

        bool TryGetPose(ActorId id, out WorldPose pose)
        {
            if (_entryOf.TryGetValue(id, out int index))
            {
                pose = _entries[index].Pose;
                return true;
            }
            pose = default;
            return false;
        }

        /// <summary>
        /// The candidates of one observer: the player (when perceivable) and every shown NPC within 20 m, with the
        /// observer's attitude toward it; an NPC whose slot the observer does not read is skipped before anything else.
        /// </summary>
        void FillCandidates(in SenseObserver observer, in PlayerInput player, Func<PerceptionSlot, bool> wanted)
        {
            _candidates.Clear();
            var crime = _session.Crime;
            var attitudes = crime.Attitudes;
            if (player.Perceivable)
                _candidates.Add(new PerceptionCandidate { Target = player.Target, IsPlayer = true, Attitude = attitudes.Of(observer.Id, ActorId.Player) });
            var vitals = _session.Combat.Vitals;
            // AttitudeService.Of(observer, other) reads both profiles; the observer's is the same for every candidate.
            var self = default(AttitudeProfile);
            bool observerKnown = !observer.Id.IsEmpty && !observer.Id.IsPlayer && attitudes.Facts.TryGetProfile(observer.Id, out self);
            float x = observer.Pose.X, z = observer.Pose.Z;
            int cx = CellIndex(x), cz = CellIndex(z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!_cells.TryGetValue(Cell(cx + dx, cz + dz), out var cell)) continue;
                for (int c = 0; c < cell.Count; c++)
                {
                    var e = _entries[cell[c]];
                    if (e.Id == observer.Id) continue;
                    float ex = e.Pose.X - x, ez = e.Pose.Z - z;
                    if (ex * ex + ez * ez > CandidateRange * CandidateRange) continue;
                    bool body = vitals.StateOf(e.Id) != VitalState.Alive;
                    var attitude = observerKnown ? AttitudeToward(attitudes, self, e.Id) : attitudes.Of(observer.Id, e.Id);
                    if (!Wanted(wanted, attitude, body)) continue;
                    _candidates.Add(new PerceptionCandidate
                    {
                        Target = new SenseTarget
                        {
                            Id = e.Id,
                            Pose = e.Pose,
                            Moving = !body && Moves(e.Id),
                            Height = body ? BodyLyingHeight : e.Height,
                            IndoorZone = observer.IndoorZone != null ? crime.Zones.IndoorZoneAt(e.Pose) : null,
                        },
                        Attitude = attitude,
                        Body = body,
                    });
                }
            }
            _stats.Candidates += _candidates.Count;
        }

        /// <summary><see cref="AttitudeService.Of(ActorId, ActorId)"/> of a known NPC observer (not the player) toward another actor.</summary>
        static Attitude AttitudeToward(AttitudeService attitudes, in AttitudeProfile self, ActorId other)
        {
            if (other.IsEmpty) return Attitude.Neutral;
            if (self.Id == other) return Attitude.Friendly;
            return attitudes.Facts.TryGetProfile(other, out var o) ? attitudes.Of(self, o) : Attitude.Neutral;
        }

        static bool Wanted(Func<PerceptionSlot, bool> wanted, Attitude attitude, bool body)
        {
            if (wanted == null) return true;
            if (body) return wanted(PerceptionSlot.Body) || (attitude == Attitude.Predator && wanted(PerceptionSlot.Food));
            return wanted(PerceptionScan.SlotOf(attitude));
        }

        /// <summary>A walking or fighting NPC makes noise (humanoids hear it within 1000) [interp: from its activity].</summary>
        bool Moves(ActorId npc)
        {
            if (_session.NpcCombat.Get(npc) is NpcCombatBrain brain && brain.IsFighting) return true;
            return _session.Npcs.TryGet(npc, out var agent) &&
                   (agent.Activity == NpcActivity.Travelling || agent.Activity == NpcActivity.Escorting);
        }

        // ------------------------------------------------------------------------------------------- one scan

        /// <summary>A body on the ground is looked at this high, m (fix-204).</summary>
        const float BodyLyingHeight = 0.5f;

        /// <summary>The eyes of a body whose top is <paramref name="top"/> m above its feet (0 = a human's), m (fix-204).</summary>
        public static float EyeHeightOf(float top) => top > 0 ? top * 0.92f : SightLines.HumanEyeHeight;

        void Scan(ActorId npc, WorldPose pose, float eyeHeight, in PlayerInput player, double now)
        {
            var crime = _session.Crime;
            if (!_session.Actors.TryGet(npc, out var state) || !_session.Npcs.TryGet(npc, out var agent)) return;
            if (_session.Combat.Vitals.StateOf(npc) != VitalState.Alive) return;
            if (_session.NpcCombat.Paralysed(npc)) return; // held by a spell (magic sleep, mist; G3 AIMode 7): perceives nothing
            bool humanoid = CombatWiring.CombatDefOf(state.Definition).Humanoid;
            var tasks = _session.Life.Tasks;
            if (tasks.TryGet(npc, out var task))
            {
                TaskPerception(npc, agent, task, pose, eyeHeight, humanoid, player, now);
                return;
            }
            var mode = crime.World.ModeOf(npc);
            if (mode == ReceiverMode.Asleep)
            {
                WakeCheck(npc, agent, pose, eyeHeight, humanoid, player, now);
                return;
            }
            if (mode != ReceiverMode.Routine) return;
            ActiveScan(npc, pose, eyeHeight, humanoid, player, now, out _);
        }

        /// <summary>
        /// G3 AssessActivePerception (the routine callbacks OnWork / OnGoto, which are one function): the scan's slots
        /// through the perception steps, then the step this NPC takes. Returns false when it did nothing (or only went on
        /// watching: the crime system's Observe). <paramref name="assessed"/>: the crime reaction of the player step
        /// (<see cref="ReactionKind.None"/> when the scan took another step).
        /// </summary>
        bool ActiveScan(ActorId npc, WorldPose pose, float eyeHeight, bool humanoid, in PlayerInput player, double now, out ReactionKind assessed)
        {
            assessed = ReactionKind.None;
            var crime = _session.Crime;
            var tasks = _session.Life.Tasks;
            var observer = new SenseObserver { Id = npc, Pose = pose, Humanoid = humanoid, IndoorZone = humanoid ? crime.Zones.IndoorZoneAt(pose) : null, EyeHeight = eyeHeight };
            var wanted = humanoid ? ActivePerception.HumanoidSlots : ActivePerception.CreatureSlots;
            FillCandidates(observer, player, wanted);
            var snapshot = PerceptionScan.Scan(_snapshot, observer, _candidates, crime.Perception, LineOfSight, wanted: wanted);
            var facts = new ActiveObserver
            {
                Humanoid = humanoid,
                // CP AssessActivePerception: party leader == player skips the hostile step (only summons act) - the
                // members AND the guide (StartGuidePlayer makes the player its leader). fix-41: the guide's own scan
                // re-engaged the enemy right after every leash break-off (r28: Cole 63x, Karlen 51x, Xardas vs IceWolf).
                InPlayerParty = _session.Party != null && _session.Party.Members.IsOnHeroSide(npc),
            };
            if (snapshot.Has(PerceptionSlot.Player)) facts.UpsetWithPlayer = crime.Attitudes.IsUpset(npc, out facts.UpsetReason);
            var decision = ActivePerception.Decide(snapshot, facts);
            // fix-142: a target this NPC just gave up as unreachable (the hero on a rock) is neither attacked nor threatened
            if ((decision.Step == PerceptionStep.Attack || decision.Step == PerceptionStep.Threaten) &&
                _session.NpcCombat.Get(npc)?.IgnoresUnreachable(decision.Target, _session.PlaySeconds) == true) return false;
            switch (decision.Step)
            {
                case PerceptionStep.AssessPlayer:
                    var reaction = AssessPlayer(npc, pose, snapshot, player, now);
                    assessed = reaction.Kind;
                    return reaction.Kind != ReactionKind.None && reaction.Kind != ReactionKind.Observe;
                case PerceptionStep.Flee:
                    if (humanoid) crime.Crime.StopWarning(npc);
                    tasks.End(npc);
                    _session.NpcCombat.Flee(npc, decision.Target);
                    return true;
                case PerceptionStep.Attack:
                    if (humanoid) crime.Crime.StopWarning(npc);
                    tasks.End(npc);
                    _session.NpcCombat.Engage(npc, decision.Target, decision.Reason);
                    return true;
                case PerceptionStep.EatBody:
                    tasks.StartEatBody(npc, decision.Target, now);
                    return true;
                case PerceptionStep.Threaten:
                    tasks.StartThreaten(npc, decision.Target, now);
                    return true;
            }
            return false;
        }

        /// <summary>The crime steps for the player (humanoids): grudge, hostility, trespassing, a drawn weapon, sneaking.</summary>
        Reaction AssessPlayer(ActorId npc, WorldPose pose, PerceptionSnapshot snapshot, in PlayerInput player, double now)
        {
            var crime = _session.Crime;
            var own = player.Conduct;
            if (own.WeaponDrawn && FightNear(pose)) own.WeaponDrawn = own.SpellDrawn = false;
            if (snapshot.TryGet(PerceptionSlot.Player, out var percept)) return crime.Crime.AssessPlayer(npc, percept, own, now);
            if (player.Perceivable && KeepsWarning(npc, pose, player.Target.Pose, out var distance))
                return crime.Crime.AssessPlayer(npc, new Percept(ActorId.Player, distance, Sense.Sensed), own, now);
            crime.Crime.StopWarning(npc);
            return Reaction.None;
        }

        /// <summary>
        /// A sleeper's wake check (G3 OnSleep / OnAnimalSleep, player only): a human wakes (ZS_ReactToFootsteps:
        /// "YouDisturbedMySlumber", stands up, observes); an animal on its sleeping place wakes angry and attacks.
        /// </summary>
        void WakeCheck(ActorId npc, NpcAgent agent, WorldPose pose, float eyeHeight, bool humanoid, in PlayerInput player, double now)
        {
            if (!player.Perceivable) return;
            var crime = _session.Crime;
            var attitude = crime.Attitudes.Of(npc, ActorId.Player);
            if (humanoid)
            {
                if (attitude == Attitude.Friendly) return;
                var sleeper = new SenseObserver { Id = npc, Pose = pose, Humanoid = true, IndoorZone = crime.Zones.IndoorZoneAt(pose), EyeHeight = eyeHeight };
                if (!Senses.WakesSleeper(sleeper, player.Target, crime.Perception, LineOfSight)) return;
                _session.Life.Tasks.StartObserveWaker(npc, ActorId.Player, now);
                _session.Events.Publish(new NpcReacted(npc, Reaction.Of(ReactionKind.Observe, ActorId.Player, AttackReason.None, SleeperRules.DisturbedLine), null));
                return;
            }
            // CP: only ZS_AnimalSleep (an animal at its SleepGround point) installs OnAnimalSleep; a wanderer's ZS_Ambient_Sleep has none.
            if (agent.Activity != NpcActivity.AtStation) return;
            if (attitude == Attitude.Friendly || attitude == Attitude.Neutral) return;
            if (!Senses.WakesAnimal(pose, player.Target, _session.Hero.Knows(SneakPerk2), player.RangedState, crime.Perception, LineOfSight, eyeHeight)) return;
            _session.NpcCombat.Engage(npc, ActorId.Player, AttackReason.Angry);
        }

        /// <summary>
        /// Perception inside a reaction task: the eater's drive-off scan (OnEatBody_Perception) and the woken sleeper's
        /// observe loop; a threatening creature looks at nothing else.
        /// </summary>
        void TaskPerception(ActorId npc, NpcAgent agent, ReactionTask task, WorldPose pose, float eyeHeight, bool humanoid, in PlayerInput player, double now)
        {
            var crime = _session.Crime;
            var tasks = _session.Life.Tasks;
            switch (task.Kind)
            {
                case ReactionTaskKind.EatBody:
                {
                    if (!TryGetPose(task.Target, out var body)) return;
                    var observer = new SenseObserver { Id = npc, Pose = pose, Humanoid = false, EyeHeight = eyeHeight };
                    FillCandidates(observer, player, ActivePerception.EaterSlots);
                    var snapshot = PerceptionScan.Scan(_snapshot, observer, _candidates, crime.Perception, LineOfSight, wanted: ActivePerception.EaterSlots);
                    _body = body;
                    _playerPose = player.Target.Pose;
                    var d = ActivePerception.DriveOff(snapshot, task.Target, _distanceToBody);
                    if (d.Step == PerceptionStep.None) return;
                    tasks.End(npc);
                    if (d.Step == PerceptionStep.Flee) _session.NpcCombat.Flee(npc, d.Target);
                    else _session.NpcCombat.Engage(npc, d.Target, d.Reason);
                    return;
                }
                case ReactionTaskKind.ObserveWaker:
                {
                    if (!player.Perceivable)
                    {
                        tasks.End(npc);
                        return;
                    }
                    float distance = Senses.Distance(pose, player.Target.Pose);
                    bool checkDue = ReactionTasks.ObserveCheckDue(task, now);
                    bool sameZone = Senses.SameIndoorZone(crime.Zones.IndoorZoneAt(pose), player.Target.IndoorZone);
                    bool friend = crime.Attitudes.Of(npc, ActorId.Player) == Attitude.Friendly;
                    if (!SleeperRules.KeepsObserving(now - task.Started, checkDue, distance, sameZone, friend, _session.Npcs.Sleeps(agent),
                                                     player.Target.Parade))
                    {
                        tasks.End(npc);
                        return;
                    }
                    // fix-217: ZS_ObserveSuspect's callback is OnGoto, the routine's AssessActivePerception: the woken
                    // sleeper that sees an enemy (a raider orc woken by the hero) attacks, a warning or a talk replaces
                    // the watching; only nothing to do (or more watching) keeps it
                    // fix-231: a warning keeps it up too. G3's warn (ZS_ReactToWeapon / Zone) is a task of its own that holds
                    // the NPC on its feet until it attacks or the player leaves; ending the watching sent the woken sleeper
                    // back to its bed or SleepGround point, where the next footsteps woke it again: it lay down and stood up
                    // over and over in front of the player (owner, build 25b). The watching ends by its own rules (distance,
                    // time) or when the warnings turn into a fight.
                    if (ActiveScan(npc, pose, eyeHeight, humanoid, player, now, out var assessed))
                    {
                        if (assessed == ReactionKind.Warn && tasks.KindOf(npc) == ReactionTaskKind.ObserveWaker)
                        {
                            WatchPlayer?.Invoke(npc);
                            return;
                        }
                        if (tasks.KindOf(npc) == ReactionTaskKind.ObserveWaker) tasks.End(npc);
                        return;
                    }
                    WatchPlayer?.Invoke(npc);
                    return;
                }
            }
        }

        float DistanceToBody(ActorId id) =>
            Senses.Distance(_body, id.IsPlayer ? _playerPose : TryGetPose(id, out var p) ? p : new WorldPose(float.MaxValue, 0, float.MaxValue, 0));

        /// <summary>
        /// G3 AssessPlayerWeapon: when perception slot 1 holds an NPC within 40 m that fights the player in the arena or
        /// holds the attack token against him, the observer assesses that fight (AssessFightSound: watch or join) instead of
        /// warning about the weapon. So arena spectators (Muff, Curtis) never warn the hero whose weapon is out for the bout
        /// (combat-6). The fight reaction itself is the perception broadcasts' (Docs/perception-crime.md).
        /// </summary>
        void CollectFightsOnPlayer()
        {
            _fightsOnPlayer.Clear();
            foreach (var brain in _session.NpcCombat.Brains)
            {
                if (!brain.IsAttacking || !brain.State.Target.IsPlayer) continue;
                if (brain.State.Reason != AttackReason.Arena && !_session.NpcCombat.Coordinator.Holds(brain.Self)) continue;
                if (TryGetPose(brain.Self, out var p)) _fightsOnPlayer.Add(p);
            }
        }

        bool FightNear(WorldPose at)
        {
            foreach (var p in _fightsOnPlayer)
            {
                float dx = p.X - at.X, dy = p.Y - at.Y, dz = p.Z - at.Z;
                if (dx * dx + dy * dy + dz * dz <= FightSlotRange * FightSlotRange) return true;
            }
            return false;
        }

        /// <summary>
        /// An NPC that has started warning keeps at it while the player stays within the player slot's range, even out of
        /// sight: G3's warner follows the player (ZS_ReactToZone / ZS_ReactToWeapon) <b>[interp: our NPC stays put]</b>.
        /// </summary>
        bool KeepsWarning(ActorId npc, WorldPose self, WorldPose player, out float distance)
        {
            distance = Senses.Distance(self, player);
            var rules = _session.Crime.Perception;
            // fix-204: not across the senses' height limit (the hero climbed a cliff: the warner below loses him)
            return _session.Crime.Crime.WarningOf(npc) != WarningStep.None && distance <= rules.RangeOf(PerceptionSlot.Player) &&
                   Senses.HeightDifference(self, player) <= rules.MaxHeightDifference;
        }
    }
}
