using System;

namespace Myrtana.Rules
{
    /// <summary>The permanent part of one attribute: its value and, for vitals, its maximum.</summary>
    public readonly struct StatValue : IEquatable<StatValue>
    {
        public StatValue(int value, int maximum = 0)
        {
            Value = value;
            Maximum = maximum;
        }

        public int Value { get; }
        /// <summary>Only meaningful for vitals (<see cref="AttributeKind.Vital"/>).</summary>
        public int Maximum { get; }

        public bool Equals(StatValue other) => Value == other.Value && Maximum == other.Maximum;
        public override bool Equals(object obj) => obj is StatValue other && Equals(other);
        public override int GetHashCode() => (Value * 397) ^ Maximum;
        public override string ToString() => Maximum != 0 ? $"{Value}/{Maximum}" : Value.ToString();
    }

    /// <summary>The modifier operations of core.json as pure functions (integer maths truncates toward zero).</summary>
    public static class ModifierMath
    {
        public static StatValue Apply(StatValue s, ModifierOp op, int v, bool hasMaximum)
        {
            int value = s.Value, max = s.Maximum;
            if (!hasMaximum)
                return op == ModifierOp.SetVal ? new StatValue(v) : op == ModifierOp.AddToVal ? new StatValue(value + v) : s;
            switch (op)
            {
                case ModifierOp.AddToVal: value += v; break;
                case ModifierOp.AddToMax: max += v; value += v; break;
                case ModifierOp.AddPercentageToVal: value += max * v / 100; break;
                case ModifierOp.AddPercentageToMax:
                {
                    int d = max * v / 100;
                    max += d;
                    value += d;
                    break;
                }
                case ModifierOp.SetVal: value = v; break;
                case ModifierOp.SetMax: value += Math.Max(0, v - max); max = v; break;
                case ModifierOp.SetValToPercentage: value = max * v / 100; break;
                case ModifierOp.SetMaxToPercentage:
                {
                    int newMax = max * v / 100;
                    value += Math.Max(0, newMax - max);
                    max = newMax;
                    break;
                }
                case ModifierOp.SetValToMax: value = max; break;
            }
            return new StatValue(Clamp(value, max), max);
        }

        /// <summary>A vital's value stays within [0, maximum].</summary>
        public static int Clamp(int value, int maximum) => value < 0 ? 0 : value > maximum ? maximum : value;
    }
}
