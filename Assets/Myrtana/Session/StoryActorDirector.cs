using System;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// The story's requests to actors (<see cref="IActorDirector"/>) carried out by the session's services: fights by the
    /// NPC combat system, routines by the NPC simulation, alignments on the actor state (read by the attitudes), healing
    /// by the vitals and the hero's sleep by <see cref="HeroSleep"/>.
    /// </summary>
    public sealed class StoryActorDirector : IActorDirector
    {
        readonly ActorRegistry _actors;
        readonly NpcCombatSystem _combat;
        readonly NpcSimulation _npcs;
        readonly ActorVitals _vitals;
        readonly Func<HeroSleep> _sleep;

        public StoryActorDirector(ActorRegistry actors, NpcCombatSystem combat, NpcSimulation npcs, ActorVitals vitals, Func<HeroSleep> sleep)
        {
            _actors = actors ?? throw new ArgumentNullException(nameof(actors));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
        }

        /// <summary>A boss always fights for the revolution (G3 Attack: "Revolution, or E1 IsBoss").</summary>
        public bool Attack(ActorId attacker, ActorId target, AttackReason reason)
        {
            if (_actors.TryGet(attacker, out var state) && CombatWiring.CombatDefOf(state.Definition).Boss) reason = AttackReason.Revolution;
            return _combat.Engage(attacker, target, reason);
        }

        /// <summary>
        /// G3 SetRoutine. An entity name several NPCs share ("Slave_05", imported as <c>Slave_05#&lt;guid&gt;</c>) switches
        /// the instances that have the routine, or all of them when none has it.
        /// </summary>
        public bool SetRoutine(ActorId npc, string routine)
        {
            if (_npcs.SetRoutine(npc, routine)) return true;
            var instances = InstancesOf(npc);
            if (instances.Length == 0) return false;
            var withRoutine = instances.Where(a => a.Definition.RoutineNamed(routine) != null).ToArray();
            foreach (var a in withRoutine.Length > 0 ? withRoutine : instances) _npcs.SetRoutine(a.Id, routine);
            return true;
        }

        /// <summary>G3 FullStop: the NPC (or every instance of a shared name) drops its current move.</summary>
        public bool FullStop(ActorId npc)
        {
            if (_npcs.FullStop(npc)) return true;
            var instances = InstancesOf(npc);
            foreach (var a in instances) _npcs.FullStop(a.Id);
            return instances.Length > 0;
        }

        NpcAgent[] InstancesOf(ActorId npc)
        {
            if (npc.IsEmpty || npc.IsPlayer) return Array.Empty<NpcAgent>();
            string prefix = npc.Name + "#";
            return _npcs.Agents.Where(a => a.Id.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        }

        public void SetAlignment(ActorId npc, string alignment)
        {
            if (_actors.TryGet(npc, out var state)) state.ChangeAlignment(alignment);
        }

        public void HealFully(ActorId actor) => _vitals.RestoreFully(actor);

        public void PlayerSleepsUntil(int hour) => _sleep().SleepUntil(hour);
    }

    /// <summary>Save section "actors.alignment": the political alignments the story changed (G3 SetPAL).</summary>
    public sealed class ActorAlignmentSaveSection : ISaveParticipant
    {
        static readonly Func<ActorState, bool> Changed = a => a.AlignmentChanged;
        readonly ActorRegistry _actors;
        readonly System.Collections.Generic.List<ActorState> _changed = new System.Collections.Generic.List<ActorState>();

        public ActorAlignmentSaveSection(ActorRegistry actors) => _actors = actors;

        public string Section => "actors.alignment";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            _actors.CollectSorted(Changed, _changed);
            w.WriteInt32(_changed.Count);
            foreach (var a in _changed)
            {
                w.WriteString(a.Id.Name);
                w.WriteString(a.Alignment ?? "");
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var id = new ActorId(r.ReadString());
                string alignment = r.ReadString();
                if (_actors.TryGet(id, out var state)) state.ChangeAlignment(alignment.Length == 0 ? null : alignment);
            }
        }
    }
}
