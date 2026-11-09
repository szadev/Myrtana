namespace Myrtana.Core
{
    /// <summary>
    /// The game-speed side of a pause (G3 gCSession::Pause stops the session clock and the application; the tutorial box,
    /// the menu, the intro and the loading screen hold it). Owners nest; the speed to come back to is remembered when the
    /// first owner pauses and restored when the last lets go. While paused the speed is 0 whatever anybody else wrote:
    /// <see cref="Guard"/> puts it back (r31 D1: a dev step wrote 1 while the start tip held the pause, and the Ardea
    /// raiders beat the hero down under the open tip). Engine-free: the host applies the returned scale.
    /// </summary>
    public sealed class PauseScale
    {
        readonly HoldSet _owners = new HoldSet();
        float _resume = 1f;

        public bool IsPaused => _owners.IsHeld;
        /// <summary>The speed the game gets back when the last owner lets go.</summary>
        public float ResumeScale => _resume;

        /// <summary>
        /// Pauses or releases on behalf of <paramref name="owner"/>; returns the scale the host must apply, or null when
        /// nothing changes. <paramref name="current"/> is the host's scale now.
        /// </summary>
        public float? Set(object owner, bool pause, float current)
        {
            bool was = _owners.IsHeld;
            if (!_owners.Set(owner, pause)) return null;
            if (!was)
            {
                _resume = current > 0f ? current : 1f;
                return 0f;
            }
            return _resume;
        }

        /// <summary>Once per frame: the scale to force (0 while paused and somebody changed it), or null when it is right.</summary>
        public float? Guard(float current) => IsPaused && current != 0f ? 0f : (float?)null;

        /// <summary>
        /// Normal speed for a dev step that wants the game running: kept for after the pause when paused (never unpauses
        /// behind an owner's back), applied at once (returned) otherwise.
        /// </summary>
        public float? NormalSpeed()
        {
            _resume = 1f;
            return IsPaused ? (float?)null : 1f;
        }

        /// <summary>Drops every owner (domain reload off in the editor); the scale to restore, or null when not paused.</summary>
        public float? Clear()
        {
            float? restore = _owners.Clear() ? _resume : (float?)null;
            _resume = 1f;
            return restore;
        }
    }
}
