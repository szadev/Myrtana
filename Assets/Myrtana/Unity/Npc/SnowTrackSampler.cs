using System.Collections.Generic;
using G3.Unity;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-178 persistent snow tracks, the session side: gives the snow the game clock (track age cap in game days), applies
    /// the tracks of a loaded save (<see cref="SnowTracksSaveSection"/>) or clears them for a new game, and twice a second
    /// reports every NPC within <c>snowTrackRange</c> of the camera to <see cref="G3SnowV2.Walk"/>, so walkers the per-step
    /// prints do not see (beyond <c>snowTrailRange</c>: far views and simulated NPCs on their routines and wander) leave a
    /// coarse trail - the owner wants to follow a wolf's trail in Nordmar. Dead NPCs do not walk; nothing runs while the
    /// tracks are off (option, snow off, no Nordmar).
    /// </summary>
    public sealed class SnowTrackSampler : MonoBehaviour, ISessionView
    {
        [Tooltip("Seconds between two passes over the NPCs.")] public float interval = 0.5f;

        SnowTracksSaveSection _section;
        GameSession _session;
        float _next;
        readonly Dictionary<string, bool> _biped = new Dictionary<string, bool>();

        public void Init(SnowTracksSaveSection section) => _section = section;

        public void Bind(GameSession session)
        {
            _session = session;
            var clock = session.Clock;
            G3SnowV2.GameSeconds = () => clock.GameSeconds;
            var pending = _section?.Pending;
            if (_section != null) _section.Pending = null;
            if (pending != null) G3SnowV2.LoadTracks(pending);
            else G3SnowV2.ResetTracks();
            _next = 0;
        }

        void Update()
        {
            if (_session == null || !G3SnowV2.TracksActive || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + interval;
            var cam = Camera.main;
            if (cam == null) return;
            var eye = cam.transform.position;
            float range = WorldRemasterLook.Current.snowTrackRange, r2 = range * range;
            var npcs = _session.Npcs;
            var dead = npcs.Lifeless;
            foreach (var agent in npcs.Agents)
            {
                var pose = npcs.PoseOf(agent);
                float dx = pose.X - eye.x, dz = pose.Z - eye.z;
                if (dx * dx + dz * dz > r2 || !npcs.IsInWorld(agent)) continue;
                if (dead != null && dead(agent.Id)) continue;
                string species = agent.Definition.Species;
                if (!_biped.TryGetValue(species ?? "", out bool biped)) _biped[species ?? ""] = biped = SnowTrackStore.IsBiped(species);
                G3SnowV2.Walk(agent.Id.GetHashCode(), new Vector3(pose.X, pose.Y, pose.Z), biped, biped ? 1.8f : 1.2f);
            }
            if (Time.frameCount % 300 < 30) G3SnowV2.ForgetIdleWalkers();
        }
    }
}
