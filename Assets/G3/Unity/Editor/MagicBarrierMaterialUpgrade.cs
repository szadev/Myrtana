using System;
using G3.Formats.World;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// fix-229: the imported material of the Vengard dome (<c>Assets/G3Imported/Materials/G3_Special_Magic_Barrier_01.mat</c>,
    /// used by the cell mesh and the low-poly copy in the master scene) was made with URP Simple Lit by older imports and
    /// shows nothing. Imported materials are only created when missing, so this rebuilds that one asset in place with the
    /// current factory (<see cref="G3AssetCache.RefreshMaterials"/>: GUID kept, no world reimport) once per editor load
    /// and before every player build, whenever it does not use <see cref="MagicBarrier.ShaderName"/> yet.
    /// CLI: <c>G3.Unity.Editor.MagicBarrierMaterialUpgrade.Run()</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class MagicBarrierMaterialUpgrade
    {
        public const string MaterialPath = G3AssetCache.Root + "/Materials/" + MagicBarrier.MaterialName + ".mat";

        static MagicBarrierMaterialUpgrade() => EditorApplication.delayCall += OnLoad;

        static void OnLoad()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (!EditorApplication.isPlaying) EditorApplication.delayCall += OnLoad;
                return;
            }
            try { Run(); }
            catch (Exception e) { Debug.LogWarning("G3 magic barrier material: " + e.Message); }
        }

        /// <summary>True when the dome's material asset exists and still uses another shader.</summary>
        public static bool NeedsUpgrade()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            return m != null && (m.shader == null || m.shader.name != MagicBarrier.ShaderName) &&
                   Shader.Find(MagicBarrier.ShaderName) != null;
        }

        /// <summary>Rebuilds the dome's material asset if needed; returns what was done.</summary>
        public static string Run()
        {
            if (!NeedsUpgrade()) return "magic barrier material up to date";
            string r = G3AssetCache.RefreshMaterialAssets(MagicBarrier.MaterialName);
            var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            string msg = $"[fix-229] magic barrier material -> {(m != null && m.shader != null ? m.shader.name : "?")} ({r})";
            Debug.Log(msg);
            return msg;
        }

        sealed class BuildCheck : IPreprocessBuildWithReport
        {
            public int callbackOrder => 0;
            public void OnPreprocessBuild(BuildReport report) => Run();
        }
    }
}
