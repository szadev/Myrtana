using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// Robbing a helpless NPC with Use (<see cref="PickpocketRules.UseOnHelpless"/>). G3 (CP 1.75 PS_Normal_Loop): an NPC
    /// asleep in its bed or on the ground, or held by the sleep spell (AIMode 7), is helpless; a sneaking hero with
    /// Perk_MasterThief robs a humanoid without a crime, otherwise Use talks to it. [owner] fix-113: an NPC held by the sleep
    /// spell cannot be talked to; Use opens a humanoid's loot page anyway and every stack taken is a theft (counted against
    /// the zone, witnesses react; the victim, asleep, perceives nothing and so does not remember it on waking).
    /// <see cref="Rob"/> rolls the victim's plunder once (CleanUpPlunderInv); the presentation then opens the loot page on
    /// it, or the hero says "NothingToGet".
    /// </summary>
    public sealed class SleeperTheft
    {
        readonly GameSession _session;
        /// <summary>Robbed spell sleepers whose stacks are a theft while they sleep on ([owner], not the G3 master-thief path).</summary>
        readonly HashSet<ActorId> _theftVictims = new HashSet<ActorId>();

        internal SleeperTheft(GameSession session)
        {
            _session = session;
            session.Events.Subscribe<ItemsTransferred>(OnTransferred);
            session.Events.Subscribe<ActorSpellbound>(e => { if (!e.On && e.Hold == SpellHold.Asleep) _theftVictims.Remove(e.Actor); });
        }

        /// <summary>Asleep in a bed / on the ground (routine) or by the sleep spell; not knocked out or dead (those are bodies).</summary>
        public bool IsHelpless(ActorId npc)
        {
            if (npc.IsEmpty || npc.IsPlayer) return false;
            var vitals = _session.Combat.Vitals;
            if (vitals.IsDown(npc) || vitals.IsDead(npc)) return false;
            if (SpellAsleep(npc)) return true;
            return _session.Npcs.TryGet(npc, out var agent) && _session.Npcs.Sleeps(agent);
        }

        /// <summary>Held by the sleep spell (alive, not down).</summary>
        public bool IsSpellbound(ActorId npc) =>
            SpellAsleep(npc) && IsHelpless(npc);

        bool SpellAsleep(ActorId npc) => _session.SpellEffects?.World.IsAsleep(npc) == true;

        /// <summary>[owner] fix-113: an NPC held by the sleep spell is not talked to (Use robs it or does nothing).</summary>
        public bool TalkBlocked(ActorId npc) => IsSpellbound(npc);

        public bool IsHumanoid(ActorId npc) =>
            _session.Actors.TryGet(npc, out var actor) && CombatWiring.CombatDefOf(actor).Humanoid;

        /// <summary>What Use does on this NPC.</summary>
        public HelplessUse UseOn(ActorId npc, bool heroSneaking) =>
            PickpocketRules.UseOnHelpless(IsHelpless(npc), IsSpellbound(npc), heroSneaking,
                                          _session.Hero.Knows(PickpocketRules.MasterThiefPerk), IsHumanoid(npc));

        public bool CanRob(ActorId npc, bool heroSneaking)
        {
            var use = UseOn(npc, heroSneaking);
            return use == HelplessUse.Rob || use == HelplessUse.RobAsTheft;
        }

        /// <summary>Whether what is taken from this sleeper now counts as theft.</summary>
        public bool IsTheftVictim(ActorId npc) => _theftVictims.Contains(npc);

        /// <summary>
        /// Rolls the plunder once; true when there is anything to take. Remembers whether this robbery is a theft
        /// (<see cref="HelplessUse.RobAsTheft"/>); false and nothing rolled when the hero may not rob it.
        /// </summary>
        public bool Rob(ActorId npc, bool heroSneaking)
        {
            var use = UseOn(npc, heroSneaking);
            if (use != HelplessUse.Rob && use != HelplessUse.RobAsTheft) return false;
            if (use == HelplessUse.RobAsTheft) _theftVictims.Add(npc);
            else _theftVictims.Remove(npc);
            var loot = _session.Items.NpcLoot;
            loot.GeneratePlunder(npc);
            return loot.HasLoot(npc);
        }

        void OnTransferred(ItemsTransferred e)
        {
            if (!e.To.IsPlayer || !_theftVictims.Contains(e.From)) return;
            if (!IsSpellbound(e.From)) { _theftVictims.Remove(e.From); return; } // woke up / knocked out: not this robbery any more
            var crime = _session.Crime;
            if (!crime.World.TryGetPose(e.From, out var where) && !crime.World.TryGetPose(ActorId.Player, out where)) return;
            crime.PlayerTookItem(e.Item, where);
        }
    }
}
