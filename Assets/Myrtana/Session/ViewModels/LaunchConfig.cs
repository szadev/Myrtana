using System;

namespace Myrtana.Session
{
    /// <summary>The graphics API the launcher starts the player with (fix-211, Video page "Graphics API").</summary>
    /// <remarks>dx12-4: <see cref="Dx12"/> = D3D12 with graphics jobs off (Unity's multithreaded render thread,
    /// <c>-force-d3d12 -force-gfx-mt</c>; dx12-3's "dx12mt"). <see cref="Dx12Jobs"/> = D3D12 with the build's graphics jobs
    /// (Split): experimental only - build 26b crashed there inside D3D12Core (heap corruption of a command list's memory,
    /// found when Unity's TrimCommandLists freed it at Present), after 25b's DEVICE_HUNG in the same mode.</remarks>
    public enum GraphicsApi { Dx11 = 0, Dx12 = 1, Dx12Jobs = 2 }

    /// <summary>dx12-3: launch.cfg <c>gpudiag</c>: D3D12 debug layer (+ GPU-based validation) and the player's GPU step log.</summary>
    public enum GpuDiag { Off = 0, DebugLayer = 1, Validation = 2 }

    /// <summary>
    /// fix-211 [owner]: the launcher's own settings file (Docs/controls-options.md "Graphics API"). The native launcher
    /// (Tools/Launcher/G3Launcher.c, G3World.exe) cannot read PlayerPrefs (the registry), so the choice lives in a small text
    /// file next to the player's persistent data: <c>%USERPROFILE%\AppData\LocalLow\G3World\G3World\launch.cfg</c>
    /// (<c>Application.persistentDataPath</c>), one <c>key=value</c> per line, <c>#</c> comments:
    /// <c>api=dx11</c> (default, also with no file: the launcher appends <c>-force-d3d11</c>) or <c>api=dx12</c>
    /// (dx12-4: D3D12 with graphics jobs off, <see cref="Dx12Arguments"/>; <c>dx12mt</c> is the same) or
    /// <c>api=dx12jobs</c> (experimental: D3D12 with the build's graphics jobs, Split: <see cref="Dx12JobsArguments"/>;
    /// it hung the GPU in 25b and crashed in D3D12Core in 26b). <c>gpudiag=1</c> / <c>gbv</c>
    /// (or <c>-g3gpudiag</c> / <c>-g3gpudiag-gbv</c> on the launcher's command line) adds <see cref="DiagArguments"/>.
    /// <para>Crash fallback: before a dx12 start the launcher writes <see cref="MarkerFile"/>; it removes it when the player
    /// exits with code 0. A dx12 session that ends with another exit code (crash, hang killed, DXGI device hung), or a marker
    /// left behind (the launcher itself did not survive, e.g. a reset after a GPU hang), switches to dx11: the launcher
    /// writes <c>api=dx11</c>. <see cref="NextStart"/> / <see cref="AfterExit"/> are the launcher's rules in C#; the C code
    /// must match them.</para>
    /// Pure: no file access (Myrtana.Unity.LaunchConfigFile reads and writes the file).
    /// </summary>
    public static class LaunchConfig
    {
        public const string FileName = "launch.cfg";
        public const string MarkerFile = "launch.dx12";
        public const string ApiKey = "api";
        public const GraphicsApi Default = GraphicsApi.Dx11;
        /// <summary>The player argument for dx11 (Unity standalone command line).</summary>
        public const string ForceDx11Argument = "-force-d3d11";
        /// <summary>dx12-3/dx12-4: D3D12 with Unity's multithreaded render thread instead of graphics jobs (api=dx12).</summary>
        public const string Dx12Arguments = "-force-d3d12 -force-gfx-mt";
        /// <summary>dx12-4: D3D12 with the build's graphics jobs mode (boot.config: Split) - api=dx12jobs, experimental.</summary>
        public const string Dx12JobsArguments = "-force-d3d12";
        public const string GpuDiagKey = "gpudiag";
        public const string GpuDiagSwitch = "-g3gpudiag";
        public const string GpuDiagGbvSwitch = "-g3gpudiag-gbv";
        public const string D3D12DebugArgument = "-force-d3d12-debug";
        public const string D3D12GbvArgument = "-force-d3d12-debug-gbv";

        /// <summary>The api of a launch.cfg text; <see cref="Default"/> (dx11) unless it says dx12.</summary>
        public static GraphicsApi ParseApi(string text)
        {
            string value = Value(text, ApiKey);
            if (value == null) return Default;
            switch (value.ToLowerInvariant())
            {
                case "dx12": case "d3d12": case "12": case "dx12mt": case "d3d12mt": return GraphicsApi.Dx12;
                case "dx12jobs": case "d3d12jobs": return GraphicsApi.Dx12Jobs;
                default: return Default;
            }
        }

        /// <summary>dx12-3: the <c>gpudiag</c> line (1 / on / true / yes = debug layer, gbv = + GPU-based validation).</summary>
        public static GpuDiag ParseGpuDiag(string text)
        {
            string value = Value(text, GpuDiagKey);
            if (value == null) return GpuDiag.Off;
            switch (value.ToLowerInvariant())
            {
                case "gbv": return GpuDiag.Validation;
                case "1": case "on": case "true": case "yes": return GpuDiag.DebugLayer;
                default: return GpuDiag.Off;
            }
        }

        /// <summary>The file text for an api (the game rewrites the whole file; a gpudiag line is kept).</summary>
        public static string Format(GraphicsApi api, GpuDiag diag = GpuDiag.Off) =>
            "# G3World launcher settings (Options > Video > Graphics API; applies at the next start)\n" +
            ApiKey + "=" + Token(api) + "\n" +
            (diag == GpuDiag.Off ? "" : GpuDiagKey + "=" + (diag == GpuDiag.Validation ? "gbv" : "1") + "\n");

        public static string Token(GraphicsApi api) => api == GraphicsApi.Dx12 ? "dx12" : api == GraphicsApi.Dx12Jobs ? "dx12jobs" : "dx11";

        /// <summary>
        /// dx12-3: what the launcher puts before the forwarded arguments (G3Launcher.c must match): the API (dx11
        /// <see cref="ForceDx11Argument"/>, dx12 <see cref="Dx12Arguments"/>, dx12jobs <see cref="Dx12JobsArguments"/>), then with
        /// diagnostics on a D3D12 start <see cref="D3D12DebugArgument"/> (+ <see cref="D3D12GbvArgument"/>), and
        /// <see cref="GpuDiagSwitch"/> for the player unless the forwarded arguments have it already.
        /// </summary>
        public static string LaunchPrefix(GraphicsApi api, GpuDiag cfgDiag, string forwarded)
        {
            bool gbv = HasArg(forwarded, GpuDiagGbvSwitch) || cfgDiag == GpuDiag.Validation;
            bool diag = gbv || HasArg(forwarded, GpuDiagSwitch) || cfgDiag == GpuDiag.DebugLayer;
            var s = "";
            if (api == GraphicsApi.Dx11) s += " " + ForceDx11Argument;
            else if (api == GraphicsApi.Dx12) s += " " + Dx12Arguments;
            else if (api == GraphicsApi.Dx12Jobs) s += " " + Dx12JobsArguments;
            if (diag && api != GraphicsApi.Dx11) s += " " + D3D12DebugArgument;
            if (gbv && api != GraphicsApi.Dx11) s += " " + D3D12GbvArgument;
            if (diag && !HasArg(forwarded, GpuDiagSwitch)) s += " " + GpuDiagSwitch;
            return s;
        }

        /// <summary>The switch as a whole word of the argument text (as G3Launcher.c HasArg).</summary>
        static bool HasArg(string args, string sw)
        {
            if (string.IsNullOrEmpty(args)) return false;
            int p = 0;
            while ((p = args.IndexOf(sw, p, StringComparison.Ordinal)) >= 0)
            {
                bool startOk = p == 0 || args[p - 1] == ' ' || args[p - 1] == '\t' || args[p - 1] == '"';
                int e = p + sw.Length;
                if (startOk && (e == args.Length || args[e] == ' ' || args[e] == '\t' || args[e] == '"')) return true;
                p = e;
            }
            return false;
        }

        public static bool IsD3D12(GraphicsApi api) => api == GraphicsApi.Dx12 || api == GraphicsApi.Dx12Jobs;

        /// <summary>
        /// The launcher's decision for a start: the stored api and whether the last dx12 session's marker is still there.
        /// Returns the api to start with and whether launch.cfg is rewritten to dx11 (the fallback).
        /// </summary>
        public static (GraphicsApi api, bool fellBack) NextStart(GraphicsApi stored, bool markerLeft)
        {
            if (!IsD3D12(stored)) return (GraphicsApi.Dx11, false);
            return markerLeft ? (GraphicsApi.Dx11, true) : (stored, false);
        }

        /// <summary>After the player exits: true when a dx12 session failed and launch.cfg becomes dx11.</summary>
        public static bool AfterExit(GraphicsApi started, uint exitCode) => IsD3D12(started) && exitCode != 0;

        /// <summary>The Video page row: label and value texts (Russian or English by the text language).</summary>
        public static string RowText(string language) =>
            IsRussian(language) ? "Графический API (после перезапуска)" : "Graphics API (after restart)";

        public static string ValueText(GraphicsApi api, string language) =>
            api == GraphicsApi.Dx12 ? "DirectX 12" :
            api == GraphicsApi.Dx12Jobs ? (IsRussian(language) ? "DirectX 12 + графич. задачи (эксперимент.)" : "DirectX 12 + graphics jobs (experimental)") : "DirectX 11";

        /// <summary>The value of the first <c>key=value</c> line (case-insensitive key, trimmed), null when absent.</summary>
        static string Value(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var raw in text.TrimStart(Bom).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        const char Bom = (char)0xFEFF;

        static bool IsRussian(string language) => string.Equals(language, "Russian", StringComparison.OrdinalIgnoreCase);
    }
}
