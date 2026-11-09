using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// What story commands ask of the actors in the world: fights, routines, political alignment, healing and the hero's
    /// sleep. Implemented by the session over the combat, NPC simulation, attitude and time services; tests pass fakes.
    /// </summary>
    public interface IActorDirector
    {
        /// <summary>The attacker starts a fight against the target (G3 info command Attack); false if it cannot fight.</summary>
        bool Attack(ActorId attacker, ActorId target, AttackReason reason);

        /// <summary>The NPC switches to the named routine (G3 SetRoutine); false for an unknown NPC.</summary>
        bool SetRoutine(ActorId npc, string routine);

        /// <summary>G3 FullStop: the NPC drops what it does right now (a walk) and goes on with its routine; false for an unknown NPC.</summary>
        bool FullStop(ActorId npc);

        /// <summary>The NPC's political alignment changes ("Reb", "Nom", null = none; G3 SetPAL).</summary>
        void SetAlignment(ActorId npc, string alignment);

        /// <summary>The actor's hit points are restored to the maximum (G3 Heal).</summary>
        void HealFully(ActorId actor);

        /// <summary>The hero sleeps until the next time the clock shows <paramref name="hour"/> and wakes rested (G3 Sleep).</summary>
        void PlayerSleepsUntil(int hour);
    }

    /// <summary>A world without actors to direct (tools, isolated tests): every request is logged and ignored.</summary>
    public sealed class NoActorDirector : IActorDirector
    {
        readonly IGameLog _log;

        public NoActorDirector(IGameLog log = null) => _log = log;

        public bool Attack(ActorId attacker, ActorId target, AttackReason reason) => Ignored($"{attacker} attacks {target}");
        public bool SetRoutine(ActorId npc, string routine) => Ignored($"{npc} routine {routine}");
        public bool FullStop(ActorId npc) => Ignored($"{npc} full stop");
        public void SetAlignment(ActorId npc, string alignment) => Ignored($"{npc} alignment {alignment}");
        public void HealFully(ActorId actor) => Ignored($"{actor} healed");
        public void PlayerSleepsUntil(int hour) => Ignored($"hero sleeps until {hour}");

        bool Ignored(string what)
        {
            _log?.Info($"No actor director: '{what}' ignored.");
            return false;
        }
    }
}
