using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// fix-168: whether URP's Spatial-Temporal Post-processing (STP) can run in this process. STP needs its compute shaders
    /// (core's internal <c>STP.RuntimeResources</c> in the URP global settings); a player build strips them unless a build
    /// render-pipeline asset selects STP (core's STPResourceStripper; the editor keeps them through
    /// <c>G3.Unity.Editor.StpBuildResources</c>). Without them <c>STP.Execute</c> throws a NullReferenceException in every
    /// frame and the screen freezes (Demo_2026-10-20a). <see cref="Usable"/> checks the resources once (reflection: the type
    /// is internal) and the device (<see cref="STP.IsSupported"/>), and a log watch marks STP unusable at its first render
    /// graph error; <see cref="Failed"/> then lets the users (remaster TAA, options upscaling) fall back.
    /// </summary>
    public static class G3StpGuard
    {
        static int _resources;      // 0 unknown, 1 present, -1 missing
        static bool _failed;
        static bool _watching;

        /// <summary>Raised once, before the next frame renders (Application.onBeforeRender), after STP failed while rendering.</summary>
        public static event Action Failed;

        /// <summary>True when STP may be selected: device support, its runtime resources present, no render failure yet.</summary>
        public static bool Usable
        {
            get
            {
                if (_failed) return false;
                if (_resources == 0) _resources = ResourcesPresent() ? 1 : -1;
                if (_resources < 0) { MarkFailed("STP runtime resources are missing from this build", false); return false; }
                if (!SafeSupported()) return false;
                Watch();
                return true;
            }
        }

        /// <summary>Why STP is off, or null (for logs and the runner).</summary>
        public static string Reason { get; private set; }

        static bool SafeSupported()
        {
            try { return STP.IsSupported(); }
            catch (Exception) { return false; }
        }

        static bool ResourcesPresent()
        {
            try
            {
                var type = typeof(STP).GetNestedType("RuntimeResources", BindingFlags.Public | BindingFlags.NonPublic);
                if (type == null) return true;   // URP changed its layout: do not block STP on a guess, the log watch still guards
                var tryGet = typeof(GraphicsSettings).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == nameof(GraphicsSettings.TryGetRenderPipelineSettings) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                if (tryGet == null) return true;
                var args = new object[] { null };
                if (!(bool)tryGet.MakeGenericMethod(type).Invoke(null, args) || args[0] == null) return false;
                foreach (var name in new[] { "setupCS", "preTaaCS", "taaCS" })
                {
                    var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.GetValue(args[0]) as ComputeShader == null) return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[G3StpGuard] STP resource check failed, assuming present: " + e.Message);
                return true;
            }
        }

        static void Watch()
        {
            if (_watching) return;
            _watching = true;
            Application.logMessageReceived += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (_failed || (type != LogType.Exception && type != LogType.Error)) return;
            if ((condition != null && condition.Contains("STP.Execute")) || (stackTrace != null && stackTrace.Contains("STP.Execute")))
                MarkFailed("STP failed while rendering: " + condition, true);
        }

        static void MarkFailed(string reason, bool raise)
        {
            if (_failed) return;
            _failed = true;
            Reason = reason;
            if (_watching) { Application.logMessageReceived -= OnLog; _watching = false; }
            Debug.LogWarning("[G3StpGuard] " + reason + " -> STP off (remaster: URP TAA; upscaling: FSR)");
            // the log callback runs inside the render pipeline's exception handler: switch the asset outside the frame
            if (raise) Application.onBeforeRender += RaiseFailed;
        }

        static void RaiseFailed()
        {
            Application.onBeforeRender -= RaiseFailed;
            try { Failed?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
