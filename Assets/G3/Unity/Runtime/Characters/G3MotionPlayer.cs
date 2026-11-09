using System;
using System.Collections.Generic;
using G3.Player;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Plays G3 motions on an Animator through a PlayableGraph (no AnimatorController). Rig-agnostic, meant for the hero
    /// and later for NPCs: the caller (a selector such as <see cref="G3HeroAnimator"/>) composes motion names and asks for
    /// either one motion or a synchronised blend set (locomotion) whose weights it updates every frame.
    /// Graph: AnimationPlayableOutput ← top layer mixer ← root mixer ← a fixed set of slot mixers (the current request and
    /// the ones fading out each take one) ← clip playables. Clip time is driven here (clips are paused): all clips of a
    /// blend share one normalised phase, advanced at the weight-averaged rate, like a Mecanim blend tree — so
    /// walk/run/strafe cycles stay in step.
    /// An upper-body overlay channel (G3 motions of variant "O": aiming, reloading, drawing while moving) sits on top: two
    /// override layers (the playing overlay and the one fading out), each masked to the nodes its motion drives
    /// (<see cref="G3MotionLibrary.Entry.drives"/>; G3 overlay mixing — a draw that tracks only the right arm leaves the
    /// spine, head and other arm to the run, fix-89); entries without that list fall back to the <c>Spine_2</c> subtree
    /// (Docs/combat.md §9).
    /// crash-2: the graph's topology is stable. Every slot mixer keeps the clip playables it once needed connected at
    /// fixed inputs (<see cref="MotionInputCache{TKey}"/>); a request reuses them by weight. The few topology changes left
    /// (a clip seen for the first time, rarely an LRU replacement) are made only in <see cref="Commit"/>, from the batched
    /// Update — never from an effect event handler or a graph callback, never while the Animator is disabled or culled
    /// completely. Nothing is destroyed until the whole graph is (OnDisable: the NPC view goes back to the pool with its
    /// Animator). Every topology change made the Animator rebuild its bindings, and doing that on each cross-fade crashed
    /// in mecanim (ChainedAllocator::Clear ← DeallocateBindings ← Animator::OnGraphTopologyChanged, build 22c).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    [DefaultExecutionOrder(200)] // after the selectors that set weights in their Update
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Motion Player")]
    public sealed class G3MotionPlayer : MonoBehaviour
    {
        public G3MotionLibrary library;

        sealed class Layer
        {
            public string key;
            public int slot;           // base slot mixer (_base)
            public G3MotionLibrary.Entry[] motions;
            public AnimationClip[] clipKeys;
            public int[] inputs;       // input of each motion's clip playable in the slot mixer, -1 = none yet
            public bool bound;         // inputs looked up in the slot's cache (after a graph rebuild: again)
            public float[] weights;
            public float phase;        // normalised (loops wrap, one-shots clamp at 1)
            public float weight, fadeRate;
            public float speed = 1;
            public bool loop;
            public bool reverse;       // played backwards (G3 eSMotionDesc reverse: a sheathe plays the draw motions backwards)
            public bool locomotion;    // a synchronised set started by PlayBlend (fix-76: the next set keeps its phase)
            public float elapsed;      // seconds since the layer started
            public float from, to;     // unwrapped phase range of the last step (effect events)
        }

        sealed class Overlay
        {
            public string key;
            public G3MotionLibrary.Entry motion;
            public readonly AnimationClip[] clipKey = new AnimationClip[1];
            public readonly int[] input = { -1 };   // input in the overlay slot's mixer, -1 = none yet
            public bool bound;
            public float phase, weight, fadeRate, speed = 1;
            public bool reverse;
            /// <summary>The motion reached its end once (a looping one wrapped around).</summary>
            public bool once;
        }

        /// <summary>One mixer of the fixed graph and the clip playables connected to it.</summary>
        sealed class Mix
        {
            public AnimationMixerPlayable mixer;
            public MotionInputCache<AnimationClip> cache;
            public readonly List<AnimationClipPlayable> clips = new List<AnimationClipPlayable>();
        }

        /// <summary>Base slots: the current request and up to three fading out (a fourth fading one is dropped).</summary>
        const int BaseSlots = 4;
        /// <summary>Clip playables a slot mixer keeps before it replaces the least recently used one.</summary>
        const int BaseSlotInputs = 32, OverlaySlotInputs = 16;

        PlayableGraph _graph;
        AnimationMixerPlayable _root;
        AnimationLayerMixerPlayable _top;
        readonly Mix[] _base = new Mix[BaseSlots];
        readonly Mix[] _over = new Mix[2];
        readonly MotionInputCache<AnimationClip>[] _baseCaches = new MotionInputCache<AnimationClip>[BaseSlots];
        readonly bool[] _baseBusy = new bool[BaseSlots];
        readonly AvatarMask[] _overMask = new AvatarMask[2];
        bool _pending;             // a request still has clips without a playable (made in Commit)
        Animator _animator;
        Renderer[] _renderers;
        int _renderersFrame = int.MinValue;
        readonly Overlay[] _overlays = new Overlay[2];
        int _overlay = -1;
        readonly Dictionary<G3MotionLibrary.Entry, AvatarMask> _masks = new Dictionary<G3MotionLibrary.Entry, AvatarMask>();
        AvatarMask _spineMask;
        Transform[] _maskBones;
        readonly List<Layer> _layers = new List<Layer>();
        Layer _current;

        /// <summary>Key of the current request (motion key or blend-set name), null before the first one.</summary>
        public string CurrentKey => _current?.key;
        /// <summary>Normalised time of the current request (one-shots stop at 1).</summary>
        public float CurrentPhase => _current?.phase ?? 0;
        /// <summary>Seconds since the current request started.</summary>
        public float CurrentElapsed => _current?.elapsed ?? 0;
        /// <summary>
        /// The current single motion passed an effect event of its .xmot track (<see cref="G3MotionLibrary.MotionEvent"/>):
        /// the effect name, e.g. "EFF_Ani_Anvil_01". Blend sets (locomotion) and fading-out motions raise none.
        /// </summary>
        public event Action<string> EffectEvent;
        /// <summary>Every effect-track event of every motion player (effect visuals). Additive (effects-1).</summary>
        public static event Action<G3MotionPlayer, string> AnyEffectEvent;

        /// <summary>The current request is a one-shot that reached its last frame.</summary>
        public bool CurrentFinished => _current != null && !_current.loop && _current.phase >= 1f;

        /// <summary>The current request loops (it never reports <see cref="CurrentFinished"/>).</summary>
        public bool CurrentLoops => _current != null && _current.loop;

        /// <summary>
        /// G3's cross-fade time when <paramref name="starter"/> replaces the current request with <paramref name="next"/>
        /// (<see cref="G3.Player.MotionBlendRules"/>): 0 after a one-shot ended, else 0.1–0.5 s by who starts it.
        /// </summary>
        public float FadeFor(G3.Player.MotionStarter starter, string next) =>
            G3.Player.MotionBlendRules.FadeFor(starter, CurrentKey, _current != null && !CurrentFinished, next);

        /// <summary>The fade of the last request that started a motion or blend set, s (dev logs).</summary>
        public float LastFade { get; private set; }

        /// <summary>
        /// The number at the end of the current single motion's name, / 100 (<see cref="G3MotionLibrary.Entry.speed"/>); 0 for
        /// blend sets. Locomotion: a speed in m/s. Combat motions: a distance in m over the whole motion (Docs/combat.md §5.6).
        /// </summary>
        public float CurrentNameNumber => _current != null && _current.motions.Length == 1 ? _current.motions[0]?.speed ?? 0 : 0;

        object _claim;

        /// <summary>
        /// Someone (the combat selector) plays one-shots that outrank the locomotion and interaction selectors: they
        /// leave their requests while the player is claimed and start over when it is released.
        /// </summary>
        public bool Claimed => _claim != null;
        /// <summary>
        /// fix-180: the current request is all the body shows: faded in fully, no older request still fading out, no
        /// upper-body overlay (a corpse is laid down only in its own dead pose, not in a cross-fade frozen half-way).
        /// </summary>
        public bool CurrentAlone => _current != null && _current.weight >= 1f && _layers.Count == 1 && !HasOverlay;
        /// <summary>fix-76: the current request is a locomotion set (<see cref="PlayBlend"/>), not a single motion.</summary>
        public bool CurrentIsLocomotion => _current != null && _current.locomotion;

        /// <summary>crash-2 (dev): clip playables in the graph (they only grow up to the slot capacities).</summary>
        public int ClipPlayableCount
        {
            get
            {
                int n = 0;
                foreach (var m in _base) if (m != null) n += m.clips.Count;
                foreach (var m in _over) if (m != null) n += m.clips.Count;
                return n;
            }
        }

        /// <summary>crash-2 (dev): graph topology changes made since the player was enabled (graph creation excluded).</summary>
        public int TopologyChanges { get; private set; }

        /// <summary>Takes the player for <paramref name="owner"/> (a newer owner replaces an older one).</summary>
        public void Claim(object owner) => _claim = owner;

        /// <summary>Gives the player back, if <paramref name="owner"/> holds it.</summary>
        public void Release(object owner)
        {
            if (ReferenceEquals(_claim, owner)) _claim = null;
        }

        readonly G3.Player.PoseHolds _poseHolds = new G3.Player.PoseHolds();

        /// <summary>
        /// fix-202: the bones keep their pose (<see cref="HoldPose"/>): the graph is stopped and writes nothing. A disabled
        /// Animator alone does not stop a playing graph: the dying motion went on posing a ragdoll's bones.
        /// </summary>
        public bool PoseHeld => _poseHolds.Held;

        /// <summary>
        /// fix-202: <paramref name="owner"/> (a ragdoll, a corpse at rest) has the bones: the graph stops writing them until
        /// every owner called <see cref="ReleasePose"/>. The requests and their phases go on as before (the step logic of the
        /// combat animator still sees its dead pose), only nothing is shown.
        /// </summary>
        public void HoldPose(object owner)
        {
            if (_poseHolds.Hold(owner) && _graph.IsValid()) _graph.Stop();
        }

        /// <summary>fix-202: <paramref name="owner"/> gives the bones back; the graph plays again when nobody holds them.</summary>
        public void ReleasePose(object owner)
        {
            if (_poseHolds.Release(owner) && _graph.IsValid() && !_graph.IsPlaying()) _graph.Play();
        }

        void OnEnable()
        {
            EnsureGraph();
            _clock.Reset(Time.timeAsDouble, Time.deltaTime, Time.frameCount, s_batch.RanThisFrame);
            s_batch.Add(this);
        }

        /// <summary>Every motion player steps from one batched Update (perf-3, <see cref="G3FrameBatch"/>; execution order 200).</summary>
        static readonly G3FrameBatch.Batch<G3MotionPlayer> s_batch =
            new G3FrameBatch.Batch<G3MotionPlayer>(G3FrameBatch.Slot.Update200, "G3MotionPlayer", p => p.FrameUpdate());

        void EnsureGraph()
        {
            if (_graph.IsValid()) return;
            _graph = PlayableGraph.Create(name + ".G3Motion");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _root = AnimationMixerPlayable.Create(_graph, BaseSlots);
            for (int s = 0; s < BaseSlots; s++)
            {
                _base[s] = NewMix(BaseSlotInputs);
                _baseCaches[s] = _base[s].cache;
                _graph.Connect(_base[s].mixer, 0, _root, s);
                _root.SetInputWeight(s, 0);
            }
            _top = AnimationLayerMixerPlayable.Create(_graph, 1 + _overlays.Length); // base, then one layer per overlay slot
            _graph.Connect(_root, 0, _top, 0);
            _top.SetInputWeight(0, 1);
            for (int i = 0; i < _overlays.Length; i++)
            {
                _over[i] = NewMix(OverlaySlotInputs);
                _graph.Connect(_over[i].mixer, 0, _top, 1 + i);
                _top.SetInputWeight(1 + i, 0);
                _overMask[i] = null;
            }
            var output = AnimationPlayableOutput.Create(_graph, "Animation", GetComponent<Animator>());
            output.SetSourcePlayable(_top);
            _top.SetSpeed(_timeScale);
            // requests made while there was no graph look their clips up again (Commit connects what is missing)
            foreach (var l in _layers) l.bound = false;
            foreach (var o in _overlays) if (o != null) o.bound = false;
            _pending = _layers.Count > 0 || HasOverlay;
            if (!_poseHolds.Held) _graph.Play(); // fix-202: a held pose stays as it is
        }

        Mix NewMix(int capacity) => new Mix
        {
            mixer = AnimationMixerPlayable.Create(_graph, 0),
            cache = new MotionInputCache<AnimationClip>(capacity),
        };

        void OnDisable()
        {
            s_batch.Remove(this);
            _interval = 1;
            if (_graph.IsValid()) _graph.Destroy();   // the only place clip playables are destroyed (crash-2)
            for (int s = 0; s < BaseSlots; s++) { _base[s] = null; _baseCaches[s] = null; _baseBusy[s] = false; }
            _over[0] = _over[1] = null;
            _overMask[0] = _overMask[1] = null;
            _pending = false;
            TopologyChanges = 0;
            _layers.Clear();
            _current = null;
            _overlays[0] = _overlays[1] = null;
            _overlay = -1;
            foreach (var m in _masks.Values)
                if (m != null && m != _spineMask) Destroy(m);
            if (_spineMask != null) Destroy(_spineMask);
            _masks.Clear();
            _spineMask = null;
            _maskBones = null; // the rig may be rebuilt (remaster swap) before the next enable
            _renderers = null;
        }

        /// <summary>
        /// fix-105: the model's bones changed (a part swapped, a bone added or removed; a load or new game): the overlay masks
        /// are built again from the rig at the next overlay. Not destroyed here: a layer may still use one (few, one set per
        /// rig change).
        /// </summary>
        public void RigChanged()
        {
            _masks.Clear();
            _spineMask = null;
            _maskBones = null;
            _renderers = null;
        }

        /// <summary>Crossfades to one motion. Returns false if the library has no such motion (nothing changes).</summary>
        public bool Play(G3MotionKey key, float fade, bool restart = false) => Play(key.ToString(), fade, restart);

        /// <summary>
        /// Crossfades to one motion; <paramref name="reverse"/> plays it backwards (G3's reverse motion flag: the phase
        /// still runs 0 → 1, the clip from its last frame to its first; effect tracks fire their <c>[R]</c> events).
        /// </summary>
        public bool Play(string key, float fade, bool restart = false, bool reverse = false)
        {
            if (!restart && _current != null && _current.key == key && _current.reverse == reverse) return true;
            var e = library != null ? library.Find(key) : null;
            if (e == null) return false;
            var l = StartLayer(key, new[] { e }, fade, reverse);
            l.weights[0] = 1;
            Apply(l);
            return true;
        }

        /// <summary>A one-shot current request jumps to its last frame (fix-217: a held enter motion shown at once).</summary>
        public void FinishCurrent()
        {
            if (_current == null || _current.loop) return;
            _current.phase = 1f;
            Apply(_current);
        }

        /// <summary>The current request plays backwards.</summary>
        public bool CurrentReversed => _current != null && _current.reverse;

        /// <summary>
        /// Crossfades to a synchronised set of motions (missing entries are skipped; returns the number found). The set is
        /// identified by <paramref name="setKey"/>; asking again for the same set keeps it running. Weights start at
        /// the first motion and are set with <see cref="SetWeights"/>.
        /// </summary>
        public int PlayBlend(string setKey, IReadOnlyList<string> keys, float fade)
        {
            if (_current != null && _current.key == setKey) return _current.motions.Length;
            var found = new List<G3MotionLibrary.Entry>();
            foreach (var k in keys)
                found.Add(library != null ? library.Find(k) : null);
            if (found.TrueForAll(f => f == null)) return 0;
            var previous = _current;
            var l = StartLayer(setKey, found.ToArray(), fade);
            l.locomotion = true;
            for (int i = 0; i < l.weights.Length; i++) if (l.motions[i] != null) { l.weights[i] = 1; break; }
            // fix-76: a set switch while walking or running keeps the gait cycle (G3.Player.MotionBlendRules.StartPhase)
            float phase = previous != null && previous != l
                ? G3.Player.MotionBlendRules.StartPhase(previous.locomotion, previous.loop, l.loop, previous.phase) : 0f;
            if (phase > 0f)
            {
                l.phase = phase;
                Evaluate(l, 0);
            }
            Apply(l);
            return found.FindAll(f => f != null).Count;
        }

        /// <summary>Blend weights of the current set, in the order of the keys given to <see cref="PlayBlend"/>.</summary>
        public void SetWeights(IReadOnlyList<float> w)
        {
            if (_current == null) return;
            for (int i = 0; i < _current.weights.Length && i < w.Count; i++)
                _current.weights[i] = _current.motions[i] != null ? Mathf.Max(0, w[i]) : 0;
        }

        /// <summary>Playback rate of the current request (1 = authored speed).</summary>
        public void SetSpeed(float speed)
        {
            if (_current != null) _current.speed = Mathf.Max(0, speed);
        }

        // ------------------------------------------------------------------------------------------- upper-body overlay

        /// <summary>Key of the overlay motion playing (not fading out), null when none.</summary>
        public string OverlayKey => _overlay >= 0 && _overlays[_overlay] is Overlay o && o.fadeRate >= 0 ? o.key : null;
        /// <summary>An overlay layer has weight (playing or still fading out).</summary>
        public bool HasOverlay => _overlays[0] != null || _overlays[1] != null;
        /// <summary>Normalised time of the overlay motion.</summary>
        public float OverlayPhase => _overlay >= 0 && _overlays[_overlay] != null ? _overlays[_overlay].phase : 0;
        /// <summary>The overlay is a one-shot that reached its last frame.</summary>
        public bool OverlayFinished => _overlay >= 0 && _overlays[_overlay] is Overlay o && !o.motion.loop && o.phase >= 1f;

        /// <summary>The current overlay motion played through once (a looping one wrapped around, a one-shot reached its end).</summary>
        public bool OverlayPlayedOnce => _overlay >= 0 && _overlays[_overlay] is Overlay o && (o.once || !o.motion.loop && o.phase >= 1f);

        /// <summary>
        /// Crossfades the upper body to an overlay motion (a G3 "O" motion: only the spine, arms and head have tracks);
        /// the legs keep the base request. False if the library has no such motion.
        /// </summary>
        public bool PlayOverlay(string key, float fade, bool restart = false, bool reverse = false)
        {
            if (!restart && OverlayKey == key && _overlays[_overlay].reverse == reverse) return true;
            var e = library != null ? library.Find(key) : null;
            if (e == null) return false;
            int slot = _overlay < 0 ? 0 : 1 - _overlay;
            float rate = 1f / Mathf.Max(0.01f, fade);
            if (_overlay >= 0 && _overlays[_overlay] != null) _overlays[_overlay].fadeRate = fade <= 0 ? -1e6f : -rate;
            var o = new Overlay { key = key, motion = e, weight = fade <= 0 ? 1 : 0, fadeRate = rate, reverse = reverse };
            o.clipKey[0] = e.clip;
            _overlays[slot] = o;   // the overlay that had this slot is replaced (its playable stays cached)
            _overlay = slot;
            if (_graph.IsValid())
            {
                var mix = _over[slot];
                for (int i = 0; i < mix.clips.Count; i++) mix.mixer.SetInputWeight(i, 0);
                BindOverlay(slot);
                var mask = MaskFor(e);
                if (mask != null && mask != _overMask[slot])
                {
                    _top.SetLayerMaskFromAvatarMask((uint)(1 + slot), mask);
                    _overMask[slot] = mask;
                }
            }
            EvaluateOverlays(0);
            return true;
        }

        /// <summary>Fades the overlay out: the whole body follows the base request again.</summary>
        public void StopOverlay(float fade)
        {
            foreach (var o in _overlays)
                if (o != null) o.fadeRate = fade <= 0 ? -1e6f : -1f / fade;
        }

        /// <summary>Playback rate of the current overlay motion.</summary>
        public void SetOverlaySpeed(float speed)
        {
            if (_overlay >= 0 && _overlays[_overlay] != null) _overlays[_overlay].speed = Mathf.Max(0, speed);
        }

        /// <summary>
        /// The mask of an overlay motion: the nodes it drives (<see cref="G3MotionLibrary.Entry.drives"/>), else (a library
        /// imported before fix-89) the <c>Spine_2</c> bone and its whole subtree; null without a humanoid spine (the overlay
        /// then drives what its clip has).
        /// </summary>
        AvatarMask MaskFor(G3MotionLibrary.Entry e)
        {
            if (_masks.TryGetValue(e, out var m)) return m;
            // The cached hierarchy also holds transient children (the spell orb and held effect on the hand, a drawn
            // weapon, effect parts): once one of them is destroyed the cache is stale and the next new mask threw a
            // NullReferenceException in the middle of BeginCast / StartHold (fix-110: mana paid, no spell).
            if (_maskBones == null || Array.Exists(_maskBones, b => b == null)) _maskBones = GetComponentsInChildren<Transform>(true);
            var all = _maskBones;
            if (e.drives != null && e.drives.Length > 0)
            {
                var set = new HashSet<string>(e.drives, StringComparer.Ordinal);
                m = Mask(all, t => t != transform && set.Contains(t.name));
            }
            else
            {
                if (_spineMask == null)
                {
                    Transform spine = Array.Find(all, t => t.name.EndsWith("Spine_Spine_2", StringComparison.Ordinal));
                    if (spine != null) _spineMask = Mask(all, t => t.IsChildOf(spine));
                }
                m = _spineMask;
            }
            _masks[e] = m;
            return m;
        }

        AvatarMask Mask(Transform[] all, Predicate<Transform> active)
        {
            var mask = new AvatarMask { transformCount = all.Length };
            for (int i = 0; i < all.Length; i++)
            {
                mask.SetTransformPath(i, PathOf(all[i]));
                mask.SetTransformActive(i, active(all[i]));
            }
            return mask;
        }

        string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null && t != transform; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        void EvaluateOverlays(float dt)
        {
            for (int i = 0; i < _overlays.Length; i++)
            {
                var o = _overlays[i];
                if (o == null) continue;
                o.weight = Mathf.Clamp01(o.weight + o.fadeRate * dt);
                if (o.weight <= 0 && o.fadeRate < 0)
                {
                    if (_graph.IsValid())
                    {
                        _top.SetInputWeight(1 + i, 0);
                        if (o.input[0] >= 0) _over[i].mixer.SetInputWeight(o.input[0], 0);   // stays connected (crash-2)
                    }
                    _overlays[i] = null;
                    if (_overlay == i) _overlay = _overlays[1 - i] != null ? 1 - i : -1;
                    continue;
                }
                float length = o.motion.Length;
                float next = o.phase + dt * o.speed / length;
                if (next >= 1f) o.once = true;
                o.phase = o.motion.loop ? Mathf.Repeat(next, 1f) : Mathf.Min(1f, next);
                ApplyOverlay(i);
            }
        }

        void ApplyOverlay(int i)
        {
            var o = _overlays[i];
            if (o == null || !_graph.IsValid()) return;
            int input = o.bound ? o.input[0] : -1;
            if (input < 0) { _top.SetInputWeight(1 + i, 0); return; }   // no playable yet (Commit): the base shows
            var mix = _over[i];
            mix.mixer.SetInputWeight(input, 1);
            mix.clips[input].SetTime((o.reverse ? 1f - o.phase : o.phase) * o.motion.Length);
            _top.SetInputWeight(1 + i, o.weight);
        }

        void BindOverlay(int slot)
        {
            var o = _overlays[slot];
            if (o == null || o.bound || !_graph.IsValid()) return;
            _over[slot].cache.Bind(o.clipKey, o.input);
            o.bound = true;
            if (o.input[0] < 0) _pending = true;
        }

        Layer StartLayer(string key, G3MotionLibrary.Entry[] motions, float fade, bool reverse = false)
        {
            LastFade = Mathf.Max(0, fade);
            var l = new Layer
            {
                key = key,
                motions = motions,
                clipKeys = new AnimationClip[motions.Length],
                inputs = new int[motions.Length],
                weights = new float[motions.Length],
                loop = true,
                reverse = reverse,
            };
            for (int i = 0; i < motions.Length; i++)
            {
                l.inputs[i] = -1;
                if (motions[i] == null) continue;
                l.clipKeys[i] = motions[i].clip;
                l.loop &= motions[i].loop;
            }

            float rate = 1f / Mathf.Max(0.01f, fade);
            if (_layers.Count == 0 || fade <= 0)
            {
                foreach (var old in _layers) old.fadeRate = -1e6f;
                l.weight = 1;
            }
            else
            {
                foreach (var old in _layers) old.fadeRate = -rate;
            }
            l.fadeRate = rate;

            // a slot: the free one holding most of the clips; all busy: the oldest fading request makes room
            int slot = _graph.IsValid() ? MotionInputCache<AnimationClip>.PickSlot(_baseCaches, _baseBusy, l.clipKeys) : FreeSlot();
            if (slot < 0)
            {
                DropLayer(0);   // the oldest; never the new current (not added yet)
                slot = _graph.IsValid() ? MotionInputCache<AnimationClip>.PickSlot(_baseCaches, _baseBusy, l.clipKeys) : FreeSlot();
            }
            l.slot = slot;
            _baseBusy[slot] = true;
            if (_graph.IsValid())
            {
                var mix = _base[slot];
                for (int i = 0; i < mix.clips.Count; i++) mix.mixer.SetInputWeight(i, 0);
                BindLayer(l);
            }
            _layers.Add(l);
            _current = l;
            Evaluate(l, 0);
            ApplyRootWeights();
            return l;
        }

        int FreeSlot()
        {
            for (int s = 0; s < BaseSlots; s++) if (!_baseBusy[s]) return s;
            return -1;
        }

        void BindLayer(Layer l)
        {
            if (l.bound || !_graph.IsValid()) return;
            _base[l.slot].cache.Bind(l.clipKeys, l.inputs);
            l.bound = true;
            for (int i = 0; i < l.inputs.Length; i++)
                if (l.motions[i] != null && l.inputs[i] < 0) { _pending = true; break; }
        }

        /// <summary>Removes a request (faded out, or pushed out by a newer one): its slot is free, its playables stay.</summary>
        void DropLayer(int index)
        {
            var l = _layers[index];
            if (_graph.IsValid())
            {
                _root.SetInputWeight(l.slot, 0);
                var mix = _base[l.slot];
                foreach (var input in l.inputs) if (input >= 0) mix.mixer.SetInputWeight(input, 0);
            }
            _baseBusy[l.slot] = false;
            _layers.RemoveAt(index);
        }

        // ------------------------------------------------------------------------------------------- topology (crash-2)

        /// <summary>
        /// The only place the graph's topology changes after it was built: connects a clip playable for every clip a
        /// request still lacks. Called from the batched Update (before and after the step), not from event handlers or
        /// graph callbacks; skipped while the Animator is disabled or culled completely (the request's state goes on, its
        /// clips are connected once the body is shown again).
        /// </summary>
        void Commit()
        {
            if (!_pending || !_graph.IsValid() || !CanChangeTopology()) return;
            _pending = false;
            foreach (var l in _layers)
            {
                BindLayer(l);
                var mix = _base[l.slot];
                for (int i = 0; i < l.motions.Length; i++)
                {
                    if (l.motions[i] == null || l.inputs[i] >= 0) continue;
                    l.inputs[i] = Connect(mix, l.clipKeys[i], l.inputs);
                }
                Apply(l);
            }
            for (int s = 0; s < _overlays.Length; s++)
            {
                var o = _overlays[s];
                if (o == null) continue;
                BindOverlay(s);
                if (o.input[0] < 0)
                {
                    o.input[0] = Connect(_over[s], o.clipKey[0], o.input);
                    var mask = MaskFor(o.motion);
                    if (mask != null && mask != _overMask[s])
                    {
                        _top.SetLayerMaskFromAvatarMask((uint)(1 + s), mask);
                        _overMask[s] = mask;
                    }
                }
                ApplyOverlay(s);
            }
            _pending = false;   // binding above may have set it again for inputs it then connected
            ApplyRootWeights();
        }

        /// <summary>Gives <paramref name="clip"/> a playable at an input of <paramref name="mix"/> (new, or an LRU one replaced).</summary>
        int Connect(Mix mix, AnimationClip clip, int[] inUse)
        {
            var p = mix.cache.Place(clip, inUse);
            var c = AnimationClipPlayable.Create(_graph, clip);
            c.SetApplyFootIK(false);
            c.Pause(); // time is set explicitly in Update
            if (p.Appended)
            {
                if (mix.mixer.GetInputCount() <= p.Input) mix.mixer.SetInputCount(p.Input + 1);
                mix.clips.Add(c);
            }
            else
            {
                mix.mixer.DisconnectInput(p.Input);
                var old = mix.clips[p.Input];
                if (old.IsValid()) old.Destroy();
                mix.clips[p.Input] = c;
            }
            _graph.Connect(c, 0, mix.mixer, p.Input);
            mix.mixer.SetInputWeight(p.Input, 0);
            TopologyChanges++;
            return p.Input;
        }

        bool CanChangeTopology()
        {
            if (_animator == null) _animator = GetComponent<Animator>();
            if (_animator == null) return false;
            bool active = _animator.isActiveAndEnabled;
            bool culls = _animator.cullingMode == AnimatorCullingMode.CullCompletely;
            return MotionTopology.CanChange(active, culls, active && culls && AnyRendererVisible());
        }

        bool AnyRendererVisible()
        {
            int frame = G3FrameBatch.Frame;
            bool stale = _renderers == null || frame - _renderersFrame > 30;
            if (!stale) foreach (var r in _renderers) if (r == null) { stale = true; break; }
            if (stale)
            {
                _renderers = GetComponentsInChildren<Renderer>();
                _renderersFrame = frame;
            }
            foreach (var r in _renderers)
                if (r != null && r.isVisible) return true;
            return false;
        }

        /// <summary>
        /// Time scale of every motion (0 = the body stands frozen, G3 ZS_Freeze; 1 = normal). Effect events wait too.
        /// </summary>
        public float TimeScale
        {
            get => _timeScale;
            set
            {
                _timeScale = Mathf.Max(0, value);
                if (_graph.IsValid()) _top.SetSpeed(_timeScale);
            }
        }

        float _timeScale = 1f;

        int _interval = 1, _phase = -1;
        static int s_nextPhase;
        StepClock _clock;

        /// <summary>
        /// Update LOD (<see cref="NpcUpdateLod.Frames"/>, set by the NPC view on its steps): the player steps every
        /// <c>UpdateInterval</c>-th frame with the time gathered meanwhile. 1 (default, the hero) = every frame. A claimed
        /// player (combat) or one with an overlay always steps every frame.
        /// </summary>
        public int UpdateInterval
        {
            get => _interval;
            set => _interval = Mathf.Clamp(value, 1, NpcUpdateLod.MaxFrames);
        }

        /// <summary>This frame is a step frame (the same answer for the view, its animation selector and this player).</summary>
        public bool UpdateDue
        {
            get
            {
                if (_interval <= 1 || _claim != null || _overlay >= 0) return true;
                if (_phase < 0) _phase = (s_nextPhase++ * 7) & 0xFFFF; // spread the views over the frames (7: coprime to the usual intervals)
                return NpcUpdateLod.Due(G3FrameBatch.Frame, _interval, _phase);   // the frame number without an engine call (perf-3)
            }
        }

        void FrameUpdate()
        {
            Commit();   // requests made since the last step (late scripts, a disabled or culled body that is shown again)
            if (!UpdateDue) return;
            float dt = _clock.Take(G3FrameBatch.Now, G3FrameBatch.DeltaTime, G3FrameBatch.Frame) * _timeScale;
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var l = _layers[i];
                l.weight = Mathf.Clamp01(l.weight + l.fadeRate * dt);
                if (l != _current && l.weight <= 0)
                {
                    DropLayer(i);
                    continue;
                }
                Evaluate(l, dt);
                Apply(l);
            }
            if (_current != null) RaiseEvents(_current);
            ApplyRootWeights();
            if (_graph.IsValid()) EvaluateOverlays(dt);
            Commit();   // requests made by the effect event handlers above
        }

        /// <summary>
        /// Raises the events of the current motion whose time lies in the phase range of the last step. In a blend set the
        /// motion with the largest weight (at least half) speaks for the set: the Swim strokes carry
        /// <c>eff_ani_swim_sound_01</c> in a walk/run blend; the land locomotion clips have no events.
        /// </summary>
        void RaiseEvents(Layer l)
        {
            if ((EffectEvent == null && AnyEffectEvent == null) || l.to <= l.from || l.motions.Length == 0) return;
            int m = 0;
            if (l.motions.Length > 1)
            {
                m = -1;
                float best = 0.5f;
                for (int i = 0; i < l.motions.Length; i++)
                    if (l.motions[i] != null && l.weights[i] >= best) { best = l.weights[i]; m = i; }
                if (m < 0) return;
            }
            var motion = l.motions[m];
            if (motion?.events == null || motion.events.Length == 0) return;
            // [F] forward playback only, [R] reverse playback only (a sheathe plays the draw motions backwards); a loop
            // fires its events every cycle (G3.Audio.MotionEventWindow, tested in G3Tests).
            _due.Clear();
            G3.Audio.MotionEventWindow.Collect(motion.Track, motion.Length, l.from, l.to, l.loop, l.reverse, _due);
            foreach (var name in _due)
            {
                EffectEvent?.Invoke(name);
                AnyEffectEvent?.Invoke(this, name); // visuals of the motion's effect track (G3EffectSystem)
            }
        }

        readonly List<string> _due = new List<string>();

        /// <summary>Advances the shared phase at the weight-averaged clip rate.</summary>
        static void Evaluate(Layer l, float dt)
        {
            float sum = 0, rate = 0;
            for (int i = 0; i < l.weights.Length; i++)
            {
                if (l.motions[i] == null) continue;
                sum += l.weights[i];
                rate += l.weights[i] / l.motions[i].Length;
            }
            if (sum <= 1e-5f)
            {
                // No weight given yet: show the first available motion.
                for (int i = 0; i < l.weights.Length; i++) if (l.motions[i] != null) { l.weights[i] = 1; sum = 1; rate = 1 / l.motions[i].Length; break; }
            }
            rate /= Mathf.Max(sum, 1e-5f);
            l.elapsed += dt;
            l.from = l.phase;
            l.to = l.phase + dt * rate * l.speed;
            if (!l.loop) l.to = Mathf.Min(l.to, 1f);
            l.phase = l.loop ? Mathf.Repeat(l.to, 1f) : l.to;
        }

        /// <summary>
        /// Sets every connected clip's time and weight (normalised over the clips that have a playable: one still waiting
        /// for <see cref="Commit"/> does not blend the body towards the default pose).
        /// </summary>
        void Apply(Layer l)
        {
            if (!_graph.IsValid() || !l.bound) return;
            var mix = _base[l.slot];
            float sum = 0;
            for (int i = 0; i < l.weights.Length; i++)
                if (l.motions[i] != null && l.inputs[i] >= 0) sum += l.weights[i];
            if (sum <= 1e-5f) return;
            for (int i = 0; i < l.weights.Length; i++)
            {
                if (l.motions[i] == null || l.inputs[i] < 0) continue;
                mix.mixer.SetInputWeight(l.inputs[i], l.weights[i] / sum);
                mix.clips[l.inputs[i]].SetTime((l.reverse ? 1f - l.phase : l.phase) * l.motions[i].Length);
            }
        }

        static bool Shown(Layer l)
        {
            if (!l.bound) return false;
            for (int i = 0; i < l.inputs.Length; i++)
                if (l.motions[i] != null && l.inputs[i] >= 0 && l.weights[i] > 1e-5f) return true;
            return false;
        }

        void ApplyRootWeights()
        {
            float sum = 0;
            foreach (var l in _layers) sum += l.weight;
            if (sum <= 1e-5f && _current != null) _current.weight = 1;
            if (!_graph.IsValid()) return;
            // the graph's share: only requests whose clips have playables (one waiting for Commit gives its weight away)
            float shown = 0;
            foreach (var l in _layers) if (Shown(l)) shown += l.weight;
            for (int s = 0; s < BaseSlots; s++) _root.SetInputWeight(s, 0);
            if (shown <= 1e-5f) return;
            foreach (var l in _layers) _root.SetInputWeight(l.slot, Shown(l) ? l.weight / shown : 0);
        }
    }
}
