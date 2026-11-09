using System.Collections.Generic;
using G3.Player;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Stand idle of the hero, as G3's character movement plays it when the hero neither moves nor turns
    /// (Docs/formats-animation.md §5.1): a breathing loop <c>…_Ambient_Loop_N_Fwd_&lt;i&gt;</c> of the current stance and hand
    /// fields. Whenever a motion nears its end, the next one is picked: with chance 1/7 a fidget
    /// <c>…_Ambient_Ambient_N_Fwd_&lt;i&gt;</c> (look around, stretch, scratch…), else a loop; the index is uniform among the
    /// variants. Motions not longer than 2 × fade + 0.1 s (the static armed poses) just keep looping. The fade is G3's
    /// standard 0.2 s; entering the idle from any other motion fades 0.5 s (<see cref="G3.Player.MotionBlendRules"/>).
    /// Owned by <see cref="G3HeroAnimator"/>, which decides when the hero stands still.
    /// </summary>
    public sealed class G3HeroIdle
    {
        public const float FidgetChance = 1f / 7;
        const int MaxVariants = 32;

        sealed class MotionSet
        {
            public G3MotionLibrary.Entry[] Loops, Fidgets;
        }

        readonly G3MotionPlayer _player;
        readonly Dictionary<string, MotionSet> _sets = new Dictionary<string, MotionSet>();
        G3MotionLibrary.Entry _current;
        string _standKey;
        float _time;

        public G3HeroIdle(G3MotionPlayer player) => _player = player;

        /// <summary>Full name of the idle motion playing, or null when the idle is not running.</summary>
        public string Motion => _current?.motion;
        public bool Playing => _current != null;
        public bool Fidgeting { get; private set; }

        /// <summary>
        /// Runs the idle for the stand motion <paramref name="stand"/> (its stance, hands and pose select the set). Returns
        /// false, without touching the player, when the library has no idle loop for that set.
        /// </summary>
        public bool Update(G3MotionKey stand, float dt)
        {
            const float fade = MotionBlendRules.StandardFade;
            string key = stand.ToString();
            var set = SetOf(stand, key);
            if (set.Loops.Length == 0)
            {
                Stop();
                return false;
            }
            if (_current == null || key != _standKey)
            {
                // Entering from another motion fades 0.5 s; a hand change while idling is a re-pick (G3 owner stays "stand").
                var starter = _current == null ? MotionStarter.StandEntry : MotionStarter.StandRepick;
                _standKey = key;
                Pick(set, starter);
                return true;
            }
            _time += dt;
            float length = _current.Length;
            if (length - _time <= fade && length > 2 * fade + 0.1f) Pick(set, MotionStarter.StandRepick);
            return true;
        }

        /// <summary>Forgets the idle; the caller has already started another motion.</summary>
        public void Stop()
        {
            _current = null;
            _standKey = null;
            Fidgeting = false;
        }

        void Pick(MotionSet set, MotionStarter starter)
        {
            Fidgeting = set.Fidgets.Length > 0 && Random.value < FidgetChance;
            var list = Fidgeting ? set.Fidgets : set.Loops;
            _current = list[Random.Range(0, list.Length)];
            _time = 0;
            _player.Play(_current.motion, _player.FadeFor(starter, _current.motion), restart: true);
        }

        MotionSet SetOf(G3MotionKey stand, string key)
        {
            if (_sets.TryGetValue(key, out var set)) return set;
            set = new MotionSet { Loops = Variants(stand, "Loop"), Fidgets = Variants(stand, "Ambient") };
            _sets[key] = set;
            return set;
        }

        G3MotionLibrary.Entry[] Variants(G3MotionKey stand, string phase)
        {
            var found = new List<G3MotionLibrary.Entry>();
            var library = _player.library;
            for (int i = 0; i < MaxVariants && library != null; i++)
            {
                var e = library.Find(stand.With("Ambient", phase, stand.Direction, i));
                if (e == null) break;
                found.Add(e);
            }
            return found.ToArray();
        }
    }
}
