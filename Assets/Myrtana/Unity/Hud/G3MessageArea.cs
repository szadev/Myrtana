using System.Collections.Generic;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3's game messages in <c>IDC_STATIC_GAME_MESSAGES_2</c> (Docs/formats-gui.md §6.3): default face, 20 px cell, bold,
    /// centred, shadowed lines laid out by <see cref="GameMessageFeed"/> (6 s, 0.4 s fades, drifting up).
    /// </summary>
    public sealed class G3MessageArea : IHudMessageView
    {
        /// <summary>Cell height of the message font (gCHUDTextEffect: LOGFONT height +20, weight 700) = one message row.</summary>
        public const int CellHeight = 20;

        static float FontSize => G3FontMetrics.FromCellHeight(CellHeight);

        readonly VisualElement _area;
        readonly GameMessageFeed _feed;
        readonly Dictionary<int, Label> _labels = new Dictionary<int, Label>();
        readonly HashSet<int> _alive = new HashSet<int>();
        readonly List<int> _dead = new List<int>();

        public G3MessageArea(VisualElement area, float height)
        {
            _area = area;
            _area.pickingMode = PickingMode.Ignore;
            _area.style.overflow = Overflow.Hidden;
            _feed = new GameMessageFeed(height, G3Text.LineHeight(FontSize));
        }

        public void Show(string text, NoticeKind kind) => _feed.Post(text, Tone(kind), Time.unscaledTimeAsDouble);

        public void Clear() => _feed.Clear();

        /// <summary>Lays the lines out for this frame (unscaled time: messages keep fading while the game menu pauses).</summary>
        public void Update()
        {
            _alive.Clear();
            var lines = _feed.Layout(Time.unscaledTimeAsDouble);
            for (int i = 0; i < lines.Count; i++) // indexed: foreach over IReadOnlyList boxes the enumerator every frame
            {
                var line = lines[i];
                _alive.Add(line.Id);
                if (!_labels.TryGetValue(line.Id, out var label)) _labels[line.Id] = label = CreateLabel(line);
                label.style.top = line.Top;
                label.style.opacity = line.Alpha;
            }
            if (_labels.Count == _alive.Count) return;
            _dead.Clear();
            foreach (var id in _labels.Keys)
                if (!_alive.Contains(id)) _dead.Add(id);
            foreach (var id in _dead)
            {
                _labels[id].RemoveFromHierarchy();
                _labels.Remove(id);
            }
        }

        Label CreateLabel(GameMessageLine line)
        {
            var label = new Label(line.Text) { pickingMode = PickingMode.Ignore };
            label.style.position = Position.Absolute;
            label.style.left = 0;
            label.style.right = 0;
            label.style.height = _feed.LineHeight;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            G3Text.Font(label, FontSize, bold: true);
            G3Text.Color(label, GameMessageColors.Of(line.Tone));
            G3Text.Shadow(label);
            _area.Add(label);
            return label;
        }

        static GameMessageTone Tone(NoticeKind kind)
        {
            switch (kind)
            {
                case NoticeKind.Result: return GameMessageTone.Result;
                case NoticeKind.Quest: return GameMessageTone.Quest;
                case NoticeKind.Error: return GameMessageTone.Error;
                case NoticeKind.Item: return GameMessageTone.Item;
                default: return GameMessageTone.Info;
            }
        }
    }
}
