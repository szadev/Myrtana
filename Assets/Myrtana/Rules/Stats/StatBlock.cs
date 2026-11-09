using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// The attributes of one character (core.json "attributeModel"): a permanent <see cref="StatValue"/> per attribute
    /// plus a temporary bonus from equipment. An attribute's shown value is permanent value + bonus; a vital's bonus
    /// raises its maximum and its value is capped to that maximum. Unknown attribute ids read as 0.
    /// </summary>
    public sealed class StatBlock
    {
        readonly Func<string, bool> _hasMaximum;
        readonly Dictionary<string, StatValue> _permanent = new Dictionary<string, StatValue>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _bonus = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <param name="hasMaximum">Whether an attribute id is a vital (from the content's <see cref="AttributeDef"/>s).</param>
        public StatBlock(Func<string, bool> hasMaximum) => _hasMaximum = hasMaximum;

        /// <summary>Raised with the attribute id after any change.</summary>
        public event Action<string> Changed;

        public IEnumerable<string> Ids => _permanent.Keys;

        public bool HasMaximum(string id) => _hasMaximum(id);

        public StatValue Permanent(string id) => _permanent.TryGetValue(id, out var v) ? v : default;

        public int Bonus(string id) => _bonus.TryGetValue(id, out var b) ? b : 0;

        public int Maximum(string id) => Permanent(id).Maximum + (HasMaximum(id) ? Bonus(id) : 0);

        public int Value(string id)
        {
            var p = Permanent(id);
            return HasMaximum(id) ? ModifierMath.Clamp(p.Value, Maximum(id)) : p.Value + Bonus(id);
        }

        public void Set(string id, StatValue value)
        {
            _permanent[id] = value;
            Changed?.Invoke(id);
        }

        /// <summary>A permanent change (potion, teaching, quest reward).</summary>
        public void Apply(string id, ModifierOp op, int amount)
        {
            bool vital = HasMaximum(id);
            var current = Permanent(id);
            // Percentages and caps of a vital use the maximum including equipment.
            var effective = vital ? new StatValue(Value(id), Maximum(id)) : current;
            var changed = ModifierMath.Apply(effective, op, amount, vital);
            int bonus = vital ? Bonus(id) : 0;
            Set(id, new StatValue(changed.Value, changed.Maximum - bonus));
        }

        /// <summary>Replaces the temporary (equipment) bonus of an attribute.</summary>
        public void SetBonus(string id, int amount)
        {
            if (amount == 0) _bonus.Remove(id);
            else _bonus[id] = amount;
            Changed?.Invoke(id);
        }

        public void ClearBonuses()
        {
            var ids = new List<string>(_bonus.Keys);
            _bonus.Clear();
            foreach (var id in ids) Changed?.Invoke(id);
        }
    }
}
