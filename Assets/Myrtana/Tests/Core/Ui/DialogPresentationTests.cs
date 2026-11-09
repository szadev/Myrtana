using System;
using System.Linq;
using System.Numerics;
using Myrtana.Ui.Dialog;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class DialogPresentationTests
    {
        static LipSyncTrack Track(params (float t, int v)[] keys) =>
            new LipSyncTrack(keys.Select(k => new LipSyncTrack.Key(k.t, k.v)));

        [Test]
        public void LipTrack_RoundTripsAndSamples()
        {
            var track = Track((0.5f, 3), (0f, 0), (0.25f, 7));
            var back = LipSyncTrack.Read(track.Write());
            Assert.That(back.Keys.Select(k => k.Viseme), Is.EqualTo(new[] { 0, 7, 3 }), "sorted by time");
            Assert.That(back.VisemeAt(-1), Is.EqualTo(LipSyncTrack.RestViseme));
            Assert.That(back.VisemeAt(0.3f), Is.EqualTo(7));
            Assert.That(back.VisemeAt(9), Is.EqualTo(3));
            Assert.That(back.Duration, Is.EqualTo(0.5f));

            back.Sample(0.28f, 0.06f, out int from, out int to, out float w);
            Assert.That((from, to), Is.EqualTo((0, 7)));
            Assert.That(w, Is.EqualTo(0.5f).Within(1e-3f), "half way through the crossfade");
        }

        [Test]
        public void LipTrack_RejectsForeignText() =>
            Assert.Throws<System.IO.InvalidDataException>(() => LipSyncTrack.Read("hello\n1 2\n"));

        [Test]
        public void Mouth_FollowsTrack_AndRestsWhenSilent()
        {
            var mouth = new MouthMotion(Track((0f, 0), (0.1f, 5)), openViseme: 9);
            MouthShape s = default;
            for (int i = 0; i < 20; i++) s = mouth.Update(0.2f, 0, speaking: true, dt: 0.02f);
            Assert.That((s.To, s.Amount), Is.EqualTo((5, 1f)));
            for (int i = 0; i < 20; i++) s = mouth.Update(0.3f, 0, speaking: false, dt: 0.02f);
            Assert.That(s.Amount, Is.EqualTo(0f), "eases out after the line");
        }

        // Lip timing (Docs/dialog-presentation.md §4): Gnar's Russian FILLER line — track ends at 0.587 s, the clip's
        // speech (VoiceSpan) runs 0.04–0.94 s, so without fitting the mouth closed 0.35 s early.
        static LipSyncTrack GnarFiller() =>
            Track((0f, 1), (0.099f, 6), (0.18f, 7), (0.261f, 11), (0.424f, 4), (0.505f, 6), (0.587f, 1));

        [Test]
        public void LipTrack_SpeechSpan_IsFirstOpenToLastKey()
        {
            var t = GnarFiller();
            Assert.That(t.SpeechStart, Is.EqualTo(0.099f));
            Assert.That(t.Duration, Is.EqualTo(0.587f));
            Assert.That(Track((0f, 1), (0.5f, 1)).SpeechStart, Is.EqualTo(0f), "never opens");
        }

        [Test]
        public void LipTrack_Retimed_MapsSpeechSpanLinearly()
        {
            var t = GnarFiller().Retimed(0.1f, 0.98f);
            Assert.That(t.SpeechStart, Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(t.Duration, Is.EqualTo(0.98f).Within(1e-5f));
            float scale = (0.98f - 0.1f) / (0.587f - 0.099f);
            Assert.That(t.Keys[3].Time, Is.EqualTo(0.1f + (0.261f - 0.099f) * scale).Within(1e-5f));
            Assert.That(t.Keys.Select(k => k.Viseme), Is.EqualTo(GnarFiller().Keys.Select(k => k.Viseme)), "visemes kept");
            Assert.That(t.Keys[0].Time, Is.EqualTo(0f), "leading rest stays at/above 0");
        }

        [Test]
        public void LipTrack_FitToVoice_StretchesMismatch_KeepsMatch()
        {
            var russian = GnarFiller();
            var fitted = russian.FitToVoice(0.08f, 0.98f);
            Assert.That(fitted, Is.Not.SameAs(russian));
            Assert.That(fitted.Duration, Is.EqualTo(0.98f).Within(1e-5f), "mouth closes when the voice ends");
            Assert.That(russian.FitToVoice(0.08f, 0.40f).Duration, Is.EqualTo(0.40f).Within(1e-5f), "too long tracks shrink");

            var english = Track((0f, 1), (0.05f, 7), (0.448f, 1)); // EN FILLER: clip 0.482 s, speech ~0.02–0.46 s
            Assert.That(english.FitToVoice(0.02f, 0.46f), Is.SameAs(english), "within tolerance: untouched");
            Assert.That(english.FitToVoice(0, 0), Is.SameAs(english), "no voiced span: untouched");
        }

        [Test]
        public void VoiceSpan_FindsLoudPart()
        {
            const int rate = 1000;
            var pcm = new float[2 * rate]; // 2 s stereo-interleaved → 1 s
            for (int i = 0; i < pcm.Length; i++)
            {
                float t = (i / 2) / (float)rate;
                pcm[i] = t >= 0.2f && t < 0.7f ? 0.3f * (float)Math.Sin(i) : 0.0005f;
            }
            Assert.That(VoiceSpan.TryFind(pcm, 2, rate, out float s, out float e), Is.True);
            Assert.That(s, Is.EqualTo(0.2f).Within(0.021f));
            Assert.That(e, Is.EqualTo(0.7f).Within(0.021f));
            Assert.That(VoiceSpan.TryFind(new float[rate], 1, rate, out _, out _), Is.False, "silence");
        }

        [Test]
        public void Mouth_FittedTrack_MovesUntilVoiceEnds()
        {
            var mouth = new MouthMotion(GnarFiller(), openViseme: 9);
            Assert.That(mouth.FitToVoice(0.08f, 0.98f), Is.True);
            for (float t = 0; t < 1.2f; t += 0.02f) mouth.Update(t, 0, speaking: true, dt: 0.02f);
            Assert.That(mouth.LastOpenTime, Is.EqualTo(0.98f).Within(0.1f), "jaw moves to the end of the voice");
        }

        [Test]
        public void HeadLook_ChainsClampAndTiming()
        {
            var hero = HeadLook.ChainOf("Hero");
            Assert.That(hero.Select(b => b.Suffix), Is.EqualTo(new[] { "Head_Neck_1", "Head_Neck_2", "Head_Neck_3", "Head_Head_1" }));
            Assert.That(HeadLook.ChainOf("Wolf"), Is.Null, "animals: no chain");
            var head = hero[3];
            float yaw = 1.5f, pitch = 0;
            HeadLook.Clamp(head, ref yaw, ref pitch);
            Assert.That(yaw, Is.EqualTo(0.873f).Within(1e-4f), "a goal behind is held at the limit, not released");
            yaw = 0.873f; pitch = 0.349f;
            HeadLook.Clamp(head, ref yaw, ref pitch);
            Assert.That(yaw / 0.873f * (yaw / 0.873f) + pitch / 0.349f * (pitch / 0.349f), Is.EqualTo(1f).Within(1e-4f), "ellipse edge");
            yaw = 0.3f; pitch = 0.2f;
            HeadLook.Clamp(hero[1], ref yaw, ref pitch);
            Assert.That((yaw, pitch), Is.EqualTo((0.087f, 0f)), "neck: yaw only, no pitch range");
            Assert.That(HeadLook.FollowStep(0.7f, 0.02f), Is.EqualTo(6 * 0.7f * 0.02f).Within(1e-6f));
            Assert.That(HeadLook.FollowStep(1f, 1f), Is.EqualTo(1f));
            Assert.That(HeadLook.FadeOut(1f, 0.7f, 0.5f), Is.EqualTo(0.3f).Within(1e-5f), "fade 2·speed per second");
            Assert.That(HeadLook.StaysSeated(sitting: true, heroInView: true), Is.True);
            Assert.That(HeadLook.StaysSeated(sitting: true, heroInView: false), Is.False);
            Assert.That(HeadLook.StaysSeated(sitting: false, heroInView: true), Is.False, "standing: body turn, no head look");
            Assert.That(HeadLook.TurnsToFace("Human"), Is.True);
            Assert.That(HeadLook.TurnsToFace("Pig"), Is.False, "CP: bison, pig and cow keep their facing");
            Assert.That(HeadLook.TurnsToFace("Bison"), Is.False);
            Assert.That(HeadLook.TurnsToFace("Cow"), Is.False);
        }

        [Test]
        public void StageDirection_FromSayId2()
        {
            Assert.That(Myrtana.Import.G3.InfoCommandTranslator.StageDirection(" SD_Point "), Is.EqualTo("SD_Point"));
            Assert.That(Myrtana.Import.G3.InfoCommandTranslator.StageDirection(""), Is.Null);
            Assert.That(Myrtana.Import.G3.InfoCommandTranslator.StageDirection("It_Gold"), Is.Null);
            Assert.That(TalkGestures.PickPhase(new Random(1), true, "SD_Point"), Is.EqualTo("Point"));
            Assert.That(TalkGestures.PickPhase(new Random(1), false, "SD_Point"), Is.EqualTo(TalkGestures.Ambient), "busy hands");
        }

        [Test]
        public void VoiceSpan_SplitsAtPauses()
        {
            const int rate = 1000;
            var pcm = new float[2 * rate];
            for (int i = 0; i < pcm.Length; i++)
            {
                float t = i / (float)rate;
                bool voiced = (t >= 0.1f && t < 0.5f) || (t >= 0.58f && t < 0.9f) || (t >= 1.3f && t < 1.8f); // 0.08 s gap, 0.4 s pause
                pcm[i] = voiced ? 0.3f * (float)Math.Sin(i) : 0.0005f;
            }
            var parts = VoiceSpan.FindSegments(pcm, 1, rate);
            Assert.That(parts.Count, Is.EqualTo(2), "short gaps are bridged, the 0.4 s pause splits");
            Assert.That(parts[0].Start, Is.EqualTo(0.1f).Within(0.021f));
            Assert.That(parts[0].End, Is.EqualTo(0.9f).Within(0.021f));
            Assert.That(parts[1].Start, Is.EqualTo(1.3f).Within(0.021f));
            Assert.That(VoiceSpan.TryFind(pcm, 1, rate, out float s, out float e), Is.True);
            Assert.That((s, e), Is.EqualTo((parts[0].Start, parts[1].End)));
        }

        // RU INFO_Orc_Warrior_Hard_Gnar_SKALVERAM320060223210833_2: an 8.00 s clip whose G3 track lasts 3.36 s.
        static LipSyncTrack Skalveram() => Track((0f, 7), (0.109f, 10), (0.219f, 6), (0.329f, 7), (0.398f, 9), (0.538f, 1),
            (0.629f, 13), (0.813f, 12), (0.967f, 9), (1.093f, 11), (1.22f, 6), (1.296f, 1), (1.297f, 4), (1.333f, 13),
            (1.407f, 12), (1.468f, 11), (1.529f, 7), (1.566f, 9), (1.653f, 8), (1.757f, 7), (1.81f, 8), (1.915f, 1),
            (2.234f, 1), (2.308f, 11), (2.383f, 7), (2.428f, 7), (2.473f, 11), (2.548f, 4), (2.593f, 1), (2.594f, 11),
            (2.684f, 7), (2.738f, 7), (2.793f, 7), (2.833f, 11), (2.902f, 1), (2.903f, 6), (2.994f, 4), (3.086f, 13),
            (3.269f, 1), (3.361f, 1));

        // Its voiced parts (ffmpeg RMS, 20 ms windows, pauses >= 0.15 s split).
        static readonly VoiceSegment[] SkalveramVoice =
        {
            new VoiceSegment(0.14f, 1.46f), new VoiceSegment(2.16f, 6.98f), new VoiceSegment(7.24f, 7.88f),
        };

        [Test]
        public void Mouth_LongFittedLine_FollowsTheVoiceToItsEnd()
        {
            var mouth = new MouthMotion(Skalveram(), openViseme: 9);
            Assert.That(mouth.FitToVoice(SkalveramVoice), Is.True);
            Assert.That(mouth.Gated, Is.True);
            int openInPause = 0, closedInSpeech = 0, speechFrames = 0;
            for (float t = 0; t < 8.2f; t += 0.02f)
            {
                var s = mouth.Update(t, level: 0.1f, speaking: t < 8f, dt: 0.02f);
                bool open = s.Amount > 0.5f && s.To != LipSyncTrack.RestViseme && s.Weight > 0.5f;
                bool inPause = t > 1.46f + 0.1f && t < 2.16f - 0.02f;
                bool inSpeech = SkalveramVoice.Any(v => t > v.Start + 0.1f && t < v.End - 0.1f);
                if (inPause && open) openInPause++;
                if (inSpeech) { speechFrames++; if (!open) closedInSpeech++; }
            }
            Assert.That(openInPause, Is.Zero, "closed in the voice's pause");
            Assert.That(closedInSpeech, Is.LessThan(speechFrames / 4), "moving while the voice is heard");
            Assert.That(mouth.LastOpenTime, Is.EqualTo(7.88f).Within(0.1f), "the mouth works until the voice ends, not 2 s early");
        }

        [Test]
        public void Mouth_MatchingTrack_IsNotGated()
        {
            var english = Track((0f, 1), (0.05f, 7), (0.448f, 1));
            var mouth = new MouthMotion(english, openViseme: 9);
            Assert.That(mouth.FitToVoice(new[] { new VoiceSegment(0.02f, 0.2f), new VoiceSegment(0.4f, 0.46f) }), Is.False);
            Assert.That(mouth.Gated, Is.False);
        }

        // editor-runner-9: Tippler's Russian line _0 (track 0–2.62 s, voice 0.08–2.56 s) was kept unfitted and logged
        // "mouth last open at 0,00 s".
        [Test]
        public void Mouth_KeptTrack_MovesToItsEnd()
        {
            var track = Track((0f, 7), (0.066f, 9), (0.199f, 7), (0.28f, 10), (0.416f, 7), (0.498f, 10), (0.569f, 3), (0.641f, 9), (0.785f, 7), (0.857f, 1), (0.858f, 7), (0.932f, 5), (1.007f, 1), (1.173f, 8), (1.339f, 10), (1.505f, 7), (1.605f, 7), (1.705f, 1), (1.706f, 7), (1.775f, 9), (1.915f, 6), (1.994f, 4), (2.073f, 10), (2.204f, 7), (2.284f, 4), (2.397f, 13), (2.623f, 1));
            var mouth = new MouthMotion(track, openViseme: 9);
            Assert.That(mouth.FitToVoice(new[] { new VoiceSegment(0.08f, 2.56f) }), Is.False, "kept");
            for (float t = 0; t < 2.64f; t += 0.02f) mouth.Update(t, 0.1f, speaking: true, dt: 0.02f);
            MouthShape s = default;
            // the clip is over: the voice player reports time 0 while the mouth eases out
            for (int i = 0; i < 10; i++) s = mouth.Update(0f, 0f, speaking: false, dt: 0.02f);
            Assert.That(mouth.LastOpenTime, Is.EqualTo(2.6f).Within(0.1f), "not overwritten by the ease-out at time 0");
            Assert.That(s.To, Is.EqualTo(LipSyncTrack.RestViseme), "eases out from the track's end, not its first (open) key");
        }

        [Test]
        public void Mouth_WithoutTrack_OpensWithLoudness()
        {
            var mouth = new MouthMotion(null, openViseme: 9);
            MouthShape s = default;
            for (int i = 0; i < 20; i++) s = mouth.Update(0, 0.5f, true, 0.02f);
            Assert.That(s.To, Is.EqualTo(9));
            Assert.That(s.Weight, Is.GreaterThan(0.95f), "loud → open");
            for (int i = 0; i < 40; i++) s = mouth.Update(0, 0.0f, true, 0.02f);
            Assert.That(s.Weight, Is.LessThan(0.05f), "quiet → closed");
        }

        static DialogShotPreset Preset(string name) => DialogShotPresets.All.First(p => p.Name == name);

        // Partner (speaker) at +Z, hero (listener) at the origin, heads at 1.7 m.
        static readonly Vector3 Speaker = new Vector3(0, 1.7f, 2), Listener = new Vector3(0, 1.7f, 0);

        [Test]
        public void ShotGeometry_CloseShots_StandAtTheListener()
        {
            var face = DialogShotGeometry.CameraPosition(Speaker, Listener, DialogShotPresets.Face);
            Assert.That(Vector3.Distance(face, new Vector3(0, 1.7f, 0.5f)), Is.LessThan(1e-4f), "half a metre in front of the listener");

            var cam = DialogShotGeometry.CameraPosition(Speaker, Listener, Preset("Shoulder1R"));
            Assert.That(cam.Z, Is.LessThan(0), "behind the listener");
            Assert.That(cam.X, Is.GreaterThan(0), "+ azimuth = right of the line listener → speaker (+X looking along +Z)");
            Assert.That(cam.Y, Is.GreaterThan(Listener.Y), "elevated");
            Assert.That(Vector3.Distance(cam, Listener), Is.EqualTo(1.05f).Within(1e-4f));
            Assert.That(Vector3.Distance(cam, Speaker), Is.LessThan(3.1f), "a close shot, not a wide one");

            Assert.That(DialogShotGeometry.LookTarget(Speaker, Listener, Preset("Face2L")), Is.EqualTo(Speaker));
            Assert.That(DialogShotGeometry.LookTarget(Speaker, Listener, Preset("Shoulder2L")), Is.EqualTo(new Vector3(0, 1.7f, 1)));
        }

        [Test]
        public void ShotGeometry_Side_StandsBesideThePair()
        {
            var l = DialogShotGeometry.CameraPosition(Speaker, Listener, Preset("SideL"));
            var r = DialogShotGeometry.CameraPosition(Speaker, Listener, Preset("SideR"));
            Assert.That(Vector3.Distance(l, new Vector3(-2.6f, 1.7f, 1)), Is.LessThan(1e-4f), "1.3 × the 2 m distance left of the midpoint");
            Assert.That(Vector3.Distance(r, new Vector3(2.6f, 1.7f, 1)), Is.LessThan(1e-4f));
        }

        [Test]
        public void ShotGeometry_Dolly_MovesBetweenItsEndsIn18s()
        {
            var lerp = Preset("Lerp1");
            var start = DialogShotGeometry.CameraPosition(Speaker, Listener, lerp, 0);
            var mid = DialogShotGeometry.CameraPosition(Speaker, Listener, lerp, 9);
            var end = DialogShotGeometry.CameraPosition(Speaker, Listener, lerp, 18);
            var later = DialogShotGeometry.CameraPosition(Speaker, Listener, lerp, 60);
            Assert.That(start.X, Is.GreaterThan(1), "starts right of the listener");
            Assert.That(end.X, Is.LessThan(-1), "ends on the left");
            Assert.That(Vector3.Distance(mid, (start + end) * 0.5f), Is.LessThan(1e-3f), "eased, symmetric");
            Assert.That(later, Is.EqualTo(end));
            Assert.That(Vector3.Distance(start, Listener), Is.EqualTo(2.5f).Within(1e-4f));
            Assert.That(DialogShotGeometry.CameraPosition(Speaker, Listener, Preset("Lerp2"), 0), Is.EqualTo(start), "Lerp2 is the same move");
        }

        [Test]
        public void ShotGeometry_WorldSide_MirrorsBetweenSpeakers()
        {
            var right = Preset("Shoulder1R");
            // Partner speaks: the listener is the hero, the line listener → speaker is the hero → partner axis.
            Assert.That(DialogShotGeometry.WorldSideOf(right, speakerIsHero: false), Is.EqualTo(1));
            Assert.That(DialogShotGeometry.WorldSideOf(right, speakerIsHero: true), Is.EqualTo(-1));
            Assert.That(DialogShotGeometry.WorldSide(Listener, Speaker, DialogShotGeometry.CameraPosition(Speaker, Listener, right)), Is.EqualTo(1));
            // The hero speaks: the same preset stands on the other side of the hero → partner axis.
            Assert.That(DialogShotGeometry.WorldSide(Listener, Speaker, DialogShotGeometry.CameraPosition(Listener, Speaker, right)), Is.EqualTo(-1));
            Assert.That(DialogShotGeometry.WorldSide(Listener, Speaker, DialogShotGeometry.CameraPosition(Speaker, Listener, Preset("SideL"))),
                        Is.EqualTo(DialogShotGeometry.WorldSideOf(Preset("SideL"), speakerIsHero: false)));
        }

        [Test]
        public void ShotGeometry_SpeakerDistance_GrowsWithTheHeadDistance()
        {
            // The listener-anchored shots stand beside/behind the listener, so camera → speaker = their offset + the gap.
            // Editor round 2 (heads 2.6 m apart, hero teleported 2.5 m from Gnar) measured Face 2.1, Shoulder2 4.3,
            // SideL 3.6, Lerp 3.4–4.6 m: exactly these formulas, nothing else moved the camera.
            var l = new Vector3(0, 1.7f, 0);
            float D(float gap, string preset, float age = 0) =>
                DialogShotGeometry.SpeakerDistance(new Vector3(0, 1.7f, gap), l, Preset(preset), age);
            Assert.That(D(2.6f, "Face"), Is.EqualTo(2.1f).Within(0.01f));
            Assert.That(D(2.6f, "Shoulder2L"), Is.EqualTo(4.41f).Within(0.05f));
            Assert.That(D(2.6f, "SideL"), Is.EqualTo(3.62f).Within(0.05f));
            Assert.That(D(2.6f, "Lerp1"), Is.EqualTo(4.0f).Within(0.05f));
            Assert.That(D(2.6f, "Lerp3", DialogShotGeometry.DollySeconds), Is.EqualTo(4.71f).Within(0.05f));
            // G3's own spacing (an NPC steps back below 2 m, ZS_Talk): the closer shots.
            Assert.That(D(2f, "Face"), Is.EqualTo(1.5f).Within(0.01f));
            Assert.That(D(2f, "Shoulder1R"), Is.EqualTo(2.83f).Within(0.05f));
            Assert.That(D(2f, "Shoulder2R"), Is.EqualTo(3.89f).Within(0.05f));
            Assert.That(D(2f, "SideR"), Is.EqualTo(2.79f).Within(0.05f));
        }

        static int Count(Func<DialogShotDirector, bool> outcome, int seeds = 2000)
        {
            int n = 0;
            for (int seed = 0; seed < seeds; seed++)
                if (outcome(new DialogShotDirector(new Random(seed)))) n++;
            return n;
        }

        [Test]
        public void Director_FirstShot_IsOverTheShoulderMostly()
        {
            int shoulder2 = Count(d => d.NextShot(false, false, null) && d.Current.Name.StartsWith("Shoulder2"));
            int shoulder1 = Count(d => d.NextShot(false, false, null) && d.Current.Name.StartsWith("Shoulder1"));
            int side = Count(d => d.NextShot(false, false, null) && d.Current.Name.StartsWith("Side"));
            Assert.That(shoulder2 + shoulder1 + side, Is.EqualTo(2000), "nothing else opens a conversation");
            Assert.That(shoulder2 / 2000.0, Is.EqualTo(0.7).Within(0.04));
            Assert.That(shoulder1 / 2000.0, Is.EqualTo(0.225).Within(0.04));
            Assert.That(side / 2000.0, Is.EqualTo(0.075).Within(0.03));
            Assert.That(Count(d => d.NextShot(false, false, null, partnerDistance: 25) && d.Current.Name.StartsWith("Side")), Is.Zero,
                        "no first Side shot 20 m apart");
            Assert.That(Count(d => d.NextShot(false, false, null, bigCreature: true) && d.Current.Name.StartsWith("Shoulder1")), Is.Zero,
                        "no Shoulder1 on a big creature");
        }

        [Test]
        public void Director_AlternatingSpeakers_ShoulderShots_KeepTheSide_NoFaceOrDollyWhenClose()
        {
            var d = new DialogShotDirector(new Random(7));
            bool hero = false;
            int shoulder2 = 0;
            for (int line = 0; line < 400; line++)
            {
                hero = !hero;
                int sideBefore = d.WorldSide;
                Assert.That(d.NextShot(hero, sameSpeaker: false, null, partnerDistance: 1.8f), Is.True);
                Assert.That(d.Current.Group, Is.Not.EqualTo(ShotGroup.Wide), "dollies only in monologues");
                Assert.That(d.Current.Name.StartsWith("Face"), Is.False, "face shots only for partners more than 2 m apart");
                if (d.Current.Name.StartsWith("Shoulder2")) shoulder2++;
                if (sideBefore != 0) Assert.That(DialogShotGeometry.WorldSideOf(d.Current, hero), Is.EqualTo(sideBefore), $"line {line}: {d.Current} crosses the line");
                Assert.That(d.Fov, Is.InRange(DialogShotPresets.MinFov, DialogShotPresets.MaxFov));
                d.Advance(3);
            }
            Assert.That(shoulder2, Is.GreaterThan(400 / 2), "Shoulder2 is G3's default shot");
        }

        [Test]
        public void Director_FarPartners_FaceShotAfterASideShot()
        {
            static bool NotSideOrShoulder1(DialogShotPreset p) => !p.Name.StartsWith("Side") && !p.Name.StartsWith("Shoulder1");
            foreach (var (gap, expected) in new[] { (2.5f, "Face"), (1.5f, "Shoulder2") })
            {
                int checkedSeeds = 0;
                for (int seed = 0; seed < 300; seed++)
                {
                    var d = new DialogShotDirector(new Random(seed));
                    d.NextShot(false, false, p => p.Name.StartsWith("Side"));       // line 1: a Side shot (when rolled)
                    d.NextShot(true, false, p => p.Name.StartsWith("Shoulder2"));  // line 2: over the shoulder
                    if (!d.Previous.Name.StartsWith("Side")) continue;
                    Assert.That(d.NextShot(false, false, NotSideOrShoulder1, gap), Is.True);
                    Assert.That(d.Current.Name, Does.StartWith(expected), $"{gap} m apart");
                    checkedSeeds++;
                }
                Assert.That(checkedSeeds, Is.GreaterThan(5));
            }
        }

        [Test]
        public void Director_Monologue_KeepsTheShot_OrCutsToFaceOrDollyWhenFar()
        {
            // Partner opens (Shoulder2), says a 2nd line (new shot), then keeps talking: from the 3rd line on G3 keeps the
            // shot, except after a shoulder shot with the partners more than 2 m apart: Face2 (30 %) or a dolly (35 %).
            bool Third(DialogShotDirector d, float gap, Func<DialogShotPreset, bool> outcome)
            {
                d.NextShot(false, false, p => p.Name == "Shoulder2R", gap);
                d.NextShot(false, true, p => p.Name.StartsWith("Shoulder2"), gap);
                Assert.That(d.SameSpeakerRun, Is.EqualTo(1));
                var before = d.Current;
                bool changed = d.NextShot(false, true, null, gap);
                Assert.That(d.SameSpeakerRun, Is.EqualTo(2));
                return outcome(changed ? d.Current : null) && (changed || d.Current == before);
            }
            Assert.That(Count(d => Third(d, 1.5f, p => p == null)), Is.EqualTo(2000), "close together: the shot stays");
            int kept = Count(d => Third(d, 3f, p => p == null));
            int face = Count(d => Third(d, 3f, p => p != null && p.Name.StartsWith("Face2")));
            int dolly = Count(d => Third(d, 3f, p => p != null && (p.Name == "Lerp1" || p.Name == "Lerp3")));
            Assert.That(kept + face + dolly, Is.EqualTo(2000));
            // G3 asks FindPreset twice (strict, then lenient), so a "keep" rolls once more: 0.3 + 0.35·0.3 and 0.35 + 0.35·0.35.
            Assert.That(face / 2000.0, Is.EqualTo(0.405).Within(0.04));
            Assert.That(dolly / 2000.0, Is.EqualTo(0.4725).Within(0.04));
        }

        [Test]
        public void Director_KeepsAYoungDolly_ThenFlipsToItsEndSide()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var d = new DialogShotDirector(new Random(seed));
                d.NextShot(false, false, null, 3);
                d.NextShot(false, true, null, 3);
                d.NextShot(false, true, p => p.Group == ShotGroup.Wide, 3);
                if (d.Current.Group != ShotGroup.Wide) continue;
                var dolly = d.Current;
                Assert.That(DialogShotGeometry.WorldSideOf(dolly, false), Is.EqualTo(d.WorldSide), "starts on the current side");
                d.Advance(5);
                Assert.That(d.NextShot(false, true, null, 3), Is.False, "a young dolly stays for the same speaker");
                d.Advance(6);
                Assert.That(d.NextShot(true, false, p => p.Name.StartsWith("Shoulder2"), 3), Is.True);
                Assert.That(d.WorldSide, Is.EqualTo(DialogShotGeometry.WorldSideOf(dolly.DollyEnd, false)), "the dolly crossed the line");
                Assert.That(DialogShotGeometry.WorldSideOf(d.Current, true), Is.EqualTo(d.WorldSide));
                return;
            }
            Assert.Fail("no seed produced a dolly");
        }

        [Test]
        public void Director_BlockedViews_SecondPassStillFrames()
        {
            var d = new DialogShotDirector(new Random(3));
            Assert.That(d.NextShot(true, false, p => false), Is.True);
            Assert.That(d.Current.Name, Does.StartWith("Shoulder").Or.StartWith("Side"), "first shot: G3's second, lenient pass");
            Assert.That(d.NextShot(false, false, p => false), Is.True);
            Assert.That(d.Current, Is.Not.Null);
        }

        [Test]
        public void Gestures_RandomPhaseUnlessBusyOrDirected()
        {
            var r = new Random(5);
            Assert.That(TalkGestures.PickPhase(r, handsFreeAndStanding: false), Is.EqualTo("Ambient"));
            Assert.That(TalkGestures.PickPhase(r, true, "SD_Thankful"), Is.EqualTo("Approve"));
            var seen = Enumerable.Range(0, 300).Select(_ => TalkGestures.PickPhase(r, true)).Distinct().Count();
            Assert.That(seen, Is.EqualTo(TalkGestures.Phases.Count));
            Assert.That(TalkGestures.PickVariant(r, i => i == 2 || i == 4), Is.EqualTo(2).Or.EqualTo(4));
            Assert.That(TalkGestures.PickVariant(r, i => false), Is.EqualTo(-1));
        }

        [Test]
        public void Gestures_ConsecutiveLinesChangeThePhase()
        {
            var r = new Random(11);
            string last = null;
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 300; i++)
            {
                string phase = TalkGestures.PickPhase(r, true, previous: last);
                Assert.That(phase, Is.Not.EqualTo(last));
                seen.Add(phase);
                last = phase;
            }
            Assert.That(seen.Count, Is.EqualTo(TalkGestures.Phases.Count));
            Assert.That(TalkGestures.PickPhase(r, true, "SD_Sad", previous: "Sad"), Is.EqualTo("Sad"), "a stage direction wins");
        }
    }
}
