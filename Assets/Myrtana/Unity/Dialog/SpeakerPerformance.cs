using System.IO;
using G3.Unity.Characters;
using Myrtana.Ui.Dialog;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// One character's part in a conversation (Docs/dialog-presentation.md §3–4): while it speaks a line it plays a
    /// talk gesture (a <c>Say_&lt;Phase&gt;</c> motion; the motion player is claimed from its animator for the gesture and
    /// handed back when the gesture ends) and moves its lips — the talking head (<see cref="G3TalkingFace"/>) follows the
    /// line's G3 visemes, or the voice's loudness when the line has none.
    /// </summary>
    public sealed class SpeakerPerformance
    {
        const float GestureFade = 0.3f;

        readonly Transform _root;
        readonly G3MotionPlayer _motion;
        readonly G3TalkingFace _face;
        readonly System.Random _random;
        MouthMotion _mouth;
        string _voiceName = "-";
        float _clipLength;
        bool _speaking, _claimed;

        public SpeakerPerformance(Transform root, System.Random random)
        {
            _root = root;
            _random = random;
            _motion = root.GetComponentInChildren<G3MotionPlayer>();
            _face = G3TalkingFace.Wear(_motion != null ? _motion.gameObject : root.gameObject);
        }

        public Transform Root => _root;
        public bool HasFace => _face != null;
        public string Gesture { get; private set; }
        /// <summary>The listen loop playing (<c>&lt;Rig&gt;_Stand_None_None_P0_Listen_Loop_N_Fwd_00</c>), null when none.</summary>
        public string Listening { get; private set; }
        /// <summary>The motion player is held for a talk gesture or the listen loop.</summary>
        public bool Claimed => _claimed;
        /// <summary>The talk gesture playing loops (<c>Say_Ambient</c>): it would never end by itself.</summary>
        public bool GestureLoops => _claimed && Gesture != null && _motion != null && _motion.CurrentLoops;

        /// <summary>
        /// Starts a line; <paramref name="voicePath"/> is the clip (its .lip beside it), null for a silent line;
        /// <paramref name="stage"/> the line's stage direction (<c>SD_*</c>, picks the gesture), null for a random one.
        /// </summary>
        public void Speak(string voicePath, string stage = null)
        {
            _speaking = true;
            _mouth = new MouthMotion(LoadTrack(voicePath), _face != null ? _face.OpenViseme : LipSyncTrack.RestViseme);
            _voiceName = string.IsNullOrEmpty(voicePath) ? "-" : Path.GetFileNameWithoutExtension(voicePath);
            ReleaseGesture(); // fix-200: a listen loop (or the last line's gesture) gives way to this line's gesture
            PlayGesture(stage);
        }

        /// <summary>
        /// The line is over: the mouth eases closed, the gesture plays out. <paramref name="howEnded"/> (diagnostics):
        /// "finished", or "skipped at t s" when the line was cut before its clip ended.
        /// </summary>
        public void StopSpeaking(string howEnded = null)
        {
            if (_speaking && _mouth != null && _mouth.HasTrack)
                Debug.Log($"[Myrtana] lip {_voiceName}: line over ({howEnded ?? "ended"}), mouth last open at {_mouth.LastOpenTime:0.00} s " +
                          $"(track ends {_mouth.Track.Duration:0.00} s, clip {_clipLength:0.00} s{(_mouth.Gated ? ", gated by the voice" : "")})");
            _speaking = false;
            // fix-200: Say_Ambient is a loop; it ends with the line (a one-shot gesture plays out)
            if (DialogRoles.AtLineEnd(spoke: true, GestureLoops) == PerformerCue.Idle) ReleaseGesture();
        }

        /// <summary>
        /// fix-200: another participant speaks this line. Any gesture of an earlier line stops, the mouth eases closed, and
        /// a standing, hands-free listener plays its stance's listen loop (G3 <c>Listen_Loop</c>; none: the animator's idle).
        /// </summary>
        public void Listen()
        {
            if (_speaking) StopSpeaking("another speaker");
            if (Listening != null && _claimed) return;
            ReleaseGesture();
            if (_motion == null || _motion.library == null || !MayAct() || !HandsFreeAndStanding()) return;
            var key = new G3MotionKey(_motion.library.rig, ListenAction, ListenPhase);
            if (_motion.library.Find(key) == null) return;
            _motion.Claim(this);
            if (!_motion.Play(key, GestureFade))
            {
                _motion.Release(this);
                return;
            }
            _claimed = true;
            Listening = key.ToString();
        }

        const string ListenAction = "Listen", ListenPhase = "Loop";

        /// <summary>
        /// The line's clip has started: lays the viseme track over the speech actually heard
        /// (<see cref="LipSyncTrack.FitToVoice"/>) and logs clip length vs. track length.
        /// </summary>
        public void FitLips(float clipLength, System.Collections.Generic.IReadOnlyList<VoiceSegment> voiced)
        {
            _clipLength = clipLength;
            if (_mouth == null || !_mouth.HasTrack) return;
            float trackStart = _mouth.Track.SpeechStart, trackEnd = _mouth.Track.Duration;
            bool hasVoicedSpan = voiced != null && voiced.Count > 0;
            bool fitted = hasVoicedSpan && _mouth.FitToVoice(voiced);
            Debug.Log($"[Myrtana] lip {_voiceName}: clip {clipLength:0.00} s, voice " +
                      (hasVoicedSpan ? $"{voiced[0].Start:0.00}-{voiced[voiced.Count - 1].End:0.00} s in {voiced.Count} part(s)" : "unknown") +
                      $", track {trackStart:0.00}-{trackEnd:0.00} s -> " +
                      (fitted ? $"fitted {_mouth.Track.SpeechStart:0.00}-{_mouth.Track.Duration:0.00} s" : "kept"));
        }

        /// <summary>Per frame: lips from the voice (time and loudness of the playing clip).</summary>
        public void Update(float voiceTime, float voiceLevel, bool voicePlaying, float dt)
        {
            if (_claimed && (_motion == null || _motion.CurrentFinished)) ReleaseGesture();
            if (_mouth == null || _face == null) return;
            var shape = _mouth.Update(voiceTime, voiceLevel, _speaking && voicePlaying, dt);
            _face.SetMouth(shape.From, shape.To, shape.Weight, shape.Amount);
        }

        /// <summary>End of the conversation: static head back, animator back in charge.</summary>
        public void Dispose()
        {
            ReleaseGesture();
            if (_face != null) _face.Remove();
        }

        void PlayGesture(string stage)
        {
            Gesture = null;
            if (_motion == null || _motion.library == null) return;
            string rig = _motion.library.rig;
            if (!MayAct()) return;
            string phase = TalkGestures.PickPhase(_random, HandsFreeAndStanding(), stage); // G3: GetRandomNumber(11), repeats allowed
            if (!TryPlay(rig, phase) && phase != TalkGestures.Ambient) TryPlay(rig, TalkGestures.Ambient);
        }

        /// <summary>
        /// A seated speaker (G3: talks from its seat) would need the Sit* Say_Ambient motions, which are not imported: it
        /// keeps its sitting loop; an NPC using an item keeps its use motion; fix-153: the hero drinks what an info gave him
        /// (HeroConsumption.ShowUse holds his motions until it ends).
        /// </summary>
        bool MayAct()
        {
            var npc = _root.GetComponentInChildren<G3NpcAnimator>();
            if (npc != null && (npc.IsSeated || npc.IsUsingItem)) return false;
            if (npc == null && HeroConsumption.Instance != null && HeroConsumption.Instance.Busy) return false;
            return true;
        }

        /// <summary>G3 plays only <c>Say_Ambient</c> when something is in the hands or the speaker does not stand.</summary>
        bool HandsFreeAndStanding()
        {
            var npc = _root.GetComponentInChildren<G3NpcAnimator>();
            return npc == null || (!npc.IsInteracting && string.IsNullOrEmpty(npc.LeftItem) && string.IsNullOrEmpty(npc.RightItem));
        }

        bool TryPlay(string rig, string phase)
        {
            var key = new G3MotionKey(rig, TalkGestures.Action, phase);
            int variant = TalkGestures.PickVariant(_random, i => _motion.library.Find(key.With(TalkGestures.Action, phase, index: i)) != null);
            if (variant < 0) return false;
            var k = key.With(TalkGestures.Action, phase, index: variant);
            _motion.Claim(this);
            if (!_motion.Play(k, GestureFade, restart: true))
            {
                _motion.Release(this);
                return false;
            }
            _claimed = true;
            Listening = null;
            Gesture = k.ToString();
            return true;
        }

        /// <summary>Ends a running talk gesture at once (an info script has the speaker use an item next).</summary>
        public void StopGesture() => ReleaseGesture();

        void ReleaseGesture()
        {
            if (!_claimed) return;
            _claimed = false;
            Listening = null;
            if (_motion != null) _motion.Release(this);
        }

        static LipSyncTrack LoadTrack(string voicePath)
        {
            if (string.IsNullOrEmpty(voicePath)) return null;
            string path = Path.ChangeExtension(voicePath, LipSyncTrack.FileExtension);
            try { return File.Exists(path) ? LipSyncTrack.Read(File.ReadAllText(path)) : null; }
            catch (InvalidDataException e)
            {
                Debug.LogWarning($"[Myrtana] lip sync {path}: {e.Message}");
                return null;
            }
        }
    }
}
