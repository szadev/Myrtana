namespace G3.Player
{
    /// <summary>
    /// fix-101 (owner: "scrolling the wheel in the inventory also zooms the camera"): the camera zoom keys (G3 Plus / Minus:
    /// the wheel halves, Num + / Num -) act only while the mouse belongs to play (fix-87 CursorPolicy Gameplay:
    /// <c>G3Cursor.Captured</c>). With a HUD page, a menu, the dialog choice list or a tip up the wheel scrolls that UI, and a
    /// spoken line or a loading screen takes no zoom either (G3 routes the keys to the open page's controls, not to the
    /// camera).
    /// </summary>
    public static class CameraZoomInput
    {
        /// <summary>Zoom notches of this frame (+ = farther): the wheel's and the held keys', none when not captured.</summary>
        public static float Notches(bool captured, float wheelNotches, float keyNotches) => captured ? wheelNotches + keyNotches : 0f;
    }
}
