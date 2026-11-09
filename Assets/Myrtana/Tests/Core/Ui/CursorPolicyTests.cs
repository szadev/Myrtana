using Myrtana.Core;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-87: the pointer only where it can be used (G3 GUI cursor rules).</summary>
    public class CursorPolicyTests
    {
        static CursorRole Eval(CursorUi ui) => CursorPolicy.Evaluate(ui);

        [Test]
        public void Play_IsLockedWithoutPointer()
        {
            var mode = Eval(new CursorUi());
            Assert.That(mode, Is.EqualTo(CursorRole.Gameplay));
            Assert.That(CursorPolicy.Visible(mode), Is.False);
            Assert.That(CursorPolicy.Locked(mode), Is.True);
            Assert.That(CursorPolicy.Captured(mode), Is.True);
        }

        [Test]
        public void SpokenLine_HidesPointer_ChoiceList_ShowsIt()
        {
            // a conversation holds the controls; while a line is spoken no list is up
            var line = new CursorUi { ControlsLocked = true };
            Assert.That(Eval(line), Is.EqualTo(CursorRole.Hidden));
            Assert.That(CursorPolicy.Captured(Eval(line)), Is.False, "a hidden cursor during a line is no mouse look");
            var choices = new CursorUi { ControlsLocked = true, DialogChoices = true };
            Assert.That(Eval(choices), Is.EqualTo(CursorRole.Pointer));
            Assert.That(CursorPolicy.Visible(Eval(choices)), Is.True);
            Assert.That(CursorPolicy.Locked(Eval(choices)), Is.False);
        }

        [Test]
        public void ActionBeforePage_IsHidden_UntilThePageOpens()
        {
            // the walk to a chest and its opening hold the controls; the loot page comes after
            Assert.That(Eval(new CursorUi { ControlsLocked = true }), Is.EqualTo(CursorRole.Hidden));
            Assert.That(Eval(new CursorUi { ControlsLocked = true, Page = true }), Is.EqualTo(CursorRole.Pointer));
            // closed: the closing motion still holds the controls, then play
            Assert.That(Eval(new CursorUi { ControlsLocked = true }), Is.EqualTo(CursorRole.Hidden));
            Assert.That(Eval(new CursorUi()), Is.EqualTo(CursorRole.Gameplay));
        }

        [Test]
        public void PagesMenusAndTips_ShowPointer()
        {
            Assert.That(Eval(new CursorUi { Page = true, ControlsLocked = true }), Is.EqualTo(CursorRole.Pointer));
            Assert.That(Eval(new CursorUi { Menu = true, ControlsLocked = true }), Is.EqualTo(CursorRole.Pointer));
            Assert.That(Eval(new CursorUi { Tip = true, ControlsLocked = true }), Is.EqualTo(CursorRole.Pointer));
            Assert.That(Eval(new CursorUi { Menu = true }), Is.EqualTo(CursorRole.Pointer), "the title menu, no game behind it");
        }

        [Test]
        public void VideosSlideshowAndLoading_HidePointer_EvenOverMenus()
        {
            // G3: intro/credits videos, the slideshow and a load call ShowCursor(false)
            Assert.That(Eval(new CursorUi { Video = true, Menu = true }), Is.EqualTo(CursorRole.Hidden), "credits from the main menu");
            Assert.That(Eval(new CursorUi { Slideshow = true, ControlsLocked = true }), Is.EqualTo(CursorRole.Hidden));
            Assert.That(Eval(new CursorUi { Loading = true, Menu = true }), Is.EqualTo(CursorRole.Hidden), "load from the menu");
            Assert.That(Eval(new CursorUi { Loading = true, Page = true, DialogChoices = true }), Is.EqualTo(CursorRole.Hidden));
            Assert.That(Eval(new CursorUi { Loading = true }), Is.Not.EqualTo(CursorRole.Gameplay), "no mouse look under a loading screen");
        }

        [Test]
        public void MenuOverAConversation_ShowsPointer()
        {
            Assert.That(Eval(new CursorUi { ControlsLocked = true, Menu = true }), Is.EqualTo(CursorRole.Pointer));
        }

        [Test]
        public void DevTools_AlwaysHavePointer()
        {
            Assert.That(Eval(new CursorUi { Dev = true }), Is.EqualTo(CursorRole.Pointer));
            Assert.That(Eval(new CursorUi { Dev = true, Loading = true }), Is.EqualTo(CursorRole.Pointer));
        }

        [Test]
        public void Names_ForTheRunner()
        {
            Assert.That(CursorPolicy.Name(CursorRole.Gameplay), Is.EqualTo("gameplay"));
            Assert.That(CursorPolicy.Name(CursorRole.Hidden), Is.EqualTo("hidden"));
            Assert.That(CursorPolicy.Name(CursorRole.Pointer), Is.EqualTo("pointer"));
        }

        [Test]
        public void TitleMenu_BeforeAnyGame_ShowsPointer()
        {
            // fix-98: the title's own world hold and the streamer's hold on the unloaded hero are no load
            var signals = LoadingSignals.Of(false, false, streamerWait: true, false, atTitle: true);
            var title = new CursorUi { Loading = signals != LoadingSignal.None, Menu = true, ControlsLocked = true };
            Assert.That(Eval(title), Is.EqualTo(CursorRole.Pointer));
            Assert.That(CursorPolicy.Visible(Eval(title)) && !CursorPolicy.Locked(Eval(title)), Is.True);
            // the credits video from the title, a load from it, the logos: no pointer
            Assert.That(Eval(new CursorUi { Menu = true, Video = true }), Is.EqualTo(CursorRole.Hidden));
            Assert.That(Eval(new CursorUi { Menu = true, Loading = true }), Is.EqualTo(CursorRole.Hidden));
        }
    }
}
