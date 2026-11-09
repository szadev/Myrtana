using System.Collections.Generic;
using G3.Unity.Characters;
using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Finds the on-screen view of an actor: first among the views the <see cref="NpcViewManager"/> spawned for the
    /// simulated NPCs, then — for worlds imported with static NPCs baked into the cell scenes (old importer option) —
    /// among the loaded <see cref="G3Character"/>s by GameObject name (the G3 name).
    /// </summary>
    public sealed class NpcViewDirectory
    {
        const float RefreshSeconds = 1f;

        readonly Dictionary<ActorId, Transform> _baked = new Dictionary<ActorId, Transform>();
        float _nextRefresh;

        /// <summary>The spawner of simulated NPC views (null = only baked views).</summary>
        public NpcViewManager Spawned { get; set; }

        /// <summary>Counts the rebuilds of <see cref="Baked"/> (perf-3: the focus' nearby list is renewed with it).</summary>
        public int BakedVersion { get; private set; }

        public IEnumerable<KeyValuePair<ActorId, Transform>> Views
        {
            get
            {
                if (Spawned != null)
                    foreach (var kv in Spawned.Views) yield return kv;
                RefreshIfDue();
                foreach (var kv in _baked)
                    if (kv.Value != null && (Spawned == null || !Spawned.TryGetView(kv.Key, out _))) yield return kv;
            }
        }

        /// <summary>
        /// The baked views (refreshed once a second), for per-frame callers that walk <see cref="NpcViewManager.ViewComponents"/>
        /// first and then these, skipping the actors the spawner shows: the same set and order as <see cref="Views"/>
        /// without its iterators (perf-9).
        /// </summary>
        public Dictionary<ActorId, Transform> Baked
        {
            get
            {
                RefreshIfDue();
                return _baked;
            }
        }

        public bool TryGet(ActorId actor, out Transform view)
        {
            if (Spawned != null && Spawned.TryGetView(actor, out view)) return true;
            RefreshIfDue();
            return _baked.TryGetValue(actor, out view) && view != null;
        }

        void RefreshIfDue()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            BakedVersion++;
            _baked.Clear();
            var all = Object.FindObjectsByType<G3Character>();
            if (_known.Count > 4 * all.Length + 256) _known.Clear(); // streamed cells come and go: drop destroyed keys
            foreach (var character in all)
            {
                // Pooled NPC views and the names of baked characters are remembered per instance: this runs once a
                // second over every G3Character, and gameObject.name allocates a string each time (perf-7).
                if (!_known.TryGetValue(character, out var actor))
                {
                    // fix-88: an interactive object's animated prefab (chest, door, grindstone …) carries a G3Character too;
                    // it is no character (fix-84 made every character focusable: one chest per name then showed its raw
                    // entity name as an NPC's focus name, anchored above its lid bone)
                    actor = character.TryGetComponent<NpcView>(out _) || IsObjectVisual(character)
                        ? (ActorId?)null : new ActorId(character.gameObject.name);
                    _known[character] = actor;
                }
                if (actor.HasValue) _baked[actor.Value] = character.transform;
            }
        }

        /// <summary>The character is the visual of an interactive object (<see cref="InteractiveObjectViews"/>).</summary>
        static bool IsObjectVisual(G3Character character) => character.GetComponentInParent<InteractiveObjectView>(true) != null;

        readonly Dictionary<G3Character, ActorId?> _known = new Dictionary<G3Character, ActorId?>();
    }
}
