using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>mt-1: demo build variants A (D3D12 + graphics jobs Split) and B (IL2CPP).</summary>
    public class BuildVariantTests
    {
        [TestCase(null, BuildVariantFlags.None)]
        [TestCase("", BuildVariantFlags.None)]
        [TestCase("default", BuildVariantFlags.None)]
        [TestCase("mono", BuildVariantFlags.None)]
        [TestCase("dx12", BuildVariantFlags.Dx12GraphicsJobs)]
        [TestCase("GfxJobs", BuildVariantFlags.Dx12GraphicsJobs)]
        [TestCase("il2cpp", BuildVariantFlags.Il2Cpp)]
        [TestCase("dx12+il2cpp", BuildVariantFlags.Dx12GraphicsJobs | BuildVariantFlags.Il2Cpp)]
        [TestCase("IL2CPP_dx12", BuildVariantFlags.Dx12GraphicsJobs | BuildVariantFlags.Il2Cpp)]
        [TestCase(" dx12 , il2cpp ", BuildVariantFlags.Dx12GraphicsJobs | BuildVariantFlags.Il2Cpp)]
        [TestCase("dx12mt", BuildVariantFlags.Dx12MultiThreaded)]
        [TestCase("dx12mt+il2cpp", BuildVariantFlags.Dx12MultiThreaded | BuildVariantFlags.Il2Cpp)]
        public void Parses(string text, BuildVariantFlags expected)
        {
            Assert.That(BuildVariant.TryParse(text, out var v, out var error), Is.True, error);
            Assert.That(v, Is.EqualTo(expected));
        }

        [Test]
        public void RejectsUnknownTokens()
        {
            Assert.That(BuildVariant.TryParse("dx12+vulkan", out var v, out var error), Is.False);
            Assert.That(v, Is.EqualTo(BuildVariantFlags.None));
            Assert.That(error, Does.Contain("vulkan"));
            Assert.That(BuildVariant.TryParse("dx12+dx12mt", out v, out error), Is.False, "Split or off, not both");
            Assert.That(v, Is.EqualTo(BuildVariantFlags.None));
        }

        [Test]
        public void FolderSuffixes()
        {
            Assert.That(BuildVariant.Suffix(BuildVariantFlags.None), Is.EqualTo(""));
            Assert.That(BuildVariant.Suffix(BuildVariantFlags.Dx12GraphicsJobs), Is.EqualTo("_dx12"));
            Assert.That(BuildVariant.Suffix(BuildVariantFlags.Il2Cpp), Is.EqualTo("_il2cpp"));
            Assert.That(BuildVariant.Suffix(BuildVariantFlags.Dx12GraphicsJobs | BuildVariantFlags.Il2Cpp), Is.EqualTo("_dx12_il2cpp"));
            Assert.That(BuildVariant.Suffix(BuildVariantFlags.Dx12MultiThreaded | BuildVariantFlags.Il2Cpp), Is.EqualTo("_dx12mt_il2cpp"));
            Assert.That(BuildVariant.Describe(BuildVariantFlags.Dx12MultiThreaded | BuildVariantFlags.Il2Cpp),
                        Is.EqualTo("variant dx12mt+il2cpp (D3D12 first, graphics jobs off (MultiThreaded), IL2CPP (Release, stripping Minimal))"));
            Assert.That(BuildVariant.Describe(BuildVariantFlags.Dx12GraphicsJobs | BuildVariantFlags.Il2Cpp),
                        Is.EqualTo("variant dx12+il2cpp (D3D12 first, graphics jobs Split, IL2CPP (Release, stripping Minimal))"));
        }
    }
}
