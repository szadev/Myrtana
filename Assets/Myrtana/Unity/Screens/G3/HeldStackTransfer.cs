using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// The single-piece transfer of the loot and trade pages while its button stays down (right mouse button on a tile, or
    /// Space on the selection): the press moves one piece, then <see cref="StackTransferRepeat"/> repeats it. The stack is
    /// followed by its item id, so the repeat stays on it while the list re-sorts, and stops when it is gone.
    /// </summary>
    sealed class HeldStackTransfer<TGrid> where TGrid : class
    {
        readonly StackTransferRepeat _repeat = new StackTransferRepeat();
        TGrid _grid;
        string _item;
        bool _space;

        /// <summary>Starts a hold on <paramref name="item"/> of <paramref name="grid"/>; returns the amount the press moves (1).</summary>
        public int Press(TGrid grid, string item, bool space)
        {
            _grid = grid;
            _item = item;
            _space = space;
            return _repeat.Press(Time.unscaledTime);
        }

        /// <summary>The amount due this frame and where (0 when nothing is held); releases when the button is up.</summary>
        public int Tick(out TGrid grid, out string item)
        {
            grid = _grid;
            item = _item;
            if (!_repeat.Held) return 0;
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            var mouse = Mouse.current;
            bool down = _space ? PageKeys.SpaceHeld : mouse != null && mouse.rightButton.isPressed; // gamepad-1: X held repeats too
            if (!down || _grid == null) { Release(); return 0; }
            return _repeat.Poll(Time.unscaledTime);
        }

        public void Release()
        {
            _repeat.Release();
            _grid = null;
            _item = null;
        }

        /// <summary>The row of <paramref name="item"/> in <paramref name="rows"/> that can move (not worn), or -1.</summary>
        public static int IndexOf(IReadOnlyList<ItemRow> rows, string item)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Item == item && !rows[i].Worn) return i;
            return -1;
        }
    }
}
