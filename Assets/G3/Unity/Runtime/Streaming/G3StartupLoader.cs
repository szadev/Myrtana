using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Start-up loading state, as in Gothic 3: after Play / game start the player stays frozen behind the loading
    /// screen (<see cref="G3LoadingOverlay"/>) until the area around it is complete — every cell the streamer wants
    /// there is loaded, all their deferred content (<see cref="G3DeferredActivation"/>) is awake, and the frame time
    /// has settled (first-use shader/PSO creation, GPU uploads). Meanwhile cells load at high background priority and
    /// deferred content wakes without a frame budget, since nobody sees the hitches. Without it the first 10–30 s of
    /// play were a slide show (Docs/performance.md, "Startup"). Added by <see cref="G3WorldStreamer"/> at Play start.
    /// With <see cref="Gate.View"/> it waits only for what the player sees first (<see cref="G3WorldStreamer.VisibleCells"/>)
    /// and loads nothing else meanwhile; the remaining cells stream in afterwards like during normal play.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Startup Loader")]
    public sealed class G3StartupLoader : MonoBehaviour
    {
        public enum Gate
        {
            /// <summary>The player's cell and the neighbour cells the camera sees.</summary>
            View,
            /// <summary>Every cell within the streamer's load distance.</summary>
            Area,
        }

        public G3WorldStreamer streamer;
        public Gate gate = Gate.View;
        [Tooltip("Frames in a row that must be shorter than settleFrameMs before the player is released.")]
        public int settleFrames = 20;
        public float settleFrameMs = 50f;
        [Tooltip("Release the player after this many seconds whatever the state.")]
        public float maxSeconds = 120f;
        [Tooltip("G3DeferredActivation budget (ms per frame) while the loading screen covers the view.")]
        public float activationBudgetMs = 250f;
        [Tooltip("G3DeferredActivation renderer cap (mesh renderers per frame) while the loading screen covers the view.")]
        public int activationRenderersPerFrame = int.MaxValue;

        public bool IsLoading { get; private set; }
        /// <summary>0..1 for the progress bar.</summary>
        public float Progress { get; private set; }
        /// <summary>Seconds the loading state lasted (valid after <see cref="Finished"/>).</summary>
        public float Duration { get; private set; }
        public event Action Finished;

        static readonly List<Func<bool>> Prerequisites = new List<Func<bool>>();

        /// <summary>
        /// Keeps the loading screen up until <paramref name="isDone"/> returns true, in addition to the cells (e.g. the
        /// game session, whose content is parsed on a worker thread). May be called before the loader exists; checked
        /// once per frame on the main thread and dropped once it is done.
        /// </summary>
        public static void WaitFor(Func<bool> isDone) => Prerequisites.Add(isDone);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Prerequisites.Clear();   // domain reload is off in the editor

        static bool PrerequisitesDone()
        {
            Prerequisites.RemoveAll(isDone => isDone());
            return Prerequisites.Count == 0;
        }

        float _t0, _savedBudget;
        int _calmFrames, _savedRenderers;
        readonly HashSet<int> _gateCells = new HashSet<int>();

        void Start()
        {
            if (streamer == null) streamer = GetComponent<G3WorldStreamer>();
            if (streamer == null || streamer.manifest == null) { enabled = false; return; }
            IsLoading = true;
            _t0 = Time.realtimeSinceStartup;
            _savedBudget = G3DeferredActivation.BudgetMs;
            G3DeferredActivation.BudgetMs = activationBudgetMs;
            _savedRenderers = G3DeferredActivation.MaxRenderersPerFrame;
            G3DeferredActivation.MaxRenderersPerFrame = activationRenderersPerFrame;
            streamer.Hold(this, true);
        }

        void Update()
        {
            if (!IsLoading) return;
            var pos = streamer.holdUntilReady != null ? streamer.holdUntilReady.transform.position : streamer.Centre;
            int loaded, total;
            bool awake;
            if (gate == Gate.View)
            {
                // Re-evaluated every frame: the camera settles behind the player during the first frames.
                streamer.VisibleCells(pos, _gateCells);
                streamer.LoadOnly = _gateCells;
                loaded = 0; total = _gateCells.Count;
                int woken = 0;
                foreach (int i in _gateCells)
                {
                    if (streamer.IsLoaded(i)) loaded++;
                    if (streamer.IsAwake(i)) woken++;
                }
                awake = woken >= total;
            }
            else
            {
                (loaded, total) = streamer.AreaProgress(pos);
                awake = loaded >= total && streamer.IsReady(pos) && G3DeferredActivation.Pending == 0;
            }
            awake &= PrerequisitesDone();
            _calmFrames = awake && Time.unscaledDeltaTime * 1000f < settleFrameMs ? _calmFrames + 1 : 0;

            float cells = total > 0 ? (float)loaded / total : 1f;
            Progress = 0.85f * cells + (awake ? 0.05f : 0f) + 0.1f * Mathf.Clamp01((float)_calmFrames / settleFrames);
            if (_calmFrames >= settleFrames) Finish();
            else if (Time.realtimeSinceStartup - _t0 > maxSeconds)
            {
                // Which gate never opened (editor runner: the 120 s limit was hit while the start-up logos played).
                Debug.LogWarning($"G3StartupLoader: {maxSeconds:0} s limit — cells {loaded}/{total}, awake {awake}, " +
                                 $"prerequisites left {Prerequisites.Count}, deferred {G3DeferredActivation.Pending}, " +
                                 $"last frame {Time.unscaledDeltaTime * 1000f:0} ms (calm needs < {settleFrameMs:0}), at {pos}");
                Finish();
            }
        }

        void Finish()
        {
            IsLoading = false;
            Progress = 1f;
            Duration = Time.realtimeSinceStartup - _t0;
            G3DeferredActivation.BudgetMs = _savedBudget;
            G3DeferredActivation.MaxRenderersPerFrame = _savedRenderers;
            Prerequisites.Clear();   // released by maxSeconds: stop waiting
            streamer.LoadOnly = null;
            streamer.Hold(this, false);
            Debug.Log($"G3StartupLoader: {gate} gate ready after {Duration:F1} s ({streamer.LoadedCount} cells loaded, {_gateCells.Count} gated)");
            Finished?.Invoke();
        }

        void OnDisable()
        {
            if (IsLoading) Finish();
        }
    }
}
