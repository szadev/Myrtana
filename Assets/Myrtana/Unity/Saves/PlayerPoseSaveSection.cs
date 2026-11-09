using G3.Unity;
using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Save section "unity.player": where the hero stands and looks. The hero's position is owned by the Unity player
    /// controller until the simulation owns the player actor, so the host saves it. Reading only remembers the pose;
    /// <see cref="ApplyTo"/> moves the hero once the loaded session is in place.
    /// </summary>
    public sealed class PlayerPoseSaveSection : ISaveParticipant
    {
        readonly G3Player _player;
        Vector3? _position, _lastSafe;
        float _yaw, _pitch;

        public PlayerPoseSaveSection(G3Player player) => _player = player;

        public string Section => "unity.player";
        public int Version => 2;

        public void Write(ISaveWriter w)
        {
            var p = _player.transform.position;
            w.WriteDouble(p.x);
            w.WriteDouble(p.y);
            w.WriteDouble(p.z);
            w.WriteDouble(_player.Yaw);
            w.WriteDouble(_player.Pitch);
            // v2 (fix-123): his last valid standing place, for a loaded pose that turns out invalid
            var safe = _player.LastSafe;
            w.WriteBool(safe.HasValue);
            if (safe.HasValue)
            {
                w.WriteDouble(safe.Value.x);
                w.WriteDouble(safe.Value.y);
                w.WriteDouble(safe.Value.z);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            _position = new Vector3((float)r.ReadDouble(), (float)r.ReadDouble(), (float)r.ReadDouble());
            _yaw = (float)r.ReadDouble();
            _pitch = (float)r.ReadDouble();
            _lastSafe = version >= 2 && r.ReadBool()
                ? new Vector3((float)r.ReadDouble(), (float)r.ReadDouble(), (float)r.ReadDouble()) : (Vector3?)null;
        }

        /// <summary>Moves the hero to the loaded pose and holds him behind the loading screen until the world around him
        /// has streamed in.</summary>
        public void ApplyTo(G3Player player)
        {
            if (_position == null) return;
            player.Teleport(_position.Value);
            player.SetLook(_yaw, _pitch);
            if (_lastSafe.HasValue) player.RestoreLastSafe(_lastSafe.Value);
            // fix-123 (owner: a save made on a cave ceiling's top / inside the rock): the loaded place is checked once the
            // hero stands (inside geometry, on a ceiling top: the nearest valid floor, else the last safe place)
            player.ValidatePlacement("load");
            _position = null;
            _lastSafe = null;
            var streamer = Object.FindAnyObjectByType<G3WorldStreamer>();
            if (streamer != null && !streamer.IsReady(player.transform.position))
            {
                streamer.Hold(true);
                G3LoadingOverlay.ShowUntilReady(streamer, player.transform.position);
            }
        }
    }
}
