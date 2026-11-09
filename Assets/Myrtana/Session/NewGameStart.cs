using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// The G3 new-game script (OnGameStartUp, Docs/game-start.md) applied to a fresh session from the content's
    /// <see cref="GameStartDef"/>: the clock, the chapter, the running quests, the routines and the stirred-up enclaves.
    /// The enclave alarm (Ardea's orcs attack the player) needs the defenders around the player, so it stays pending
    /// until the session runs and NPCs are present (<see cref="Tick"/>); a developer start elsewhere drops it
    /// (<see cref="DropAlarms"/>). A loaded game has one only while its save still had an alarm pending.
    /// </summary>
    public sealed class NewGameStart
    {
        /// <summary>Real seconds between alarm attempts.</summary>
        public const float AlarmRetrySeconds = 0.5f;
        /// <summary>Real seconds of play after which a pending alarm is given up (nobody to send).</summary>
        public const float AlarmGiveUpSeconds = 120f;

        readonly List<EnclaveStart> _pendingAlarms;
        readonly IGameLog _log;
        float _played, _nextTry;

        NewGameStart(GameStartDef definition, IGameLog log)
        {
            Definition = definition;
            _log = log ?? NullGameLog.Instance;
            _pendingAlarms = definition.Enclaves.Where(e => !string.IsNullOrEmpty(e.AlarmBy)).ToList();
        }

        public GameStartDef Definition { get; }
        /// <summary>Real seconds the pending alarms have waited (saved with them).</summary>
        public float PlayedSeconds => _played;

        /// <summary>
        /// The pending part of a saved new-game script: a game saved before Ardea's alarm went off still gets it after
        /// loading. Null when nothing is pending.
        /// </summary>
        public static NewGameStart Resume(GameStartDef def, IEnumerable<string> pendingEnclaves, float playedSeconds, IGameLog log)
        {
            if (def == null || pendingEnclaves == null) return null;
            var pending = new HashSet<string>(pendingEnclaves, StringComparer.Ordinal);
            var start = new NewGameStart(def, log);
            start._pendingAlarms.RemoveAll(e => !pending.Contains(e.Enclave));
            if (start._pendingAlarms.Count == 0) return null;
            start._played = playedSeconds;
            return start;
        }
        /// <summary>Enclaves still waiting for their alarm.</summary>
        public IReadOnlyList<string> PendingAlarms => _pendingAlarms.Select(e => e.Enclave).ToList();

        /// <summary>
        /// Applies the start state. <paramref name="skipClock"/> moves the clock forward by game seconds; the clock of a
        /// fresh session stands at <paramref name="clockHour"/> of day 0.
        /// </summary>
        public static NewGameStart Apply(GameStartDef def, float clockHour, Action<double> skipClock, StoryChapter chapter,
                                         QuestService quests, NpcSimulation npcs, EnclaveStates enclaves, IGameLog log)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            log ??= NullGameLog.Instance;
            double target = def.Day * 24.0 + def.Hour;
            if (target >= clockHour) skipClock?.Invoke((target - clockHour) * 3600.0);
            else log.Warning($"game start: the clock cannot go back to day {def.Day} {def.Hour:0.##} h");
            chapter?.Set(def.Chapter);
            foreach (var quest in def.Quests)
                if (quests == null || !quests.TrySetStatus(new Id<QuestDef>(quest), QuestStatus.Running))
                    log.Warning($"game start: quest {quest} could not be started");
            foreach (var r in def.Routines)
                if (npcs == null || !npcs.SetRoutine(new ActorId(r.Npc), r.Routine))
                    log.Warning($"game start: no NPC {r.Npc} for routine {r.Routine}");
            foreach (var e in def.Enclaves)
                enclaves?.Set(e.Enclave, new EnclaveState(EnclaveStatus.Routine, e.Raid, e.Revolution));
            return new NewGameStart(def, log);
        }

        /// <summary>The developer start is somewhere else: no first fight.</summary>
        public void DropAlarms() => _pendingAlarms.Clear();

        /// <summary>
        /// One frame of play (nothing happens while paused). Every <see cref="AlarmRetrySeconds"/> each pending alarm is
        /// tried: <paramref name="raise"/> sends the enclave's defenders at the player and returns them; once someone is
        /// sent the enclave goes to Attack (G3 NotifyEnclave in raid mode → ChangeEnclaveStatus).
        /// <paramref name="mayAlarm"/> is G3's raid condition (the alarming member is alive and politically friendly to
        /// its enclave). Returns the enclaves alarmed in this frame.
        /// </summary>
        public IReadOnlyList<string> Tick(float realDeltaSeconds, EnclaveStates enclaves, Func<EnclaveStart, bool> mayAlarm,
                                          Func<string, IReadOnlyList<ActorId>> raise)
        {
            if (_pendingAlarms.Count == 0 || realDeltaSeconds <= 0) return Array.Empty<string>();
            _played += realDeltaSeconds;
            if (_played < _nextTry) return Array.Empty<string>();
            _nextTry = _played + AlarmRetrySeconds;
            var raised = new List<string>();
            for (int i = _pendingAlarms.Count - 1; i >= 0; i--)
            {
                var e = _pendingAlarms[i];
                var state = enclaves.Of(e.Enclave);
                if (!state.Raid || state.Status != EnclaveStatus.Routine || (mayAlarm != null && !mayAlarm(e)))
                {
                    _pendingAlarms.RemoveAt(i); // G3: no raid alarm unless raiding in routine by a friendly member
                    continue;
                }
                var sent = raise(e.Enclave);
                if (sent == null || sent.Count == 0) continue;
                enclaves.SetStatus(e.Enclave, EnclaveStatus.Attack);
                _pendingAlarms.RemoveAt(i);
                raised.Add(e.Enclave);
                _log.Info($"game start: {e.Enclave} raid alarm, {sent.Count} defenders attack the player");
            }
            if (_pendingAlarms.Count > 0 && _played >= AlarmGiveUpSeconds)
            {
                _log.Warning($"game start: no defenders near the player for {string.Join(", ", PendingAlarms)}; alarm given up");
                _pendingAlarms.Clear();
            }
            return raised;
        }
    }
}
