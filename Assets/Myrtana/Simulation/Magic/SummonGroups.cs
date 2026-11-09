using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Summoned creatures and their summoners (G3 party member type Summoned; Docs/combat.md §12). A summon spell calls
    /// the creatures of its <see cref="SummonSpec"/> (Army of Darkness: N skeletons and N zombies by the caster's INT);
    /// each caster leads at most one group, so a new summon dismisses the previous one. The creatures fight on their
    /// summoner's side. They have no timer in G3; they end when they die, when a new group replaces them, when one of the
    /// player's strays more than 40 m from him (CP leash), when an NPC's summon finds no player within 20 m to attack, or
    /// when their summoner dies or its fight ends [interp: G3 keeps an NPC's summons until they die]. Pure; the world
    /// spawns and removes the bodies.
    /// </summary>
    public sealed class SummonGroups
    {
        const string SpawnTemplate = "*";

        readonly SummonRules _rules;
        readonly Dictionary<ActorId, List<ActorId>> _groups = new Dictionary<ActorId, List<ActorId>>();
        readonly Dictionary<ActorId, ActorId> _leaders = new Dictionary<ActorId, ActorId>();

        public SummonGroups(SummonRules rules) => _rules = rules ?? new SummonRules(0, 0, null);

        public SummonRules Rules => _rules;

        /// <summary>
        /// The creature templates a summon spell calls, one entry per creature; empty when the spell summons nothing or
        /// this caster may not cast it (Summon Monster is Runak's alone).
        /// </summary>
        public IReadOnlyList<string> Plan(SpellDef spell, int casterIntelligence, string casterName)
        {
            var spec = spell != null ? _rules.For(spell.Effect) : null;
            if (spec == null) return Array.Empty<string>();
            if (spec.OnlyCaster != null && !string.Equals(spec.OnlyCaster, casterName, StringComparison.Ordinal)) return Array.Empty<string>();
            int count = spec.CountFor(casterIntelligence);
            var templates = new List<string>();
            foreach (var t in spec.Templates)
            {
                string template = t == SpawnTemplate ? spell.Spawn : t;
                if (string.IsNullOrEmpty(template)) continue;
                for (int i = 0; i < count; i++) templates.Add(template);
            }
            return templates;
        }

        public bool IsSummoned(ActorId actor) => _leaders.ContainsKey(actor);

        /// <summary>The summoner of a creature (empty when it is not summoned).</summary>
        public ActorId LeaderOf(ActorId summon) => _leaders.TryGetValue(summon, out var leader) ? leader : default;

        public IReadOnlyList<ActorId> MembersOf(ActorId leader) =>
            _groups.TryGetValue(leader, out var members) ? members : (IReadOnlyList<ActorId>)Array.Empty<ActorId>();

        /// <summary>Whether a caster has living summons (the summoner bosses summon again only when they have none).</summary>
        public bool HasSummons(ActorId leader) => MembersOf(leader).Count > 0;

        /// <summary>Ends a caster's group (before a new summon, or when the caster is gone); returns the creatures that vanish.</summary>
        public IReadOnlyList<ActorId> Dismiss(ActorId leader)
        {
            if (!_groups.TryGetValue(leader, out var members)) return Array.Empty<ActorId>();
            _groups.Remove(leader);
            foreach (var m in members) _leaders.Remove(m);
            return members;
        }

        /// <summary>A spawned creature joins its summoner's group.</summary>
        public void Join(ActorId leader, ActorId summon)
        {
            if (leader.IsEmpty || summon.IsEmpty || leader == summon) return;
            Remove(summon);
            if (!_groups.TryGetValue(leader, out var members)) _groups[leader] = members = new List<ActorId>();
            members.Add(summon);
            _leaders[summon] = leader;
        }

        /// <summary>A creature is gone (died, removed).</summary>
        public void Remove(ActorId summon)
        {
            if (!_leaders.TryGetValue(summon, out var leader)) return;
            _leaders.Remove(summon);
            if (_groups.TryGetValue(leader, out var members))
            {
                members.Remove(summon);
                if (members.Count == 0) _groups.Remove(leader);
            }
        }

        /// <summary>The player's creatures farther from him than the leash (cm); they die (CP 1.75).</summary>
        public IReadOnlyList<ActorId> BeyondLeash(Func<ActorId, float> distanceToPlayer)
        {
            if (_rules.LeashDistance <= 0 || !_groups.TryGetValue(ActorId.Player, out var members)) return Array.Empty<ActorId>();
            return members.Where(m => distanceToPlayer(m) > _rules.LeashDistance).ToList();
        }

        /// <summary>An NPC's new creature attacks the player when he is within reach (cm); otherwise it vanishes at once.</summary>
        public bool NpcSummonAttacks(float distanceToPlayer) => distanceToPlayer <= _rules.NpcSummonReach;

        /// <summary>Everybody's groups (for the presentation after a load).</summary>
        /// <summary>No summoner has a group (the per-frame checks skip <see cref="Groups"/>, which allocates, perf-9).</summary>
        public bool IsEmpty => _groups.Count == 0;

        public IEnumerable<KeyValuePair<ActorId, IReadOnlyList<ActorId>>> Groups =>
            _groups.Select(g => new KeyValuePair<ActorId, IReadOnlyList<ActorId>>(g.Key, g.Value));
    }
}
