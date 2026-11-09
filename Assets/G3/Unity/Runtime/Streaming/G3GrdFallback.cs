using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Troubleshooting switch (Docs/performance.md, "Native crash with the GPU Resident Drawer"; Docs/demo/README_*.md):
    /// <c>-g3nogrd</c> on the player command line (or env <c>G3_NOGRD=1</c>) turns the GPU Resident Drawer off before the
    /// render pipeline is created, by setting the active URP asset's <c>gpuResidentDrawerMode</c> to Disabled in memory
    /// (never saved; the editor gets the asset's value back when Play ends). Everything then draws through the SRP Batcher
    /// (more draw calls, more CPU per frame; see the draw-count numbers in Docs/performance.md) but no BatchRendererGroup
    /// draw exists that could crash. <see cref="SetDisabled"/> switches it at runtime (the pipeline is recreated).
    /// By reflection: the mode property and its enum live in the GPU-driven assembly, which G3.Unity does not reference.
    /// </summary>
    public static class G3GrdFallback
    {
        const string Property = "gpuResidentDrawerMode";
        static bool _haveOriginal;
        static object _original;
        static RenderPipelineAsset _asset;

        /// <summary>True while this switch keeps the drawer off.</summary>
        public static bool Disabled { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            Disabled = false;
            _haveOriginal = false;
            _original = null;
            _asset = null;
            bool off;
            try { off = GrdFallback.Disable(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(GrdFallback.EnvVar)); }
            catch (Exception) { return; }
            if (!off) return;
            Application.quitting -= Restore;
            Application.quitting += Restore;
            SetDisabled(true);
        }

        /// <summary>Turns the drawer off (true) or back to the asset's own mode (false). Returns false when the asset has no such mode.</summary>
        public static bool SetDisabled(bool off)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return false;
            var p = rp.GetType().GetProperty(Property, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanRead || !p.CanWrite || !p.PropertyType.IsEnum)
            {
                Debug.LogWarning($"[G3GrdFallback] {rp.GetType().Name}.{Property} not found: GPU Resident Drawer left as it is");
                return false;
            }
            try
            {
                if (!_haveOriginal || _asset != rp)
                {
                    _original = p.GetValue(rp);
                    _asset = rp;
                    _haveOriginal = true;
                }
                var want = off ? Enum.ToObject(p.PropertyType, 0) : _original;   // 0 = GPUResidentDrawerMode.Disabled
                if (!Equals(p.GetValue(rp), want)) p.SetValue(rp, want);
                Disabled = off;
                Debug.Log($"[G3GrdFallback] GPU Resident Drawer {(off ? "OFF (" + GrdFallback.OffSwitch + " / " + GrdFallback.EnvVar + ")" : "back to " + _original)} on {rp.name}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[G3GrdFallback] switching the GPU Resident Drawer failed: {e.GetBaseException().Message}");
                return false;
            }
        }

        static void Restore()
        {
            if (Disabled && _haveOriginal) SetDisabled(false);
        }
    }
}
