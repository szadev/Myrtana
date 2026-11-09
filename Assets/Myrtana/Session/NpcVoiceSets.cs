using Myrtana.Core;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>Voice sets from the NPC definitions (for the story's shared lines).</summary>
    public sealed class NpcVoiceSets : IVoiceSets
    {
        readonly IContentDatabase _content;

        public NpcVoiceSets(IContentDatabase content) => _content = content;

        public string VoiceSetOf(ActorId actor) => _content.TryGet(actor.Name, out NpcDef npc) ? npc.VoiceSet : null;
    }
}
