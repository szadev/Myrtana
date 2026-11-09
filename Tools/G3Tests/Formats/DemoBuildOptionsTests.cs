using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// The demo build's code switches (Docs/build.md §2–3), checked in the sources: the dev console is compiled into a
    /// G3_DEMO player, the dev keys are not, and the remaster switches live on their own options page.
    /// </summary>
    [TestFixture]
    public class DemoBuildOptionsTests
    {
        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        static string Read(string path) => File.ReadAllText(Path.Combine(Repo(), path));

        [Test]
        public void DevConsoleAndScenarios_AreCompiledIntoTheDemo()
        {
            var files = new[] { "Assets/Myrtana/Session/Dev", "Assets/Myrtana/Unity/Dev" }
                        .SelectMany(d => Directory.GetFiles(Path.Combine(Repo(), d), "*.cs")).ToList();
            Assert.That(files.Count, Is.GreaterThanOrEqualTo(28));
            foreach (var f in files)
            {
                string first = File.ReadLines(f).First(l => l.TrimStart().StartsWith("#if"));
                Assert.That(first, Does.Contain("UNITY_EDITOR").And.Contain("DEVELOPMENT_BUILD").And.Contain("G3_DEMO"), Path.GetFileName(f));
            }
            Assert.That(Read("Assets/Myrtana/Unity/Dev/DevConsole.cs"), Does.Not.Contain("isDebugBuild"), "nothing else gates the console");
        }

        [Test]
        public void DevKeys_StayOffInARelease()
        {
            string player = Read("Assets/G3/Unity/Runtime/Player/G3Player.cs");
            Assert.That(player, Does.Match(@"s_devKeys \?\?= Debug\.isDebugBuild"), "fly / time / F1 help: development builds only");
        }

        [Test]
        public void Remaster_HasItsOwnOptionsPage()
        {
            string settings = Read("Assets/Myrtana/Unity/Menu/G3MenuSettingsPages.cs");
            Assert.That(settings, Does.Not.Contain("AddRemasterRow"), "the world row moved from Video to the Remaster page");
            Assert.That(settings, Does.Contain("MenuPage.Remaster"));
            string page = Read("Assets/Myrtana/Unity/Menu/G3MenuRemasterPage.cs");
            foreach (var call in new[] { "G3WorldRemaster.SetPart(f, on, persist: true)", "G3CharacterRemaster.SetEnabled(on)" })
                Assert.That(page, Does.Contain(call));
            Assert.That(page, Does.Contain("\"VIDEO_GAMMA_BLUE\""), "fix-71: seven rows in the seven row places of the video layout");
            string hotkeys = Read("Assets/Myrtana/Unity/Settings/RemasterHotkeys.cs");
            Assert.That(hotkeys, Does.Contain("G3WorldRemaster.SetAll(on, persist: true)").And.Contain("RemasterOptions.MasterTarget("),
                        "F7 is the master switch of every world part");
            string world = Read("Assets/G3/Unity/Runtime/Remaster/G3WorldRemaster.cs");
            Assert.That(world, Does.Contain("G3TreeV2.SetOption(on, persist, refresh: false)"), "the Trees part goes through the tree option");
            Assert.That(world, Does.Contain("RemasterOptions.Migrate("), "the old G3.Remaster.World key is migrated");
            Assert.That(Read("Assets/Myrtana/Unity/Menu/G3MenuView.cs"), Does.Contain("AddRemasterButton(languages)"));
            Assert.That(Read("Assets/G3/Unity/Runtime/Remaster/G3WorldV2.cs"), Does.Contain("G3TreeV2.Refresh()"),
                        "trees follow their own option, not only the world v2 layer");
            Assert.That(Read("Assets/Myrtana/Unity/Bootstrap/GameBootstrap.cs"), Does.Contain("G3CharacterRemaster.Register(player.gameObject)"));
            string armour = Read("Assets/Myrtana/Unity/Items/HeroArmorView.cs");
            Assert.That(Regex.IsMatch(armour, @"Suspend\(_hero\.gameObject\)[\s\S]*Resume\(_hero\.gameObject\)"), "armour changes suspend the swap");
        }
    }
}
