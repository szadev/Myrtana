using G3.Player;

namespace G3.Unity
{
    /// <summary>
    /// gamepad-1 (Docs/gamepad.md §5): the name of a session key's input for text prompts, following the device used last:
    /// the first keyboard/mouse input ("Mouse 0", "E") or the pad binding ("A", "Cross", "LB + Y"). For the pad a face
    /// button is preferred (Use1 shows A, not RT).
    /// </summary>
    public static class G3InputPrompts
    {
        public static string Name(SessionKey key)
        {
            if (G3Pad.PadIsLast && G3Pad.Connected && Preferred(key) is PadBinding b)
                return PadGlyphs.Name(b, G3Pad.Family);
            foreach (var input in G3Input.Keys.Inputs(key)) return input.Name;
            return "";
        }

        /// <summary>"[A]  " / "[Mouse 0]  " — the plain HUD's use prefix.</summary>
        public static string Prefix(SessionKey key)
        {
            string n = Name(key);
            return n.Length == 0 ? "" : "[" + n + "]  ";
        }

        static PadBinding? Preferred(SessionKey key)
        {
            PadBinding? best = null;
            int bestRank = int.MaxValue;
            foreach (var b in G3Input.Options.Pad.Buttons.Of(key))
            {
                int rank = (b.Button >= PadButton.A && b.Button <= PadButton.Y ? 0 : 2) + (b.Chord ? 1 : 0) + (b.Gesture == PadGesture.Hold ? 4 : 0);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = b;
                }
            }
            return best;
        }
    }
}
