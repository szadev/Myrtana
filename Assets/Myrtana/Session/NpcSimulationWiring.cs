using Myrtana.Core;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// Creates the NPC routine simulation for a session and connects it to the story module, which it must not know
    /// directly: an NPC in a conversation stops its routine until the conversation ends. The session places the NPCs at their
    /// routine start once the world sectors are known (<see cref="NpcSimulation.PlaceAtRoutineStart"/>, fix-67).
    /// </summary>
    public static class NpcSimulationWiring
    {
        public static NpcSimulation Create(IContentDatabase content, IGameClock clock, IRandom random, IEventBus events)
        {
            var simulation = new NpcSimulation(content.All<NpcDef>(), RoutinePointCatalog.From(content), RoutineSchedule.From(content),
                                               clock, random);
            events.Subscribe<DialogStarted>(e => simulation.SetTalking(e.Partner, true));
            events.Subscribe<DialogEnded>(e => simulation.SetTalking(e.Partner, false));
            return simulation;
        }
    }
}
