using System;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Story;
using Myrtana.Ui.Dialog;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// How a conversation looks (Docs/dialog-presentation.md): the dialog camera (<see cref="DialogCameraRig"/>) frames
    /// the speaker of every line, and hero and partner act their lines (<see cref="SpeakerPerformance"/>: talk gesture,
    /// lip sync from the voice). <see cref="ConversationStaging"/> turns them to each other and locks the controls;
    /// <see cref="DialogView"/> reports the lines. Everything is undone when the conversation ends.
    /// </summary>
    public sealed class DialogPresentation : MonoBehaviour, ISessionView
    {
        G3Player _player;
        NpcViewDirectory _views;
        DialogView _lines;
        VoicePlayer _voice;
        DialogCameraRig _camera;
        readonly System.Random _random = new System.Random();
        IDisposable _started, _ended, _used, _wore;
        GameSession _session;

        SpeakerPerformance _hero, _partner, _speaking;
        ActorId _partnerId;
        // fix-200: everyone acting in this conversation (hero, partner, third NPCs who joined in with a line), by actor
        readonly System.Collections.Generic.Dictionary<string, SpeakerPerformance> _cast =
            new System.Collections.Generic.Dictionary<string, SpeakerPerformance>(StringComparer.Ordinal);

        public DialogCameraRig Camera => _camera;
        public SpeakerPerformance Hero => _hero;
        public SpeakerPerformance Partner => _partner;
        /// <summary>The conversation's voice player (the one whose clip drives the lips).</summary>
        public VoicePlayer Voice => _voice;
        /// <summary>Lines started in this conversation by the hero and by the partner (diagnostics).</summary>
        public int HeroLines { get; private set; }
        public int PartnerLines { get; private set; }

        public void Init(G3Player player, NpcViewDirectory views, DialogView lines, VoicePlayer voice)
        {
            _player = player;
            _views = views;
            _lines = lines;
            _voice = voice;
            _voice.LogPlayback = true;
            var playerCamera = player.playerCamera != null ? player.playerCamera.GetComponent<G3PlayerCamera>() : null;
            _camera = new DialogCameraRig(playerCamera, _random);
            _lines.LineStarted += OnLineStarted;
            _lines.LineEnded += OnLineEnded;
        }

        public void Bind(GameSession session)
        {
            Unsubscribe();
            End();
            // fix-76: the camera takes over at the first line or real choice (G3 SetDialogRoles / ShowDialogManager), not at
            // the talk key: a talk with nothing to say runs only its exit and keeps the player camera
            _started = session.Events.Subscribe<DialogEngaged>(e => Begin(e.Partner));
            _ended = session.Events.Subscribe<DialogEnded>(e => End());
            _used = session.Events.Subscribe<ActorUsedItem>(OnActorUsedItem);
            _wore = session.Events.Subscribe<ActorWoreItem>(OnActorWoreItem);
            _session = session;
        }

        /// <summary>
        /// An info script has an actor use an item ("Use It_Booze", G3 _AI_UseInventoryItem; the conversation waits for it,
        /// UseItemHandler): an NPC plays the standing use motion with the item in its hand (take out, drink/eat/read, put
        /// away; orcs and humans alike, their rig's <c>&lt;Rig&gt;_Stand_None_&lt;Field&gt;_P0</c> motions), the hero his
        /// consumption motion at the player's double speed (fix-153).
        /// </summary>
        void OnActorUsedItem(ActorUsedItem e)
        {
            string field = HandField(e.Item);
            if (e.Actor.IsPlayer)
            {
                _hero?.StopGesture();
                bool shown = HeroConsumption.Instance != null && HeroConsumption.Instance.ShowUse(e.Item);
                Debug.Log($"[Myrtana] hero uses {e.Item} ({field ?? "no hand field"}) in the talk: {(shown ? "use motion" : "no use motion (busy or missing)")}");
                return;
            }
            if (!_views.TryGet(e.Actor, out var view) || view == null)
            {
                Debug.Log($"[Myrtana] {e.Actor} uses {e.Item}: no view");
                return;
            }
            var animator = view.GetComponentInChildren<G3.Unity.Characters.G3NpcAnimator>();
            if (animator == null) return;
            // The talk gesture of the line before still holds the motion player (it outlives a short line): G3 starts the
            // use motion right away, so the gesture gives way.
            if (e.Actor.Name != null && _cast.TryGetValue(e.Actor.Name, out var acting)) acting.StopGesture();
            bool started = animator.UseItemOnce(e.Item, field);
            Debug.Log($"[Myrtana] {e.Actor} uses {e.Item} ({field ?? "no hand field"}): {(started ? "use motion" : "no use motion (busy or missing)")}");
        }

        /// <summary>
        /// G3 Wear: the NPC's carried weapons are shown anew from its loadout (which now holds the worn item). A body or
        /// armour template (no item) has no view path yet: logged.
        /// </summary>
        void OnActorWoreItem(ActorWoreItem e)
        {
            if (!_views.TryGet(e.Actor, out var view) || view == null) return;
            var combat = view.GetComponent<NpcCombatView>();
            if (e.IsItem && combat != null) combat.RefreshCarried();
            Debug.Log($"[Myrtana] {e.Actor} wears {e.Item}: {(e.IsItem ? combat != null ? "carried weapons refreshed" : "no combat view" : "body/armour template, not shown")}");
        }

        /// <summary>Hand field of the use motions: the item's use type ("Potion", "Meat" …), else the prop's ("It_Letter" → "Letter").</summary>
        string HandField(string item)
        {
            if (_session != null && _session.Content.Database.TryGet(item, out Myrtana.Rules.ItemDef def) && !string.IsNullOrEmpty(def.Kind))
                return def.Kind;
            string field = G3.Unity.Characters.G3HandItems.FieldOf(item);
            if (field != null) return field;
            return item != null && item.StartsWith("It_", StringComparison.Ordinal) && item.Length > 3 ? item.Substring(3) : null;
        }

        void Unsubscribe()
        {
            _started?.Dispose();
            _ended?.Dispose();
            _used?.Dispose();
            _wore?.Dispose();
        }

        void OnDestroy()
        {
            Unsubscribe();
            End();
            if (_lines != null)
            {
                _lines.LineStarted -= OnLineStarted;
                _lines.LineEnded -= OnLineEnded;
            }
        }

        void Begin(ActorId partner)
        {
            End();
            if (!_views.TryGet(partner, out var npc) || npc == null) return; // a partner without a view: subtitles only
            _camera.Begin(_player);
            _hero = new SpeakerPerformance(_player.transform, _random);
            _partner = new SpeakerPerformance(npc, _random);
            _partnerId = partner;
            _cast[ActorId.Player.Name] = _hero;
            _cast[partner.Name] = _partner;
            _camera.Frame(npc, _player.transform, speakerIsHero: false); // the partner, until the first line
            HeroLines = PartnerLines = 0;
        }

        void End()
        {
            foreach (var p in _cast.Values) p.Dispose();
            _cast.Clear();
            _hero = _partner = _speaking = null;
            _camera?.End();
        }

        void OnLineStarted(DialogLine line, string voicePath)
        {
            if (_hero == null || _partner == null) return;
            _speaking?.StopSpeaking();
            bool hero = line.Speaker.IsPlayer;
            _speaking = Performer(line.Speaker) ?? _partner; // a speaker without a view: the partner stands in, as before
            string speakerName = _speaking == _hero ? ActorId.Player.Name : _speaking == _partner ? _partnerId.Name : line.Speaker.Name;
            // fix-200 (G3 SetDialogRoles / _AI_Say_Ex): only the speaker gestures and moves its lips; everyone else listens
            foreach (var cue in DialogRoles.LineStart(_cast.Keys, speakerName))
            {
                var p = _cast[cue.Key];
                if (cue.Value == PerformerCue.Talk) p.Speak(voicePath, line.Stage);
                else p.Listen();
            }
            string listenerName = DialogRoles.ListenerOf(speakerName, ActorId.Player.Name, _partnerId.Name);
            var listener = _cast.TryGetValue(listenerName, out var l) ? l : _hero;
            _camera.Frame(_speaking.Root, listener.Root, hero);
            if (hero) HeroLines++;
            else PartnerLines++;
            Debug.Log($"[Myrtana] dialog line {(hero ? "hero" : "partner")} #{(hero ? HeroLines : PartnerLines)}: voice " +
                      $"{(voicePath != null ? System.IO.Path.GetFileName(voicePath) : "none")}, stage {line.Stage ?? "-"}, gesture {_speaking.Gesture ?? "-"}, " +
                      $"face {_speaking.HasFace}, shot {_camera.Shot?.Name ?? "-"} (preset d {_camera.PresetSpeakerDistance:0.0} m, " +
                      $"heads {_camera.HeadDistance:0.0} m apart, run {_camera.SameSpeakerRun})");
        }

        /// <summary>The performance of <paramref name="actor"/>; a third NPC speaking a line joins the cast (null: no view).</summary>
        SpeakerPerformance Performer(ActorId actor)
        {
            if (actor.IsPlayer) return _hero;
            if (_cast.TryGetValue(actor.Name ?? "", out var p)) return p;
            if (!_views.TryGet(actor, out var view) || view == null) return null;
            p = new SpeakerPerformance(view, _random);
            _cast[actor.Name] = p;
            Debug.Log($"[Myrtana] dialog: {actor} joins the conversation");
            return p;
        }

        int _fittedClip = -1;

        float _lastClipTime;

        // DialogView stops the voice before LineEnded: "stopped" = cut short (skip key, script), else it had finished.
        void OnLineEnded() =>
            _speaking?.StopSpeaking(_voice.Status == "stopped" ? $"skipped at {_lastClipTime:0.00} s" : _voice.Status);

        void LateUpdate()
        {
            if (_hero == null) return;
            float dt = Time.deltaTime;
            if (_speaking != null && _voice.ClipSerial != _fittedClip && _voice.ClipActive)
            {
                _fittedClip = _voice.ClipSerial;
                _speaking.FitLips(_voice.ClipLength, _voice.VoicedSegments);
            }
            float time = _voice.ClipTime, level = _voice.Level;
            bool playing = _voice.ClipActive;
            if (playing) _lastClipTime = time;
            foreach (var p in _cast.Values) p.Update(time, level, playing && _speaking == p, dt);
            _camera.Update(dt);
        }
    }
}
