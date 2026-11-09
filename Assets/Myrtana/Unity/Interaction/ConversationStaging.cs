using System;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Story;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// What a conversation does to the scene: the hero's controls are suspended (<see cref="HeroControlLock"/>), and
    /// hero and NPC turn to face each other — fix-76: once the conversation engages (<see cref="DialogEngaged"/>: its
    /// first line or a real choice), so a talk with nothing to say turns nobody. Everything is restored when it ends.
    /// </summary>
    public sealed class ConversationStaging : MonoBehaviour, ISessionView, ISessionControlHolder
    {
        [Tooltip("Degrees per second the NPC turns towards the hero.")]
        public float npcTurnSpeed = 240f;

        HeroControlLock _controls;
        G3Player _player;
        NpcViewDirectory _views;
        Transform _npc;
        NpcView _npcView;
        HeadLookController _look;
        bool _seated, _turns, _engaged, _lying;
        IDisposable _started, _engagedSub, _ended, _preludeEnded;
        GameSession _session;

        public void Init(HeroControlLock controls, NpcViewDirectory views)
        {
            _controls = controls;
            _player = controls.Player;
            _views = views;
        }

        public void Bind(GameSession session)
        {
            Unsubscribe();
            _session = session;
            _started = session.Events.Subscribe<DialogStarted>(e => Begin(e.Partner));
            _preludeEnded = session.Events.Subscribe<DialogPreludeEnded>(e => GetUp());
            _engagedSub = session.Events.Subscribe<DialogEngaged>(e => Engage());
            _ended = session.Events.Subscribe<DialogEnded>(e => End());
        }

        void Unsubscribe()
        {
            _started?.Dispose();
            _engagedSub?.Dispose();
            _ended?.Dispose();
            _preludeEnded?.Dispose();
        }

        void OnDestroy() => Unsubscribe();

        void Begin(ActorId partner)
        {
            _controls.Acquire(this);
            _npc = _views.TryGet(partner, out var view) ? view : null;
            if (_npc == null) return;
            // G3 ZS_Talk: a sitting NPC that sees the hero stays seated and turns its head to him; all others stand up and
            // turn the body (no head look-at). The hero's view field counts as 180° (CP perception FieldOfView).
            _npcView = _npc.GetComponent<NpcView>();
            var toHero = Vector3.ProjectOnPlane(_player.transform.position - _npc.position, Vector3.up);
            bool heroInView = Vector3.Dot(_npc.forward, toHero) >= 0;
            _seated = _npcView != null && Myrtana.Ui.Dialog.HeadLook.StaysSeated(_npcView.IsSeated, heroInView);
            // fix-247, G3 ZS_Talk: a woken sleeper says "YouDisturbedMySlumber" lying, and only then stands up (GetUp)
            _lying = _session != null && _session.Dialog.InPrelude;
            if (_npcView != null) _npcView.StaySeatedInTalk = _seated || _lying;
            _turns = Myrtana.Ui.Dialog.HeadLook.TurnsToFace(_npcView != null ? _npcView.Species : null);
            if (_seated)
            {
                _look = HeadLookController.On(_npc);
                if (_look != null) _look.LookAt(HeadLookController.AimPointOf(_player.transform));
            }
        }

        /// <summary>fix-76: the conversation shows a line or a choice: the hero faces the NPC (the camera swings, no snap), the NPC turns.</summary>
        void Engage()
        {
            if (_engaged) return;
            _engaged = true;
            if (_npc == null || _player == null) return;
            var toNpc = Vector3.ProjectOnPlane(_npc.position - _player.transform.position, Vector3.up);
            if (toNpc.sqrMagnitude > 0.01f) _player.TurnTo(Quaternion.LookRotation(toNpc).eulerAngles.y, _player.Pitch);
        }

        /// <summary>fix-247: the woken sleeper's line is over: it stands up (_AI_StandUp) and turns to the hero.</summary>
        void GetUp()
        {
            if (!_lying) return;
            _lying = false;
            if (_npcView != null) _npcView.StaySeatedInTalk = _seated;
        }

        void End()
        {
            _lying = false;
            if (_look != null) _look.Release();
            if (_npcView != null) _npcView.StaySeatedInTalk = false;
            _look = null;
            _npcView = null;
            _seated = false;
            _engaged = false;
            _npc = null;
            _controls.Release(this);
        }

        void Update()
        {
            if (_npc == null || _player == null || _seated || _lying || !_turns || !_engaged) return;
            var toPlayer = Vector3.ProjectOnPlane(_player.transform.position - _npc.position, Vector3.up);
            if (toPlayer.sqrMagnitude < 0.01f) return;
            _npc.rotation = Quaternion.RotateTowards(_npc.rotation, Quaternion.LookRotation(toPlayer), npcTurnSpeed * Time.deltaTime);
        }
    }
}
