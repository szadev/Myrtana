using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;
using static Myrtana.Tests.StoryFixture;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-247, G3 ZS_Talk @10024ff0: a sleeper talked to says "YouDisturbedMySlumber" first, then stands up
    /// (<see cref="DialogPreludeEnded"/>), then the conversation's own lines run — one after the other, never overlapping.
    /// </summary>
    public class DialogPreludeTests
    {
        static readonly TextId Slumber = new TextId("SVM_Ass_Elite_Insidious_YouDisturbedMySlumber");

        static StoryFixture Fixture() => new StoryFixture()
            .Entry("imp", DialogEntryKind.Important, commands: new DialogCommand[] { NpcSays("i1") })
            .Entry("a").Entry("b");

        [Test]
        public void Sleeper_SaysTheSlumberLineAlone_ThenGetsUp_ThenTheDialogRuns()
        {
            var f = Fixture();
            f.Dialog.Prelude = npc => new DialogLine(npc, Slumber, Slumber.Value);
            var order = new List<string>();
            f.Events.Subscribe<DialogStarted>(e => order.Add("started"));
            f.Events.Subscribe<DialogPreludeEnded>(e => order.Add("stands up " + e.Partner.Name));
            f.Presenter.OnLine = () => order.Add("line " + f.Presenter.Lines[f.Presenter.Lines.Count - 1].Text.Value);

            f.Dialog.Talk(Npc);
            Assert.That(f.Dialog.InPrelude, Is.True);
            Assert.That(f.Presenter.Lines.Count, Is.EqualTo(1), "only the slumber line until it is over");
            Assert.That(f.Presenter.Lines[0].Speaker, Is.EqualTo(Npc));
            Assert.That(f.Presenter.Lines[0].Voice, Is.EqualTo(Slumber.Value), "voiced from the NPC's voice set");
            Assert.That(f.Presenter.Menus, Is.Empty);
            Assert.That(f.Dialog.IsEngaged, Is.True, "a spoken line to the hero: the dialog camera frames it");

            f.Dialog.CompleteLine(); // the slumber voice ended
            Assert.That(f.Dialog.InPrelude, Is.False);
            f.FinishLines();
            Assert.That(order, Is.EqualTo(new[] { "started", "line " + Slumber.Value, "stands up Gnar", "line i1" }));
            Assert.That(f.Presenter.Menus.Count, Is.EqualTo(1), "then the menu");
        }

        [Test]
        public void NoPrelude_TheDialogStartsAtOnce()
        {
            var f = Fixture();
            f.Dialog.Prelude = npc => null;
            f.Dialog.Talk(Npc);
            Assert.That(f.Dialog.InPrelude, Is.False);
            Assert.That(f.Presenter.Lines.Count, Is.EqualTo(1));
            Assert.That(f.Presenter.Lines[0].Text.Value, Is.EqualTo("i1"));
        }

        [Test]
        public void AbortDuringThePrelude_ClosesCleanly()
        {
            var f = Fixture();
            f.Dialog.Prelude = npc => new DialogLine(npc, Slumber, Slumber.Value);
            f.Dialog.Talk(Npc);
            f.Dialog.Abort();
            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Dialog.InPrelude, Is.False);
        }

        [Test]
        public void SaysDisturbed_OnlyWhenStillAsleep()
        {
            Assert.That(SleeperRules.SaysDisturbedOnTalk(sleeps: true, inReactionTask: false), Is.True, "asleep in bed / on the ground");
            Assert.That(SleeperRules.SaysDisturbedOnTalk(sleeps: true, inReactionTask: true), Is.False, "woken by footsteps, watching: already said it");
            Assert.That(SleeperRules.SaysDisturbedOnTalk(sleeps: false, inReactionTask: false), Is.False, "awake");
        }
    }
}
