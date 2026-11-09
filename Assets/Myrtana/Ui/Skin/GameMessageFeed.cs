using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Colour group of a game message (§6.3, gEGameMessageType table built in gCScreenOutputCtrl::PrintGameMessage): gold
    /// C7A648 for most (types 0/1/4/7/10/11/14/15), tan E0C495 for items taken or given (2/3/6/16), teal 0C9770 for
    /// results (5/8/9), orange D17210 for quests (12/17/18), red 9F1813 for errors (13).
    /// </summary>
    public enum GameMessageTone { Info, Result, Quest, Error, Item }

    public static class GameMessageColors
    {
        public static SkinColor Of(GameMessageTone tone)
        {
            switch (tone)
            {
                case GameMessageTone.Result: return G3Palette.Teal;
                case GameMessageTone.Quest: return G3Palette.Orange;
                case GameMessageTone.Error: return G3Palette.BloodRed;
                case GameMessageTone.Item: return G3Palette.Tan;
                default: return G3Palette.Gold;
            }
        }
    }

    /// <summary>One message line as laid out at a moment: top edge inside the message area and opacity.</summary>
    public readonly struct GameMessageLine
    {
        public GameMessageLine(int id, string text, GameMessageTone tone, float top, float alpha)
        {
            Id = id;
            Text = text;
            Tone = tone;
            Top = top;
            Alpha = alpha;
        }

        /// <summary>Stable per message, so a view can keep one label per message.</summary>
        public int Id { get; }
        public string Text { get; }
        public GameMessageTone Tone { get; }
        public float Top { get; }
        public float Alpha { get; }
    }

    /// <summary>
    /// G3's game messages (<c>IDC_STATIC_GAME_MESSAGES_2</c>, gCHUDTextEffect, §6.3
    /// [V 100a76d0, 100a77e0, 100a7880, 100884b0]). Every message is a centred single line in a row of the control:
    /// <c>PrintGameMessage</c> moves every message already shown one row up (row − 1) and puts the new one in row
    /// <see cref="NewestRow"/> — a queue that never merges or replaces, each item or gain is its own line. A line's top is
    /// <c>row × lineHeight − 20 px/s × age</c> (flag 0x10: it drifts up on its own), it fades in over the first 0.4 s
    /// (flag 4) and out over the last 0.4 s (flag 8) and is removed only when its 6 s are over; there is no count limit.
    /// Lines that drifted or were pushed wholly above the control can never come back and are dropped early.
    /// </summary>
    public sealed class GameMessageFeed
    {
        public const double LifeSeconds = 6.0;
        public const double FadeSeconds = 0.4;
        public const float DriftPixelsPerSecond = 20f;
        /// <summary>The row a new message takes (G3: 10; 10 × 20 px = 200 of the control's 216 px).</summary>
        public const int NewestRow = 10;

        readonly List<(int id, string text, GameMessageTone tone, double posted, int row)> _messages =
            new List<(int, string, GameMessageTone, double, int)>();
        readonly List<GameMessageLine> _lines = new List<GameMessageLine>();
        int _nextId;

        public GameMessageFeed(float areaHeight, float lineHeight)
        {
            AreaHeight = areaHeight;
            LineHeight = lineHeight;
        }

        public float AreaHeight { get; }
        public float LineHeight { get; }
        public int Count => _messages.Count;

        /// <summary>
        /// Top of the newest row: G3's row 10 (200 px), kept inside the control (the HUD's is 216 px high: 196 px, so the
        /// 20 px row is not cut at the bottom).
        /// </summary>
        public float NewestTop => Math.Max(0f, Math.Min(NewestRow * LineHeight, AreaHeight - LineHeight));

        public int Post(string text, GameMessageTone tone, double now)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            for (int i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i];
                _messages[i] = (m.id, m.text, m.tone, m.posted, m.row - 1);
            }
            int id = _nextId++;
            _messages.Add((id, text, tone, now, 0));
            return id;
        }

        public void Clear() => _messages.Clear();

        /// <summary>The visible lines at <paramref name="now"/>, oldest first; expired and pushed-out messages are removed.</summary>
        public IReadOnlyList<GameMessageLine> Layout(double now)
        {
            // Runs every frame: plain loops, no closures (perf-7).
            _lines.Clear();
            float newest = NewestTop;
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var m = _messages[i];
                double age = Math.Max(0.0, now - m.posted);
                float top = newest + m.row * LineHeight - (float)age * DriftPixelsPerSecond;
                if (age >= LifeSeconds || top + LineHeight <= 0f)
                {
                    _messages.RemoveAt(i);
                    continue;
                }
                _lines.Add(new GameMessageLine(m.id, m.text, m.tone, top, Alpha(age)));
            }
            _lines.Reverse();
            return _lines;
        }

        static float Alpha(double age)
        {
            double a = Math.Min(1.0, Math.Min(age / FadeSeconds, (LifeSeconds - age) / FadeSeconds));
            return (float)Math.Max(0.0, a);
        }
    }
}
