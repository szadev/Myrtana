using System.Collections.Generic;
using Myrtana.Session;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>Where short game messages appear: the G3 message area (<see cref="G3MessageArea"/>) or the plain label.</summary>
    public interface IHudMessageView
    {
        void Show(string text, NoticeKind kind);
    }

    /// <summary>
    /// Short game messages ("Game saved", "New quest", "Experience +50", refusals), like G3's GO_ messages. Screens and
    /// controllers post here; the view decides the look.
    /// </summary>
    public sealed class HudMessages
    {
        const int RecentKept = 32;
        static readonly List<string> _recent = new List<string>();

        readonly IHudMessageView _view;

        public HudMessages(IHudMessageView view) => _view = view;

        /// <summary>The last messages shown, oldest first (dev runner probe <c>hudmsg</c>, fix-77).</summary>
        public static IReadOnlyList<string> Recent => _recent;

        public void Show(string text, NoticeKind kind = NoticeKind.Info)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_recent.Count >= RecentKept) _recent.RemoveAt(0);
            _recent.Add(text);
            _view.Show(text, kind);
        }
    }

    /// <summary>
    /// The plain look (no G3 GUI import): one message at a time in the <c>hud-message</c> label of Hud.uxml, each for a
    /// few seconds.
    /// </summary>
    public sealed class PlainHudMessages : IHudMessageView
    {
        const long ShowMilliseconds = 2500;
        const int MaxQueued = 6;

        readonly Label _label;
        readonly Queue<string> _queue = new Queue<string>();
        bool _showing;

        public PlainHudMessages(VisualElement hudRoot)
        {
            _label = hudRoot.Q<Label>("hud-message");
            _label.style.display = DisplayStyle.None;
        }

        public void Show(string text, NoticeKind kind)
        {
            if (_queue.Count >= MaxQueued) _queue.Dequeue();
            _queue.Enqueue(text);
            if (!_showing) Next();
        }

        void Next()
        {
            if (_queue.Count == 0)
            {
                _showing = false;
                _label.style.display = DisplayStyle.None;
                return;
            }
            _showing = true;
            _label.text = _queue.Dequeue();
            _label.style.display = DisplayStyle.Flex;
            _label.schedule.Execute(Next).StartingIn(ShowMilliseconds);
        }
    }
}
