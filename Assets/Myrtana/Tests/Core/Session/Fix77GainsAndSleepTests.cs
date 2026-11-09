using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-77: the bed's sleep dialog (G3 PS_Interact_SleepBed → AssessTalk(hero, bed) → the bed's infos, info command
    /// Sleep → PlayerSleep(hour)) and the HUD texts of scripted gains (PrintAddValue, OnEndInfo).
    /// </summary>
    public class Fix77GainsAndSleepTests
    {
        static readonly ActorId Bed = new ActorId("G3_Object_Interact_Bed_01");

        static DialogEntryDef SleepEntry(string id, long order, int hour) =>
            new DialogEntryDef(new Id<DialogEntryDef>(id), Bed, default, DialogEntryKind.Choice, order, new TextId("INFO_" + id), true,
                               false, false, null, null, null, new DialogCommand[] { new Sleep(hour) });

        // the four infos every G3 bed template owns (8, 12, 20, midnight) and the hero's general End (BPANKRATZ30)
        static SessionFixture Fixture() => new SessionFixture()
            .Add(StorySchema.DialogFolder,
                 SleepEntry("FILLERX293", 1, 8), SleepEntry("FILLERX294", 2, 12), SleepEntry("FILLERX295", 3, 20), SleepEntry("FILLERX296", 4, 0),
                 new DialogEntryDef(new Id<DialogEntryDef>("BPANKRATZ30"), ActorId.Player, default, DialogEntryKind.Exit, 99,
                                    new TextId("BPANKRATZ31"), true, false, false, "End", null, null, new DialogCommand[] { new EndDialog() }));

        [Test]
        public void Bed_OffersTheFourHoursAndEnd()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Dialog.CanTalkTo(Bed), Is.True, "the bed has infos: the hero talks to it instead of sleeping to 08:00");
            s.Dialog.Talk(Bed);
            var options = s.Dialog.Current.Options;
            Assert.That(options.Select(o => o.Entry.ToString()),
                        Is.EqualTo(new[] { "FILLERX293", "FILLERX294", "FILLERX295", "FILLERX296", "BPANKRATZ30" }));
            Assert.That(options.Last().IsExit, Is.True, "ESC moves to End");
        }

        [Test]
        public void Bed_UntilNoon_SkipsTheClockAndRefills()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.AddHitPoints(ActorId.Player, -30);
            int hurt = s.Combat.Vitals.HitPoints(ActorId.Player);
            s.Dialog.Talk(Bed);
            s.Dialog.Choose(new Id<DialogEntryDef>("FILLERX294"));
            Assert.That(s.Clock.HourOfDay, Is.EqualTo(12f).Within(0.01f));
            Assert.That(s.Combat.Vitals.HitPoints(ActorId.Player), Is.GreaterThan(hurt));
            Assert.That(s.Combat.Vitals.HitPoints(ActorId.Player), Is.EqualTo(s.Combat.Vitals.HitPointsMax(ActorId.Player)));
            Assert.That(s.Dialog.IsActive, Is.False, "the dialog ends: the hero gets up");
        }

        [Test]
        public void Bed_UntilMidnight_WrapsToTheNextDay()
        {
            using var f = Fixture();
            var s = f.NewGame();
            int day = s.Clock.Day;
            s.Dialog.Talk(Bed);
            s.Dialog.Choose(new Id<DialogEntryDef>("FILLERX296"));
            Assert.That(s.Clock.HourOfDay, Is.EqualTo(0f).Within(0.01f));
            Assert.That(s.Clock.Day, Is.EqualTo(day + 1));
        }

        [Test]
        public void Bed_End_GetsUpWithoutSleeping()
        {
            using var f = Fixture();
            var s = f.NewGame();
            double before = s.Clock.GameSeconds;
            s.Dialog.Talk(Bed);
            s.Dialog.Choose(new Id<DialogEntryDef>("BPANKRATZ30"));
            Assert.That(s.Dialog.IsActive, Is.False);
            Assert.That(s.Clock.GameSeconds, Is.EqualTo(before).Within(1.0));
        }

        [Test]
        public void Notices_PrintRaisesLessonsAndItemsLikeG3()
        {
            using var f = new SessionFixture();
            var s = f.NewGame();
            using var notices = new GameNotices(s);
            var posted = new List<(string text, NoticeKind kind)>();
            notices.Posted += (text, kind) => posted.Add((text, kind));
            string T(string id) => s.Localizer.Text(new TextId(id));

            s.Progression.Raise(new AttributeGain("STR", ModifierOp.AddToVal, 5)); // the orc boulder's first lift
            int str = s.Hero.Stats.Value("STR");
            Assert.That(posted.Last(), Is.EqualTo(($"{T("ATTRIB_STR")} + 5 = {str}", NoticeKind.Info)));

            s.Events.Publish(new LessonTaught("Perk_Test", null, 7));
            Assert.That(posted.Skip(1).ToArray(), Is.EqualTo(new[]
            {
                (T("GO_Learned") + T("FO_It_Perk_Test"), NoticeKind.Result),
                (T("GO_LearningPointsLeft").Replace("$(value)", "7"), NoticeKind.Result),
            }));

            s.Events.Publish(new LessonTaught(null, "STR", 3));
            Assert.That(posted.Last(), Is.EqualTo((T("GO_LearningPointsLeft").Replace("$(value)", "3"), NoticeKind.Info)), "type 15 after an attribute");
        }
    }
}
