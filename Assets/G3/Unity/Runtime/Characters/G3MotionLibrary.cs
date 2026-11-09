using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// The structured part of a G3 motion name that the engine composes from an animation state
    /// (Docs/formats-animation.md §5): <c>Rig_Stance_LeftItem_RightItem_Pose_Action_Phase_Variant_Direction_Index</c>,
    /// e.g. <c>Hero_Stand_None_None_P0_Move_Walk_N_Fwd_00</c>. The file name continues with <c>_%_Weight_Pose2_Speed</c>,
    /// which is data of the clip (random weight, root speed), not part of the request.
    /// </summary>
    [Serializable]
    public struct G3MotionKey
    {
        public string Rig, Stance, LeftItem, RightItem, Pose, Action, Phase, Variant, Direction;
        public int Index;

        public G3MotionKey(string rig, string action, string phase, string direction = "Fwd", string stance = "Stand",
            string leftItem = "None", string rightItem = "None", string pose = "P0", string variant = "N", int index = 0)
        {
            Rig = rig; Stance = stance; LeftItem = leftItem; RightItem = rightItem; Pose = pose;
            Action = action; Phase = phase; Variant = variant; Direction = direction; Index = index;
        }

        public G3MotionKey With(string action, string phase, string direction = "Fwd", int index = 0)
        {
            var k = this;
            k.Action = action; k.Phase = phase; k.Direction = direction; k.Index = index;
            return k;
        }

        public override string ToString() =>
            $"{Rig}_{Stance}_{LeftItem}_{RightItem}_{Pose}_{Action}_{Phase}_{Variant}_{Direction}_{Index:00}";

        /// <summary>Key part (first ten fields) of a full motion file name, or null if it is not a structured name.</summary>
        public static string KeyOf(string motionName)
        {
            if (string.IsNullOrEmpty(motionName)) return null;
            int fields = 0;
            for (int i = 0; i < motionName.Length; i++)
                if (motionName[i] == '_' && ++fields == 10) return motionName.Substring(0, i);
            return null;
        }
    }

    /// <summary>
    /// Clips of one rig, addressed by G3 motion name (generic Mecanim clips, played through <see cref="G3MotionPlayer"/>).
    /// Built by the editor importer from the .xmot files; <see cref="Entry.speed"/> is the root speed encoded in the name.
    /// </summary>
    [CreateAssetMenu(menuName = "G3/Motion Library")]
    public sealed class G3MotionLibrary : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string motion;      // full .xmot stem
            public AnimationClip clip;
            [Tooltip("Root speed in m/s encoded in the motion name (0 = in place)")] public float speed;
            public bool loop;
            [Tooltip("Effect events of the .xmot (tool hits, eating, steps …), in clip time")] public MotionEvent[] events;
            [Tooltip("Overlay motions (variant O): the skeleton nodes the motion drives (its tracks and their folded helpers); " +
                     "the rest of the body keeps the motion underneath. Empty for full-body motions.")]
            public string[] drives;
            public float Length => clip != null ? Mathf.Max(clip.length, 1f / 30) : 1f / 30;

            [NonSerialized] G3.Audio.MotionEventWindow.TrackEvent[] _track;

            /// <summary>The effect track as the pure event window reads it (<see cref="G3.Audio.MotionEventWindow"/>).</summary>
            public IReadOnlyList<G3.Audio.MotionEventWindow.TrackEvent> Track
            {
                get
                {
                    int n = events?.Length ?? 0;
                    if (_track != null && _track.Length == n) return _track;
                    _track = new G3.Audio.MotionEventWindow.TrackEvent[n];
                    for (int i = 0; i < n; i++) _track[i] = new G3.Audio.MotionEventWindow.TrackEvent(events[i].time, events[i].effect);
                    return _track;
                }
            }
        }

        /// <summary>
        /// One entry of the motion's effect track (Docs/formats-animation.md §8.7): the g3.efm effect fired when the clip
        /// reaches <see cref="time"/>. A "[F]" / "[R]" prefix limits it to forward / reverse playback.
        /// </summary>
        [Serializable]
        public struct MotionEvent
        {
            public float time;
            public string effect;
        }

        public string rig;
        public List<Entry> entries = new List<Entry>();

        Dictionary<string, Entry> _byKey;
        int _indexedCount;

        /// <summary>Entry whose motion name starts with the composed key (first variant when several exist), or null.</summary>
        public Entry Find(G3MotionKey key) => Find(key.ToString());

        public Entry Find(string key)
        {
            if (_byKey == null || _indexedCount != entries.Count) // entries added by an importer while the asset is loaded
            {
                _indexedCount = entries.Count;
                _byKey = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    if (e?.clip == null) continue;
                    string k = G3MotionKey.KeyOf(e.motion) ?? e.motion;
                    if (!_byKey.ContainsKey(k)) _byKey[k] = e;
                    if (!_byKey.ContainsKey(e.motion)) _byKey[e.motion] = e;
                }
            }
            return key != null && _byKey.TryGetValue(key, out var r) ? r : null;
        }

        /// <summary>
        /// A random alternative of a composed key (ten fields, the last one the index): G3 picks the index uniformly
        /// (<c>rand() % count</c>) among the variants _00, _01 … that exist (Docs/formats-animation.md §5.1). Returns the key
        /// itself when it has no further variants or is not a composed key. <paramref name="random"/>(n) returns 0..n-1.
        /// </summary>
        public string RandomVariant(string key, Func<int, int> random)
        {
            if (key == null || key.Length < 4 || key[key.Length - 3] != '_' || !char.IsDigit(key[key.Length - 2]) || !char.IsDigit(key[key.Length - 1]))
                return key;
            var variants = VariantsOf(key);
            return variants.Length <= 1 ? key : variants[random(variants.Length)];
        }

        /// <summary>
        /// The variants _00, _01 … of a composed key that exist, cached (perf-3: every loop restart of every NPC built and
        /// looked up up to a hundred key strings; the entries only change when an importer adds some).
        /// </summary>
        string[] VariantsOf(string key)
        {
            Find((string)null); // (re)builds the index when entries were added
            if (_variants == null || _variantsCount != _indexedCount)
            {
                _variants = new Dictionary<string, string[]>(StringComparer.Ordinal);
                _variantsCount = _indexedCount;
            }
            if (_variants.TryGetValue(key, out var found)) return found;
            string stem = key.Substring(0, key.Length - 2);
            var list = new List<string>();
            while (list.Count < 100)
            {
                string k = stem + list.Count.ToString("00");
                if (Find(k) == null) break;
                list.Add(k);
            }
            found = list.ToArray();
            _variants[key] = found;
            return found;
        }

        Dictionary<string, string[]> _variants;
        int _variantsCount = -1;

        /// <summary>
        /// The key with another phase (the seventh field: "Loop" → "Ambient" for the fidgets of an idle), or null when
        /// <paramref name="key"/> is not a composed key.
        /// </summary>
        public static string WithPhase(string key, string phase)
        {
            if (key == null) return null;
            if (s_withPhase.TryGetValue((key, phase), out var cached)) return cached; // perf-3: asked on every idle loop restart
            if (s_withPhase.Count > 8192) s_withPhase.Clear();
            return s_withPhase[(key, phase)] = ComposeWithPhase(key, phase);
        }

        static readonly Dictionary<(string key, string phase), string> s_withPhase = new Dictionary<(string, string), string>();

        static string ComposeWithPhase(string key, string phase)
        {
            var f = key.Split('_');
            if (f.Length != 10) return null;
            f[6] = phase;
            f[9] = "00";
            return string.Join("_", f);
        }

        void OnValidate()
        {
            _byKey = null;
            _variants = null;
        }
    }
}
