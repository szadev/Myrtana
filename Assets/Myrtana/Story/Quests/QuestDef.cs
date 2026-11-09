using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// A quest definition: title, prerequisites, what has to be done (<see cref="Objective"/>) and the rewards on success.
    /// Its runtime state (status, counters) is kept by <see cref="QuestService"/>.
    /// </summary>
    public sealed class QuestDef : IDefinition
    {
        [JsonConstructor]
        public QuestDef(Id<QuestDef> id, TextId title, IReadOnlyList<Id<QuestDef>> prerequisites,
                        QuestObjective objective = null, QuestRewards rewards = null, double timeLimitHours = 0,
                        string folder = null)
        {
            Id = id;
            Title = title;
            Prerequisites = prerequisites ?? Array.Empty<Id<QuestDef>>();
            Objective = objective ?? QuestObjective.None;
            Rewards = rewards ?? QuestRewards.None;
            TimeLimitHours = timeLimitHours;
            Folder = folder ?? string.Empty;
        }

        public Id<QuestDef> Id { get; }
        public TextId Title { get; }
        /// <summary>Quests that must be finished before this one can be offered.</summary>
        public IReadOnlyList<Id<QuestDef>> Prerequisites { get; }
        public QuestObjective Objective { get; }
        public QuestRewards Rewards { get; }
        /// <summary>
        /// Game hours after the start after which the quest is overdue (G3 RunningTime; 0 = no limit). Being overdue only
        /// makes Overtime dialog entries available; the quest does not fail by itself.
        /// </summary>
        public double TimeLimitHours { get; }
        /// <summary>
        /// G3 editor folder of the quest ("Myrtana/Silden_City/", "Hauptstory/"); the quest log's location filter matches
        /// its places against it (<c>Session.QuestLocations</c>). Empty in packs imported before it was stored.
        /// </summary>
        public string Folder { get; }

        string IDefinition.Key => Id.Name;
    }

    public enum QuestStatus { Open, Running, Success, Failed, Obsolete, Cancelled, Lost, Won }

    public static class QuestStatusExtensions
    {
        /// <summary>Open, Running and Lost (which can be reactivated) are not final; everything else is.</summary>
        public static bool IsFinished(this QuestStatus status) =>
            status != QuestStatus.Open && status != QuestStatus.Running && status != QuestStatus.Lost;

        /// <summary>A successful end (rewards are given).</summary>
        public static bool IsSuccess(this QuestStatus status) => status == QuestStatus.Success || status == QuestStatus.Won;
    }

    /// <summary>Published when a quest changes status.</summary>
    public readonly struct QuestStatusChanged : IGameEvent
    {
        public QuestStatusChanged(Id<QuestDef> quest, QuestStatus from, QuestStatus to)
        {
            Quest = quest;
            From = from;
            To = to;
        }

        public Id<QuestDef> Quest { get; }
        public QuestStatus From { get; }
        public QuestStatus To { get; }
    }
}
