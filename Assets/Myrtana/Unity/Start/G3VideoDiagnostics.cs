using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace Myrtana.Unity
{
    /// <summary>
    /// Finds out why videos do not play on a machine (editor-runner-9: every VideoPlayer stayed on frame 0, isPaused,
    /// for VP8 .webm and H.264 .mp4 alike). Logs the environment (OS, Media Foundation DLLs — Windows "N" editions lack
    /// them without the Media Feature Pack —, audio output, run-in-background, focus) and then plays one converted video
    /// for <see cref="SecondsPerTry"/> s in every combination of audio output (AudioSource, Direct, None) and clock
    /// (unscaled game time, DSP time), logging whether frames advance. Every line starts with <c>[video-diag]</c>; the
    /// last one is a summary.
    /// Run: in the editor during Play, <c>Myrtana.Unity.G3VideoDiagnostics.Run()</c> (or <c>Run("G3_Logo_01")</c>);
    /// in a development player build, start it with <c>-videoDiag</c> (or <c>-videoDiag=&lt;name&gt;</c>) and read Player.log.
    /// </summary>
    public sealed class G3VideoDiagnostics : MonoBehaviour
    {
        public const string CommandLineSwitch = "-videoDiag";
        const float SecondsPerTry = 3f;
        const string Tag = "[video-diag] ";

        string _video;

        /// <summary>A development build started with <see cref="CommandLineSwitch"/> runs the diagnostic after the first scene.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void FromCommandLine()
        {
            if (Application.isEditor || !Debug.isDebugBuild) return;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith(CommandLineSwitch, StringComparison.OrdinalIgnoreCase)) continue;
                int eq = arg.IndexOf('=');
                Run(eq > 0 ? arg.Substring(eq + 1) : null);
                return;
            }
        }

        /// <summary>Starts the diagnostic for <paramref name="video"/> (a name in StreamingAssets/Video), or the first video found.</summary>
        public static G3VideoDiagnostics Run(string video = null)
        {
            var go = new GameObject("G3 video diagnostics");
            DontDestroyOnLoad(go);
            var d = go.AddComponent<G3VideoDiagnostics>();
            d._video = video;
            return d;
        }

        IEnumerator Start()
        {
            LogEnvironment();
            string path = string.IsNullOrEmpty(_video) ? FirstVideo() : G3VideoScreen.PathOf(_video);
            if (path == null)
            {
                Debug.LogWarning(Tag + $"no video {(_video ?? "*")} in {Path.Combine(Application.streamingAssetsPath, G3VideoScreen.Folder)}");
                Destroy(gameObject);
                yield break;
            }
            Debug.Log(Tag + "file " + path + $" ({new FileInfo(path).Length / 1024} KB)");
            var audio = gameObject.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.ignoreListenerPause = true;
            var texture = new RenderTexture(320, 180, 0) { name = "video diag" };
            texture.Create();
            int working = 0, tries = 0;
            string first = null;
            foreach (var output in new[] { VideoAudioOutputMode.AudioSource, VideoAudioOutputMode.Direct, VideoAudioOutputMode.None })
                foreach (var clock in new[] { VideoTimeUpdateMode.UnscaledGameTime, VideoTimeUpdateMode.DSPTime })
                {
                    tries++;
                    var result = new Result();
                    yield return Try(path, output, clock, audio, texture, result);
                    Debug.Log(Tag + $"audio {output}, clock {clock}: {result}");
                    if (result.Advanced)
                    {
                        working++;
                        first ??= $"{output}/{clock}";
                    }
                }
            texture.Release();
            Debug.Log(Tag + $"SUMMARY {working}/{tries} combinations advance" +
                      (first != null ? $"; first working: {first}" : "; none: the platform's video decoding/clock is broken here " +
                       "(check the Media Feature Pack on Windows N, the audio device, a dev player build vs the editor)"));
            Destroy(gameObject);
        }

        sealed class Result
        {
            public bool Prepared, Advanced;
            public long LastFrame = -1;
            public double LastTime;
            public bool Paused, Playing;
            public string Error;
            public float PrepareSeconds;

            public override string ToString() =>
                Error != null ? "error " + Error :
                !Prepared ? "not prepared" :
                $"prepared in {PrepareSeconds:0.00} s, frame {LastFrame}, time {LastTime:0.00}, isPlaying {Playing}, isPaused {Paused} -> " +
                (Advanced ? "ADVANCES" : "STUCK");
        }

        static IEnumerator Try(string path, VideoAudioOutputMode output, VideoTimeUpdateMode clock, AudioSource audio,
                               RenderTexture texture, Result result)
        {
            var holder = new GameObject("video diag player");
            var player = holder.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.isLooping = false;
            player.waitForFirstFrame = true;
            player.skipOnDrop = true;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.targetTexture = texture;
            player.timeUpdateMode = clock;
            player.audioOutputMode = output;
            if (output == VideoAudioOutputMode.AudioSource)
            {
                player.controlledAudioTrackCount = 1;
                player.EnableAudioTrack(0, true);
                player.SetTargetAudioSource(0, audio);
            }
            player.errorReceived += (_, message) => result.Error = message;
            player.url = path;
            float start = Time.realtimeSinceStartup;
            player.Prepare();
            while (!player.isPrepared && result.Error == null && Time.realtimeSinceStartup - start < 8f) yield return null;
            result.Prepared = player.isPrepared;
            result.PrepareSeconds = Time.realtimeSinceStartup - start;
            if (result.Prepared && result.Error == null)
            {
                player.Play();
                float until = Time.realtimeSinceStartup + SecondsPerTry;
                while (Time.realtimeSinceStartup < until && result.Error == null) yield return null;
                result.LastFrame = player.frame;
                result.LastTime = player.time;
                result.Playing = player.isPlaying;
                result.Paused = player.isPaused;
                result.Advanced = player.frame > 1 || player.time > 0.2;
            }
            player.Stop();
            Destroy(holder);
            yield return null;
        }

        static void LogEnvironment()
        {
            var audio = AudioSettings.GetConfiguration();
            Debug.Log(Tag + $"Unity {Application.unityVersion}, {(Application.isEditor ? "editor" : Debug.isDebugBuild ? "development build" : "build")}, " +
                      $"OS {SystemInfo.operatingSystem}, GPU {SystemInfo.graphicsDeviceType}");
            Debug.Log(Tag + $"runInBackground {Application.runInBackground}, focused {Application.isFocused}, timeScale {Time.timeScale}, " +
                      $"captureFramerate {Time.captureFramerate}, listener pause {AudioListener.pause}, listener volume {AudioListener.volume:0.00}");
            Debug.Log(Tag + $"audio: {audio.speakerMode}, {audio.sampleRate} Hz, buffer {audio.dspBufferSize}, driver caps {AudioSettings.driverCapabilities}, " +
                      $"dspTime {AudioSettings.dspTime:0.00}");
            if (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer)
            {
                string sys = Environment.SystemDirectory;
                // Windows N/KN editions ship without Media Foundation: H.264 .mp4 cannot be decoded until the Media Feature
                // Pack is installed (VP8 .webm is decoded by Unity itself).
                Debug.Log(Tag + $"Media Foundation: mfplat.dll {File.Exists(Path.Combine(sys, "mfplat.dll"))}, " +
                          $"mfreadwrite.dll {File.Exists(Path.Combine(sys, "mfreadwrite.dll"))}, msmpeg2vdec.dll {File.Exists(Path.Combine(sys, "msmpeg2vdec.dll"))}");
            }
        }

        static string FirstVideo()
        {
            string dir = Path.Combine(Application.streamingAssetsPath, G3VideoScreen.Folder);
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".webm" || ext == ".mp4" || ext == ".mov") return f;
            }
            return null;
        }
    }
}
