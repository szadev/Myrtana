using System.Collections.Generic;
using System.Linq;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>gamepad-1: pad bindings, gestures, sticks, UI navigation, device tracking, glyphs (Docs/gamepad.md).</summary>
    public class GamepadTests
    {
        sealed class Rig
        {
            public readonly PadMap Map;
            public readonly PadResolver R = new PadResolver();
            public readonly PadSnapshot Pad = new PadSnapshot();
            public double Now;
            public bool Active = true;

            public Rig(PadMap map = null) => Map = map ?? PadMap.Default();

            public void Frame(double dt = 1.0 / 60)
            {
                Now += dt;
                R.Update(Map, Pad, Now, Active);
            }

            public void Hold(PadButton b, double seconds)
            {
                Pad[b] = 1f;
                for (double t = 0; t < seconds; t += 1.0 / 60) Frame();
            }

            public void Up(PadButton b)
            {
                Pad[b] = 0f;
                Frame();
            }
        }

        // ------------------------------------------------------------------ PadMap

        [Test]
        public void DefaultsFollowTheDesignTable()
        {
            var m = PadMap.Default();
            PadBinding Only(SessionKey k, PadGesture g = PadGesture.Press, bool chord = false) =>
                m.Of(k).Single(b => b.Gesture == g && b.Chord == chord);
            Assert.That(Only(SessionKey.Use1).Button, Is.EqualTo(PadButton.RT));
            Assert.That(Only(SessionKey.Use1, PadGesture.Tap).Button, Is.EqualTo(PadButton.A));
            Assert.That(Only(SessionKey.Use2).Button, Is.EqualTo(PadButton.LT));
            Assert.That(Only(SessionKey.Down).Button, Is.EqualTo(PadButton.B), "sneak");
            Assert.That(Only(SessionKey.Up).Button, Is.EqualTo(PadButton.Y), "jump");
            Assert.That(Only(SessionKey.WeaponMode).Button, Is.EqualTo(PadButton.X));
            Assert.That(Only(SessionKey.Forward).Button, Is.EqualTo(PadButton.LsUp));
            Assert.That(Only(SessionKey.InventoryMode).Button, Is.EqualTo(PadButton.DDown));
            Assert.That(Only(SessionKey.JournalModeLog, PadGesture.Hold).Button, Is.EqualTo(PadButton.View));
            Assert.That(Only(SessionKey.Plus, chord: true).Button, Is.EqualTo(PadButton.RsUp));
            Assert.That(m.Bindings.Count(b => b.IsAction && b.Action == PadAction.QuickLoot), Is.EqualTo(1));
            Assert.That(m.IsDefault);
        }

        [Test]
        public void DefaultTableHasNoConflicts()
        {
            var t = PadMap.DefaultTable;
            for (int i = 0; i < t.Count; i++)
                for (int j = i + 1; j < t.Count; j++)
                    Assert.That(t[i].Conflicts(t[j]), Is.False, t[i] + " vs " + t[j]);
        }

        [Test]
        public void SerializeRoundTrips()
        {
            var m = PadMap.Default();
            m.Assign(PadBinding.ForKey(SessionKey.Up, PadButton.A, PadGesture.Hold), 0);
            var text = m.Serialize();
            var back = PadMap.Parse(text);
            Assert.That(back.Serialize(), Is.EqualTo(text));
            Assert.That(PadMap.Parse("").IsDefault, "empty = defaults");
            Assert.That(PadMap.Parse("-").Bindings, Is.Empty, "dash = nothing bound");
            Assert.That(PadMap.Parse("Use1=RT;Bogus=A;Use2=Nope;@QuickLoot=A:hold;Jump=Y").Bindings.Count, Is.EqualTo(2));
        }

        [Test]
        public void BindingTextParses()
        {
            Assert.That(PadBinding.TryParse("Look=R3+LB", out var b));
            Assert.That(b.Chord && b.Button == PadButton.R3 && b.Key == SessionKey.Look && b.Gesture == PadGesture.Press);
            Assert.That(PadBinding.TryParse("@QuickSlotNext=RB:tap", out b));
            Assert.That(b.IsAction && b.Action == PadAction.QuickSlotNext && b.Gesture == PadGesture.Tap);
            Assert.That(PadBinding.TryParse("Use1=A:long", out _), Is.False);
            Assert.That(PadBinding.TryParse("@None=A", out _), Is.False);
            Assert.That(PadBinding.ForKey(SessionKey.Use1, PadButton.LB, chord: true).Chord, Is.False, "LB cannot chord itself");
        }

        [Test]
        public void AssignTakesTheButtonFromOtherCommands()
        {
            var m = PadMap.Default();
            var taken = m.Assign(PadBinding.ForKey(SessionKey.Up, PadButton.B), 0);
            Assert.That(taken.Select(t => t.Key), Has.Member(SessionKey.Down), "B was sneak");
            Assert.That(m.Of(SessionKey.Down), Is.Empty);
            Assert.That(m.Of(SessionKey.Up).Single().Button, Is.EqualTo(PadButton.B), "slot 0 replaced Y");
            // a press on A takes both the tap (Use1) and the hold (quick loot)
            taken = m.Assign(PadBinding.ForKey(SessionKey.WeaponMode, PadButton.A), 1);
            Assert.That(taken.Count, Is.EqualTo(2));
            Assert.That(m.Of(SessionKey.WeaponMode).Select(b => b.Button), Is.EqualTo(new[] { PadButton.X, PadButton.A }));
            // tap and hold of one button live together
            m.Assign(PadBinding.ForKey(SessionKey.QuickSave, PadButton.Y, PadGesture.Hold), 0);
            m.Assign(PadBinding.ForKey(SessionKey.QuickLoad, PadButton.Y, PadGesture.Tap), 0);
            Assert.That(m.Of(SessionKey.QuickSave).Count(), Is.EqualTo(1));
            Assert.That(m.Of(SessionKey.QuickLoad).Count(), Is.EqualTo(1));
            m.Clear(PadBinding.ForKey(SessionKey.QuickLoad, PadButton.None), 0);
            Assert.That(m.Of(SessionKey.QuickLoad), Is.Empty);
            Assert.That(m.IsDefault, Is.False);
        }

        [Test]
        public void CommandsCoverEverySessionKeyButTheModifier()
        {
            var keys = PadMap.Commands.Where(c => !c.IsAction).Select(c => c.Key).ToList();
            Assert.That(keys, Has.No.Member(SessionKey.QuickLootModifier));
            Assert.That(keys.Count, Is.EqualTo(KeyMap.All.Length - 1));
            Assert.That(PadMap.Commands.Count(c => c.IsAction), Is.EqualTo(4));
        }

        // ------------------------------------------------------------------ resolver

        [Test]
        public void PressFollowsTheButton()
        {
            var r = new Rig();
            r.Pad[PadButton.RT] = 1f;
            r.Frame();
            Assert.That(r.R.WasPressed(SessionKey.Use1) && r.R.IsDown(SessionKey.Use1));
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Use1) && !r.R.WasPressed(SessionKey.Use1));
            r.Up(PadButton.RT);
            Assert.That(r.R.WasReleased(SessionKey.Use1) && !r.R.IsDown(SessionKey.Use1));
        }

        [Test]
        public void TriggerHasHysteresisAndOption()
        {
            var r = new Rig();
            r.Pad[PadButton.LT] = 0.45f;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Use2), Is.False, "below 0.5");
            r.Pad[PadButton.LT] = 0.6f;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Use2));
            r.Pad[PadButton.LT] = 0.4f;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Use2), "held until below 0.35");
            r.Pad[PadButton.LT] = 0.3f;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Use2), Is.False);
            r.R.TriggerThreshold = 0.2f;
            r.Pad[PadButton.LT] = 0.25f;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Use2), "lighter press point");
        }

        [Test]
        public void TapFiresOnShortReleaseAndHoldAfterHoldTime()
        {
            var r = new Rig();
            r.Hold(PadButton.A, 0.1);
            Assert.That(r.R.IsDown(SessionKey.Use1), Is.False, "A waits: it has a hold");
            r.Up(PadButton.A);
            Assert.That(r.R.WasPressed(SessionKey.Use1), "tap pulse");
            Assert.That(r.R.IsDown(SessionKey.QuickLootModifier), Is.False);
            r.Frame();
            Assert.That(r.R.WasReleased(SessionKey.Use1), "one frame only");

            r.Hold(PadButton.A, 0.5);
            Assert.That(r.R.IsDown(PadAction.QuickLoot));
            Assert.That(r.R.IsDown(SessionKey.QuickLootModifier) && r.R.IsDown(SessionKey.Use1), "quick loot = Ctrl + Use1");
            r.Up(PadButton.A);
            Assert.That(r.R.WasPressed(SessionKey.Use1), Is.False, "no tap after a hold");
            Assert.That(r.R.WasReleased(SessionKey.QuickLootModifier));
        }

        [Test]
        public void TapWithoutHoldActsAtOnce()
        {
            var r = new Rig();
            r.Pad[PadButton.RB] = 1f;
            r.Frame();
            Assert.That(r.R.WasPressed(PadAction.QuickSlotNext));
            r.Frame();
            Assert.That(r.R.IsDown(PadAction.QuickSlotNext), Is.False, "a pulse");
        }

        [Test]
        public void ChordsUseTheModifierLayer()
        {
            var r = new Rig();
            r.Pad[PadButton.LB] = 1f;
            r.Frame();
            r.Pad[PadButton.DLeft] = 1f;
            r.Frame();
            Assert.That(r.R.WasPressed(SessionKey.UsePotionHP));
            Assert.That(r.R.IsDown(SessionKey.QuickUse1), Is.False, "the plain layer rests");
            r.Up(PadButton.DLeft);
            r.Up(PadButton.LB);
            Assert.That(r.R.WasPressed(PadAction.QuickSlotPrev), Is.False, "LB's tap was used for a chord");

            r.Hold(PadButton.LB, 0.1);
            r.Up(PadButton.LB);
            Assert.That(r.R.WasPressed(PadAction.QuickSlotPrev), "a plain LB tap");

            r.Pad[PadButton.DLeft] = 1f;
            r.Frame();
            Assert.That(r.R.WasPressed(SessionKey.QuickUse1));
            Assert.That(r.R.IsDown(SessionKey.UsePotionHP), Is.False);
        }

        [Test]
        public void ChordedStickZoomsInsteadOfPitching()
        {
            var r = new Rig();
            r.Pad.SetSticks(0, 0, 0, 0.8f);
            r.Frame();
            Assert.That(r.R.Analog(SessionKey.TurnUp), Is.EqualTo(0.8f).Within(1e-5));
            Assert.That(r.R.IsDown(SessionKey.Plus), Is.False);
            r.Pad[PadButton.LB] = 1f;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Plus));
            Assert.That(r.R.Analog(SessionKey.TurnUp), Is.EqualTo(0f), "no pitch while zooming");
        }

        [Test]
        public void StickHalvesGiveAnalogBelowThePressPoint()
        {
            var r = new Rig();
            r.Pad.SetSticks(0.3f, 0, 0, 0);
            r.Frame();
            Assert.That(r.R.Analog(SessionKey.StrafeRight), Is.EqualTo(0.3f).Within(1e-5));
            Assert.That(r.R.IsDown(SessionKey.StrafeRight), Is.False, "as a key only past 0.5");
            r.Pad.SetSticks(0.7f, 0, 0, 0);
            r.Frame();
            Assert.That(r.R.WasPressed(SessionKey.StrafeRight));
            Assert.That(r.R.Analog(SessionKey.StrafeLeft), Is.EqualTo(0f));
        }

        [Test]
        public void InactiveReleasesAndBlocksHeldButtons()
        {
            var r = new Rig();
            r.Pad[PadButton.RT] = 1f;
            r.Frame();
            r.Active = false;
            r.Frame();
            Assert.That(r.R.WasReleased(SessionKey.Use1), "a page took the pad");
            r.Pad[PadButton.B] = 1f; // B closes the page
            r.Frame();
            r.Active = true;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Down), Is.False, "the B that closed the page is no sneak");
            Assert.That(r.R.IsDown(SessionKey.Use1), Is.False, "RT held across the page waits for a new press");
            r.Up(PadButton.B);
            r.Up(PadButton.RT);
            r.Pad[PadButton.B] = 1f;
            r.Frame();
            Assert.That(r.R.WasPressed(SessionKey.Down));
            // sticks are never blocked: walking on right after closing a page
            r.Active = false;
            r.Pad.SetSticks(0, 1f, 0, 0);
            r.Frame();
            r.Active = true;
            r.Frame();
            Assert.That(r.R.IsDown(SessionKey.Forward));
        }

        // ------------------------------------------------------------------ sticks

        [Test]
        public void RadialDeadZoneRescalesAndKeepsDirection()
        {
            Assert.That(PadStick.Radial(0.1f, 0.05f, 0.15f), Is.EqualTo((0f, 0f)));
            var (x, y) = PadStick.Radial(0.95f, 0f, 0.15f);
            Assert.That(x, Is.EqualTo(1f).Within(1e-5));
            (x, y) = PadStick.Radial(0.4f, 0.4f, 0.15f);
            Assert.That(x, Is.EqualTo(y).Within(1e-6), "diagonal stays diagonal");
            float len = (float)System.Math.Sqrt(0.32);
            Assert.That(System.Math.Sqrt(x * x + y * y), Is.EqualTo((len - 0.15f) / 0.8f).Within(1e-5));
            Assert.That(PadStick.Trigger(0.1f, 0.15f), Is.EqualTo(0f));
            Assert.That(PadStick.Trigger(1f, 0.15f), Is.EqualTo(1f));
        }

        [Test]
        public void AnalogWalkAndRun()
        {
            Assert.That(PadStick.Walks(0f), Is.False, "no input");
            Assert.That(PadStick.Walks(0.3f));
            Assert.That(PadStick.Walks(0.8f), Is.False);
            var (x, y) = PadStick.MoveDirection(0.3f, 0.4f);
            Assert.That(x * x + y * y, Is.EqualTo(1f).Within(1e-5));
        }

        [Test]
        public void CameraRatesFollowSensitivityAndInvert()
        {
            var o = new PadOptions();
            var (yaw, pitch) = PadStick.CameraRates(1f, 1f, o);
            Assert.That(yaw, Is.EqualTo(PadStick.YawRate).Within(1e-3));
            Assert.That(pitch, Is.EqualTo(PadStick.PitchRate).Within(1e-3));
            Assert.That(PadStick.CameraRates(0.5f, 0, o).yaw, Is.LessThan(PadStick.YawRate * 0.5f), "curve: slow near the centre");
            o.InvertY = true;
            o.SensitivityX = 1f;
            (yaw, pitch) = PadStick.CameraRates(1f, 1f, o);
            Assert.That(pitch, Is.LessThan(0));
            Assert.That(yaw, Is.EqualTo(PadStick.YawRate * 2.5f / 1.5f).Within(1e-3));
        }

        [Test]
        public void PadOptionsClampAndStore()
        {
            var c = new ControlOptions();
            int changes = 0;
            c.Changed += () => changes++;
            c.Pad.DeadZone = 2f;
            Assert.That(c.Pad.DeadZone, Is.EqualTo(PadOptions.DeadZoneMax));
            c.Pad.Buttons.Assign(PadBinding.ForKey(SessionKey.Up, PadButton.A), 0);
            Assert.That(changes, Is.EqualTo(2), "pad changes reach the Controls options (they are saved)");
            Assert.That(PadOptions.FromStep(PadOptions.ToStep(0.15f, 0.05f, 0.4f), 0.05f, 0.4f), Is.EqualTo(0.15f).Within(0.01f));
            c.Pad.UseStandardButtons();
            Assert.That(c.Pad.Buttons.IsDefault && c.Pad.Layout == ControlOptions.Standard);
        }

        // ------------------------------------------------------------------ UI navigation

        [Test]
        public void RepeatWaitsThenRepeats()
        {
            var n = new UiNavRepeat();
            Assert.That(n.Update(NavDir.Down, 0), Is.EqualTo(NavDir.Down));
            Assert.That(n.Update(NavDir.Down, 0.2), Is.EqualTo(NavDir.None));
            Assert.That(n.Update(NavDir.Down, 0.41), Is.EqualTo(NavDir.Down));
            Assert.That(n.Update(NavDir.Down, 0.45), Is.EqualTo(NavDir.None));
            Assert.That(n.Update(NavDir.Down, 0.50), Is.EqualTo(NavDir.Down));
            Assert.That(n.Update(NavDir.Up, 0.51), Is.EqualTo(NavDir.Up), "a new direction moves at once");
            Assert.That(n.Update(NavDir.None, 0.6), Is.EqualTo(NavDir.None));
            Assert.That(n.Update(NavDir.Up, 0.61), Is.EqualTo(NavDir.Up));
            Assert.That(UiNavRepeat.StickDir(0.2f, 0.3f), Is.EqualTo(NavDir.None));
            Assert.That(UiNavRepeat.StickDir(0.7f, -0.6f), Is.EqualTo(NavDir.Right));
            Assert.That(UiNavRepeat.StickDir(0.1f, -0.6f), Is.EqualTo(NavDir.Down));
        }

        [Test]
        public void SpatialNavPrefersTheRowAhead()
        {
            // a 2×2 grid of buttons and a slider row below
            var rects = new List<NavRect>
            {
                new NavRect(0, 0, 100, 20), new NavRect(150, 0, 100, 20),
                new NavRect(0, 40, 100, 20), new NavRect(150, 40, 100, 20),
                new NavRect(0, 80, 250, 20),
            };
            Assert.That(SpatialNav.Pick(rects[0], rects, NavDir.Right, 0), Is.EqualTo(1));
            Assert.That(SpatialNav.Pick(rects[0], rects, NavDir.Down, 0), Is.EqualTo(2));
            Assert.That(SpatialNav.Pick(rects[3], rects, NavDir.Left, 3), Is.EqualTo(2));
            Assert.That(SpatialNav.Pick(rects[3], rects, NavDir.Down, 3), Is.EqualTo(4));
            Assert.That(SpatialNav.Pick(rects[4], rects, NavDir.Up, 4), Is.EqualTo(2).Or.EqualTo(3));
            Assert.That(SpatialNav.Pick(rects[0], rects, NavDir.Up, 0), Is.EqualTo(-1), "nothing above");
            Assert.That(SpatialNav.First(rects), Is.EqualTo(0));
        }

        [Test]
        public void GridNavMatchesTheArrowKeys()
        {
            Assert.That(GridNav.Move(-1, 10, 5, NavDir.Down), Is.EqualTo(0), "nothing selected: the first cell");
            Assert.That(GridNav.Move(2, 10, 5, NavDir.Down), Is.EqualTo(7));
            Assert.That(GridNav.Move(7, 10, 5, NavDir.Down), Is.EqualTo(7), "stays at the edge");
            Assert.That(GridNav.Move(2, 10, 5, NavDir.Up), Is.EqualTo(2));
            Assert.That(GridNav.Move(9, 10, 5, NavDir.Right), Is.EqualTo(9));
            Assert.That(GridNav.Move(0, 0, 5, NavDir.Right), Is.EqualTo(-1));
            Assert.That(GridNav.MoveList(3, 4, NavDir.Down), Is.EqualTo(3));
        }

        // ------------------------------------------------------------------ device, glyphs, quick bar, journal

        [Test]
        public void TrackerFollowsTheLastDevice()
        {
            var t = new InputDeviceTracker();
            var seen = new List<ActiveInput>();
            t.Changed += d => seen.Add(d);
            t.Note(false, false);
            Assert.That(t.Current, Is.EqualTo(ActiveInput.KeyboardMouse));
            t.Note(true, true);
            Assert.That(t.Current, Is.EqualTo(ActiveInput.Pad), "the pad wins a tie");
            t.Note(false, false);
            Assert.That(t.Current, Is.EqualTo(ActiveInput.Pad), "no activity keeps it");
            t.Note(true, false);
            Assert.That(seen, Is.EqualTo(new[] { ActiveInput.Pad, ActiveInput.KeyboardMouse }));
        }

        [Test]
        public void FamiliesAndNames()
        {
            Assert.That(PadGlyphs.FamilyOf("DualSenseGamepadHID", "DualSense Wireless Controller"), Is.EqualTo(PadFamily.PlayStation));
            Assert.That(PadGlyphs.FamilyOf("DualShock4GamepadHID", ""), Is.EqualTo(PadFamily.PlayStation));
            Assert.That(PadGlyphs.FamilyOf("SwitchProControllerHID", "Pro Controller"), Is.EqualTo(PadFamily.Nintendo));
            Assert.That(PadGlyphs.FamilyOf("XInputControllerWindows", "Xbox Controller"), Is.EqualTo(PadFamily.Xbox));
            Assert.That(PadGlyphs.FamilyOf("HID::Generic", "USB Joystick", isJoystick: true), Is.EqualTo(PadFamily.Generic));
            Assert.That(PadGlyphs.Name(PadButton.A, PadFamily.PlayStation), Is.EqualTo("Cross"));
            Assert.That(PadGlyphs.Name(PadButton.A, PadFamily.Nintendo), Is.EqualTo("B"), "south button");
            Assert.That(PadGlyphs.Name(PadButton.RT, PadFamily.Xbox), Is.EqualTo("RT"));
            var look = PadBinding.ForKey(SessionKey.Look, PadButton.R3, chord: true);
            Assert.That(PadGlyphs.Name(look, PadFamily.Xbox), Is.EqualTo("LB + RS"));
            var log = PadBinding.ForKey(SessionKey.JournalModeLog, PadButton.View, PadGesture.Hold);
            Assert.That(PadGlyphs.Name(log, PadFamily.PlayStation), Is.EqualTo("Share (hold)"));
        }

        [Test]
        public void QuickSlotCursorCyclesAndSkipsEmpty()
        {
            var c = new QuickSlotCursor(10);
            Assert.That(c.Move(-1), Is.EqualTo(9), "wraps");
            Assert.That(c.Move(1), Is.EqualTo(0));
            var filled = new HashSet<int> { 3, 7 };
            Assert.That(c.Move(1, filled.Contains), Is.EqualTo(3));
            Assert.That(c.Move(1, filled.Contains), Is.EqualTo(7));
            Assert.That(c.Move(1, filled.Contains), Is.EqualTo(3));
            Assert.That(c.Move(1, _ => false), Is.EqualTo(3), "all empty: stays");
        }

        [Test]
        public void JournalCycleOrder()
        {
            Assert.That(JournalCycle.Next(null), Is.EqualTo(SessionKey.InventoryMode));
            Assert.That(JournalCycle.Next(SessionKey.InventoryMode), Is.EqualTo(SessionKey.JournalModeCharScreen));
            Assert.That(JournalCycle.Next(SessionKey.JournalModeMap), Is.EqualTo(SessionKey.InventoryMode));
            Assert.That(JournalCycle.Next(SessionKey.InventoryMode, -1), Is.EqualTo(SessionKey.JournalModeMap));
        }

        [Test]
        public void HitRumbleIsStrongerWhenTheHeroIsHit()
        {
            var dealt = HitRumble.For(false, false, false, false);
            var taken = HitRumble.For(true, false, false, false);
            var heavy = HitRumble.For(true, false, true, false);
            Assert.That(taken.Low, Is.GreaterThan(dealt.Low));
            Assert.That(heavy.Seconds, Is.GreaterThan(taken.Seconds));
            Assert.That(HitRumble.For(true, true, false, false).Low, Is.LessThan(taken.Low), "a parried hit buzzes");
            Assert.That(dealt.IsNone, Is.False);
        }

        [Test]
        public void CaptureTakesButtonsChordsAndLbOnRelease()
        {
            var c = new PadCapture();
            var down = new HashSet<PadButton>();
            var pressed = new HashSet<PadButton>();
            bool Step() { var r = c.Update(down.Contains, pressed.Contains); pressed.Clear(); return r; }
            down.Add(PadButton.X); pressed.Add(PadButton.X);
            Assert.That(Step() && c.Button == PadButton.X && !c.Chord);
            down.Clear(); c.Reset();
            down.Add(PadButton.LB); pressed.Add(PadButton.LB);
            Assert.That(Step(), Is.False, "LB waits for its release");
            down.Add(PadButton.Y); pressed.Add(PadButton.Y);
            Assert.That(Step() && c.Button == PadButton.Y && c.Chord, "LB + Y");
            down.Clear(); c.Reset();
            down.Add(PadButton.LB); pressed.Add(PadButton.LB);
            Step();
            down.Clear();
            Assert.That(Step() && c.Button == PadButton.LB && !c.Chord, "LB alone");
            c.Reset();
            down.Add(PadButton.Start); pressed.Add(PadButton.Start);
            Assert.That(Step(), Is.False, "Start cancels, it is never bound");
            Assert.That(PadCapture.NextGesture(PadGesture.Hold), Is.EqualTo(PadGesture.Press));
        }

        [Test]
        public void HintsEndWithCloseOnPagesAndBackInTheMenu()
        {
            Assert.That(PadHints.For(PadHintPage.Loot).Any(h => h.button == PadButton.Y));
            Assert.That(PadHints.For(PadHintPage.Inventory).Last().button, Is.EqualTo(PadButton.B));
            Assert.That(PadHints.For(PadHintPage.Menu).Select(h => h.button), Is.EqualTo(new[] { PadButton.A, PadButton.B }));
            foreach (PadHintPage p in System.Enum.GetValues(typeof(PadHintPage)))
                Assert.That(PadHints.For(p).All(h => h.en.Length > 0 && h.ru.Length > 0), p.ToString());
        }
    }
}
