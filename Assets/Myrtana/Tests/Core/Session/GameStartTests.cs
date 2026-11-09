using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The new-game script (Docs/game-start.md), the revolution attitudes it switches on and the program flow.</summary>
    public class GameStartTests
    {
        static GameStartDef Start(params EnclaveStart[] enclaves) =>
            new GameStartDef(GameStartDef.DefinitionId, new WorldPose(879.8f, 51.5f, -102f, 119f), 0, 12f, 1,
                             new[] { "Xardas_FindXardas" },
                             new[] { new RoutineChange("Larson", "Start") },
                             enclaves, new[] { "Diego", "Milten", "Gorn", "Lester" });

        static readonly EnclaveStart ArdeaRaid = new EnclaveStart("Ardea", "Orc", true, true, "Ardea_Orkboss");

        // ------------------------------------------------------------------ new-game script

        [Test]
        public void NewGame_SetsClockQuestRoutinesAndTheRaidingEnclave()
        {
            using (var f = new SessionFixture().Quest("Xardas_FindXardas").Add(SimulationSchema.GameStartFolder, Start(ArdeaRaid)))
            {
                var game = f.NewGame();

                Assert.That(game.Clock.Day, Is.EqualTo(0));
                Assert.That(game.Clock.HourOfDay, Is.EqualTo(12f).Within(0.001f), "World_MCP gCClock_PS: day 0, 12:00");
                Assert.That(game.Chapter.Current, Is.EqualTo(1));
                Assert.That(game.Quests.StatusOf(new Id<QuestDef>("Xardas_FindXardas")), Is.EqualTo(QuestStatus.Running));
                Assert.That(game.Enclaves.Of("Ardea"), Is.EqualTo(new EnclaveState(EnclaveStatus.Routine, true, true)));
                Assert.That(game.Start, Is.Not.Null);
                Assert.That(game.Start.PendingAlarms, Is.EqualTo(new[] { "Ardea" }));
                Assert.That(f.Log.Messages.Count(m => m.Contains("game start:")), Is.EqualTo(1), "Larson is not in this content");
                Assert.That(f.Log.Messages, Has.Some.Contains("no NPC Larson"));
            }
        }

        [Test]
        public void LoadedGame_KeepsTheEnclaveState_AndThePendingAlarm()
        {
            using (var f = new SessionFixture().Quest("Xardas_FindXardas").Add(SimulationSchema.GameStartFolder, Start(ArdeaRaid)))
            {
                var game = f.NewGame();
                game.Enclaves.SetStatus("Ardea", EnclaveStatus.Attack);

                var loaded = f.Reload(game);

                Assert.That(loaded.Enclaves.Of("Ardea"), Is.EqualTo(new EnclaveState(EnclaveStatus.Attack, true, true)));
                Assert.That(loaded.Start.PendingAlarms, Is.EqualTo(new[] { "Ardea" }), "saved before the alarm went off");

                loaded.Start.DropAlarms();
                Assert.That(f.Reload(loaded).Start, Is.Null, "no alarm left to resume");
                Assert.That(loaded.Clock.HourOfDay, Is.EqualTo(12f).Within(0.01f));
            }
        }

        [Test]
        public void WithoutGameStartContent_NewGameIsAsBefore()
        {
            using (var f = new SessionFixture())
            {
                var game = f.NewGame();
                Assert.That(game.Start, Is.Null);
                Assert.That(game.Clock.HourOfDay, Is.EqualTo(8f).Within(0.001f));
                Assert.That(game.Enclaves.Of("Ardea").IsQuiet, Is.True);
            }
        }

        [Test]
        public void RaidAlarm_WaitsForDefendersAndPlay_ThenSendsTheEnclaveToAttack()
        {
            var enclaves = new EnclaveStates();
            var log = new ListLog();
            var start = NewGameStart.Apply(Start(ArdeaRaid), 8f, null, new StoryChapter(), null, null, enclaves, log);
            var nobody = new List<ActorId>();
            var orcs = new List<ActorId> { new ActorId("Orc_GameStartRaider_Warrior_04") };
            IReadOnlyList<ActorId> defenders = nobody;

            Assert.That(start.Tick(0f, enclaves, null, _ => orcs), Is.Empty, "paused (intro): no alarm");
            Assert.That(start.Tick(0.1f, enclaves, null, _ => defenders), Is.Empty, "no NPCs spawned yet");
            Assert.That(start.PendingAlarms, Is.EqualTo(new[] { "Ardea" }));
            Assert.That(start.Tick(0.1f, enclaves, null, _ => orcs), Is.Empty, "retries every half second");

            var raised = start.Tick(NewGameStart.AlarmRetrySeconds, enclaves, null, _ => orcs);

            Assert.That(raised, Is.EqualTo(new[] { "Ardea" }));
            Assert.That(enclaves.Of("Ardea").Status, Is.EqualTo(EnclaveStatus.Attack));
            Assert.That(start.PendingAlarms, Is.Empty);
        }

        [Test]
        public void RaidAlarm_IsDroppedWhenTheBossCannotAlarm_OrForTheDeveloperStart()
        {
            var enclaves = new EnclaveStates();
            var start = NewGameStart.Apply(Start(ArdeaRaid), 8f, null, null, null, null, enclaves, null);
            start.Tick(1f, enclaves, e => false, _ => new[] { ActorId.Player });
            Assert.That(start.PendingAlarms, Is.Empty);
            Assert.That(enclaves.Of("Ardea").Status, Is.EqualTo(EnclaveStatus.Routine));

            var dev = NewGameStart.Apply(Start(ArdeaRaid), 8f, null, null, null, null, enclaves, null);
            dev.DropAlarms();
            Assert.That(dev.Tick(1f, enclaves, null, _ => new[] { ActorId.Player }), Is.Empty);
        }

        [Test]
        public void RaidAlarm_GivesUpAfterAWhileWithoutDefenders()
        {
            var enclaves = new EnclaveStates();
            var log = new ListLog();
            var start = NewGameStart.Apply(Start(ArdeaRaid), 8f, null, null, null, null, enclaves, log);
            for (int i = 0; i < 300; i++) start.Tick(1f, enclaves, null, _ => new ActorId[0]);
            Assert.That(start.PendingAlarms, Is.Empty);
            Assert.That(log.Messages, Has.Some.Contains("alarm given up"));
        }

        // ------------------------------------------------------------------ revolution attitudes

        sealed class States : IEnclaveStates
        {
            public EnclaveState State;
            public EnclaveState Of(string enclave) => enclave == "Ardea" ? State : default;
        }

        static AttitudeProfile Npc(string name, string alignment, string enclave = "Ardea", bool friend = false) =>
            new AttitudeProfile
            {
                Id = new ActorId(name), Alignment = alignment, Enclave = enclave, EnclaveAlignment = enclave == null ? null : "Orc",
                Humanoid = true, Species = "Human", FriendType = friend,
            };

        static readonly AttitudeProfile Player = new AttitudeProfile { Id = ActorId.Player, IsPlayer = true, Humanoid = true };

        /// <summary>Orcs and slaves are neutral to each other; same alignment friendly; Rebels hostile to orcs.</summary>
        static Attitude? Politics(string self, string toward)
        {
            if (self == null || toward == null) return null;
            if (self == toward) return Attitude.Friendly;
            if ((self == "Reb" && toward == "Orc") || (self == "Orc" && toward == "Reb")) return Attitude.Hostile;
            return Attitude.Neutral;
        }

        [Test]
        public void Revolution_RaidInRoutine_MakesTheOrcsAngryAtThePlayerOnly()
        {
            var states = new States { State = new EnclaveState(EnclaveStatus.Routine, true, true) };
            var orc = Npc("Orc_GameStartRaider_Warrior_04", "Orc");
            var slave = Npc("Hamlar", "Slave");

            Assert.That(RevolutionAttitude.Of(orc, Player, states, Politics), Is.EqualTo(Attitude.Angry));
            Assert.That(RevolutionAttitude.Of(orc, Npc("Diego", null, null, friend: true), states, Politics), Is.Null,
                        "a friend is not the player himself");
            Assert.That(RevolutionAttitude.Of(slave, Player, states, Politics), Is.EqualTo(Attitude.Friendly), "neutral + revolution");
            Assert.That(RevolutionAttitude.Of(orc, slave, states, Politics), Is.Null, "not the player's side: only during Attack");
        }

        [Test]
        public void Revolution_InAttack_OrcsFightThePlayerSideAndTheSlavesRise()
        {
            var states = new States { State = new EnclaveState(EnclaveStatus.Attack, true, true) };
            var orc = Npc("Orc_GameStartRaider_Warrior_04", "Orc");
            var boss = Npc("Ardea_Orkboss", "Orc");
            var slave = Npc("Hamlar", "Slave");
            var diego = Npc("Diego", null, null, friend: true);

            Assert.That(RevolutionAttitude.Of(orc, Player, states, Politics), Is.EqualTo(Attitude.Hostile));
            Assert.That(RevolutionAttitude.Of(orc, diego, states, Politics), Is.EqualTo(Attitude.Hostile));
            Assert.That(RevolutionAttitude.Of(orc, boss, states, Politics), Is.EqualTo(Attitude.Friendly));
            Assert.That(RevolutionAttitude.Of(orc, slave, states, Politics), Is.EqualTo(Attitude.Hostile), "F vs N with revolution");
            Assert.That(RevolutionAttitude.Of(slave, orc, states, Politics), Is.EqualTo(Attitude.Hostile));
            Assert.That(RevolutionAttitude.Of(slave, Player, states, Politics), Is.EqualTo(Attitude.Friendly));
            Assert.That(RevolutionAttitude.Of(diego, orc, states, Politics), Is.Null, "a friend has no enclave: it mirrors instead");
        }

        [Test]
        public void Revolution_HasNoOpinionForQuietEnclaves_AndFleeingMembersPanic()
        {
            var orc = Npc("Orc_Warrior_01", "Orc");
            Assert.That(RevolutionAttitude.Of(orc, Player, new States(), Politics), Is.Null);
            Assert.That(RevolutionAttitude.Of(orc, Player, null, Politics), Is.Null);
            var flee = new States { State = new EnclaveState(EnclaveStatus.Flee, true, true) };
            Assert.That(RevolutionAttitude.Of(orc, Player, flee, Politics), Is.EqualTo(Attitude.Panic));
            Assert.That(RevolutionAttitude.Of(Npc("Hamlar", "Slave"), Player, flee, Politics), Is.EqualTo(Attitude.Friendly));
        }

        /// <summary>
        /// fix-94, G3 GetRevolutionAttitude's column for aS 0 (a member without an alignment of its own, as Phil or Seruk
        /// at Kap Dun): toward the player's side Friendly while a revolution fights and while the enclave flees, else no
        /// opinion; toward anyone else none.
        /// </summary>
        [Test]
        public void Revolution_AMemberWithoutAnAlignment_SidesWithTheHeroInARevolutionAndAFlight()
        {
            var unaligned = Npc("Phil", null);
            var orc = Npc("Orc_Warrior_01", "Orc");
            Assert.That(RevolutionAttitude.Of(unaligned, Player, new States { State = new EnclaveState(EnclaveStatus.Routine, true, true) }, Politics), Is.Null);
            Assert.That(RevolutionAttitude.Of(unaligned, Player, new States { State = new EnclaveState(EnclaveStatus.Attack, false, true) }, Politics),
                        Is.EqualTo(Attitude.Friendly));
            Assert.That(RevolutionAttitude.Of(unaligned, Player, new States { State = new EnclaveState(EnclaveStatus.Attack, true, false) }, Politics), Is.Null);
            Assert.That(RevolutionAttitude.Of(unaligned, Player, new States { State = new EnclaveState(EnclaveStatus.Flee, false, false) }, Politics),
                        Is.EqualTo(Attitude.Friendly));
            Assert.That(RevolutionAttitude.Of(unaligned, orc, new States { State = new EnclaveState(EnclaveStatus.Attack, true, true) }, Politics), Is.Null);
            Assert.That(RevolutionAttitude.Of(orc, unaligned, new States { State = new EnclaveState(EnclaveStatus.Attack, true, true) }, Politics), Is.Null);
        }

        [Test]
        public void EnclaveStates_ForgetQuietEnclaves()
        {
            var states = new EnclaveStates();
            int changes = 0;
            states.Changed += _ => changes++;
            states.Set("Ardea", new EnclaveState(EnclaveStatus.Routine, true, false));
            states.SetStatus("Ardea", EnclaveStatus.Attack);
            states.Set("Ardea", default);
            Assert.That(states.NotQuiet, Is.Empty);
            Assert.That(changes, Is.EqualTo(3));
        }

        // ------------------------------------------------------------------ flow and menu

        [Test]
        public void Flow_LogosSkipOnAnyPress_IntroAndCreditsNeedTheHold()
        {
            // fix-68: the start-up logos go on one key press or click; the intro and the credits keep the hold-to-skip.
            var flow = new GameFlow(GameFlow.DefaultLogos, quickStart: false);
            foreach (var logo in GameFlow.DefaultLogos)
            {
                Assert.That(flow.CurrentVideo, Is.EqualTo(logo));
                Assert.That(flow.CurrentVideoSkip, Is.EqualTo(VideoSkip.AnyPress), logo);
                flow.VideoFinished();
            }
            Assert.That(GameFlow.SkipFor(GamePhase.Intro), Is.EqualTo(VideoSkip.Hold));
            Assert.That(GameFlow.SkipFor(GamePhase.Credits), Is.EqualTo(VideoSkip.Hold));
            flow.StartNewGame();
            flow.WorldReady(introAvailable: true);
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3_Intro"));
            Assert.That(flow.CurrentVideoSkip, Is.EqualTo(VideoSkip.Hold));
        }

        [Test]
        public void VideoEnd_LastFrameHeldBriefly_IsTheEnd()
        {
            // fix-170: the watchdog ends a clip that stands on its last frame after 0.2 s, not after the 3 s stall timeout.
            Assert.That(VideoEnd.Reached(164, 165, 0.25f), Is.True);
            Assert.That(VideoEnd.Reached(165, 165, 0.25f), Is.True);
            Assert.That(VideoEnd.Reached(164, 165, 0.1f), Is.False);   // just arrived
            Assert.That(VideoEnd.Reached(163, 165, 5f), Is.False);     // a real stall mid-clip keeps the stall timeout
            Assert.That(VideoEnd.Reached(-1, 165, 5f), Is.False);      // no frame yet
            Assert.That(VideoEnd.Reached(0, 0, 5f), Is.False);         // length unknown
        }

        [Test]
        public void Flow_LogosTitleNewGameIntroPlay()
        {
            var flow = new GameFlow(GameFlow.DefaultLogos, quickStart: false);
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Logos));
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3_Logo_01"));
            flow.VideoFinished();
            Assert.That(flow.CurrentVideo, Is.EqualTo("Publisher"));
            flow.VideoFinished();
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3R_Remaster")); // fix-170: the port's logo in CP's CPT slot
            flow.VideoFinished();
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Title));
            Assert.That(flow.AtTitle, Is.True);
            Assert.That(flow.NewGameQuestion(alternativeBalancing: true).Value, Is.EqualTo("HUD_Confirm_NewGameABYes"));
            Assert.That(flow.NewGameQuestion(alternativeBalancing: false).Value, Is.EqualTo("HUD_Confirm_NewGameABNo"));

            flow.StartNewGame();
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Loading));
            Assert.That(flow.Starting, Is.EqualTo(StartKind.NewGame));
            flow.WorldReady(introAvailable: true);
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Intro));
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3_Intro"));
            Assert.That(flow.TutorialPending, Is.False);
            flow.VideoFinished();
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Playing));
            Assert.That(flow.TutorialPending, Is.True);
            flow.TutorialShown();
            Assert.That(flow.TutorialPending, Is.False);
            Assert.That(flow.NewGameQuestion(true).Value, Is.EqualTo("HUD_Confirm_NewGame"), "over a running game");
        }

        [Test]
        public void Flow_LogosRunWhileTheContentLoads_TitleWaitsForBoth()
        {
            // fix-115: the logos start at once; content that is late holds the title on black, content that is early waits
            var late = new GameFlow(GameFlow.DefaultLogos, quickStart: false, waitForContent: true);
            Assert.That(late.Phase, Is.EqualTo(GamePhase.Logos));
            Assert.That(late.CurrentVideo, Is.EqualTo("G3_Logo_01"));
            Assert.That(late.LogosOver, Is.False);
            int changes = 0;
            late.Changed += () => changes++;
            late.VideoFinished();
            late.VideoFinished();
            late.VideoFinished();
            Assert.That(late.Phase, Is.EqualTo(GamePhase.Logos));
            Assert.That(late.WaitingForContent, Is.True);
            Assert.That(late.LogosOver, Is.True);
            Assert.That(late.CurrentVideo, Is.Null);
            late.VideoFinished(); // a stray callback changes nothing
            Assert.That(late.WaitingForContent, Is.True);
            late.ContentReady();
            Assert.That(late.Phase, Is.EqualTo(GamePhase.Title));
            Assert.That(late.WaitingForContent, Is.False);
            Assert.That(changes, Is.EqualTo(4));

            var early = new GameFlow(GameFlow.DefaultLogos, quickStart: false, waitForContent: true);
            early.ContentReady();
            Assert.That(early.Phase, Is.EqualTo(GamePhase.Logos));
            Assert.That(early.CurrentVideo, Is.EqualTo("G3_Logo_01"));
            early.VideoFinished();
            early.VideoFinished();
            early.VideoFinished();
            Assert.That(early.Phase, Is.EqualTo(GamePhase.Title));

            var none = new GameFlow(new string[0], quickStart: false, waitForContent: true);
            Assert.That(none.WaitingForContent, Is.True);
            Assert.That(none.CurrentVideo, Is.Null);
            none.ContentReady();
            Assert.That(none.Phase, Is.EqualTo(GamePhase.Title));
        }

        [Test]
        public void Flow_LoadSkipsTheIntro_AndQuickStartSkipsEverything()
        {
            var flow = new GameFlow(new string[0], quickStart: false);
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Title));
            flow.StartLoad();
            flow.WorldReady(introAvailable: true);
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Playing));
            Assert.That(flow.TutorialPending, Is.False);

            var dev = new GameFlow(GameFlow.DefaultLogos, quickStart: true);
            Assert.That(dev.Phase, Is.EqualTo(GamePhase.Loading));
            dev.WorldReady(introAvailable: true);
            Assert.That(dev.Phase, Is.EqualTo(GamePhase.Playing));
            Assert.That(dev.TutorialPending, Is.False);
        }

        [Test]
        public void Flow_NoIntroVideo_StillShowsTheTip_CreditsReturn_DeathIsGameOver()
        {
            var flow = new GameFlow(null, quickStart: false);
            flow.ShowCredits();
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3_Credits2"));
            Assert.That(flow.AtTitle, Is.True);
            flow.VideoFinished();
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3_Credits"));
            flow.VideoFinished();
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Title));

            flow.PlayerDied();
            Assert.That(flow.GameOver, Is.False, "no game yet");
            flow.StartNewGame();
            flow.WorldReady(introAvailable: false);
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Playing));
            Assert.That(flow.TutorialPending, Is.True);
            flow.ShowCredits();
            flow.VideoFinished();
            flow.VideoFinished();
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Playing));

            flow.PlayerDied();
            Assert.That(flow.GameOver, Is.True);
            flow.StartLoad();
            flow.WorldReady(false);
            Assert.That(flow.GameOver, Is.False);
        }

        [Test]
        public void MainMenu_TitleHasNewGameAndAB_GameHasResumeAndSave_DeathOffersNewGame()
        {
            var title = MainMenuButtons.For(atTitle: true, gameOver: false);
            Assert.That(title.NewGame && !title.Resume && title.Credits && title.AlternativeBalancing, Is.True);
            Assert.That(title.Save && !title.SaveAvailable, Is.True, "title: Save shows greyed out");
            var game = MainMenuButtons.For(atTitle: false, gameOver: false);
            Assert.That(!game.NewGame && game.Resume && game.Save && game.SaveAvailable && !game.AlternativeBalancing, Is.True);
            var dead = MainMenuButtons.For(atTitle: false, gameOver: true);
            Assert.That(dead.NewGame && !dead.Resume, Is.True);
        }

        [Test]
        public void MenuAtTitle_BackStaysOnTheMainPage()
        {
            var menu = new GameMenuState { AtTitle = true };
            menu.Open();
            menu.Show(MenuPage.Options);
            menu.Show(MenuPage.Difficulty);
            Assert.That(menu.Back(), Is.True);
            Assert.That(menu.Page, Is.EqualTo(MenuPage.Options));
            menu.Back();
            Assert.That(menu.Back(), Is.True);
            Assert.That(menu.Page, Is.EqualTo(MenuPage.Main));
        }

        [Test]
        public void NewGameOptions_BecomeTheSessionRules()
        {
            var options = new NewGameOptions();
            Assert.That(options.ToRules().AlternativeBalancing, Is.True);
            int changes = 0;
            options.Changed += () => changes++;
            options.AlternativeBalancing = false;
            options.Difficulty = Difficulty.Hard;
            options.AlternativeAI = false;
            options.Difficulty = Difficulty.Hard;
            var rules = options.ToRules(new RuleOptions(xpModifierPercent: 150));
            Assert.That(changes, Is.EqualTo(3));
            Assert.That(rules.AlternativeBalancing || rules.ExtendedAI, Is.False);
            Assert.That(rules.Difficulty, Is.EqualTo(Difficulty.Hard));
            Assert.That(rules.XpModifierPercent, Is.EqualTo(150));
        }
    }
}
