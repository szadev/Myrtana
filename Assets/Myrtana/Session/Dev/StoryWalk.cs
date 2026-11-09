#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Linq;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// What a <see cref="WalkRule"/> needs: a tree of facts (all / any / one fact / always / never). Negated story
    /// conditions are not modelled (the walk over-approximates: "could this ever become true").
    /// </summary>
    public sealed class WalkNeed
    {
        public enum NeedKind { True, False, Fact, All, Any }

        public static readonly WalkNeed Always = new WalkNeed(NeedKind.True, null, null);
        public static readonly WalkNeed Never = new WalkNeed(NeedKind.False, null, null);

        WalkNeed(NeedKind kind, string fact, IReadOnlyList<WalkNeed> parts)
        {
            Kind = kind;
            Fact = fact;
            Parts = parts ?? Array.Empty<WalkNeed>();
        }

        public NeedKind Kind { get; }
        public string Fact { get; }
        public IReadOnlyList<WalkNeed> Parts { get; }

        public static WalkNeed Of(string fact) => fact == null ? Always : new WalkNeed(NeedKind.Fact, fact, null);

        public static WalkNeed All(IEnumerable<WalkNeed> parts)
        {
            var list = parts.Where(p => p != null && p.Kind != NeedKind.True).ToList();
            if (list.Any(p => p.Kind == NeedKind.False)) return Never;
            return list.Count == 0 ? Always : list.Count == 1 ? list[0] : new WalkNeed(NeedKind.All, null, list);
        }

        public static WalkNeed Any(IEnumerable<WalkNeed> parts)
        {
            var list = parts.Where(p => p != null && p.Kind != NeedKind.False).ToList();
            if (list.Any(p => p.Kind == NeedKind.True)) return Always;
            return list.Count == 0 ? Never : list.Count == 1 ? list[0] : new WalkNeed(NeedKind.Any, null, list);
        }

        public bool Holds(Func<string, bool> has)
        {
            switch (Kind)
            {
                case NeedKind.True: return true;
                case NeedKind.False: return false;
                case NeedKind.Fact: return has(Fact);
                case NeedKind.All: return Parts.All(p => p.Holds(has));
                default: return Parts.Any(p => p.Holds(has));
            }
        }

        /// <summary>The leaf facts that keep this need from holding (for Any: the alternative missing the fewest).</summary>
        public IEnumerable<string> Missing(Func<string, bool> has)
        {
            switch (Kind)
            {
                case NeedKind.True: return Enumerable.Empty<string>();
                case NeedKind.False: return new[] { "never" };
                case NeedKind.Fact: return has(Fact) ? Enumerable.Empty<string>() : new[] { Fact };
                case NeedKind.All: return Parts.SelectMany(p => p.Missing(has)).Distinct().ToList();
                default:
                    if (Holds(has)) return Enumerable.Empty<string>();
                    return Parts.Select(p => p.Missing(has).ToList()).OrderBy(m => m.Count).First();
            }
        }

        /// <summary>Every fact named anywhere in the tree.</summary>
        public IEnumerable<string> Facts() => Kind == NeedKind.Fact ? new[] { Fact } : Parts.SelectMany(p => p.Facts());

        public override string ToString()
        {
            switch (Kind)
            {
                case NeedKind.True: return "-";
                case NeedKind.False: return "never";
                case NeedKind.Fact: return Fact;
                case NeedKind.All: return string.Join(" & ", Parts.Select(p => p.Kind == NeedKind.Any ? "(" + p + ")" : p.ToString()));
                default: return string.Join(" | ", Parts.Select(p => p.ToString()));
            }
        }
    }

    /// <summary>
    /// One way the story state can change: a dialog entry (its owner present, its condition true → its commands), an item
    /// source, an objective that the runtime counts, a sector switch. Facts are strings built by <see cref="WalkFacts"/>.
    /// </summary>
    public sealed class WalkRule
    {
        public WalkRule(string id, string kind, string owner, WalkNeed need, IEnumerable<string> effects, IEnumerable<string> notes = null)
        {
            Id = id;
            Kind = kind;
            Owner = owner;
            Need = need ?? WalkNeed.Always;
            Effects = (effects ?? Enumerable.Empty<string>()).Distinct().ToList();
            Notes = (notes ?? Enumerable.Empty<string>()).ToList();
        }

        public string Id { get; }
        /// <summary>"entry", "source", "objective", "sector", "start".</summary>
        public string Kind { get; }
        public string Owner { get; }
        public WalkNeed Need { get; }
        public IReadOnlyList<string> Effects { get; }
        /// <summary>Things a static walk cannot prove (NPC must stand at a place, an enclave must be freed in combat …).</summary>
        public IReadOnlyList<string> Notes { get; }

        public override string ToString() => $"{Kind} {Id}" + (Owner != null ? $" [{Owner}]" : "");
    }

    /// <summary>Fact names of the story walk.</summary>
    public static class WalkFacts
    {
        public static string Flag(string name) => "flag:" + name;
        public static string Quest(string quest, string status) => "quest:" + quest + "=" + status;
        public static string Item(string item) => "item:" + item;
        public static string Present(string actor) => "npc:" + actor;
        public static string Sector(string sector) => "sector:" + sector;
        /// <summary>The player handed this item to that actor (a dialog GiveItems from the player).</summary>
        public static string Gave(string actor, string item) => "gave:" + actor + ":" + item;
        /// <summary>One target of a several-target Report quest has reported (QuestService.Count).</summary>
        public static string Reported(string quest, string target) => "report:" + quest + ":" + target;

        public static readonly string[] FinishedStatuses = { "Success", "Failed", "Obsolete", "Cancelled", "Won" };
    }

    /// <summary>
    /// The offline main-story walk (G3Cli <c>story walk</c>, Docs/story-walk.md): a monotone fixpoint over
    /// <see cref="WalkRule"/>s from the initial facts. A fact's depth is the number of rule rounds needed to reach it, so
    /// depth orders the story; a rule that never fires names the facts it is missing. Pure; no runtime involved.
    /// </summary>
    public sealed class StoryWalk
    {
        readonly List<WalkRule> _rules;
        readonly Dictionary<string, int> _depth = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, WalkRule> _by = new Dictionary<string, WalkRule>(StringComparer.Ordinal);
        readonly Dictionary<WalkRule, int> _fired = new Dictionary<WalkRule, int>();

        public StoryWalk(IEnumerable<WalkRule> rules, IEnumerable<string> initial)
        {
            _rules = rules.ToList();
            foreach (var f in initial ?? Enumerable.Empty<string>()) _depth[f] = 0;
        }

        public IReadOnlyList<WalkRule> Rules => _rules;
        public int Rounds { get; private set; }

        /// <summary>Fires rules until nothing new is reached (each rule at most once).</summary>
        public StoryWalk Run(int maxRounds = 200)
        {
            for (int round = 1; round <= maxRounds; round++)
            {
                var now = _rules.Where(r => !_fired.ContainsKey(r) && r.Need.Holds(Has)).ToList();
                if (now.Count == 0) break;
                Rounds = round;
                foreach (var r in now)
                {
                    _fired[r] = round;
                    foreach (var e in r.Effects)
                        if (!_depth.ContainsKey(e))
                        {
                            _depth[e] = round;
                            _by[e] = r;
                        }
                }
            }
            return this;
        }

        public bool Has(string fact) => fact != null && _depth.ContainsKey(fact);

        /// <summary>Round in which the fact was first reached (0 = initial), -1 = never.</summary>
        public int DepthOf(string fact) => fact != null && _depth.TryGetValue(fact, out int d) ? d : -1;

        /// <summary>The rule that first produced the fact (null for initial or unreached facts).</summary>
        public WalkRule ProducerOf(string fact) => fact != null && _by.TryGetValue(fact, out var r) ? r : null;

        public bool Fired(WalkRule rule) => _fired.ContainsKey(rule);

        public int RoundOf(WalkRule rule) => _fired.TryGetValue(rule, out int r) ? r : -1;

        public IReadOnlyList<string> Missing(WalkRule rule) => rule.Need.Missing(Has).ToList();

        /// <summary>Rules that produce the fact (whether they fired or not).</summary>
        public IEnumerable<WalkRule> ProducersOf(string fact) => _rules.Where(r => r.Effects.Contains(fact));

        /// <summary>
        /// The chain of rules leading to a fact: its producer, then the producers of the facts that producer needed
        /// (first producer of each, depth-first, each rule once), deepest first.
        /// </summary>
        public IReadOnlyList<WalkRule> ChainTo(string fact)
        {
            var order = new List<WalkRule>();
            var seen = new HashSet<WalkRule>();
            void Visit(string f, int guard)
            {
                var r = ProducerOf(f);
                if (r == null || guard > 64 || !seen.Add(r)) return;
                foreach (var need in r.Need.Facts().Where(Has).OrderBy(DepthOf)) Visit(need, guard + 1);
                order.Add(r);
            }
            Visit(fact, 0);
            return order;
        }
    }
}
#endif
