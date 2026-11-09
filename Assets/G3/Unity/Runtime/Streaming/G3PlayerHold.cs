using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Freezes a player object (its own Behaviours and Colliders; children such as the camera keep running) while at
    /// least one owner asks for it — e.g. the streamer until the cells under the player exist, the start-up loading
    /// screen until the whole area is ready. Released when the last owner lets go.
    /// </summary>
    public sealed class G3PlayerHold
    {
        readonly HashSet<object> _owners = new HashSet<object>();
        readonly List<Behaviour> _behaviours = new List<Behaviour>();
        readonly List<Collider> _colliders = new List<Collider>();

        public bool IsHeld => _owners.Count > 0;
        public bool IsHeldBy(object owner) => _owners.Contains(owner);

        /// <param name="keepRunning">A behaviour on the player that must not be frozen (e.g. the caller itself).</param>
        public void Set(GameObject player, object owner, bool hold, Behaviour keepRunning = null)
        {
            if (hold)
            {
                if (player == null || !_owners.Add(owner) || _owners.Count > 1) return;
                foreach (var b in player.GetComponents<Behaviour>())
                    if (b.enabled && b != keepRunning) { b.enabled = false; _behaviours.Add(b); }
                foreach (var c in player.GetComponents<Collider>())
                    if (c.enabled) { c.enabled = false; _colliders.Add(c); }
            }
            else if (_owners.Remove(owner) && _owners.Count == 0)
            {
                foreach (var b in _behaviours) if (b != null) b.enabled = true;
                foreach (var c in _colliders) if (c != null) c.enabled = true;
                _behaviours.Clear();
                _colliders.Clear();
            }
        }
    }
}
