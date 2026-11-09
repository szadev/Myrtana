using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Import settings for the extracted G3 audio (Docs/formats-audio.md §7): music and long ambience/wind beds stream,
    /// water loops stay compressed in memory (many instances), short SFX (footsteps, birds, fire) decompress on load.
    /// </summary>
    sealed class G3AudioPostprocessor : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            var path = assetPath.Replace('\\', '/');
            if (!path.StartsWith(G3AudioImporter.Root + "/")) return;
            var importer = (AudioImporter)assetImporter;
            var lower = path.ToLowerInvariant();
            var s = importer.defaultSampleSettings;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;

            if (lower.StartsWith(G3AudioImporter.MusicFolder.ToLowerInvariant() + "/") ||
                lower.Contains("/environment/ambience/") || lower.Contains("/environment/wind/") || lower.Contains("/environment/weather/"))
            {
                s.loadType = AudioClipLoadType.Streaming;
                s.quality = 0.7f;
                importer.loadInBackground = true;
            }
            else if (lower.Contains("/environment/water/") || lower.Contains("/environment/fire/"))
            {
                s.loadType = AudioClipLoadType.CompressedInMemory;
                s.quality = 0.6f;
            }
            else
            {
                s.loadType = AudioClipLoadType.DecompressOnLoad;
                s.quality = 0.6f;
            }
            importer.defaultSampleSettings = s;
            importer.forceToMono = false;
        }
    }
}
