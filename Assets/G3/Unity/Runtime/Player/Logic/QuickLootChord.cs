namespace G3.Player
{
    /// <summary>
    /// fix-205 [owner] QoL, not G3: the quick-loot chord. "Aktion 1" (left button) pressed while the quick-loot modifier
    /// (<see cref="SessionKey.QuickLootModifier"/>, default Ctrl) or "Aktion 2" (right button) is held takes everything from
    /// the focus instead of opening the loot page. A right button press that already quick-looted (CP right-click looting)
    /// makes the left button of the same hold do nothing.
    /// </summary>
    public static class QuickLootChord
    {
        /// <param name="enabled">The port option "Take all with Ctrl / right button + click" is on.</param>
        /// <param name="modifierHeld">An input of the quick-loot modifier is held.</param>
        /// <param name="secondaryHeld">An input of "Aktion 2" is held.</param>
        public static bool Active(bool enabled, bool modifierHeld, bool secondaryHeld) => enabled && (modifierHeld || secondaryHeld);
    }

    /// <summary>
    /// A toggle key whose press can be held back (fix-205): Sneak shares Ctrl with the quick-loot modifier, so while the hero
    /// faces something to loot the sneak toggle waits for the key's release and is dropped when the key served a chord
    /// (Ctrl + click). Without a lootable focus the toggle fires on the press, as in G3.
    /// </summary>
    public sealed class ChordedToggle
    {
        bool _pending, _consumed;

        /// <summary>A press is held back.</summary>
        public bool Pending => _pending;

        /// <summary>The key went down; true when the toggle fires now (<paramref name="defer"/> false).</summary>
        public bool Press(bool defer)
        {
            if (!defer)
            {
                _pending = false;
                return true;
            }
            _pending = true;
            _consumed = false;
            return false;
        }

        /// <summary>The key served a chord while held: its release does not toggle.</summary>
        public void Consume()
        {
            if (_pending) _consumed = true;
        }

        /// <summary>The key was released; true when the held-back toggle fires now.</summary>
        public bool Release()
        {
            bool fire = _pending && !_consumed;
            _pending = false;
            _consumed = false;
            return fire;
        }

        /// <summary>Drops a held-back press (input disabled, scripted control).</summary>
        public void Cancel() => _pending = _consumed = false;
    }
}
