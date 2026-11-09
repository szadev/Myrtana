using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Myrtana.Session;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// fix-170: builds the port's third start-up logo "Gothic 3 Remaster" (<see cref="GameFlow.RemasterLogo"/>, in CP's CPT
    /// slot) into <c>StreamingAssets/Video/G3R_Remaster.webm</c> (git-ignored, game-derived) with
    /// <c>Tools/RemasterLogo/remaster_logo.py</c> (Python 3 + numpy + Pillow, ffmpeg on PATH) from the G3 install
    /// (splash.bmp, Gothic3.ttf) and the content import (<c>Assets/G3Imported</c>: the menu backdrop, the level-up sting,
    /// the fire crackle). The demo preflight rebuilds it when it is missing or older than the script or a source; when
    /// it cannot (no sources, no Python/ffmpeg) it warns and the flow skips the slot (missing video → skipped,
    /// <c>G3VideoScreen</c>). fix-170b: 2560x1440 at 30 fps (~9 min); first the logo and the backdrop are upscaled 4x
    /// with Real-ESRGAN by <c>Tools/RemasterLogo/upscale_sources.py</c> into <c>Library/RemasterLogo</c> (local tool: the
    /// Python of EditorPrefs <c>Myrtana.AiPython</c>, default ComfyUI's embedded one in C:/AI, with torch + spandrel, and the
    /// model <c>Myrtana.EsrganModel</c>); without it the renderer falls back to a Lanczos 4x (WARN). Docs/game-start.md 2.2.
    /// </summary>
    public static class RemasterLogoBuild
    {
        static string ProjectDir => Path.GetDirectoryName(Application.dataPath);
        public static string Script => Path.Combine(ProjectDir, "Tools", "RemasterLogo", "remaster_logo.py");
        public static string Output => Path.Combine(Application.streamingAssetsPath, "Video", GameFlow.RemasterLogo + ".webm");
        static string Imported => Path.Combine(ProjectDir, "Assets", "G3Imported");
        public static string UpscaleScript => Path.Combine(ProjectDir, "Tools", "RemasterLogo", "upscale_sources.py");
        public static string UpscaleCache => Path.Combine(ProjectDir, "Library", "RemasterLogo");
        /// <summary>logo-1: the logo's alpha matte (shared by the video and the launcher's splash).</summary>
        public static string MatteScript => Path.Combine(ProjectDir, "Tools", "RemasterLogo", "logo_matte.py");
        /// <summary>logo-1: the launcher's anti-aliased splash, built here and copied next to the player.</summary>
        public static string SplashHd => Path.Combine(UpscaleCache, StartupSplash.HdBitmapName);
        const string AiRoot = @"C:\AI\ComfyUI-portable\ComfyUI_windows_portable";
        static string AiPython => EditorPrefs.GetString("Myrtana.AiPython", Path.Combine(AiRoot, "python_embeded", "python.exe"));
        static string EsrganModel => EditorPrefs.GetString("Myrtana.EsrganModel",
            Path.Combine(AiRoot, "ComfyUI", "models", "upscale_models", "RealESRGAN_x4plus.pth"));

        static string[] Sources => new[]
        {
            Path.Combine(StartupSplash.GameRoot, "splash.bmp"),
            Path.Combine(StartupSplash.GameRoot, "Gothic3.ttf"),
            Path.Combine(Imported, "Gui", "Images", "hud_backgrounds", "G3_HUD_View_Menu_Secret_Back.png"),
            Path.Combine(Imported, "Audio", "Sound", "event", "sfx_event_levelup_01.ogg"),
            Path.Combine(Imported, "Audio", "Sound", "environment", "fire", "SFX_Env_Fire_01.ogg"),
        };

        [MenuItem("Myrtana/Build/Build Remaster Logo Video", priority = 110)]
        static void Menu() => Debug.Log(Ensure(force: true, out _));

        /// <summary>True when the video exists and is newer than the script and every source.</summary>
        public static bool UpToDate(out string why)
        {
            if (!File.Exists(Output)) { why = "missing"; return false; }
            DateTime built = File.GetLastWriteTimeUtc(Output);
            string newer = Sources.Append(Script).Append(UpscaleScript).Append(MatteScript).FirstOrDefault(f => File.Exists(f) && File.GetLastWriteTimeUtc(f) > built);
            why = newer == null ? "up to date" : "older than " + Path.GetFileName(newer);
            return newer == null;
        }

        /// <summary>
        /// Builds the video when it is missing or stale (or always with <paramref name="force"/>). <paramref name="ok"/>
        /// false = no usable video (the flow then skips the slot). Returns a one-line report for the preflight.
        /// </summary>
        public static string Ensure(bool force, out bool ok)
        {
            ok = false;
            if (!force && UpToDate(out _)) { ok = true; return $"remaster logo {Rel(Output)} (up to date)"; }
            string[] missing = Sources.Where(f => !File.Exists(f)).ToArray();
            if (!File.Exists(Script)) missing = missing.Append(Script).ToArray();
            if (missing.Length > 0)
                return Fallback("sources missing: " + string.Join(", ", missing));
            string python = FindPython();
            if (python == null) return Fallback("no Python 3 on PATH");
            if (FindOnPath("ffmpeg.exe") == null) return Fallback("no ffmpeg on PATH");
            var sw = Stopwatch.StartNew();
            string upscale = Upscale();
            string args = $"-I \"{Script}\" --g3 \"{StartupSplash.GameRoot}\" --imported \"{Imported}\" --upscaled \"{UpscaleCache}\" --out \"{Output}\"";
            if (Path.GetFileName(python).Equals("py.exe", StringComparison.OrdinalIgnoreCase)) args = "-3 " + args;
            string output = Run(python, args, out int code);
            if (code != 0 || !File.Exists(Output))
                return Fallback($"remaster_logo.py exit {code}: {output.Trim()}");
            AssetDatabase.ImportAsset("Assets/StreamingAssets/Video/" + GameFlow.RemasterLogo + ".webm");
            ok = true;
            return $"remaster logo built {Rel(Output)} ({new FileInfo(Output).Length / 1024} KB, {sw.Elapsed.TotalSeconds:0} s; {upscale})";
        }

        /// <summary>
        /// logo-1: <see cref="SplashHd"/> (Library/RemasterLogo/splash_hd.bmp: the G3 logo 4x with an anti-aliased alpha
        /// matte, 32-bit BGRA) from the install's splash.bmp and its Real-ESRGAN copy (made first when missing or stale),
        /// with <c>Tools/RemasterLogo/logo_matte.py</c>; rebuilt when older than the script or a source. <paramref name="ok"/>
        /// false = none (the launcher then shows splash.bmp with G3's colour key). Returns a one-line report.
        /// </summary>
        public static string EnsureSplashHd(out bool ok)
        {
            ok = false;
            string bmp = StartupSplash.SourceBitmap, x4 = Path.Combine(UpscaleCache, "splash_x4.png");
            if (!File.Exists(bmp) || !File.Exists(MatteScript)) return "no " + StartupSplash.HdBitmapName + " (no " + (File.Exists(bmp) ? MatteScript : bmp) + ")";
            string upscale = "";
            if (!File.Exists(x4) || File.GetLastWriteTimeUtc(x4) < File.GetLastWriteTimeUtc(bmp)) upscale = "; " + Upscale();
            if (File.Exists(SplashHd))
            {
                DateTime built = File.GetLastWriteTimeUtc(SplashHd);
                if (new[] { bmp, MatteScript, x4 }.All(f => !File.Exists(f) || File.GetLastWriteTimeUtc(f) <= built))
                {
                    ok = true;
                    return StartupSplash.HdBitmapName + " (up to date)";
                }
            }
            string python = FindPython();
            if (python == null) return "no " + StartupSplash.HdBitmapName + " (no Python 3 on PATH)" + upscale;
            string args = $"-I \"{MatteScript}\" --g3 \"{StartupSplash.GameRoot}\" --upscaled \"{UpscaleCache}\" --out \"{SplashHd}\"";
            if (Path.GetFileName(python).Equals("py.exe", StringComparison.OrdinalIgnoreCase)) args = "-3 " + args;
            string output = Run(python, args, out int code);
            ok = code == 0 && File.Exists(SplashHd);
            return ok ? StartupSplash.HdBitmapName + " built (" + output.Trim().Replace("\r", "").Replace('\n', ' ') + ")" + upscale
                      : $"no {StartupSplash.HdBitmapName} (logo_matte.py exit {code}: {output.Trim()})" + upscale;
        }

        static string FindPython() => FindOnPath("python.exe") ?? FindOnPath("python3.exe") ?? FindOnPath("py.exe");

        /// <summary>The Real-ESRGAN 4x copies of the image sources (skipped when up to date); a one-line note.</summary>
        static string Upscale()
        {
            if (!File.Exists(AiPython) || !File.Exists(EsrganModel) || !File.Exists(UpscaleScript))
                return "no local ESRGAN (" + AiPython + "): Lanczos 4x sources";
            string output = Run(AiPython, $"-I \"{UpscaleScript}\" --g3 \"{StartupSplash.GameRoot}\" --imported \"{Imported}\" " +
                                          $"--cache \"{UpscaleCache}\" --model \"{EsrganModel}\"", out int code);
            return code == 0 ? "ESRGAN sources ok" : $"ESRGAN failed (exit {code}: {output.Trim()}): Lanczos 4x sources";
        }

        static string Fallback(string why) => File.Exists(Output)
            ? $"remaster logo not rebuilt ({why}); the existing {Rel(Output)} ships"
            : $"no remaster logo ({why}): the third logo slot is skipped";

        static string Rel(string path) => path.Replace('\\', '/').Replace(ProjectDir.Replace('\\', '/') + "/", "");

        static string FindOnPath(string exe) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.Combine(d.Trim('"'), exe))
            .FirstOrDefault(f => File.Exists(f) && !f.Contains("WindowsApps"));   // skip the Store's python.exe stub

        static string Run(string file, string args, out int code)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = ProjectDir,
            };
            using var p = Process.Start(psi);
            var err = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(1800000)) { try { p.Kill(); } catch { } }
            code = p.HasExited ? p.ExitCode : -1;
            return o + err.Result;
        }
    }
}
