using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The attack token (G3 combat state Active/Passive, combat_ai.json attackToken): per target only a few fighters may
    /// attack at a time; the others keep their distance. One holder against an NPC; against the player 1/2/3 on
    /// Easy/Medium/Hard with AB or extended AI. A holder is displaced by a fighter that is clearly closer to the target and
    /// not standing next to the holder. Fighters report every tick; a fighter that leaves (fight over, down, dead) frees
    /// its token. The state is derived and rebuilt from the brains after loading.
    /// </summary>
    public sealed class CombatCoordinator
    {
        sealed class Fighter
        {
            public ActorId Target;
            public float Distance, X, Z;
            public bool Active;
        }

        readonly AttackTokenRules _rules;
        readonly RuleOptions _options;
        readonly Dictionary<ActorId, Fighter> _fighters = new Dictionary<ActorId, Fighter>();

        public CombatCoordinator(AttackTokenRules rules, RuleOptions options)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public int MaxHolders(ActorId target) =>
            target.IsPlayer && (_options.AlternativeBalancing || _options.ExtendedAI) ? _rules.MaxHoldersVsPlayer.For(_options.Difficulty) : _rules.MaxHolders;

        /// <summary>
        /// A fighter reports its target, its distance to it and its position, and learns whether it holds the token.
        /// </summary>
        public bool Update(ActorId self, ActorId target, float distance, float x, float z)
        {
            if (!_fighters.TryGetValue(self, out var me)) _fighters[self] = me = new Fighter();
            if (me.Target != target) me.Active = false;
            me.Target = target;
            me.Distance = distance;
            me.X = x;
            me.Z = z;
            if (me.Active) return true;

            var holders = _fighters.Where(f => f.Value.Active && f.Value.Target == target && f.Key != self).ToList();
            if (holders.Count < MaxHolders(target)) return me.Active = true;
            foreach (var holder in holders.OrderByDescending(h => h.Value.Distance))
            {
                var h = holder.Value;
                float apart = (float)Math.Sqrt((h.X - x) * (h.X - x) + (h.Z - z) * (h.Z - z));
                if (h.Distance > distance + _rules.StealIfFartherBy && apart > _rules.StealMinDistance)
                {
                    h.Active = false;
                    return me.Active = true;
                }
            }
            return false;
        }

        /// <summary>Restores a fighter after loading (no checks).</summary>
        public void Restore(ActorId self, ActorId target, bool active) => _fighters[self] = new Fighter { Target = target, Active = active };

        /// <summary>The fighter is out of the fight (ended, fled, down, dead): its token is free.</summary>
        public void Leave(ActorId self) => _fighters.Remove(self);

        public bool Holds(ActorId self) => _fighters.TryGetValue(self, out var f) && f.Active;

        public int HoldersOf(ActorId target) => _fighters.Values.Count(f => f.Active && f.Target == target);

        /// <summary>How many NPCs fight this target (≥ 2 = a group fight).</summary>
        public int FightersOn(ActorId target) => _fighters.Values.Count(f => f.Target == target);

        public bool IsFighting(ActorId self) => _fighters.ContainsKey(self);

        public bool TryGetTarget(ActorId self, out ActorId target)
        {
            target = default;
            if (!_fighters.TryGetValue(self, out var f)) return false;
            target = f.Target;
            return true;
        }
    }
}
