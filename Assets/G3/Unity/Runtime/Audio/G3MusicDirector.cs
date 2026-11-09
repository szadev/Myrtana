using System;
using System.Collections.Generic;
using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Plays the G3 music: the zone under the player gives the location, <see cref="IMusicSituationSource"/>s the
    /// situation (fight …), the G3 clock day/night; <see cref="MusicStateResolver"/> makes that playable and the
    /// engine-free <see cref="MusicSequencer"/> schedules fragments and g3.mul transitions on the DSP clock.
    /// </summary>
    [RequireComponent(typeof(G3AudioContext))]
    [AddComponentMenu("G3/Audio/G3 Music Director")]
    public sealed class G3MusicDirector : MonoBehaviour
    {
        [Range(0, 1)] [Tooltip("ge3.ini Sound.MusicVolume = 80 (of 100).")]
        public float volume = 0.8f;
        [Tooltip("Longest wait for an exit link when a trigger waits for the next exit (seconds); fight, showdown and revolution change at once (G3's Music::Trigger* parameters).")]
        public float exploreMaxWait = 15f;
        public bool mute;
        [Tooltip("Log every target change with its trigger (Console: [G3Music]).")]
        public bool logChanges = true;

        [Header("Current (read only)")]
        [SerializeField] string target;
        [SerializeField] string playing;
        [SerializeField] string next;

        readonly List<IMusicSituationSource> _sources = new List<IMusicSituationSource>();
        G3AudioContext _context;
        MusicSequencer _sequencer;
        MusicStateResolver _resolver;
        MusicVoicePool _voices;

        public MusicSequencer Sequencer => _sequencer;
        public MusicState Target => _sequencer != null ? _sequencer.Target : default;
        public string Playing => playing;

        /// <summary>Situation sources are asked in registration order; components on this object and its children register at Start.</summary>
        public void AddSituationSource(IMusicSituationSource source)
        {
            if (source != null && !_sources.Contains(source)) _sources.Add(source);
        }

        public void RemoveSituationSource(IMusicSituationSource source) => _sources.Remove(source);

        /// <summary>
        /// The game's own situation rule (fight, showdown, dead …), asked after the registered sources (the scene's
        /// override for story/title music comes first). Null/empty answers mean explore. A static slot, so it survives
        /// the director being rebuilt; null removes it.
        /// </summary>
        public static Func<string> GameSituation { get; set; }

        static readonly HashSet<object> SilenceOwners = new HashSet<object>();

        /// <summary>
        /// Silences the situation music while any owner holds it: the start-up logos, the intro and the credits videos
        /// (G3 plays them with their own soundtrack and no music; the end credits after the epilogue included). The music
        /// stops at once and the director neither re-targets nor logs while silent; on the last release it starts afresh
        /// from the situation of that moment. Static, so a hold set before the director exists (or while it is being
        /// rebuilt) still counts.
        /// </summary>
        public static void Silence(object owner, bool silent)
        {
            if (owner == null) return;
            if (silent) SilenceOwners.Add(owner);
            else SilenceOwners.Remove(owner);
        }

        public static bool IsSilenced => SilenceOwners.Count > 0;

        /// <summary>
        /// The port's "smooth music transitions" option (player feedback item 12; off = G3): longer fades where G3 cuts with
        /// the sequencer's short ones (a start from silence, a stop, and the crossfade of changes without a g3.mul
        /// transition, such as the fight start). The g3.mul transitions keep their own fade ranges.
        /// </summary>
        public static bool SmoothTransitions { get; set; }

        /// <summary>Sequencer fades (start, stop, crossfade; s): G3's and the smooth option's.</summary>
        public static readonly (double start, double stop, double crossfade) OriginalFades = (1.5, 3.0, 2.0), SmoothFades = (4.0, 6.0, 5.0);

        void ApplyFades()
        {
            var f = SmoothTransitions ? SmoothFades : OriginalFades;
            _sequencer.StartFade = f.start;
            _sequencer.StopFade = f.stop;
            _sequencer.FallbackCrossfade = f.crossfade;
        }

        /// <summary>Play mode without a domain reload keeps statics: a hold left by the last session (credits → quit) goes.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => SilenceOwners.Clear();

        bool _silent;

        // Built in OnEnable, torn down in OnDisable rather than in Start/OnDestroy: a script recompile during Play reloads
        // the domain, which drops these non-serialized fields and calls OnDisable/OnEnable again but never Start.
        void OnEnable()
        {
            _context = GetComponent<G3AudioContext>();
            var lib = _context.library;
            if (lib == null || lib.music.fragments.Count == 0)
            {
                Debug.LogWarning("G3MusicDirector: no imported music library (G3/Audio/Import All). Music disabled.");
                enabled = false;
                return;
            }
            foreach (var s in GetComponentsInChildren<IMusicSituationSource>()) AddSituationSource(s);
            _resolver = new MusicStateResolver(lib.music);
            _sequencer = new MusicSequencer(lib.music, new System.Random(), lib.ClipLength);
            _sequencer.Cancelled += c => { if (logChanges) Debug.Log($"[G3Music] cancelled {c.Clip} (situation changed back before it was heard)"); };
            _voices = new MusicVoicePool(transform, lib);
            _context.Refreshed += Evaluate;
            _context.Refresh();   // also raises Refreshed → Evaluate once the focus is known (else the context's Start does)
        }

        void OnDisable()
        {
            if (_context != null) _context.Refreshed -= Evaluate;
            _voices?.Dispose();
            _voices = null;
            _sequencer = null;
        }

        void Update()
        {
            double now = AudioSettings.dspTime;
            ApplyFades();
            if (IsSilenced != _silent)
            {
                _silent = IsSilenced;
                if (_silent)
                {
                    _sequencer.Stop(now);   // forget the target: nothing is scheduled under the video
                    target = "";
                    if (logChanges) Debug.Log("[G3Music] silenced (video / credits)");
                }
                else Evaluate(restart: true);
            }
            _sequencer.Update(now);
            _voices.Sync(_sequencer.Cues, now, mute || _silent ? 0f : volume);
            playing = _sequencer.Current?.Clip ?? "";
            next = _sequencer.Next?.Clip ?? "";
        }

        void Evaluate() => Evaluate(restart: false);

        /// <param name="restart">After a silence: start the current situation afresh (its own trigger) even if it equals the
        /// target before the silence.</param>
        void Evaluate(bool restart)
        {
            if (_silent || IsSilenced) return;   // no re-targeting under a video; the release evaluates once
            var state = _resolver.Resolve(_context.Zone, CurrentSituation(), _context.Hour);
            var previous = _sequencer.Target;
            if (!restart && state.Equals(previous)) return;
            // A situation change uses that situation's trigger; a zone or day-time change the location trigger.
            var trigger = restart || previous.IsEmpty || state.Situation != previous.Situation
                ? MusicTriggers.ForSituation(state.Situation)
                : MusicTriggers.Location;
            if (!_sequencer.SetTarget(state, trigger, exploreMaxWait)) return;   // locked by revolutionwon
            target = state.ToString();
            if (logChanges) Debug.Log($"[G3Music] target {state} ({trigger})");
        }

        string CurrentSituation()
        {
            foreach (var s in _sources)
            {
                var v = s?.MusicSituation;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            var game = GameSituation?.Invoke();
            return string.IsNullOrEmpty(game) ? MusicSituations.Explore : game;
        }
    }
}
