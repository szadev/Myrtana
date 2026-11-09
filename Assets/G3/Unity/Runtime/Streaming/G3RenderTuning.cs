using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Applies <see cref="RenderTuningOptions"/> to the streamed world (Docs/performance.md, "CPU cost of the GPU Resident
    /// Drawer").
    /// <para>Light probes: the world has no baked light probes, yet every imported mesh renderer used
    /// LightProbeUsage.BlendProbes. <see cref="G3Environment"/> rewrites RenderSettings.ambientProbe every frame (the game
    /// clock moves the sun), and the GPU Resident Drawer answers each change by re-interpolating and re-uploading the probe
    /// data of every BlendProbes instance (InstanceDataSystem.UpdateAllInstanceProbes, ~150k instances around Faring).
    /// With LightProbeUsage.Off the shader reads the ambient probe directly: same image, no per-instance work.</para>
    /// Renderers are switched before the GPU Resident Drawer registers them: active cell content when its scene has loaded
    /// (registration happens later in the frame), deferred content in <see cref="G3DeferredActivation"/> just before it wakes.
    /// </summary>
    public static class G3RenderTuning
    {
        /// <summary>The effective switches: the menu's (<see cref="SetMenuOptions"/>) under the command line's.</summary>
        public static RenderTuningOptions Options { get; private set; } = new RenderTuningOptions();

        /// <summary>The switches given on the player command line (they win over the menu).</summary>
        public static RenderTuningOptions CommandLine { get; private set; } = new RenderTuningOptions();

        /// <summary>Raised after <see cref="Options"/> changed at runtime (the Performance options).</summary>
        public static event System.Action Changed;

        static RenderTuningOptions _menu;
        static bool _haveAssetDefaults;
        static int _assetCascades;
        static Vector4 _assetShadowSmall;
        static float _assetSmallMesh;

        /// <summary>Renderers whose light-probe usage was changed so far (for the perf probe).</summary>
        public static long RenderersChanged { get; private set; }

        static readonly List<MeshRenderer> Buffer = new List<MeshRenderer>();
        static readonly ProfilerMarker SceneMarker = new ProfilerMarker("G3RenderTuning.SceneLoaded");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            CommandLine = RenderTuningOptions.Parse(System.Environment.GetCommandLineArgs());
            Options = RenderTuningOptions.Merge(_menu, CommandLine);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.quitting -= RestoreAsset;
            Application.quitting += RestoreAsset; // the editor's asset on disk must not keep the menu's values
            if (Application.isPlaying) ApplyPipelineOverrides();
        }

        /// <summary>
        /// -g3cascades / -g3shadowsmall / -g3smallmesh: written into the active URP asset once at start (player only in
        /// practice: the editor gets no such arguments, so the asset on disk is never changed). By reflection because
        /// G3.Unity does not reference the URP assembly; the property names are URP's public API.
        /// </summary>
        static void ApplyPipelineOverrides()
        {
            G3HitchLog.Mark("render pipeline settings applied (GPU Resident Drawer rebuild)");   // fix-251
            var o = Options;
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return;
            var t = rp.GetType();
            try
            {
                // The asset's own values, to go back to when a switch is "leave alone" again (the menu's changes are runtime
                // only; in the editor the asset on disk would otherwise keep the last menu value).
                if (!_haveAssetDefaults)
                {
                    _assetCascades = Get(rp, t, "shadowCascadeCount", 4);
                    _assetShadowSmall = Get(rp, t, "shadowSmallMeshScreenPercentages", Vector4.zero);
                    _assetSmallMesh = Get(rp, t, "smallMeshScreenPercentage", 0f);
                    _haveAssetDefaults = true;
                }
                if (o.Cascades <= 0 && o.ShadowSmallMesh < 0 && o.SmallMesh < 0 && _menu == null) return;
                Set(rp, t, "shadowCascadeCount", o.Cascades > 0 ? o.Cascades : _assetCascades);
                Set(rp, t, "shadowSmallMeshScreenPercentages", o.ShadowSmallMesh >= 0 ? Vector4.one * o.ShadowSmallMesh : _assetShadowSmall);
                Set(rp, t, "smallMeshScreenPercentage", o.SmallMesh >= 0 ? o.SmallMesh : _assetSmallMesh);
                Debug.Log($"[G3RenderTuning] {rp.name}: {o.Describe()}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[G3RenderTuning] pipeline override failed: {e.GetBaseException().Message}");
            }
        }

        /// <summary>
        /// The Performance options' switches (cascades, shadow and camera small-mesh culling, shadowed point lights, light
        /// distance, NPC LOD); the command line still wins. Applied at once (URP asset, light budget, NPC views).
        /// </summary>
        public static void SetMenuOptions(RenderTuningOptions menu)
        {
            _menu = menu;
            Options = RenderTuningOptions.Merge(menu, CommandLine);
            if (Application.isPlaying) ApplyPipelineOverrides();
            Changed?.Invoke();
        }

        /// <summary>Puts the URP asset back to its own values (the editor leaving Play mode).</summary>
        public static void RestoreAsset()
        {
            if (!_haveAssetDefaults) return;
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return;
            var t = rp.GetType();
            Set(rp, t, "shadowCascadeCount", _assetCascades);
            Set(rp, t, "shadowSmallMeshScreenPercentages", _assetShadowSmall);
            Set(rp, t, "smallMeshScreenPercentage", _assetSmallMesh);
        }

        static T Get<T>(object target, System.Type t, string property, T fallback)
        {
            var p = t.GetProperty(property);
            return p != null && p.CanRead && p.GetValue(target) is T v ? v : fallback;
        }

        static void Set(object target, System.Type t, string property, object value)
        {
            var p = t.GetProperty(property);
            if (p == null || !p.CanWrite) Debug.LogWarning($"[G3RenderTuning] {t.Name}.{property} not found (stripped?)");
            else p.SetValue(target, value);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Application.isPlaying) return;
            // World cells only (the master scene's few renderers, e.g. the low-poly backdrop, keep their own settings).
            if (!scene.name.StartsWith("Cell_", System.StringComparison.Ordinal)) return;
            using var _ = SceneMarker.Auto();
            // Active content only: inactive (deferred) content is handled by G3DeferredActivation when it wakes.
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!root.activeSelf) continue;
                root.GetComponentsInChildren(false, Buffer);
                ApplyProbes(Buffer);
                G3FarCull.Register(root, Buffer);   // perf-2: far shadows off / small far objects hidden from the start
            }
            Buffer.Clear();
        }

        /// <summary>Called by <see cref="G3DeferredActivation"/> for a target and its mesh renderers before it is activated.</summary>
        public static void BeforeWake(GameObject target, List<MeshRenderer> renderers)
        {
            ApplyProbes(renderers);
            if (!Options.TreeCrossFade && target != null && target.TryGetComponent(out LODGroup group) && group.fadeMode != LODFadeMode.None)
                group.fadeMode = LODFadeMode.None;
            G3TreeFarLod.BeforeWake(target);   // perf-22d: far trees wake with their geometry off (billboard only)
            // perf-2: trees (not NPCs: their equipment moves and G3CharacterRemaster copies their shadow mode)
            if (target != null && target.TryGetComponent(out LODGroup _)) G3FarCull.Register(target, renderers);
        }

        static void ApplyProbes(List<MeshRenderer> renderers)
        {
            if (Options.Probes == WorldProbeMode.Keep || renderers == null) return;
            var want = Options.Probes == WorldProbeMode.Off ? LightProbeUsage.Off : LightProbeUsage.BlendProbes;
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r == null || r.lightProbeUsage == want) continue;
                // Only the two plain modes are switched; proxy volumes / custom provided probes are left alone.
                if (r.lightProbeUsage != LightProbeUsage.BlendProbes && r.lightProbeUsage != LightProbeUsage.Off) continue;
                r.lightProbeUsage = want;
                RenderersChanged++;
            }
        }
    }
}
