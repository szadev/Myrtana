using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// What a guard or boss says about the player's crimes (G3 _AI_SayGuardNews / _AI_SayBossNews / _AI_Bribe / _AI_Excuse):
    /// the shared (SVM) lines to speak, and whether the dialog's bribe/excuse/provoke choices open.
    /// </summary>
    public readonly struct CrimeSpeech
    {
        public CrimeSpeech(string[] lines, bool offersWayOut = false, bool attacks = false)
        {
            Lines = lines ?? System.Array.Empty<string>();
            OffersWayOut = offersWayOut;
            Attacks = attacks;
        }

        /// <summary>Shared line ids in order ("Halt", "SecondTheft" …).</summary>
        public string[] Lines { get; }
        /// <summary>The news is serious (level ≥ 2): the bribe, excuse and provoke choices are offered.</summary>
        public bool OffersWayOut { get; }
        /// <summary>The NPC attacks the player (bribe refused, excuse failed); the dialog ends.</summary>
        public bool Attacks { get; }
    }

    /// <summary>
    /// The crime side of dialogs (Docs/perception-crime.md §6.2), implemented by the session over the crime ledger and
    /// the NPC combat. The story module only sees this port.
    /// </summary>
    public interface ICrimeTalk
    {
        /// <summary>A guard of an enclave with news the player has not heard yet (G3 HasGuardNews, condition EnclaveCrime).</summary>
        bool HasGuardNews(ActorId guard);
        /// <summary>A boss whose enclave counted crimes it has not commented on (G3 HasBossNews, condition PoliticalCrime).</summary>
        bool HasBossNews(ActorId boss);
        /// <summary>The guard tells what it heard; the news counts as discussed.</summary>
        CrimeSpeech TellGuardNews(ActorId guard);
        /// <summary>The boss comments on its enclave's crime count; the count counts as discussed.</summary>
        CrimeSpeech TellBossNews(ActorId boss);
        /// <summary>The player pays (the gold is given by the dialog): accepted below Known, otherwise the guard attacks.</summary>
        CrimeSpeech Bribe(ActorId guard);
        /// <summary>The player makes excuses (needs Perk_Excuse; a known crime needs luck and the Theft skill).</summary>
        CrimeSpeech Excuse(ActorId guard);
        /// <summary>The NPC's enclave forgets the player's crimes (CP command ResetCrime; fame is kept).</summary>
        void ResetCrime(ActorId member);
    }

    /// <summary>No crime system: nobody has news.</summary>
    public sealed class NoCrimeTalk : ICrimeTalk
    {
        public static readonly NoCrimeTalk Instance = new NoCrimeTalk();
        public bool HasGuardNews(ActorId guard) => false;
        public bool HasBossNews(ActorId boss) => false;
        public CrimeSpeech TellGuardNews(ActorId guard) => default;
        public CrimeSpeech TellBossNews(ActorId boss) => default;
        public CrimeSpeech Bribe(ActorId guard) => default;
        public CrimeSpeech Excuse(ActorId guard) => default;
        public void ResetCrime(ActorId member) { }
    }
}
