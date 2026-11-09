using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace G3.Unity.Editor.Remaster
{
    /// <summary>
    /// fix-168: keeps URP's STP compute shaders (core's <c>STP.RuntimeResources</c>) in player builds. The game switches
    /// STP on at runtime (world remaster AA <c>v2Taa</c> 2, the options' STP upscaling), but the project's URP assets ship
    /// with the Automatic filter, and core's STPResourceStripper drops the resources unless a build render-pipeline asset
    /// selects STP: <c>STP.Execute</c> then threw a NullReferenceException in every frame (Demo_2026-10-20a froze).
    /// The stripper asks <see cref="CoreBuildData.renderPipelineAssets"/> (read when the URP global settings are serialized
    /// for the player, after the pre-process callbacks); this adds a transient URP asset with the STP filter to that list,
    /// after URP's own build data has copied it (order int.MinValue + 100), so shader stripping and the shipped assets do
    /// not change. Runtime fallback if this ever fails: <see cref="G3StpGuard"/>.
    /// </summary>
    sealed class StpBuildResources : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        static UniversalRenderPipelineAsset _marker;

        public int callbackOrder => int.MinValue + 150;

        public void OnPreprocessBuild(BuildReport report)
        {
            var data = CoreBuildData.instance;
            if (!data.buildingPlayerForRenderPipeline || data.currentRenderPipelineAssetType != typeof(UniversalRenderPipelineAsset)) return;
            foreach (var asset in data.renderPipelineAssets)
                if (asset is UniversalRenderPipelineAsset urp && urp.isStpUsed) return;   // an asset selects STP already
            if (_marker == null)
            {
                _marker = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                _marker.name = "G3 STP build marker (fix-168)";
                _marker.hideFlags = HideFlags.HideAndDontSave;
                _marker.upscalingFilter = UpscalingFilterSelection.STP;
            }
            data.renderPipelineAssets.Add(_marker);
            Debug.Log("[StpBuildResources] STP runtime resources kept for the player (transient STP asset in CoreBuildData)");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (_marker == null) return;
            Object.DestroyImmediate(_marker);
            _marker = null;
        }
    }
}
