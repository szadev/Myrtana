using System;

namespace Myrtana.Core
{
    /// <summary>
    /// mt-1 (Docs/multithreading-plan.md, items A and B): A/B variants of the demo player build. The default build (no
    /// variant) is the shipped one: D3D11 first, graphics jobs off (crash-1), Mono.
    /// <list type="bullet">
    /// <item><see cref="BuildVariantFlags.Dx12GraphicsJobs"/> ("dx12"): D3D12 first in the Windows API list (D3D11 stays as
    /// the fallback), graphics jobs on, mode Split: render-command recording moves to the job workers.</item>
    /// <item><see cref="BuildVariantFlags.Dx12MultiThreaded"/> ("dx12mt", dx12-3): D3D12 first, graphics jobs off: Unity's
    /// multithreaded render thread records everything (no Split jobs) - the middle ground after the D3D12 + Split GPU
    /// hangs (builds 24a, 25b). Not together with "dx12".</item>
    /// <item><see cref="BuildVariantFlags.Il2Cpp"/> ("il2cpp"): IL2CPP scripting backend (Release C++, managed stripping
    /// Minimal: user assemblies are kept whole).</item>
    /// </list>
    /// Selected by <c>DemoBuild.Build(variant: "dx12+il2cpp")</c>, the command-line argument <c>-demoVariant</c> or the
    /// environment variable <see cref="EnvVar"/>. The project settings on disk are not changed (the build restores them).
    /// </summary>
    [Flags]
    public enum BuildVariantFlags
    {
        None = 0,
        Dx12GraphicsJobs = 1,
        Il2Cpp = 2,
        Dx12MultiThreaded = 4,
    }

    public static class BuildVariant
    {
        public const string EnvVar = "G3_DEMO_VARIANT";
        public const string Argument = "-demoVariant";

        /// <summary>
        /// "" / null / "default" / "mono" = none; otherwise tokens "dx12" (alias "gfxjobs") and "il2cpp" joined by '+', ',',
        /// '_' or spaces, any case. False with <paramref name="error"/> for an unknown token.
        /// </summary>
        public static bool TryParse(string text, out BuildVariantFlags variant, out string error)
        {
            variant = BuildVariantFlags.None;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            foreach (var raw in text.Split(new[] { '+', ',', '_', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().ToLowerInvariant();
                switch (t)
                {
                    case "default": case "mono": case "none": break;
                    case "dx12": case "d3d12": case "gfxjobs": variant |= BuildVariantFlags.Dx12GraphicsJobs; break;
                    case "dx12mt": case "d3d12mt": variant |= BuildVariantFlags.Dx12MultiThreaded; break;
                    case "il2cpp": variant |= BuildVariantFlags.Il2Cpp; break;
                    default:
                        error = $"unknown build variant '{raw}' (use dx12, dx12mt, il2cpp, dx12+il2cpp or dx12mt+il2cpp)";
                        variant = BuildVariantFlags.None;
                        return false;
                }
            }
            if ((variant & BuildVariantFlags.Dx12GraphicsJobs) != 0 && (variant & BuildVariantFlags.Dx12MultiThreaded) != 0)
            {
                error = "build variants dx12 and dx12mt exclude each other (graphics jobs Split or off)";
                variant = BuildVariantFlags.None;
                return false;
            }
            return true;
        }

        /// <summary>The build folder suffix: "" for the default build, else "_dx12", "_il2cpp", "_dx12_il2cpp".</summary>
        public static string Suffix(BuildVariantFlags variant) =>
            ((variant & BuildVariantFlags.Dx12GraphicsJobs) != 0 ? "_dx12" : "") +
            ((variant & BuildVariantFlags.Dx12MultiThreaded) != 0 ? "_dx12mt" : "") + ((variant & BuildVariantFlags.Il2Cpp) != 0 ? "_il2cpp" : "");

        /// <summary>One line for BUILD_INFO and the build log.</summary>
        public static string Describe(BuildVariantFlags variant)
        {
            if (variant == BuildVariantFlags.None) return "variant default (D3D11 first, graphics jobs off, Mono)";
            string gfx = (variant & BuildVariantFlags.Dx12GraphicsJobs) != 0 ? "D3D12 first, graphics jobs Split" :
                (variant & BuildVariantFlags.Dx12MultiThreaded) != 0 ? "D3D12 first, graphics jobs off (MultiThreaded)" : "D3D11 first, graphics jobs off";
            string backend = (variant & BuildVariantFlags.Il2Cpp) != 0 ? "IL2CPP (Release, stripping Minimal)" : "Mono";
            return $"variant {Suffix(variant).TrimStart('_').Replace('_', '+')} ({gfx}, {backend})";
        }
    }
}
