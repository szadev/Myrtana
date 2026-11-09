using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Metadata of an imported Gothic 3 actor (Assets/G3Imported/Characters/Prefabs). Clips are legacy
    /// AnimationClips on the sibling <see cref="Animation"/> component; <see cref="Clips"/> carries what the motion
    /// file name encodes (Docs/formats-animation.md §5).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class G3Character : MonoBehaviour
    {
        [Serializable]
        public sealed class ClipInfo
        {
            public string Motion;          // .xmot stem
            public AnimationClip Clip;
            public string Action, Phase, Direction;
            /// <summary>Forward speed in m/s the engine moves the entity with while the clip plays (0 = in place).</summary>
            public float Speed;
            public bool Loop;
        }

        public string Rig;                 // motion name prefix, e.g. "Hero", "Wolf"
        public string Skeleton;            // .xact the bone hierarchy was built from
        public List<string> Parts = new List<string>();
        public List<ClipInfo> Clips = new List<ClipInfo>();
        [Tooltip("Clip to start in play mode (motion stem or clip name); empty = none.")]
        public string AutoPlay;
        [Tooltip("Move the object forward with the clip's encoded speed while playing.")]
        public bool ApplyClipSpeed;
        [Tooltip("Normalized start time of the AutoPlay clip (NPCs get a random phase so crowds do not move in sync).")]
        [Range(0, 1)] public float StartPhase;

        ClipInfo _current;

        void Start()
        {
            if (!string.IsNullOrEmpty(AutoPlay) && Play(AutoPlay, 0f) && StartPhase > 0)
            {
                var st = GetComponent<Animation>()[_current.Clip.name];
                st.normalizedTime = StartPhase;
            }
        }

        public ClipInfo Find(string nameOrMotion)
        {
            foreach (var c in Clips)
                if (c.Clip != null && (string.Equals(c.Motion, nameOrMotion, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Clip.name, nameOrMotion, StringComparison.OrdinalIgnoreCase)))
                    return c;
            return null;
        }

        public bool Play(string nameOrMotion, float fade = 0.2f)
        {
            var c = Find(nameOrMotion);
            var anim = GetComponent<Animation>();
            if (c == null || anim == null) return false;
            if (anim[c.Clip.name] == null) anim.AddClip(c.Clip, c.Clip.name);
            anim[c.Clip.name].wrapMode = c.Loop ? WrapMode.Loop : WrapMode.ClampForever;
            anim.CrossFade(c.Clip.name, fade);
            _current = c;
            UpdateMoverRegistration();
            return true;
        }

        // ---- clip-speed movers --------------------------------------------------------------------------------------
        // Only a few characters move by their clip (ApplyClipSpeed), but a per-instance Update() made Unity call into
        // every G3Character (NPC views, doors, baked characters) each frame. The movers are driven by one hidden
        // component instead (perf-7).

        static readonly HashSet<G3Character> Movers = new HashSet<G3Character>();
        static readonly List<G3Character> MoverScratch = new List<G3Character>();
        static MoverDriver s_driver;

        bool Moves => ApplyClipSpeed && _current != null && _current.Speed > 0 && isActiveAndEnabled;

        // ---- live actors (remaster snow prints, G3SnowV2: every enabled character, no per-frame scene search) ----
        static readonly HashSet<G3Character> s_live = new HashSet<G3Character>();

        /// <summary>Every enabled G3Character in play mode (actors, but also doors and other animated objects).</summary>
        public static IReadOnlyCollection<G3Character> Live => s_live;

        /// <summary>How often this character was enabled (fix-193: caches of its bones, G3SnowV2, look again after a re-enable).</summary>
        public int Enables { get; private set; }

        void OnEnable()
        {
            Enables++;
            if (Application.isPlaying) s_live.Add(this);
            UpdateMoverRegistration();
        }

        void OnDisable()
        {
            s_live.Remove(this);
            Movers.Remove(this);
        }

        void UpdateMoverRegistration()
        {
            if (!Application.isPlaying) return;
            if (!Moves) { Movers.Remove(this); return; }
            if (!Movers.Add(this) || s_driver != null) return;
            var go = new GameObject("G3CharacterMovers") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            s_driver = go.AddComponent<MoverDriver>();
        }

        void Move(float dt)
        {
            if (Moves) transform.position += transform.forward * (_current.Speed * dt);
        }

        sealed class MoverDriver : MonoBehaviour
        {
            void Update()
            {
                if (Movers.Count == 0) return;
                MoverScratch.Clear();
                MoverScratch.AddRange(Movers);
                float dt = Time.deltaTime;
                foreach (var c in MoverScratch)
                {
                    if (c == null) { Movers.Remove(c); continue; }
                    c.Move(dt);
                }
            }

            void OnDestroy()
            {
                if (s_driver == this) s_driver = null;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Movers.Clear();
            s_live.Clear();
            s_driver = null;
        }
    }
}
