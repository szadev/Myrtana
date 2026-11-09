using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Story;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Arena fights (ZS_Arena, OnArena; Docs/combat.md §5.2): while an Arena quest is running, its opponent waits with full
    /// HP and stamina and attacks the player with the reason Arena when he steps up to it. Arena hits never kill
    /// (knock-out, 20 s down, +15 regeneration for the loser) and the winner ransacks per the rules. The opponent first
    /// walks into the ring and waits there (G3 SendToArena + ZS_Arena: NpcSimulation.ArenaPointOf). [interp] The fight
    /// starts when the hero steps into the quest's arena zone or comes within <see cref="StartDistance"/> of the opponent (no zone known). Winning (opponent
    /// down), losing (hero down) and leaving the arena zone (Lost / opponent counted) are the story's QuestArenaCounter.
    /// </summary>
    public sealed class ArenaFights : MonoBehaviour, ISessionView
    {
        const float StartDistance = 6f, CheckSeconds = 0.5f;

        readonly List<(Id<QuestDef> quest, ActorId opponent, string zone)> _arena = new List<(Id<QuestDef>, ActorId, string)>();
        readonly HashSet<ActorId> _started = new HashSet<ActorId>();
        CombatPresenter _presenter;
        GameSession _session;
        float _next;

        public void Init(CombatPresenter presenter) => _presenter = presenter;

        public void Bind(GameSession session)
        {
            _session = session;
            _arena.Clear();
            _started.Clear();
            foreach (var q in session.Content.Database.All<QuestDef>())
                if (q.Objective.Kind == QuestObjectiveKind.Arena)
                    foreach (var t in q.Objective.Targets)
                        _arena.Add((q.Id, new ActorId(t.Target), q.Objective.Destination.Name));
        }

        void Update()
        {
            if (_session == null || GamePause.IsPaused || Time.time < _next) return;
            _next = Time.time + CheckSeconds;
            if (!_presenter.TryGet(ActorId.Player, out var player)) return;
            var vitals = _session.Combat.Vitals;
            if (vitals.StateOf(ActorId.Player) != VitalState.Alive) return;
            var areas = _session.WorldState?.Areas;
            foreach (var (quest, opponent, zone) in _arena)
            {
                if (_session.Quests.StatusOf(quest) != QuestStatus.Running)
                {
                    _started.Remove(opponent); // Lost → a Reactivator entry runs the quest again: a new fight (story-2)
                    continue;
                }
                var brain = _session.NpcCombat.Get(opponent);
                if (brain != null && brain.IsFighting) continue;
                if (vitals.StateOf(opponent) != VitalState.Alive || !_presenter.TryGet(opponent, out var fighter)) continue;
                // G3 ZS_Arena: the opponent first walks to the arena point (NpcSimulation.ArenaPointOf), then waits for the hero.
                if (_session.Npcs.InArena(opponent) == false) continue;
                bool inRing = areas != null && areas.TryIsInside(ActorId.Player, zone, out bool inside) && inside;
                if (!inRing && (fighter.transform.position - player.transform.position).sqrMagnitude > StartDistance * StartDistance) continue;
                if (!_started.Add(opponent)) continue; // one fight per quest run; a new one after the next Bind
                vitals.RestoreFully(opponent);
                _session.NpcCombat.Engage(opponent, ActorId.Player, AttackReason.Arena);
                Debug.Log($"[Myrtana] arena fight: {opponent} vs player ({quest.Name}, hero {(inRing ? "in " + zone : "near the opponent")})");
            }
        }
    }
}
