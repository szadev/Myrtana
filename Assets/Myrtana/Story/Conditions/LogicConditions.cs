using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Myrtana.Story
{
    /// <summary>True when every child is true (and for an empty list).</summary>
    public sealed class AllOf : Condition
    {
        [JsonConstructor]
        public AllOf(IReadOnlyList<Condition> conditions) => Conditions = conditions ?? Array.Empty<Condition>();

        public IReadOnlyList<Condition> Conditions { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            foreach (var c in Conditions)
                if (!c.Evaluate(context)) return false;
            return true;
        }
    }

    /// <summary>True when at least one child is true.</summary>
    public sealed class AnyOf : Condition
    {
        [JsonConstructor]
        public AnyOf(IReadOnlyList<Condition> conditions) => Conditions = conditions ?? Array.Empty<Condition>();

        public IReadOnlyList<Condition> Conditions { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            foreach (var c in Conditions)
                if (c.Evaluate(context)) return true;
            return false;
        }
    }

    public sealed class Not : Condition
    {
        [JsonConstructor]
        public Not(Condition condition) => Condition = condition;

        public Condition Condition { get; }

        public override bool Evaluate(in ConditionContext context) => !Condition.Evaluate(context);
    }

    /// <summary>Always false: content that exists in the data but is never offered.</summary>
    public sealed class Never : Condition
    {
        public override bool Evaluate(in ConditionContext context) => false;
    }

    /// <summary>
    /// A condition the runtime cannot evaluate yet (an importer met a feature of a later slice). It is false, so the
    /// guarded content stays hidden, and it is logged (the session's log reports each message once).
    /// </summary>
    public sealed class Unsupported : Condition
    {
        [JsonConstructor]
        public Unsupported(string feature) => Feature = feature;

        public string Feature { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            context.Log?.Info($"Condition '{Feature}' is not supported yet; treated as false.");
            return false;
        }
    }
}
