using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// G3's start-up splash (fix-137, fix-152; Docs/game-start.md "Start-up splash"). Gothic3 (Engine.dll
    /// eCApplication::CreateSplashScreen → eCSplash) shows <c>&lt;game&gt;/splash.bmp</c> (300×300, 24-bit) in a borderless
    /// topmost layered window centred on the primary screen with the colour key 0x808080: only the Gothic 3 logo floats on
    /// the desktop, unscaled, until the game window is up. The port does the same outside Unity: the launcher
    /// (<c>G3World.exe</c>, Tools/Launcher, <see cref="LauncherBuild"/>) reads <c>splash.bmp</c> next to itself, which
    /// <see cref="CopyBitmap"/> takes from the game install at packaging time (game-derived: only in the build folder,
    /// never in git).
    /// <list type="bullet">
    /// <item>The Unity splash is off (<see cref="Apply"/>, before every player build): no Unity logo, no logos, black.
    /// The settings are saved at once, so ProjectSettings.asset never stays modified by a build (fix-137 left a logo
    /// entry there).</item>
    /// <item>fix-137's generated files (the in-window logo sprite and cover picture) are removed by the demo preflight,
    /// so they never ship again.</item>
    /// </list>
    /// </summary>
    public sealed class StartupSplash : IPreprocessBuildWithReport
    {
        public const string BitmapName = "splash.bmp";
        /// <summary>
        /// logo-1: the anti-aliased splash the launcher prefers (<see cref="RemasterLogoBuild.EnsureSplashHd"/>): the logo 4x
        /// with a soft alpha matte instead of the colour key's grey-fringed stair-step edge; game-derived, build folder only.
        /// </summary>
        public const string HdBitmapName = "splash_hd.bmp";
        /// <summary>fix-137's generated, git-ignored files (Unity splash sprite, runtime cover); obsolete.</summary>
        static readonly string[] Obsolete = { "Assets/G3Imported/Splash/G3Splash.png", "Assets/StreamingAssets/Video/G3_Splash.png" };

        public int callbackOrder => -100;

        static string DataPath => G3.Formats.G3Install.Pref(EditorPrefs.GetString("Myrtana.G3DataPath", "")) ?? G3.Formats.G3Install.DataDirOrHint;
        /// <summary>The Gothic 3 install root (parent of the importer's Data path; Gothic3.exe, splash.bmp).</summary>
        public static string GameRoot => Path.GetDirectoryName(DataPath.TrimEnd('\\', '/'));
        /// <summary>The game install's splash bitmap (Gothic 3 root, next to Gothic3.exe).</summary>
        public static string SourceBitmap => Path.Combine(GameRoot, BitmapName);

        /// <summary>A one-line check of the game's splash bitmap (preflight).</summary>
        public static bool Check(out string report)
        {
            string bmp = SourceBitmap;
            if (!File.Exists(bmp)) { report = "no " + bmp + ": the launcher starts the game without G3's splash logo"; return false; }
            try
            {
                Describe(File.ReadAllBytes(bmp), out int w, out int h, out int bpp);
                report = $"G3 splash {bmp} ({w}x{h}, {bpp} bit; copied next to the launcher)";
                return true;
            }
            catch (Exception e) { report = bmp + " not usable (" + e.Message + "): no splash logo"; return false; }
        }

        /// <summary>
        /// Copies the game's <c>splash.bmp</c> and (logo-1) the anti-aliased <c>splash_hd.bmp</c> (built when missing or
        /// stale) next to the player in <paramref name="outputDir"/>; without the latter the launcher keys splash.bmp.
        /// </summary>
        public static string CopyBitmap(string outputDir)
        {
            if (!Check(out string report)) return report;
            File.Copy(SourceBitmap, Path.Combine(outputDir, BitmapName), true);
            string hd = Path.Combine(outputDir, HdBitmapName);
            string hdReport = RemasterLogoBuild.EnsureSplashHd(out bool hdOk);
            if (hdOk) File.Copy(RemasterLogoBuild.SplashHd, hd, true);
            else if (File.Exists(hd)) File.Delete(hd);
            return BitmapName + " from " + SourceBitmap + "; " + hdReport;
        }

        /// <summary>The Unity splash off (Unity 6: allowed on every licence), black, no logos; saved when changed.</summary>
        public static string Apply()
        {
            bool changed = PlayerSettings.SplashScreen.show || PlayerSettings.SplashScreen.showUnityLogo ||
                           (PlayerSettings.SplashScreen.logos?.Length ?? 0) > 0 ||
                           PlayerSettings.SplashScreen.backgroundColor != Color.black || PlayerSettings.SplashScreen.background != null;
            if (changed)
            {
                PlayerSettings.SplashScreen.show = false;
                PlayerSettings.SplashScreen.showUnityLogo = false;
                PlayerSettings.SplashScreen.logos = Array.Empty<PlayerSettings.SplashScreenLogo>();
                PlayerSettings.SplashScreen.backgroundColor = Color.black;
                PlayerSettings.SplashScreen.background = null;
                AssetDatabase.SaveAssets(); // writes ProjectSettings.asset now, not whenever the editor next saves
            }
            return "Unity splash off (no Unity logo, no logos, black)" + (changed ? ": settings fixed and saved" : "");
        }

        /// <summary>Deletes fix-137's generated files (demo preflight; not inside a build: no asset changes there).</summary>
        public static int RemoveObsolete()
        {
            int n = 0;
            foreach (string path in Obsolete)
                if (File.Exists(path) && AssetDatabase.DeleteAsset(path)) n++;
            return n;
        }

        public void OnPreprocessBuild(BuildReport report) => Debug.Log("[Myrtana] " + Apply());

        /// <summary>Size and depth of an uncompressed 24/32-bit BMP (what eCSplash and the launcher load).</summary>
        public static void Describe(byte[] bmp, out int width, out int height, out int bpp)
        {
            if (bmp.Length < 54 || bmp[0] != 'B' || bmp[1] != 'M') throw new InvalidDataException("not a BMP");
            width = BitConverter.ToInt32(bmp, 18);
            height = Math.Abs(BitConverter.ToInt32(bmp, 22));
            bpp = BitConverter.ToInt16(bmp, 28);
            int compression = BitConverter.ToInt32(bmp, 30);
            if ((bpp != 24 && bpp != 32) || (compression != 0 && compression != 3)) throw new InvalidDataException($"BMP {bpp} bpp, compression {compression}");
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new InvalidDataException($"BMP {width}x{height}");
        }
    }
}
