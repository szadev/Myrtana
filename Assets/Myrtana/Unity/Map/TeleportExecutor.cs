using System;
using G3.Unity;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Carries out the session's <see cref="TeleportRequested"/> (a cast teleport stone, Docs/map-teleport.md §1.4): the hero
    /// takes the destination's pose (position and heading; the camera pitch stays). When the cells there are not loaded
    /// yet, the streamer holds him behind the loading screen until they are, as after a load-game.
    /// </summary>
    public sealed class TeleportExecutor : MonoBehaviour, ISessionView
    {
        G3Player _player;
        G3WorldStreamer _streamer;
        IDisposable _subscription;
        TeleportDestinationDef _pending;

        public void Init(G3Player player, G3WorldStreamer streamer)
        {
            _player = player;
            _streamer = streamer;
        }

        public void Bind(GameSession session)
        {
            _subscription?.Dispose();
            _pending = null;
            _subscription = session.Events.Subscribe<TeleportRequested>(e => _pending = e.Destination);
        }

        // After the cast's own frame: the spell's release and its effects finish where the hero stood.
        void LateUpdate()
        {
            if (_pending == null) return;
            var destination = _pending;
            _pending = null;
            Teleport(destination);
        }

        void Teleport(TeleportDestinationDef destination)
        {
            if (_player == null) return;
            var p = destination.Position;
            var feet = new Vector3(p[0], p[1], p[2]);
            _player.Teleport(feet);
            _player.SetLook(destination.Yaw, _player.Pitch);
            Debug.Log($"[Myrtana] teleport to {destination.Id} {feet}");
            if (_streamer == null || _streamer.IsReady(feet)) return;
            _streamer.Hold(true);
            G3LoadingOverlay.ShowUntilReady(_streamer, feet);
        }

        void OnDestroy() => _subscription?.Dispose();
    }
}
