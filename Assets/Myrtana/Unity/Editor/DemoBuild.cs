using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using G3.Unity;
using G3.Unity.Editor;
using G3.Unity.Editor.Characters;
using G3.Unity.Editor.Remaster;
using Myrtana.Session;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// The private Windows demo build (Docs/build.md): a release (non-development) x64 player of the master scene plus
    /// every streamed cell scene of the world manifest, into <c>Builds/Demo_&lt;yyyy-MM-dd&gt;/bin/</c> (project
    /// <c>Builds/</c>, git-ignored), then the package files (launcher, README_RU/EN, Screenshots, BUILD_INFO in the root;
    /// splash.bmp, Scenarios in bin/). The player is <c>bin/G3WorldPlayer.exe</c> + <c>G3WorldPlayer_Data</c>; <c>G3World.exe</c>,
    /// the exe the owner starts and the only one in the root, is the native launcher with G3's desktop splash (fix-152,
    /// <see cref="LauncherBuild"/>). fix-167: the root holds only the launcher and the docs (<see cref="Myrtana.Core.BuildFolderLayout"/>).
    /// Menu Myrtana/Build, <c>-executeMethod Myrtana.Unity.Editor.DemoBuild.BuildFromCommandLine</c>, or
    /// <c>Tools/eval_job.sh 'return Myrtana.Unity.Editor.DemoBuild.Build();'</c>.
    /// <list type="bullet">
    /// <item>Scripting backend as the project (Mono; every earlier player build was Mono), define <see cref="Define"/>.</item>
    /// <item>The dev console (F10 / `), scenario runner and its ops are compiled in (<c>G3_DEMO</c> joins the
    /// <c>UNITY_EDITOR || DEVELOPMENT_BUILD</c> guards; owner request 2026-10-16); <c>Tools/Scenarios</c> is copied next
    /// to the player exe as <c>bin/Scenarios/</c> (spots, scenario files). The player's fly/time/help keys still follow
    /// <c>Debug.isDebugBuild</c> (off); <c>-quickstart</c> is ignored.</item>
    /// <item>StreamingAssets (content packs, videos) ship by Unity; <c>GameData/Voice</c> is copied next to the player exe (bin/) by
    /// <see cref="GameDataBuildStep"/> (forced copy, never a junction).</item>
    /// <item>World remaster pair list (r3 + v2 materials, textures, tree catalog) ships through
    /// <c>Resources/G3WorldRemasterSet</c>; the grass v2 shader through <c>Resources/G3GrassV2ShaderRef</c>.</item>
    /// <item>Character remaster: the import-time switch stays OFF (the preflight turns it off and rebuilds the prefabs when
    /// it was on); the runtime set <c>Resources/G3CharacterRemasterSet</c> (<see cref="CharacterRemasterBuilder"/>, rebuilt
    /// by the preflight) ships the hero v2 parts for Options → Remaster → Characters (default off).</item>
    /// </list>
    /// </summary>
    public static class DemoBuild
    {
        public const string Define = "G3_DEMO";

        /// <summary>A/B pictures (Docs/screens) copied into the package's Screenshots folder.</summary>
        public static readonly string[] Screens =
        {
            "ingame_v2r5_house_15.png", "ingame_v2r5_keeppath.png", "ingame_v2r5_tower.png", "ingame_v2r5_willow.png",
            "ingame_v2r5_cliff_close.png", "ingame_v2r5_cliff.png", "ingame_v2r3_courtyard.png",
            "ingame_v2r5_desert_dunes.png", "ingame_v2r5_desert_sand.png", "ingame_v2r5_desert_haze.png", "ingame_v2r5_desert_shafts.png",
            "ingame_v2r5_desert_storm.png", "ingame_v2r5_bakaresh.png", "ingame_v2r5_ishtar.png", "ingame_v2r5_morasul_tents.png",
            "ingame_v2r5_nordmar_hammer.png",
        };

        static string ProjectDir => Path.GetDirectoryName(Application.dataPath);

        /// <summary>Builds/Demo_&lt;date&gt; (the project's Builds folder).</summary>
        public static string DefaultOutput => Path.Combine(ProjectDir, "Builds", "Demo_" + DateTime.Now.ToString("yyyy-MM-dd"));

        [MenuItem("Myrtana/Build/Windows Demo (release, full world)", priority = 100)]
        static void MenuBuild()
        {
            string result = Build();   // no dialog: the runner drives this menu through UnityMCP
            if (result.StartsWith("Succeeded", StringComparison.Ordinal)) Debug.Log(result); else Debug.LogError(result);
        }

        [MenuItem("Myrtana/Build/Demo Preflight (check only)", priority = 101)]
        static void MenuPreflight() => Debug.Log(Preflight(fix: false, out _));

        [MenuItem("Myrtana/Build/Demo Package Files Only (README, screenshots)", priority = 102)]
        static void MenuPackage() => Debug.Log(Package(DefaultOutput, null));

        /// <summary><c>Unity.exe -batchmode -projectPath . -executeMethod Myrtana.Unity.Editor.DemoBuild.BuildFromCommandLine -quit</c>;
        /// optional <c>-demoOut &lt;dir&gt;</c>, <c>-demoRect ix0,iz0,ix1,iz1</c>, <c>-demoVariant dx12|dx12mt|il2cpp|dx12+il2cpp|dx12mt+il2cpp</c>
        /// (mt-1, <see cref="Myrtana.Core.BuildVariant"/>). Exit code 0 on success.</summary>
        public static void BuildFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string result = Build(Arg("-demoOut"), Arg("-demoRect"), variant: Arg(Myrtana.Core.BuildVariant.Argument));
            Debug.Log(result);
            if (Application.isBatchMode) EditorApplication.Exit(result.StartsWith("Succeeded", StringComparison.Ordinal) ? 0 : 1);
        }

        /// <summary>
        /// Preflight (character remaster off, content present), the player build and the package files. <paramref name="rect"/>
        /// "ix0,iz0,ix1,iz1" limits the cells (null = the whole world; teleporter stones outside it land in an empty world).
        /// <paramref name="variant"/> (mt-1): null = the environment variable <c>G3_DEMO_VARIANT</c>, else "dx12" (D3D12 first +
        /// graphics jobs Split), "il2cpp" or "dx12+il2cpp" (<see cref="Myrtana.Core.BuildVariant"/>); the player settings are
        /// changed for this build only and restored afterwards; without <paramref name="outputDir"/> the folder gets the
        /// variant suffix (<c>Builds/Demo_&lt;date&gt;_dx12</c>). Returns "Succeeded …" or the reason it stopped.
        /// </summary>
        public static string Build(string outputDir = null, string rect = null, bool fixCharacters = true, string variant = null)
        {
            var log = new StringBuilder();
            if (variant == null) variant = Environment.GetEnvironmentVariable(Myrtana.Core.BuildVariant.EnvVar);
            if (!Myrtana.Core.BuildVariant.TryParse(variant, out var flags, out string variantError)) return "Stopped: " + variantError;
            string pre = Preflight(fixCharacters, out bool ok);
            log.AppendLine(pre);
            if (!ok) return "Stopped: preflight failed\n" + log;

            var scenes = Scenes(rect, out int missing);
            if (scenes.Count < 2) return "Stopped: no cell scenes (run the world import)\n" + log;
            outputDir = string.IsNullOrEmpty(outputDir) ? DefaultOutput + Myrtana.Core.BuildVariant.Suffix(flags) : outputDir;
            Directory.CreateDirectory(outputDir);
            // fix-152/fix-167: a rebuild into a folder of an older layout (player G3World.exe + G3World_Data, or the player
            // next to the launcher) would ship the stale player and its 8 GB next to the new bin/
            var stale = Myrtana.Core.BuildFolderLayout.DeleteStalePlayer(outputDir);
            if (stale.Count > 0) log.AppendLine("removed the old-layout player files from the root: " + string.Join(", ", stale));
            var opts = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = Path.Combine(outputDir, LauncherBuild.BinDir, LauncherBuild.PlayerExe).Replace('\\', '/'),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,                 // release: no Development, no script debugging, no profiler
                extraScriptingDefines = new[] { Define },
            };
            var sw = Stopwatch.StartNew();
            BuildReport report;
            GameDataBuildStep.ForceCopy = true;
            var restore = ApplyVariant(flags, out string variantLine);
            log.AppendLine(variantLine);
            try { report = BuildPipeline.BuildPlayer(opts); }
            finally { GameDataBuildStep.ForceCopy = false; restore(); }
            var s = report.summary;
            string head = $"{s.result} {scenes.Count} scenes ({missing} manifest cells without a scene file), player {s.totalSize / 1048576} MB, " +
                          $"build {s.totalTime:hh\\:mm\\:ss} (wall {sw.Elapsed:hh\\:mm\\:ss}), errors {s.totalErrors}, warnings {s.totalWarnings} -> {opts.locationPathName}";
            if (s.result != BuildResult.Succeeded)
            {
                foreach (var step in report.steps)
                    foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception).Take(20))
                        log.AppendLine("  " + m.content);
                return head + "\n" + log;
            }
            if ((flags & Myrtana.Core.BuildVariantFlags.Il2Cpp) != 0) log.AppendLine(KeepDoNotShip(outputDir));
            log.AppendLine(Package(outputDir, $"{head}\n{variantLine}, define {Define}"));
            return head + "\n" + log;
        }

        /// <summary>
        /// mt-1: sets the player settings of <paramref name="flags"/> for one build and returns the action that restores
        /// the previous values (and saves them, so ProjectSettings.asset ends as it was). <paramref name="line"/> = what the
        /// build uses (BUILD_INFO). Variant A turns graphics jobs on although the preflight turned them off (crash-1): an
        /// A/B for the owner; the default build stays without them.
        /// </summary>
        static Action ApplyVariant(Myrtana.Core.BuildVariantFlags flags, out string line)
        {
            const BuildTarget target = BuildTarget.StandaloneWindows64;
            var named = NamedBuildTarget.Standalone;
            bool jobs = PlayerSettings.graphicsJobs;
            var jobMode = PlayerSettings.graphicsJobMode;
            bool defaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            var apis = PlayerSettings.GetGraphicsAPIs(target);
            var backend = PlayerSettings.GetScriptingBackend(named);
            var cpp = PlayerSettings.GetIl2CppCompilerConfiguration(named);
            var strip = PlayerSettings.GetManagedStrippingLevel(named);
            // the restore below sets the old values again, but Unity then writes them as explicit per-target entries
            // (graphics job mode, IL2CPP configuration, stripping): the file itself is put back byte for byte afterwards
            string settingsFile = Path.Combine(ProjectDir, "ProjectSettings", "ProjectSettings.asset");
            byte[] settingsBytes = flags != Myrtana.Core.BuildVariantFlags.None && File.Exists(settingsFile) ? File.ReadAllBytes(settingsFile) : null;
            bool dx12Jobs = (flags & Myrtana.Core.BuildVariantFlags.Dx12GraphicsJobs) != 0;
            bool dx12Mt = (flags & Myrtana.Core.BuildVariantFlags.Dx12MultiThreaded) != 0;   // dx12-3
            if (dx12Jobs || dx12Mt)
            {
                var list = new List<UnityEngine.Rendering.GraphicsDeviceType> { UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 };
                list.AddRange(apis.Where(a => a != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12));
                if (!list.Contains(UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)) list.Add(UnityEngine.Rendering.GraphicsDeviceType.Direct3D11);
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                PlayerSettings.SetGraphicsAPIs(target, list.ToArray());
                PlayerSettings.graphicsJobs = dx12Jobs;   // dx12mt: off = MultiThreaded render thread
                if (dx12Jobs) PlayerSettings.graphicsJobMode = GraphicsJobMode.Split;
            }
            if ((flags & Myrtana.Core.BuildVariantFlags.Il2Cpp) != 0)
            {
                PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetIl2CppCompilerConfiguration(named, Il2CppCompilerConfiguration.Release);
                PlayerSettings.SetManagedStrippingLevel(named, ManagedStrippingLevel.Minimal);
            }
            var nowBackend = PlayerSettings.GetScriptingBackend(named);
            line = $"{Myrtana.Core.BuildVariant.Describe(flags)}; graphics {string.Join(",", PlayerSettings.GetGraphicsAPIs(target))}, " +
                   $"graphics jobs {(PlayerSettings.graphicsJobs ? PlayerSettings.graphicsJobMode.ToString() : "off")}, backend {nowBackend}" +
                   (nowBackend == ScriptingImplementation.IL2CPP
                       ? $" ({PlayerSettings.GetIl2CppCompilerConfiguration(named)}, stripping {PlayerSettings.GetManagedStrippingLevel(named)})" : "");
            if (flags == Myrtana.Core.BuildVariantFlags.None) return () => { };
            return () =>
            {
                PlayerSettings.graphicsJobs = jobs;
                PlayerSettings.graphicsJobMode = jobMode;
                PlayerSettings.SetGraphicsAPIs(target, apis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, defaultApis);
                PlayerSettings.SetScriptingBackend(named, backend);
                PlayerSettings.SetIl2CppCompilerConfiguration(named, cpp);
                PlayerSettings.SetManagedStrippingLevel(named, strip);
                AssetDatabase.SaveAssets();
                if (settingsBytes != null) File.WriteAllBytes(settingsFile, settingsBytes);
            };
        }

        /// <summary>
        /// mt-1: an IL2CPP build leaves its symbols in bin/<c>G3WorldPlayer_BackUpThisFolder_ButDontShipItWithYourGame</c>
        /// (and Burst's in <c>*_DoNotShip</c>); <see cref="Package"/> deletes those from bin/. This moves them next to the
        /// build folder first (<c>&lt;build&gt;_symbols</c>, not shipped) so a native crash of the A/B player can be symbolised.
        /// </summary>
        static string KeepDoNotShip(string outputDir)
        {
            string bin = Path.Combine(outputDir, LauncherBuild.BinDir);
            if (!Directory.Exists(bin)) return "symbols: no bin/";
            string keep = outputDir.TrimEnd('/', '\\') + "_symbols";
            var moved = new List<string>();
            foreach (var dir in Directory.GetDirectories(bin))
            {
                string n = Path.GetFileName(dir);
                if (!n.EndsWith("_DoNotShip", StringComparison.Ordinal) && !n.EndsWith("_ButDontShipItWithYourGame", StringComparison.Ordinal)) continue;
                Directory.CreateDirectory(keep);
                string to = Path.Combine(keep, n);
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(dir, to);
                moved.Add(n);
            }
            return moved.Count == 0 ? "symbols: none in bin/" : "symbols moved to " + keep + ": " + string.Join(", ", moved);
        }

        /// <summary>Master scene + the manifest's cell scenes (in <paramref name="rect"/> when given) that exist on disk.</summary>
        public static List<string> Scenes(string rect, out int missing)
        {
            missing = 0;
            var scenes = new List<string> { G3WorldBatch.MasterPath };
            var manifest = AssetDatabase.LoadAssetAtPath<G3WorldManifest>(G3WorldBatch.ManifestPath);
            if (manifest == null) return scenes;
            int[] r = string.IsNullOrWhiteSpace(rect) ? null : rect.Split(',').Select(int.Parse).ToArray();
            foreach (var c in manifest.cells)
            {
                if (!c.HasScene) continue;
                if (r != null && (c.ix < r[0] || c.ix > r[2] || c.iz < r[1] || c.iz > r[3])) continue;
                if (File.Exists(c.scenePath)) scenes.Add(c.scenePath);
                else missing++;
            }
            return scenes;
        }

        /// <summary>
        /// Checks what the demo needs; with <paramref name="fix"/> turns the character remaster off and rebuilds the
        /// characters when it was on. <paramref name="ok"/> false = do not build.
        /// </summary>
        public static string Preflight(bool fix, out bool ok)
        {
            bool good = true;
            var sb = new StringBuilder("demo preflight:\n");
            void Fail(string m) { good = false; sb.AppendLine("  FAIL " + m); }
            void Ok(string m) => sb.AppendLine("  ok   " + m);
            void Warn(string m) => sb.AppendLine("  WARN " + m);

            if (EditorApplication.isPlayingOrWillChangePlaymode) Fail("the editor is in play mode");
            if (EditorApplication.isCompiling) Fail("scripts are compiling");
            // fix-187: runtime render-pipeline values (shadow distance, cascades, upscaler, SSAO boost) left in memory by a Play
            // session must not reach the build (Demo_2026-10-22a shipped m_ShadowDistance 90); checked first, before any save
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                string rp = RenderPipelineAssetGuard.CheckAgainstDisk(fix, out bool rpOk);
                if (!rpOk) Fail("render-pipeline assets differ from disk (runtime values in memory; the build restores them):\n    " + rp.TrimEnd().Replace("\n", "\n    "));
                else if (rp.Length > 0) Warn("render-pipeline assets had runtime values in memory, restored from disk:\n    " + rp.TrimEnd().Replace("\n", "\n    "));
                else Ok("render-pipeline assets equal the files on disk");
            }

            if (!File.Exists(G3WorldBatch.MasterPath)) Fail("no " + G3WorldBatch.MasterPath);
            var scenes = Scenes(null, out int missing);
            if (scenes.Count < 2) Fail("no cell scenes in the manifest"); else Ok($"{scenes.Count - 1} cell scenes + master ({missing} missing files)");

            string pack = Path.Combine(Application.streamingAssetsPath, "Content", ContentLocations.BasePack, "pack.json");
            if (File.Exists(pack)) Ok("content pack " + ContentLocations.BasePack); else Fail("no content pack " + pack);
            string voice = Myrtana.Core.GameDataLayout.VoiceRoot(ProjectDir);
            if (Directory.Exists(voice)) Ok("voice " + voice + " (copied to bin/GameData)"); else Warn("no " + voice + ": the demo is silent in dialogs");
            if (Directory.Exists(Path.Combine(Application.streamingAssetsPath, "Video"))) Ok("videos in StreamingAssets/Video"); else Warn("no StreamingAssets/Video: no logos/intro");
            // fix-170/170b: the third start-up logo "Gothic 3 Remaster" (CP's CPT slot, 2560x1440, ~9 min incl. ESRGAN), built from the G3 install; a build
            // (fix) rebuilds it when missing/stale, the check-only preflight just reports; without it the slot is skipped
            if (fix) { string logo = RemasterLogoBuild.Ensure(force: false, out bool logoOk); if (logoOk) Ok(logo); else Warn(logo); }
            else if (RemasterLogoBuild.UpToDate(out string logoWhy)) Ok("remaster logo " + GameFlow.RemasterLogo + ".webm");
            else Warn("remaster logo " + logoWhy + ": the build rebuilds it (Myrtana/Build/Build Remaster Logo Video)");
            // fix-152: G3World.exe is the native launcher with G3's desktop splash; the Unity splash is off
            string compiler = LauncherBuild.FindCompiler();
            if (compiler != null) Ok("launcher compiler: " + compiler + " (Tools/Launcher/build.cmd)");
            else Fail("no C compiler for the launcher G3World.exe: install Visual Studio Build Tools (\"Desktop development with C++\") or MinGW-w64 gcc");
            if (StartupSplash.Check(out string splash)) Ok(splash); else Warn(splash);
            // fix-165: player + launcher exe icon = the install's Gothic 3 icon (extracted into Library, set on the exes after the build)
            if (GameIconBuildStep.Ensure(out string icon) != null) Ok("G3 icon: " + icon + " (player + launcher exe)");
            else Warn(icon + ": the exes keep Unity's default icon");
            if (fix)
            {
                Ok(StartupSplash.Apply());
                int removed = StartupSplash.RemoveObsolete();
                if (removed > 0) Ok($"removed {removed} obsolete fix-137 splash files");
            }
            else if (PlayerSettings.SplashScreen.show || PlayerSettings.SplashScreen.showUnityLogo || (PlayerSettings.SplashScreen.logos?.Length ?? 0) > 0)
                Warn("Unity splash is on or has logos; the build turns it off");
            else Ok("Unity splash off");

            var set = AssetDatabase.LoadAssetAtPath<G3WorldRemasterSet>(G3WorldRemasterBuilder.SetPath);
            if (set == null) Warn("no " + G3WorldRemasterBuilder.SetPath + ": F7 / Options remaster switch has nothing to swap (run G3WorldRemasterBuilder + G3WorldV2Builder)");
            else
            {
                int r3 = set.pairs.Count(p => p.original != null && p.remaster != null), v2 = set.v2pairs?.Count(p => p.original != null && p.v2 != null) ?? 0;
                if (v2 == 0) Warn($"remaster set has {r3} r3 pairs but no v2 pairs (run G3WorldV2Builder)");
                else Ok($"remaster set: {r3} r3 + {v2} v2 material pairs (Resources: shipped)");
                // v2 trees (Docs/remaster-world.md 10.15): the set is referenced from the remaster set, so its prefabs ship with it
                int trees = set.treeV2 != null ? set.treeV2.models.Count(m => m.prefab != null) : 0;
                if (trees > 0) Ok($"v2 trees: {trees} prefabs (switch v2TreeReplace in G3WorldLook.json)");
                else if (G3.Unity.WorldRemasterLook.Current.v2TreeReplace) Warn("v2TreeReplace is on but no v2 trees are built (G3/Remaster/World/V2/Build Trees): G3's trees stay");
                else Ok("v2 trees: not built (v2TreeReplace off: G3's trees)");
            }
            if (AssetDatabase.LoadAssetAtPath<Material>("Assets/G3/Unity/Runtime/Resources/G3GrassV2ShaderRef.mat") is Material grass && grass.shader != null && grass.shader.isSupported)
                Ok("grass v2 shader reference");
            else Warn("G3GrassV2ShaderRef.mat missing or its shader unsupported: remastered grass falls back to the original");
            if (!(AssetDatabase.LoadAssetAtPath<Material>("Assets/G3/Unity/Runtime/Resources/G3ScatterShaderRef.mat") is Material scatter && scatter.shader != null && scatter.shader.isSupported))
                Warn("G3ScatterShaderRef.mat missing or its shader unsupported: the remaster ground cover (scatter) is off in the player");

            string env = Environment.GetEnvironmentVariable(G3RemasterSettings.EnvVar);
            if (env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
                Fail($"environment {G3RemasterSettings.EnvVar}={env} forces the character remaster on (unset it and restart the editor)");
            else if (G3RemasterSettings.Enabled)
            {
                if (!fix) Fail("character remaster is ON (G3/Remaster/Use Remaster Overrides); the build turns it off and rebuilds the characters");
                else
                {
                    G3RemasterSettings.Enabled = false;
                    var t = Stopwatch.StartNew();
                    string rebuilt = G3RemasterTools.RebuildCharacters();
                    Ok($"character remaster was ON: turned off and characters rebuilt in {t.Elapsed:mm\\:ss} (turn it on again after the build if wanted)");
                    sb.AppendLine("    " + rebuilt.Replace("\n", "\n    ").TrimEnd());
                }
            }
            else Ok("character remaster off (original characters)");

            // crash-1: with graphics jobs ("Split", gfx-threading-mode=6) the D3D11 player crashed on its render thread in the
            // GPU Resident Drawer's draws (HybridRendererFillVisibilityBuffer); the editor renders MultiThreaded and never did.
            // The player renders like the editor (Docs/performance.md, "Native crash with the GPU Resident Drawer").
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                Warn("active build target " + EditorUserBuildSettings.activeBuildTarget + ": graphics jobs of Windows not checked (switch to Windows x64)");
            else if (PlayerSettings.graphicsJobs)
            {
                if (!fix) Fail("graphics jobs are ON for Windows (render-thread crash with the GPU Resident Drawer); the build turns them off");
                else
                {
                    PlayerSettings.graphicsJobs = false; // the active target: the demo is built for StandaloneWindows64
                    AssetDatabase.SaveAssets();
                    Ok("graphics jobs were ON for Windows: turned off (render-thread crash with the GPU Resident Drawer)");
                }
            }
            else Ok("graphics jobs off for Windows (MultiThreaded rendering, as in the editor)");

            // runtime character remaster (Options → Remaster → Characters): rebuilt from the current overrides
            if (fix)
            {
                try
                {
                    var t = Stopwatch.StartNew();
                    string built = CharacterRemasterBuilder.Build() + "\n" + CharacterRemasterBuilder.BuildMaterials(1800f);
                    Ok($"runtime character set rebuilt in {t.Elapsed:mm\\:ss}");
                    sb.AppendLine("    " + built.Replace("\n", "\n    ").TrimEnd());
                }
                catch (Exception e) { Warn("runtime character set not rebuilt: " + e.Message); }
            }
            var chars = AssetDatabase.LoadAssetAtPath<G3CharacterRemasterSet>(CharacterRemasterBuilder.SetPath);
            if (chars == null || chars.swaps.Count == 0)
                Warn("no runtime character set (" + CharacterRemasterBuilder.SetPath + "): Options → Remaster → Characters shows 'not built'");
            else
            {
                int changed = chars.swaps.Count(x => !string.IsNullOrEmpty(x.mesh));
                string kits = string.Join(", ", chars.faceKits.Select(k => k.head));
                Ok($"runtime character set: {chars.swaps.Count} swaps ({changed} meshes), {chars.swaps.Sum(x => x.extras.Count)} extras, face kits [{kits}] ({chars.built})");
            }
            if (Directory.Exists(Path.Combine(ProjectDir, "Tools", "Scenarios"))) Ok("Tools/Scenarios (copied to bin/Scenarios for the console)");
            else Warn("no Tools/Scenarios: the console's spots and scenario files are missing in the build");

            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            Ok($"backend {backend}; Windows x64 release; define {Define} (variants dx12 / il2cpp set their own for one build: Build(variant: ...))");
            ok = good;
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// fix-166: the A|B composites of <see cref="Screens"/> are play shots of a development session, whose corner shows
        /// G3Player's "F1 — help" label (its OnGUI, dev keys on) under each half's caption; the demo has no such key. The
        /// package copy gets the band just below pasted over it (each half: x 0..40, rows 38..51 over 25..38 from the top,
        /// as the runners' manual Work/scratch/r34/unhint.py did, which build-1019d forgot). The sources stay as shot.
        /// False when the picture is not such a composite (left untouched).
        /// </summary>
        public static bool StripHelpHint(string png)
        {
            const int Width = 1600, BandX = 40, FromRow = 38, ToRow = 25, Rows = 13;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(png), false) || tex.width != Width || tex.height < FromRow + Rows) return false;
                int h = tex.height;
                for (int half = 0; half < 2; half++)
                {
                    int x0 = half * Width / 2;
                    // Texture rows count from the bottom: top row r is texture row h - 1 - r
                    var band = tex.GetPixels(x0, h - FromRow - Rows, BandX, Rows);
                    tex.SetPixels(x0, h - ToRow - Rows, BandX, Rows, band);
                }
                tex.Apply(false);
                File.WriteAllBytes(png, tex.EncodeToPNG());
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>README_RU/EN (Docs/demo), Screenshots, BUILD_INFO.txt; removes the do-not-ship folders.</summary>
        public static string Package(string outputDir, string buildLine)
        {
            var sb = new StringBuilder("package:");
            Directory.CreateDirectory(outputDir);
            // fix-167: the player and its files in bin/, the root keeps the launcher and the docs
            string bin = Path.Combine(outputDir, LauncherBuild.BinDir);
            Directory.CreateDirectory(bin);
            // fix-152: the launcher the owner starts (G3World.exe) and G3's splash bitmap (next to the player; the launcher reads it there)
            bool launcher = LauncherBuild.Build(outputDir, out string launcherReport);
            sb.Append(' ').Append(launcherReport).Append(',');
            sb.Append(' ').Append(StartupSplash.CopyBitmap(bin)).Append(',');
            string docs = Path.Combine(ProjectDir, "Docs");
            foreach (var dir in Directory.GetDirectories(bin))
            {
                string n = Path.GetFileName(dir);
                if (n.EndsWith("_DoNotShip", StringComparison.Ordinal) || n.EndsWith("_ButDontShipItWithYourGame", StringComparison.Ordinal))
                {
                    Directory.Delete(dir, true);
                    sb.Append(" removed ").Append(n).Append(',');
                }
            }
            // the dev console's data (spots for "tp spot:…", scenario files): <player exe>/Scenarios (Scenario.DataDir)
            string scenarios = Path.Combine(ProjectDir, "Tools", "Scenarios");
            if (Directory.Exists(scenarios))
            {
                string to = Path.Combine(bin, "Scenarios");
                int files = 0;
                foreach (var f in Directory.GetFiles(scenarios, "*", SearchOption.AllDirectories))
                {
                    string dst = Path.Combine(to, f.Substring(scenarios.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(f, dst, true);
                    files++;
                }
                sb.Append($" Scenarios ({files} files),");
            }
            string shots = Path.Combine(outputDir, "Screenshots");
            Directory.CreateDirectory(shots);
            int copied = 0, unhinted = 0;
            for (int i = 0; i < Screens.Length; i++)
            {
                string from = Path.Combine(docs, "screens", Screens[i]);
                if (!File.Exists(from)) continue;
                string pretty = $"{i + 1:00}_" + Screens[i].Replace("ingame_v2r5_", "").Replace("ingame_v2r3_", "");
                string to = Path.Combine(shots, pretty);
                File.Copy(from, to, true);
                if (StripHelpHint(to)) unhinted++;
                copied++;
            }
            sb.Append($" {copied}/{Screens.Length} screenshots (A original | B remastered, {unhinted} without the F1 hint)");
            long Size(string d) => Directory.Exists(d) ? new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            long data = Size(Path.Combine(bin, LauncherBuild.PlayerData)), gameData = Size(Path.Combine(bin, "GameData")), total = Size(outputDir);
            string sizes = $"sizes: {LauncherBuild.PlayerData} {data / 1048576} MB, GameData {gameData / 1048576} MB, total {total / 1048576} MB";
            sb.Append(", ").Append(sizes);
            // qa-1: line-ending-only rewrites by the editor (PC_RPAsset.asset) are not local changes: numstat of the normalised diff
            string commit = Myrtana.Core.BuildCommitLabel.Format(Git("rev-parse --short HEAD"), Git(Myrtana.Core.BuildCommitLabel.NumstatArgs));
            string stamp = DateTime.Now.ToString("yyyy-MM-dd") + ", commit " + commit + ", Unity " + Application.unityVersion;
            string sizeGb = Math.Max(1, (int)Math.Ceiling(total / 1073741824.0 + 0.5)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var name in new[] { "README_RU.md", "README_EN.md" })
            {
                string from = Path.Combine(docs, "demo", name);
                if (!File.Exists(from)) { sb.Append(", missing ").Append(from); continue; }
                string text = File.ReadAllText(from).Replace("__SIZE_GB__", sizeGb).Replace("__BUILD__", stamp);
                File.WriteAllText(Path.Combine(outputDir, name), text, new UTF8Encoding(false));
                sb.Append(", ").Append(name);
            }
            var info = new StringBuilder();
            // rebrand: the main menu's version line reads this stamp ("Gothic 3 Remaster — demo <date>, build <commit>")
            string streaming = Path.Combine(bin, LauncherBuild.PlayerData, "StreamingAssets");
            if (Directory.Exists(streaming))
            {
                File.WriteAllText(Path.Combine(streaming, Myrtana.Unity.BuildStamp.FileName),
                                  DateTime.Now.ToString("yyyy-MM-dd") + " " + commit.Split(' ')[0], new UTF8Encoding(false));
                sb.Append(", build stamp");
            }
            else sb.Append(", NO StreamingAssets for the build stamp");
            info.AppendLine("Gothic 3 Remaster — demo build (internal name G3World; based on Gothic 3 Community Patch 1.75)");
            info.AppendLine("date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            info.AppendLine("commit: " + commit);
            info.AppendLine("unity: " + Application.unityVersion);
            if (!string.IsNullOrEmpty(buildLine)) info.AppendLine(buildLine);
            info.AppendLine($"start: {LauncherBuild.LauncherExe} (launcher: G3 splash, then {LauncherBuild.BinDir}\\{LauncherBuild.PlayerExe} in {LauncherBuild.BinDir}; arguments passed through)" +
                            (launcher ? "" : $" -- LAUNCHER NOT BUILT, start {LauncherBuild.BinDir}\\{LauncherBuild.PlayerExe}"));
            info.AppendLine(Myrtana.Core.BuildFolderLayout.Describe());
            info.AppendLine(sizes);
            File.WriteAllText(Path.Combine(outputDir, "BUILD_INFO.txt"), info.ToString());
            // fix-167: the finished folder: launcher + docs in the root, the game in bin/
            var layout = Myrtana.Core.BuildFolderLayout.Check(outputDir);
            sb.Append(layout.Count == 0 ? ", layout ok (root: launcher + docs, game in bin/)" : ", LAYOUT: " + string.Join("; ", layout));
            return sb.ToString();
        }

        static string Git(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = ProjectDir, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                p.ErrorDataReceived += (_, __) => { };   // drain stderr (CRLF warnings) so a full pipe cannot block git
                p.BeginErrorReadLine();
                string o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(10000);
                return o;
            }
            catch (Exception e) { return "? (" + e.Message + ")"; }
        }
    }
}
