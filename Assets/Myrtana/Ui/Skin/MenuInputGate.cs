using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// fix-233 [owner: the click that skipped the last logo also clicked Continue on the title]: the input that ended a
    /// video must not reach the menu that appears under it. <see cref="Close"/> when the menu appears; it stays shut for
    /// <see cref="GraceSeconds"/> and then until every mouse button, key and pad button is released
    /// (<see cref="Tick"/>). While shut the menu takes no click, Enter, Space, Esc or pad button. A pointer counts as a
    /// click only when both its press and its release reach the menu while it is open (<see cref="PointerDown"/>,
    /// <see cref="PointerUp"/>): a press swallowed while shut never completes a click.
    /// </summary>
    public sealed class MenuInputGate
    {
        public const double GraceSeconds = 0.3;

        double _until = double.NegativeInfinity;
        bool _shut;
        readonly HashSet<int> _pressed = new HashSet<int>();

        /// <summary>The menu takes no input now.</summary>
        public bool Blocks => _shut;

        /// <summary>The menu appears at <paramref name="now"/> (seconds): shut for the grace and until all is released.</summary>
        public void Close(double now)
        {
            _shut = true;
            if (now + GraceSeconds > _until) _until = now + GraceSeconds;
            _pressed.Clear();
        }

        /// <summary>Each frame: <paramref name="anyHeld"/> = a mouse button, key or pad button is held.</summary>
        public void Tick(double now, bool anyHeld)
        {
            if (_shut && now >= _until && !anyHeld) _shut = false;
        }

        /// <summary>A pointer press reaches the menu: true when it may start a click.</summary>
        public bool PointerDown(int pointerId)
        {
            if (_shut)
            {
                _pressed.Remove(pointerId);
                return false;
            }
            _pressed.Add(pointerId);
            return true;
        }

        /// <summary>A pointer release reaches the menu: true only when its press was taken while open.</summary>
        public bool PointerUp(int pointerId) => _pressed.Remove(pointerId) && !_shut;
    }
}
