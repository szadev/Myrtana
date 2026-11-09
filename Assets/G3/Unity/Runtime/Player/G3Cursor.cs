using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The one writer of <see cref="Cursor.visible"/> and <see cref="Cursor.lockState"/>. A game installs its cursor
    /// policy with <see cref="Apply"/> every frame (Myrtana: CursorDirector, fix-87); without one (the bare streaming
    /// demo) <see cref="G3Player"/> captures the mouse on a left click and frees it on Esc.
    /// Gameplay code asks <see cref="Captured"/> (the mouse turns the hero, the buttons attack and use) instead of the
    /// lock state, so a hidden-but-locked cursor during a spoken line or a use motion does not count as play.
    /// </summary>
    public static class G3Cursor
    {
        static bool _captured;
        static bool _managed;
        static bool? _visible, _locked;

        /// <summary>A game policy drives the cursor (<see cref="Apply"/> was called).</summary>
        public static bool Managed => _managed;

        /// <summary>The mouse belongs to the game this frame: the policy says play and the window holds the lock.</summary>
        public static bool Captured => (_managed ? _captured : true) && Cursor.lockState == CursorLockMode.Locked;

        /// <summary>
        /// Applies the policy's wish: <paramref name="visible"/> pointer, <paramref name="locked"/> cursor, and whether the
        /// mouse belongs to play. A lock the platform dropped (Esc in the editor, focus lost) comes back on a change of
        /// the wish, on a left click or on focus (<paramref name="recapture"/>), as in G3 — not every frame.
        /// </summary>
        public static void Apply(bool visible, bool locked, bool captured, bool recapture)
        {
            _managed = true;
            _captured = captured;
            if (_locked != locked || recapture && locked && Cursor.lockState != CursorLockMode.Locked)
                Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            _locked = locked;
            if (_visible != visible || Cursor.visible != visible) Cursor.visible = visible;
            _visible = visible;
        }

        /// <summary>The policy is gone (its game was destroyed): the demo fallback takes over again.</summary>
        public static void Release()
        {
            _managed = false;
            _captured = false;
            _visible = _locked = null;
        }

        /// <summary>The demo's own capture (no policy installed): click locks, Esc and fly mode free.</summary>
        internal static void DemoCapture(bool on)
        {
            if (_managed) return;
            Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !on;
        }
    }
}
