namespace Myrtana.Ui.Dialog
{
    /// <summary>
    /// When a mouse click activates a dialog choice (G3 list control LVS_EX_ONECLICKACTIVATE, notes/gui_dialog_hud.md
    /// §1.4): only a press and a release of the left button on the same row, with the press made while the rows were
    /// already on screen. A click elsewhere — beside the list, or the click that skipped the line before the choices
    /// appeared and whose press therefore landed on rows created in that frame — never activates the highlighted row;
    /// Enter still confirms the highlight (keyboard, not this gate).
    /// </summary>
    public sealed class ChoiceClickGate
    {
        int _shownFrame = int.MinValue;
        int _pressedRow = -1;

        /// <summary>New rows are on screen from <paramref name="frame"/> on; an earlier press is forgotten.</summary>
        public void Shown(int frame)
        {
            _shownFrame = frame;
            _pressedRow = -1;
        }

        /// <summary>The left button went down on <paramref name="row"/> (−1 = not on a row).</summary>
        public void Press(int row, int frame) => _pressedRow = frame > _shownFrame ? row : -1;

        /// <summary>The left button came up on <paramref name="row"/> (−1 = not on a row): true when this activates it.</summary>
        public bool Release(int row)
        {
            bool activate = row >= 0 && row == _pressedRow;
            _pressedRow = -1;
            return activate;
        }
    }
}
