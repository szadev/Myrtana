using System;

namespace G3.Unity
{
    /// <summary>
    /// Which story-switchable world sectors are on (G3 SetSectorStatus: liberated towns' *_NPC_01/_02, story camps,
    /// Nordmar's ancestor lights …). The game sets <see cref="IsEnabled"/> from its world state and calls
    /// <see cref="NotifyChanged"/> after a switch; the world's sector objects (<see cref="G3SectorContent"/> in the cell
    /// scenes) and the placed effects and lights (<see cref="G3WorldEffects"/>) follow it. Without the game (tools, test
    /// scenes) every sector shows its new-game state.
    /// </summary>
    public static class G3StorySectors
    {
        static Func<string, bool> _isEnabled;

        /// <summary>The game's sector state; null = each sector as at the start of a new game.</summary>
        public static Func<string, bool> IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                NotifyChanged();
            }
        }

        /// <summary>Raised after a sector was switched (or the state was replaced, e.g. after loading a save).</summary>
        public static event Action Changed;

        /// <summary>Whether things of <paramref name="sector"/> are in the world; empty = an always-present sector.</summary>
        public static bool IsOn(string sector, bool enabledAtStart)
        {
            if (string.IsNullOrEmpty(sector)) return true;
            var f = _isEnabled;
            return f != null ? f(sector) : enabledAtStart;
        }

        public static void NotifyChanged() => Changed?.Invoke();
    }
}
