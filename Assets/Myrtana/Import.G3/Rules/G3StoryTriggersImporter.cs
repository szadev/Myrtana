using Myrtana.Story;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Story changes CP 1.75 makes in Script_Game rather than in infos (Docs/story-walk.md §5), as data. The names are the
    /// script's own: OnItemReceived (the CPT books switch on the "Community Patch" quest sectors; Guru of Kill_Guru stands in
    /// CPT_Quest_NPC_05), _AI_UseInventoryItem (Stollengrollen drunk with 200 maximum stamina: Ugolf's bet is won; with less the hero drops
    /// what he holds and collapses) and
    /// OnEnterArea (the slave Lago_Slave_03 reaches the Lago gallery: Sancho's slaves are ready).
    /// OB_Faring_Boulder (Faring_Boulder_Success) is an orc boulder's use function: <see cref="Myrtana.Rules.BoulderRules"/>.
    /// </summary>
    public static class G3StoryTriggersImporter
    {
        static readonly string[] ReceivedEffects = { "eff_event_druidheal_01", "eff_magic_armyofdarkness_load_01" };

        public static StoryTriggerRules Rules() => new StoryTriggerRules(
            StoryTriggerRules.DefinitionId,
            new[]
            {
                new ItemReceivedTrigger("It_Book_CPT_01", new[] { "CPT_Quest_NPC_01" }, ReceivedEffects),
                new ItemReceivedTrigger("It_Book_CPT_02", new[] { "CPT_Quest_NPC_02" }, ReceivedEffects),
                new ItemReceivedTrigger("It_Book_CPT_03", new[] { "CPT_Quest_NPC_03" }, ReceivedEffects),
                new ItemReceivedTrigger("It_Book_CPT_04", new[] { "CPT_Quest_NPC_04" }, ReceivedEffects),
                new ItemReceivedTrigger("It_Book_CPT_Teleport", new[] { "CPT_Quest_NPC_05" }, ReceivedEffects),
            },
            new[]
            {
                new ItemUsedTrigger("It_Stollengrollen", 200, new[] { "Ugolf_StollengrollenSuccess" }, new[] { "Ugolf_StollengrollenRunning" },
                                    // below 200: DropHandItems, the DOWN collapse (fix-13), in a talk the hero's current target
                                    // (Ugolf, the only one who hands it out) says "Адская штука" with his own voice (fix-212)
                                    new ItemUseFailure(true, "INFO_SKALVERAM320060405151107_1",
                                                       "INFO_Hum_Warrior_Dumb_Ugolf_SKALVERAM320060405151107_1", partnerSays: true)),
            },
            new[]
            {
                new AreaEnteredTrigger("Lago_Slave_03", "Nav_Zone_Lago_Stollen", new[] { "Emaciated_Ready" }),
            });
    }
}
