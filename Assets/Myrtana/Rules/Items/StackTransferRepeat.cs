namespace Myrtana.Rules
{
    /// <summary>
    /// Hold-to-repeat of the single-piece transfer on the loot and trade pages (G3 GUI.dll gCHUDListCtrlInvLootStacks /
    /// gCHUDListCtrlInvTradeStacks: right mouse button or Space): the press moves one piece; while the button is held the
    /// move repeats, the first time after 0.33 s and then every 0.05 s, and the step grows ×10 after every 10 repeats
    /// (1, 10, 100…). The caller caps each step by what is left of the stack.
    /// </summary>
    public sealed class StackTransferRepeat
    {
        public const float FirstDelay = 0.33f, Interval = 0.05f;
        public const int RepeatsPerStep = 10, StepFactor = 10, MaxStep = 1_000_000;

        float _next;
        int _repeats, _step;

        public bool Held { get; private set; }

        /// <summary>The press: starts the repeat clock; returns the amount the press itself moves (1).</summary>
        public int Press(float now)
        {
            Held = true;
            _repeats = 0;
            _step = 1;
            _next = now + FirstDelay;
            return 1;
        }

        public void Release() => Held = false;

        /// <summary>The amount due by <paramref name="now"/> while held (the sum of the repeats a long frame spans); 0 when released.</summary>
        public int Poll(float now)
        {
            if (!Held) return 0;
            long total = 0;
            for (int guard = 0; now >= _next && guard < 1000; guard++)
            {
                total += _step;
                _next += Interval;
                if (++_repeats % RepeatsPerStep == 0 && _step < MaxStep) _step *= StepFactor;
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }
}
