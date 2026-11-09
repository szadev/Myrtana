using System.Collections.Generic;
using System.Linq;
using Myrtana.Ui.Dialog;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-200: only the line's speaker talks (gesture, lips); everyone else listens, switching per line.</summary>
    public class DialogRolesTests
    {
        const string Hero = "player", Gnar = "Gnar", Rufus = "Rufus";

        [Test]
        public void SpeakerTalks_OthersListen()
        {
            var cues = DialogRoles.LineStart(new[] { Hero, Gnar }, Gnar).ToDictionary(c => c.Key, c => c.Value);
            Assert.That(cues[Gnar], Is.EqualTo(PerformerCue.Talk));
            Assert.That(cues[Hero], Is.EqualTo(PerformerCue.Listen), "the hero must not talk while the NPC speaks");
            cues = DialogRoles.LineStart(new[] { Hero, Gnar }, Hero).ToDictionary(c => c.Key, c => c.Value);
            Assert.That(cues[Hero], Is.EqualTo(PerformerCue.Talk), "the hero's choice line");
            Assert.That(cues[Gnar], Is.EqualTo(PerformerCue.Listen));
        }

        [Test]
        public void Conversation_SwitchesRolesEveryLine()
        {
            // Gnar greets, the hero picks a choice and says it, Gnar answers twice, Rufus joins in, the hero again
            var lines = new[] { Gnar, Hero, Gnar, Gnar, Rufus, Hero };
            var cast = new List<string> { Hero, Gnar };
            foreach (var speaker in lines)
            {
                if (!cast.Contains(speaker)) cast.Add(speaker);
                var cues = DialogRoles.LineStart(cast, speaker);
                Assert.That(DialogRoles.Talkers(cues), Is.EqualTo(1), $"one talker in {speaker}'s line");
                foreach (var c in cues)
                    Assert.That(c.Value, Is.EqualTo(c.Key == speaker ? PerformerCue.Talk : PerformerCue.Listen), $"{c.Key} in {speaker}'s line");
            }
            Assert.That(cast, Is.EqualTo(new[] { Hero, Gnar, Rufus }));
        }

        [Test]
        public void CameraListener()
        {
            Assert.That(DialogRoles.ListenerOf(Hero, Hero, Gnar), Is.EqualTo(Gnar));
            Assert.That(DialogRoles.ListenerOf(Gnar, Hero, Gnar), Is.EqualTo(Hero));
            Assert.That(DialogRoles.ListenerOf(Rufus, Hero, Gnar), Is.EqualTo(Hero), "a third NPC talks to the hero");
        }

        [Test]
        public void LineEnd_LoopingGestureStops_OneShotPlaysOut()
        {
            Assert.That(DialogRoles.AtLineEnd(spoke: true, gestureLoops: true), Is.EqualTo(PerformerCue.Idle), "Say_Ambient loops");
            Assert.That(DialogRoles.AtLineEnd(spoke: true, gestureLoops: false), Is.EqualTo(PerformerCue.PlayOut));
            Assert.That(DialogRoles.AtLineEnd(spoke: false, gestureLoops: false), Is.EqualTo(PerformerCue.Listen));
        }

        [Test]
        public void ListenerMouth_StaysClosed()
        {
            // the listener's mouth is driven with speaking = false while the other's voice plays: it never opens
            var listener = new MouthMotion(new LipSyncTrack(new[] { new LipSyncTrack.Key(0, 7), new LipSyncTrack.Key(1, 1) }), openViseme: 9);
            for (int i = 0; i < 60; i++)
                Assert.That(listener.Update(i / 60f, 0.2f, speaking: false, 1 / 60f).Amount, Is.EqualTo(0f));
            var speaker = new MouthMotion(new LipSyncTrack(new[] { new LipSyncTrack.Key(0, 7), new LipSyncTrack.Key(1, 1) }), openViseme: 9);
            float amount = 0;
            for (int i = 0; i < 30; i++) amount = speaker.Update(i / 60f, 0.2f, speaking: true, 1 / 60f).Amount;
            Assert.That(amount, Is.GreaterThan(0.9f));
        }
    }
}
