using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>Content kinds and JSON type names of the story module.</summary>
    public static class StorySchema
    {
        public const string DialogFolder = "dialogs";
        public const string QuestFolder = "quests";
        public const string SharedLinesFolder = "sharedLines";
        public const string TriggerFolder = "storyTriggers";

        public static void Register(ContentSchema schema)
        {
            schema.AddKind<DialogEntryDef>(DialogFolder);
            schema.AddKind<QuestDef>(QuestFolder);
            schema.AddKind<SharedLines>(SharedLinesFolder);
            schema.AddKind<StoryTriggerRules>(TriggerFolder);

            schema.AddFamily<Condition>("type");
            schema.AddMember<Condition, AllOf>("allOf");
            schema.AddMember<Condition, AnyOf>("anyOf");
            schema.AddMember<Condition, Not>("not");
            schema.AddMember<Condition, FlagSet>("flagSet");
            schema.AddMember<Condition, QuestStatusIs>("questStatusIs");
            schema.AddMember<Condition, QuestFinished>("questFinished");
            schema.AddMember<Condition, TalkedToPlayer>("talkedToPlayer");
            schema.AddMember<Condition, Never>("never");
            schema.AddMember<Condition, Unsupported>("unsupported");
            schema.AddMember<Condition, ActorIs>("actorIs");
            schema.AddMember<Condition, HasItems>("hasItems");
            schema.AddMember<Condition, CarriesQuestItems>("carriesQuestItems");
            schema.AddMember<Condition, PartnerIs>("partnerIs");
            schema.AddMember<Condition, HasRole>("hasRole");
            schema.AddMember<Condition, LessonOffered>("lessonOffered");
            schema.AddMember<Condition, HasGuardNews>("hasGuardNews");
            schema.AddMember<Condition, HasBossNews>("hasBossNews");
            schema.AddMember<Condition, QuestOverdue>("questOverdue");
            schema.AddMember<Condition, PartyAllows>("partyAllows");
            schema.AddMember<Condition, InPlayerParty>("inPlayerParty");
            schema.AddMember<Condition, Near>("near");
            schema.AddMember<Condition, GateGuardMayWarn>("gateGuardMayWarn");
            schema.AddMember<Condition, CanHeal>("canHeal");
            schema.AddMember<Condition, CanFight>("canFight");
            schema.AddMember<Condition, DialogEndedHoursAgo>("dialogEndedHoursAgo");
            schema.AddMember<Condition, QuestRepeatDue>("questRepeatDue");
            schema.AddMember<Condition, MayPickpocket>("mayPickpocket");

            schema.AddFamily<DialogCommand>("op");
            schema.AddMember<DialogCommand, Say>("say");
            schema.AddMember<DialogCommand, SetFlag>("setFlag");
            schema.AddMember<DialogCommand, ClearFlag>("clearFlag");
            schema.AddMember<DialogCommand, StartQuest>("startQuest");
            schema.AddMember<DialogCommand, EndDialog>("endDialog");
            schema.AddMember<DialogCommand, LeaveSubmenu>("leaveSubmenu");
            schema.AddMember<DialogCommand, NotSupported>("notSupported");
            schema.AddMember<DialogCommand, SucceedQuest>("succeedQuest");
            schema.AddMember<DialogCommand, CloseQuest>("closeQuest");
            schema.AddMember<DialogCommand, FailQuest>("failQuest");
            schema.AddMember<DialogCommand, ReportToQuest>("reportToQuest");
            schema.AddMember<DialogCommand, DeliverQuestItems>("deliverQuestItems");
            schema.AddMember<DialogCommand, AddQuestLog>("addQuestLog");
            schema.AddMember<DialogCommand, GiveXp>("giveXp");
            schema.AddMember<DialogCommand, BoostAttributes>("boostAttributes");
            schema.AddMember<DialogCommand, GiveItems>("giveItems");
            schema.AddMember<DialogCommand, SetRole>("setRole");
            schema.AddMember<DialogCommand, OpenTrade>("openTrade");
            schema.AddMember<DialogCommand, TeachLesson>("teachLesson");
            schema.AddMember<DialogCommand, ReportLearnShortfalls>("reportLearnShortfalls");
            schema.AddMember<DialogCommand, SayShared>("sayShared");
            schema.AddMember<DialogCommand, ShowMessage>("showMessage");
            schema.AddMember<DialogCommand, SayGuardNews>("sayGuardNews");
            schema.AddMember<DialogCommand, SayBossNews>("sayBossNews");
            schema.AddMember<DialogCommand, Bribe>("bribe");
            schema.AddMember<DialogCommand, Excuse>("excuse");
            schema.AddMember<DialogCommand, ResetCrime>("resetCrime");
            schema.AddMember<DialogCommand, SetChapter>("setChapter");
            schema.AddMember<DialogCommand, StartFight>("startFight");
            schema.AddMember<DialogCommand, SetRoutine>("setRoutine");
            schema.AddMember<DialogCommand, SetAlignment>("setAlignment");
            schema.AddMember<DialogCommand, HealActor>("healActor");
            schema.AddMember<DialogCommand, Sleep>("sleep");
            schema.AddMember<DialogCommand, RemoveItems>("removeItems");
            schema.AddMember<DialogCommand, GiveAllItems>("giveAllItems");
            schema.AddMember<DialogCommand, UseItem>("useItem");
            schema.AddMember<DialogCommand, JoinParty>("joinParty");
            schema.AddMember<DialogCommand, WaitParty>("waitParty");
            schema.AddMember<DialogCommand, LeaveParty>("leaveParty");
            schema.AddMember<DialogCommand, CompleteDelivery>("completeDelivery");
            schema.AddMember<DialogCommand, PartyRefusal>("partyRefusal");
            schema.AddMember<DialogCommand, ShowPicture>("showPicture");
            schema.AddMember<DialogCommand, ShowSubtitle>("showSubtitle");
            schema.AddMember<DialogCommand, WaitInScript>("wait");
            schema.AddMember<DialogCommand, PlayMusic>("playMusic");
            schema.AddMember<DialogCommand, EndGame>("endGame");
            schema.AddMember<DialogCommand, StartEffect>("startEffect");
            schema.AddMember<DialogCommand, SaySound>("saySound");
            schema.AddMember<DialogCommand, SetSectorStatus>("setSectorStatus");
            schema.AddMember<DialogCommand, OpenDoor>("openDoor");
            schema.AddMember<DialogCommand, SetEnclaveFlag>("setEnclaveFlag");
            schema.AddMember<DialogCommand, StartRevolution>("startRevolution");
            schema.AddMember<DialogCommand, SetClass>("setClass");
            schema.AddMember<DialogCommand, FullStop>("fullStop");
            schema.AddMember<DialogCommand, HumanizeActor>("humanize");
            schema.AddMember<DialogCommand, WearItem>("wear");
            schema.AddMember<DialogCommand, SetGuardStatus>("setGuardStatus");
            schema.AddMember<DialogCommand, JoinNpc>("joinNpc");
            schema.AddMember<DialogCommand, GateGuardWarned>("gateGuardWarned");
            schema.AddMember<DialogCommand, RememberRun>("rememberRun");
            schema.AddMember<DialogCommand, Pickpocket>("pickpocket");

            schema.AddFamily<PreCheck>("type");
            schema.AddMember<PreCheck, MinEnclaveFame>("minEnclaveFame");
            schema.AddMember<PreCheck, MinGold>("minGold");
            schema.AddMember<PreCheck, LessonCheck>("lessonCheck");
            schema.AddMember<PreCheck, MinPoliticalFame>("minPoliticalFame");
            schema.AddMember<PreCheck, PartyHasRoom>("partyHasRoom");
        }
    }
}
