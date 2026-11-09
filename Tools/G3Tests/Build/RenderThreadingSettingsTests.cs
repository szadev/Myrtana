using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace G3Tests.Build
{
    /// <summary>
    /// crash-1 (demo 2026-10-15_v2, Crash_2026-10-16_124609096): the Windows player crashed natively on its render thread in
    /// GfxDeviceWorker::RunCommand > GfxDeviceD3D11Base::DrawBuffersBatchMode > HybridRendererFillVisibilityBuffer (an access
    /// violation reading a BatchRendererGroup draw's visible-instance list, i.e. the GPU Resident Drawer's draws) under the
    /// player's default "Split" graphics jobs (boot.config gfx-threading-mode=6). The editor renders MultiThreaded and never
    /// crashed in hundreds of runner hours. With the GPU Resident Drawer on, the Windows player must not use graphics jobs,
    /// so it renders in the editor's tested mode (Docs/performance.md, "Native crash with the GPU Resident Drawer").
    /// </summary>
    public class RenderThreadingSettingsTests
    {
        static string Repo(string relative)
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, relative))) return Path.Combine(d.FullName, relative);
            Assert.Fail("not found: " + relative);
            return null;
        }

        /// <summary>The per-target m_GraphicsJobs of ProjectSettings.asset, or null when the target has no entry (engine default: on).</summary>
        public static bool? GraphicsJobs(string projectSettings, string target)
        {
            var m = Regex.Match(projectSettings,
                @"m_BuildTargetGraphicsJobs:\s*\r?\n((?:\s+-?\s*m_\w+: .*\r?\n)*)");
            if (!m.Success) return null;
            var e = Regex.Match(m.Groups[1].Value, @"m_BuildTarget: " + Regex.Escape(target) + @"\s*\r?\n\s+m_GraphicsJobs: (\d)");
            return e.Success ? e.Groups[1].Value != "0" : (bool?)null;
        }

        [Test]
        public void ParserReadsTheTargetEntry()
        {
            const string yaml = "  m_BuildTargetShaderSettings: []\n  m_BuildTargetGraphicsJobs:\n  - m_BuildTarget: MacStandaloneSupport\n" +
                                "    m_GraphicsJobs: 1\n  - m_BuildTarget: WindowsStandaloneSupport\n    m_GraphicsJobs: 0\n  m_BuildTargetGraphicsJobMode: []\n";
            Assert.That(GraphicsJobs(yaml, "WindowsStandaloneSupport"), Is.False);
            Assert.That(GraphicsJobs(yaml, "MacStandaloneSupport"), Is.True);
            Assert.That(GraphicsJobs("  m_BuildTargetGraphicsJobs: []\n", "WindowsStandaloneSupport"), Is.Null);
        }

        [Test]
        public void WindowsPlayerHasNoGraphicsJobsWhileTheResidentDrawerIsOn()
        {
            string rp = File.ReadAllText(Repo("Assets/Settings/PC_RPAsset.asset"));
            var mode = Regex.Match(rp, @"m_GPUResidentDrawerMode: (\d+)");
            Assert.That(mode.Success, "PC_RPAsset has m_GPUResidentDrawerMode");
            if (mode.Groups[1].Value == "0") Assert.Pass("GPU Resident Drawer off: graphics jobs are free");
            string ps = File.ReadAllText(Repo("ProjectSettings/ProjectSettings.asset"));
            Assert.That(GraphicsJobs(ps, "WindowsStandaloneSupport"), Is.False,
                "Windows standalone needs m_GraphicsJobs: 0 (PlayerSettings.graphicsJobs off) with the GPU Resident Drawer on");
        }
    }
}
