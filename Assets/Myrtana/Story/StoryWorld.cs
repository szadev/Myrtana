using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// What the story module uses from other modules: actor facts, health and voices (simulation), reputation, the
    /// hero's progression and fame, items, trade and teaching (rules), and the actors in the world (director). Wired by
    /// the session; tests pass fakes.
    /// </summary>
    public sealed class StoryServices
    {
        public StoryServices(IActorFacts actors, IActorVitals vitals, IVoiceSets voiceSets, IReputation reputation,
                             ProgressionService progression, FameLedger fame, ItemServices items, ICrimeTalk crime = null,
                             StoryChapter chapter = null, IActorDirector director = null, IRandom random = null,
                             PlayerParty party = null, IWorldPositions positions = null,
                             IWorldDirector world = null, IActorTraits traits = null, IPickpocket pickpocket = null)
        {
            Actors = actors;
            Vitals = vitals;
            VoiceSets = voiceSets;
            Reputation = reputation;
            Progression = progression;
            Fame = fame;
            Items = items;
            Crime = crime ?? NoCrimeTalk.Instance;
            Chapter = chapter ?? new StoryChapter();
            Director = director ?? new NoActorDirector();
            Random = random ?? new SeededRandom(0);
            Party = party ?? (items != null ? new PlayerParty(items.Roles) : null);
            Positions = positions ?? NoWorldPositions.Instance;
            World = world ?? new NoWorldDirector();
            Traits = traits ?? DefaultActorTraits.Instance;
            Pickpocket = pickpocket ?? NoPickpocket.Instance;
        }

        public IActorFacts Actors { get; }
        public IActorVitals Vitals { get; }
        public IVoiceSets VoiceSets { get; }
        public IReputation Reputation { get; }
        public ProgressionService Progression { get; }
        public FameLedger Fame { get; }
        public ItemServices Items { get; }
        /// <summary>Guard news, bribes and excuses (crime slice).</summary>
        public ICrimeTalk Crime { get; }
        public StoryChapter Chapter { get; }
        /// <summary>Fights, routines, alignments, healing and sleep of actors in the world.</summary>
        public IActorDirector Director { get; }
        /// <summary>Random choices of the story (variants of shared lines).</summary>
        public IRandom Random { get; }
        /// <summary>The hero's party: companions, mob members, freed slaves and the guide (Docs/party.md).</summary>
        public PlayerParty Party { get; }
        /// <summary>Where actors and named places are (owner-near conditions, delivery tests).</summary>
        public IWorldPositions Positions { get; }
        /// <summary>World sectors, doors, enclave flags, NPC classes and gate guards (Docs/story-coverage.md).</summary>
        public IWorldDirector World { get; }
        /// <summary>Humanoid, can fight (heal and animal conditions).</summary>
        public IActorTraits Traits { get; }
        /// <summary>Pickpocketing (condition type PickPocket, command Pickpocket).</summary>
        public IPickpocket Pickpocket { get; }
    }

    /// <summary>The story state and the services it works with, bundled so story code takes one dependency.</summary>
    public sealed class StoryWorld
    {
        public StoryWorld(IGameEventFlags flags, QuestService quests, DialogMemory dialogMemory, QuestLog questLog,
                          StoryServices services, IEventBus events, IGameLog log)
        {
            Flags = flags;
            Quests = quests;
            DialogMemory = dialogMemory;
            QuestLog = questLog;
            Services = services;
            Events = events;
            Log = log;
        }

        public IGameEventFlags Flags { get; }
        public QuestService Quests { get; }
        public DialogMemory DialogMemory { get; }
        public QuestLog QuestLog { get; }
        public StoryServices Services { get; }
        public IEventBus Events { get; }
        public IGameLog Log { get; }

        public IActorFacts Actors => Services.Actors;
        public IReputation Reputation => Services.Reputation;

        public ConditionContext ConditionsFor(ActorId partner) => new ConditionContext(this, partner);
    }
}
