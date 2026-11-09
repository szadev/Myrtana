using System;
using System.IO;
using System.Linq;
using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-167: the demo build folder layout (launcher + docs in the root, the game in bin/) and the conversion.</summary>
    public class BuildFolderLayoutTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "buildlayout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        void Write(string relative, string text = "x")
        {
            string path = Path.Combine(_dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        /// <summary>A build of the flat layout before fix-167 (Demo_2026-10-19d).</summary>
        void MakeFlatBuild()
        {
            foreach (var f in new[] { "G3World.exe", "README_RU.md", "README_EN.md", "BUILD_INFO.txt", "G3WorldPlayer.exe", "UnityPlayer.dll",
                                      "UnityCrashHandler64.exe", "dstorage.dll", "dstoragecore.dll", "splash.bmp", "startup_trace.txt" })
                Write(f);
            Write("Screenshots/01_house_15.png");
            Write("G3WorldPlayer_Data/boot.config");
            Write("G3WorldPlayer_Data/StreamingAssets/Content/Gothic3/pack.json");
            Write("MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll");
            Write("D3D12/D3D12Core.dll");
            Write("GameData/Voice/Russian/A.ogg", "ogg");
            Write("Scenarios/spots.json", "{}");
        }

        [Test]
        public void RootEntriesStay()
        {
            Assert.That(BuildFolderLayout.BelongsInRoot("G3World.exe", false), Is.True);
            Assert.That(BuildFolderLayout.BelongsInRoot("readme_ru.md", false), Is.True);
            Assert.That(BuildFolderLayout.BelongsInRoot("BUILD_INFO.txt", false), Is.True);
            Assert.That(BuildFolderLayout.BelongsInRoot("Screenshots", true), Is.True);
            Assert.That(BuildFolderLayout.BelongsInRoot("bin", true), Is.True);
            Assert.That(BuildFolderLayout.BelongsInRoot("G3WorldPlayer.exe", false), Is.False);
            Assert.That(BuildFolderLayout.BelongsInRoot("splash.bmp", false), Is.False);
            Assert.That(BuildFolderLayout.BelongsInRoot("GameData", true), Is.False);
            Assert.That(BuildFolderLayout.BelongsInRoot("Screenshots", false), Is.False, "a file named like the folder");
        }

        [Test]
        public void FlatBuildIsReported()
        {
            MakeFlatBuild();
            var problems = BuildFolderLayout.Check(_dir);
            Assert.That(problems, Has.Some.Contains("bin/G3WorldPlayer.exe"));
            Assert.That(problems, Has.Some.StartsWith("GameData in the root"));
            Assert.That(BuildFolderLayout.Misplaced(_dir), Has.Count.EqualTo(12));
        }

        [Test]
        public void RelayoutMovesTheGameIntoBin()
        {
            MakeFlatBuild();
            string report = BuildFolderLayout.Relayout(_dir, out int moved, out int conflicts);
            Assert.That(moved, Is.EqualTo(12), report);
            Assert.That(conflicts, Is.Zero, report);
            Assert.That(BuildFolderLayout.Check(_dir), Is.Empty, report);
            Assert.That(Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal),
                Is.EqualTo(new[] { "BUILD_INFO.txt", "G3World.exe", "README_EN.md", "README_RU.md", "Screenshots", "bin" }));
            Assert.That(File.ReadAllText(Path.Combine(_dir, "bin", "GameData", "Voice", "Russian", "A.ogg")), Is.EqualTo("ogg"));
            Assert.That(File.Exists(Path.Combine(_dir, "bin", "Scenarios", "spots.json")));
            Assert.That(File.Exists(Path.Combine(_dir, "bin", "splash.bmp")));
            Assert.That(File.Exists(Path.Combine(_dir, "bin", "G3WorldPlayer_Data", "boot.config")));
            Assert.That(File.Exists(Path.Combine(_dir, "Screenshots", "01_house_15.png")));

            BuildFolderLayout.Relayout(_dir, out moved, out conflicts);
            Assert.That(moved + conflicts, Is.Zero, "a second run has nothing to do");
        }

        [Test]
        public void RelayoutKeepsAConflictInTheRoot()
        {
            MakeFlatBuild();
            Write("bin/UnityPlayer.dll", "new");
            BuildFolderLayout.Relayout(_dir, out int moved, out int conflicts);
            Assert.That(conflicts, Is.EqualTo(1));
            Assert.That(moved, Is.EqualTo(11));
            Assert.That(File.ReadAllText(Path.Combine(_dir, "bin", "UnityPlayer.dll")), Is.EqualTo("new"));
            Assert.That(File.Exists(Path.Combine(_dir, "UnityPlayer.dll")), "nothing is overwritten or deleted");
            Assert.That(BuildFolderLayout.Check(_dir), Has.Some.StartsWith("UnityPlayer.dll in the root"));
        }

        [Test]
        public void StalePlayerIsDeletedButOtherFilesStay()
        {
            MakeFlatBuild();
            Write("dstorage_extra.dll");
            Write("my_notes.txt");
            Write("G3WorldPlayer_BurstDebugInformation_DoNotShip/lib.txt");
            var removed = BuildFolderLayout.DeleteStalePlayer(_dir);
            Assert.That(removed, Does.Contain("G3WorldPlayer_Data").And.Contain("GameData").And.Contain("dstorage_extra.dll").And.Contain("splash.bmp")
                .And.Contain("G3WorldPlayer_BurstDebugInformation_DoNotShip"));
            Assert.That(BuildFolderLayout.Misplaced(_dir), Is.EquivalentTo(new[] { "my_notes.txt", "startup_trace.txt" }));
            Assert.That(File.Exists(Path.Combine(_dir, "G3World.exe")));
            Assert.That(File.Exists(Path.Combine(_dir, "Screenshots", "01_house_15.png")));
        }
    }
}
