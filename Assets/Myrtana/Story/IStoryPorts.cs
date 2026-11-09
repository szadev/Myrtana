using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>Body facts of actors the story asks about (G3 IsHumanoid, CanAttack). Implemented by the session.</summary>
    public interface IActorTraits
    {
        bool IsHumanoid(ActorId actor);
        /// <summary>G3 CanAttack: the actor can fight at all (livestock and critters cannot).</summary>
        bool CanFight(ActorId actor);
    }

    /// <summary>Without a simulation every actor is a fighting humanoid.</summary>
    public sealed class DefaultActorTraits : IActorTraits
    {
        public static readonly DefaultActorTraits Instance = new DefaultActorTraits();
        public bool IsHumanoid(ActorId actor) => !actor.IsEmpty;
        public bool CanFight(ActorId actor) => !actor.IsEmpty;
    }

    /// <summary>Pickpocketing in a dialog (G3 condition type PickPocket and the command Pickpocket; Rules/Crime/Pickpocket.cs).</summary>
    public interface IPickpocket
    {
        /// <summary>The hero knows the perk, the victim carries pickpocket loot and was not robbed yet.</summary>
        bool MayPickpocket(ActorId victim);
        /// <summary>One attempt; on success the loot is the hero's and the victim counts as robbed; caught, the victim attacks (G3 AssessTarget, Theft).</summary>
        PickpocketVerdict Pickpocket(ActorId victim);
    }

    /// <summary>Nobody can be pickpocketed.</summary>
    public sealed class NoPickpocket : IPickpocket
    {
        public static readonly NoPickpocket Instance = new NoPickpocket();
        public bool MayPickpocket(ActorId victim) => false;
        public PickpocketVerdict Pickpocket(ActorId victim) => PickpocketVerdict.Impossible;
    }
}
