using System;
using System.Globalization;

namespace Myrtana.Ui.Skin
{
    /// <summary>8-bit RGBA colour of the skin, written as <c>#RRGGBBAA</c> (or <c>#RRGGBB</c> = opaque) in the skin data.</summary>
    public readonly struct SkinColor : IEquatable<SkinColor>
    {
        public readonly byte R, G, B, A;

        public SkinColor(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }

        public static readonly SkinColor White = new SkinColor(255, 255, 255);
        public static readonly SkinColor Black = new SkinColor(0, 0, 0);
        public static readonly SkinColor Transparent = new SkinColor(0, 0, 0, 0);

        public bool IsInvisible => A == 0;

        public SkinColor WithAlpha(byte a) => new SkinColor(R, G, B, a);

        public string Hex => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

        /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (the leading '#' is optional).</summary>
        public static SkinColor Parse(string hex)
        {
            if (!TryParse(hex, out var c)) throw new FormatException($"not a colour: '{hex}'");
            return c;
        }

        public static bool TryParse(string hex, out SkinColor color)
        {
            color = default;
            if (string.IsNullOrEmpty(hex)) return false;
            string s = hex[0] == '#' ? hex.Substring(1) : hex;
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            if (s.Length == 6) v = (v << 8) | 0xFF;
            color = new SkinColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        public bool Equals(SkinColor o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object obj) => obj is SkinColor o && Equals(o);
        public override int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;
        public static bool operator ==(SkinColor a, SkinColor b) => a.Equals(b);
        public static bool operator !=(SkinColor a, SkinColor b) => !a.Equals(b);
        public override string ToString() => Hex;
    }
}
