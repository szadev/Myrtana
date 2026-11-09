using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-211: launch.cfg (Graphics API option) parsing and the launcher's D3D12 crash fallback rules.</summary>
    public class LaunchConfigTests
    {
        [Test]
        public void ParseApi_DefaultsToDx11()
        {
            Assert.That(LaunchConfig.ParseApi(null), Is.EqualTo(GraphicsApi.Dx11));
            Assert.That(LaunchConfig.ParseApi(""), Is.EqualTo(GraphicsApi.Dx11));
            Assert.That(LaunchConfig.ParseApi("# api=dx12\n"), Is.EqualTo(GraphicsApi.Dx11), "a comment");
            Assert.That(LaunchConfig.ParseApi("api=vulkan"), Is.EqualTo(GraphicsApi.Dx11));
            Assert.That(LaunchConfig.ParseApi("api=dx11"), Is.EqualTo(GraphicsApi.Dx11));
        }

        [Test]
        public void ParseApi_ReadsDx12Leniently()
        {
            Assert.That(LaunchConfig.ParseApi("api=dx12"), Is.EqualTo(GraphicsApi.Dx12));
            Assert.That(LaunchConfig.ParseApi((char)0xFEFF + "# c\r\n  API = DX12 \r\n"), Is.EqualTo(GraphicsApi.Dx12), "BOM, CRLF, blanks, case");
            Assert.That(LaunchConfig.ParseApi("other=1\napi=d3d12"), Is.EqualTo(GraphicsApi.Dx12));
        }

        [Test]
        public void Format_RoundTrips()
        {
            foreach (var api in new[] { GraphicsApi.Dx11, GraphicsApi.Dx12, GraphicsApi.Dx12Jobs })
                Assert.That(LaunchConfig.ParseApi(LaunchConfig.Format(api)), Is.EqualTo(api));
            StringAssert.Contains("api=dx11\n", LaunchConfig.Format(GraphicsApi.Dx11));
            foreach (var d in new[] { GpuDiag.Off, GpuDiag.DebugLayer, GpuDiag.Validation })
                Assert.That(LaunchConfig.ParseGpuDiag(LaunchConfig.Format(GraphicsApi.Dx12Jobs, d)), Is.EqualTo(d));
        }

        [Test]
        public void Dx12Mt_IsDx12_Dx12Jobs_ParsesAndFallsBackLikeDx12()
        {
            // dx12-4: dx12 itself runs without graphics jobs now; dx12-3's "dx12mt" files keep working
            Assert.That(LaunchConfig.ParseApi("api=dx12mt"), Is.EqualTo(GraphicsApi.Dx12));
            Assert.That(LaunchConfig.ParseApi("api = D3D12MT"), Is.EqualTo(GraphicsApi.Dx12));
            Assert.That(LaunchConfig.ParseApi("api=dx12jobs"), Is.EqualTo(GraphicsApi.Dx12Jobs));
            Assert.That(LaunchConfig.ParseApi("api = D3D12JOBS"), Is.EqualTo(GraphicsApi.Dx12Jobs));
            Assert.That(LaunchConfig.NextStart(GraphicsApi.Dx12Jobs, false), Is.EqualTo((GraphicsApi.Dx12Jobs, false)));
            Assert.That(LaunchConfig.NextStart(GraphicsApi.Dx12Jobs, true), Is.EqualTo((GraphicsApi.Dx11, true)));
            Assert.That(LaunchConfig.AfterExit(GraphicsApi.Dx12Jobs, 1), Is.True);
            Assert.That(LaunchConfig.AfterExit(GraphicsApi.Dx12Jobs, 0), Is.False);
        }

        [Test]
        public void LaunchPrefix_ApiAndGpuDiagnostics()
        {
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx11, GpuDiag.Off, ""), Is.EqualTo(" -force-d3d11"));
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx12, GpuDiag.Off, "-screen-width 800"), Is.EqualTo(" -force-d3d12 -force-gfx-mt"));
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx12Jobs, GpuDiag.Off, ""), Is.EqualTo(" -force-d3d12"));
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx12Jobs, GpuDiag.Off, "-g3gpudiag"), Is.EqualTo(" -force-d3d12 -force-d3d12-debug"));
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx12Jobs, GpuDiag.DebugLayer, ""), Is.EqualTo(" -force-d3d12 -force-d3d12-debug -g3gpudiag"));
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx12, GpuDiag.Off, "-g3gpudiag-gbv"),
                Is.EqualTo(" -force-d3d12 -force-gfx-mt -force-d3d12-debug -force-d3d12-debug-gbv -g3gpudiag"));
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx11, GpuDiag.Validation, ""), Is.EqualTo(" -force-d3d11 -g3gpudiag"), "dx11: only the player log");
            Assert.That(LaunchConfig.LaunchPrefix(GraphicsApi.Dx12Jobs, GpuDiag.Off, "-g3gpudiagx"), Is.EqualTo(" -force-d3d12"), "whole words only");
        }

        [Test]
        public void NextStart_FallsBackWhenTheLastDx12SessionLeftItsMarker()
        {
            Assert.That(LaunchConfig.NextStart(GraphicsApi.Dx12, false), Is.EqualTo((GraphicsApi.Dx12, false)));
            Assert.That(LaunchConfig.NextStart(GraphicsApi.Dx12, true), Is.EqualTo((GraphicsApi.Dx11, true)));
            Assert.That(LaunchConfig.NextStart(GraphicsApi.Dx11, true), Is.EqualTo((GraphicsApi.Dx11, false)));
        }

        [Test]
        public void AfterExit_AnyFailedDx12SessionFallsBack()
        {
            Assert.That(LaunchConfig.AfterExit(GraphicsApi.Dx12, 0), Is.False);
            Assert.That(LaunchConfig.AfterExit(GraphicsApi.Dx12, 0xC0000005), Is.True);
            Assert.That(LaunchConfig.AfterExit(GraphicsApi.Dx12, 1), Is.True);
            Assert.That(LaunchConfig.AfterExit(GraphicsApi.Dx11, 1), Is.False);
        }

        [Test]
        public void Texts()
        {
            Assert.That(LaunchConfig.RowText("Russian"), Is.EqualTo("Графический API (после перезапуска)"));
            Assert.That(LaunchConfig.RowText("English"), Is.EqualTo("Graphics API (after restart)"));
            Assert.That(LaunchConfig.ValueText(GraphicsApi.Dx11, "English"), Is.EqualTo("DirectX 11"));
            Assert.That(LaunchConfig.ValueText(GraphicsApi.Dx12, "English"), Is.EqualTo("DirectX 12"));
            Assert.That(LaunchConfig.ValueText(GraphicsApi.Dx12Jobs, "English"), Is.EqualTo("DirectX 12 + graphics jobs (experimental)"));
            StringAssert.StartsWith("DirectX 12 + ", LaunchConfig.ValueText(GraphicsApi.Dx12Jobs, "Russian"));
        }
    }
}
