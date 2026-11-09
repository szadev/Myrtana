namespace Myrtana.Rules
{
    /// <summary>
    /// How a modifier changes an attribute (gameplay-data/core.json "modifierOperations"). Percentages refer to the
    /// maximum of a vital. Attributes without maximum only support <see cref="AddToVal"/> and <see cref="SetVal"/>.
    /// </summary>
    public enum ModifierOp
    {
        /// <summary>value += v (the only operation of equipment modifiers).</summary>
        AddToVal,
        /// <summary>max += v and value += v.</summary>
        AddToMax,
        /// <summary>value += max·v/100.</summary>
        AddPercentageToVal,
        /// <summary>d = max·v/100; max += d and value += d.</summary>
        AddPercentageToMax,
        /// <summary>value = v.</summary>
        SetVal,
        /// <summary>max = v; value grows by the increase of max (if any).</summary>
        SetMax,
        /// <summary>value = max·v/100.</summary>
        SetValToPercentage,
        /// <summary>max = max·v/100; value grows by the increase (if any).</summary>
        SetMaxToPercentage,
        /// <summary>value = max.</summary>
        SetValToMax,
    }
}
