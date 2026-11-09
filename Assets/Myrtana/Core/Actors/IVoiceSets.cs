namespace Myrtana.Core
{
    /// <summary>The voice set of an actor ("Orc_Warrior_Hard"), whose shared lines (SVMs) it speaks.</summary>
    public interface IVoiceSets
    {
        /// <summary>Null when the actor is unknown or has no voice.</summary>
        string VoiceSetOf(ActorId actor);
    }
}
