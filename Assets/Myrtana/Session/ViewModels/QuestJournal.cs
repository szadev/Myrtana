using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>The quest log screen's data in the text language: quests grouped like G3 (current, successful, failed).</summary>
    public sealed class QuestJournal
    {
        public enum Group { Running, Succeeded, Failed }

        static readonly TextId RunningText = new TextId("HUD_Quest_Running"), SuccessText = new TextId("HUD_Quest_Success"),
                               FailedText = new TextId("HUD_Quest_Failed"), NoQuestsText = new TextId("HUD_NoQuests");

        /// <summary>One log entry: who said it (empty for notes; <see cref="ByHero"/> for the hero's own lines) and what.</summary>
        public readonly struct Line
        {
            public Line(string speaker, string text, bool byHero)
            {
                Speaker = speaker;
                Text = text;
                ByHero = byHero;
            }

            public string Speaker { get; }
            public string Text { get; }
            public bool ByHero { get; }
        }

        public sealed class Quest
        {
            public Quest(Id<QuestDef> id, string title, QuestStatus status, IReadOnlyList<Line> lines, string folder = null)
            {
                Folder = folder ?? string.Empty;
                Id = id;
                Title = title;
                Status = status;
                Lines = lines;
            }

            public Id<QuestDef> Id { get; }
            public string Title { get; }
            public QuestStatus Status { get; }
            public IReadOnlyList<Line> Lines { get; }
            /// <summary>G3 editor folder (<see cref="QuestDef.Folder"/>) for the location filter.</summary>
            public string Folder { get; }
        }

        QuestJournal(IReadOnlyDictionary<Group, IReadOnlyList<Quest>> groups, IReadOnlyDictionary<Group, string> titles, string emptyText)
        {
            Groups = groups;
            GroupTitles = titles;
            EmptyText = emptyText;
        }

        public IReadOnlyDictionary<Group, IReadOnlyList<Quest>> Groups { get; }
        public IReadOnlyDictionary<Group, string> GroupTitles { get; }
        public string EmptyText { get; }

        /// <summary>
        /// The tab a quest is listed on, as G3's quest log page (CP 1.75 Game.dll 1009f120: a tab lists a quest whose status
        /// equals the tab's status, tabs {Running, Success, Failed} at 102bba40): only Running, Success and Failed are listed.
        /// Open, Obsolete (an Open quest given up, e.g. Gorn_ShowReddock when the hero meets Gorn in Gotha first), Cancelled,
        /// Lost and Won appear on no tab (fix-161: Obsolete/Cancelled showed under "Failed").
        /// </summary>
        public static Group? GroupOf(QuestStatus status)
        {
            switch (status)
            {
                case QuestStatus.Running: return Group.Running;
                case QuestStatus.Success: return Group.Succeeded;
                case QuestStatus.Failed: return Group.Failed;
                default: return null;
            }
        }

        public static QuestJournal Of(GameSession session)
        {
            var text = session.Localizer;
            var groups = new Dictionary<Group, List<Quest>> { [Group.Running] = new List<Quest>(), [Group.Succeeded] = new List<Quest>(), [Group.Failed] = new List<Quest>() };
            foreach (var id in session.QuestLog.Quests)
            {
                var status = session.Quests.StatusOf(id);
                var group = GroupOf(status);
                if (group == null) continue;
                bool known = session.Quests.TryGetDefinition(id, out var def);
                string title = known && !def.Title.IsEmpty ? text.Text(def.Title) : id.Name;
                var lines = session.QuestLog.EntriesOf(id)
                    .Select(e => new Line(SpeakerName(session, e.Speaker), text.Text(e.Text), e.Speaker == ActorId.Player))
                    .ToList();
                groups[group.Value].Add(new Quest(id, title, status, lines, known ? def.Folder : null));
            }
            var titles = new Dictionary<Group, string>
            {
                [Group.Running] = text.Text(RunningText), [Group.Succeeded] = text.Text(SuccessText), [Group.Failed] = text.Text(FailedText),
            };
            return new QuestJournal(groups.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Quest>)kv.Value), titles, text.Text(NoQuestsText));
        }

        /// <summary>An actor's display name in the text language (the id without one; empty for none).</summary>
        public static string SpeakerName(GameSession session, ActorId speaker)
        {
            if (speaker.IsEmpty) return string.Empty;
            return session.Content.Database.TryGet(speaker.Name, out NpcDef def) && !def.DisplayName.IsEmpty
                ? session.Localizer.Text(def.DisplayName)
                : speaker.Name;
        }
    }
}
