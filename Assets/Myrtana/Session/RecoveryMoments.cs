using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// fix-155: the key moments after which the hidden recovery save is written soon (<see cref="RecoverySchedule.KeyMoment"/>):
    /// a quest changes status, the hero takes a quest item (G3's Mission flag: from a chest, the ground, a corpse or a
    /// conversation), and a teleport is requested (the new region loads next). Raises <see cref="Happened"/> with a
    /// short reason for the log.
    /// </summary>
    public sealed class RecoveryMoments : IDisposable
    {
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly IContentDatabase _content;

        public RecoveryMoments(IEventBus events, IContentDatabase content, Action<string> happened)
        {
            _content = content;
            Happened = happened;
            _subscriptions.Add(events.Subscribe<QuestStatusChanged>(e => Raise("quest " + e.Quest.Name + " " + e.To)));
            _subscriptions.Add(events.Subscribe<WorldItemTaken>(e => QuestItem(e.Item)));
            _subscriptions.Add(events.Subscribe<ContainerItemTaken>(e => QuestItem(e.Item)));
            _subscriptions.Add(events.Subscribe<ItemsTransferred>(e => { if (e.To.IsPlayer) QuestItem(e.Item); }));
            _subscriptions.Add(events.Subscribe<TeleportRequested>(_ => Raise("teleport")));
        }

        public Action<string> Happened { get; }

        /// <summary>True when <paramref name="item"/> is a quest item (G3 Mission flag).</summary>
        public static bool IsQuestItem(IContentDatabase content, string item) =>
            !string.IsNullOrEmpty(item) && content != null && content.TryGet(item, out ItemDef def) && def.IsMission;

        void QuestItem(string item)
        {
            if (IsQuestItem(_content, item)) Raise("quest item " + item);
        }

        void Raise(string reason) => Happened?.Invoke(reason);

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
