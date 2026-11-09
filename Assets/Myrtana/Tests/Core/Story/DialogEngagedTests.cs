using Myrtana.Core;
using Myrtana.Story;
using NUnit.Framework;
using static Myrtana.Tests.StoryFixture;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-76: the dialog camera and the turn to the partner start with the conversation's first line or real choice menu
    /// (<see cref="DialogEngaged"/>; G3 SetDialogRoles / ShowDialogManager), not at the talk key. A talk with nothing to
    /// say (only an exit runs) never engages, so the camera does not cut to a dialog shot and back.
    /// </summary>
    public class DialogEngagedTests
    {
        static int CountEngaged(StoryFixture f, System.Action act)
        {
            int n = 0;
            using (f.Events.Subscribe<DialogEngaged>(e => n++)) act();
            return n;
        }

        [Test]
        public void Talk_WithOnlyAnExit_StartsAndEnds_WithoutEngaging()
        {
            var f = new StoryFixture()
                .Entry("told", commands: new DialogCommand[] { NpcSays("t1") })
                .Entry("end", DialogEntryKind.Exit, order: 100, owner: ActorId.Player, repeatable: true, commands: new DialogCommand[] { new EndDialog() });
            f.Memory.MarkGiven(new Id<DialogEntryDef>("told")); // its only topic is told: nothing left to say
            int started = 0;
            f.Events.Subscribe<DialogStarted>(e => started++);
            int engaged = CountEngaged(f, () => f.Dialog.Talk(Npc));
            Assert.That(started, Is.EqualTo(1), "the talk itself still starts (controls are locked for its frame)");
            Assert.That(engaged, Is.Zero, "no line, no choice: the camera and the hero stay as they are");
            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Dialog.IsEngaged, Is.False);
        }

        [Test]
        public void Important_Line_EngagesOnce_BeforeTheLineIsShown()
        {
            var f = new StoryFixture()
                .Entry("imp", DialogEntryKind.Important, commands: new DialogCommand[] { NpcSays("i1"), NpcSays("i2") })
                .Entry("a").Entry("b");
            int linesAtEngage = -1, engaged = 0;
            f.Events.Subscribe<DialogEngaged>(e => { engaged++; linesAtEngage = f.Presenter.Lines.Count; });
            f.Dialog.Talk(Npc);
            f.FinishLines();
            Assert.That(engaged, Is.EqualTo(1));
            Assert.That(linesAtEngage, Is.Zero, "published before the presenter shows the first line (the camera frames it)");
            Assert.That(f.Dialog.IsEngaged, Is.True);
        }

        [Test]
        public void Menu_WithARealChoice_Engages_AndARefusalLineToo()
        {
            var menu = new StoryFixture().Entry("a").Entry("b");
            Assert.That(CountEngaged(menu, () => menu.Dialog.Talk(Npc)), Is.EqualTo(1), "G3 ShowDialogManager starts the dialog camera");

            var refuse = new StoryFixture()
                .Entry("refuse", DialogEntryKind.Refuse, commands: new DialogCommand[] { NpcSays("go away"), new EndDialog() })
                .Entry("a");
            Assert.That(CountEngaged(refuse, () => { refuse.Dialog.Talk(Npc); refuse.FinishLines(); }), Is.EqualTo(1),
                        "a refusal is a spoken line to the hero: G3 frames it (SetDialogRoles)");
        }
    }
}
