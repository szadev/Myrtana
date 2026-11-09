using System;
using System.IO;
using Myrtana.Core;
using Myrtana.Import.G3;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The GameData folder outside Assets: layout, the one-shot move out of StreamingAssets, the build copy.</summary>
    public class GameDataFilesTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gamedata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_dir, true);

        void Write(string relative, string text)
        {
            string path = Path.Combine(_dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        void MakeLegacyVoice()
        {
            Write("Assets/StreamingAssets/Voice.meta", "m");
            Write("Assets/StreamingAssets/Voice/Russian.meta", "m");
            Write("Assets/StreamingAssets/Voice/Russian/A.ogg", "ogg");
            Write("Assets/StreamingAssets/Voice/Russian/A.ogg.meta", "m");
            Write("Assets/StreamingAssets/Voice/Russian/A.lip", "lip");
        }

        [Test]
        public void Layout_puts_voice_next_to_assets()
        {
            if (Environment.GetEnvironmentVariable(GameDataLayout.OverrideVariable) != null) Assert.Ignore("override set");
            Assert.That(GameDataLayout.VoiceRoot(_dir), Is.EqualTo(Path.Combine(_dir, "GameData", "Voice")));
            Assert.That(GameDataLayout.LegacyVoiceRoot(_dir), Is.EqualTo(Path.Combine(_dir, "Assets", "StreamingAssets", "Voice")));
        }

        [Test]
        public void Move_renames_the_tree_and_drops_meta_files()
        {
            MakeLegacyVoice();
            GameDataFiles.Move(GameDataLayout.LegacyVoiceRoot(_dir), Path.Combine(_dir, "GameData", "Voice"));
            Assert.That(File.ReadAllText(Path.Combine(_dir, "GameData/Voice/Russian/A.ogg")), Is.EqualTo("ogg"));
            Assert.That(File.Exists(Path.Combine(_dir, "GameData/Voice/Russian/A.lip")));
            Assert.That(Directory.GetFiles(Path.Combine(_dir, "GameData"), "*.meta", SearchOption.AllDirectories), Is.Empty);
            Assert.That(Directory.Exists(Path.Combine(_dir, "Assets/StreamingAssets/Voice")), Is.False);
            Assert.That(File.Exists(Path.Combine(_dir, "Assets/StreamingAssets/Voice.meta")), Is.False);
        }

        [Test]
        public void Move_merges_into_an_existing_target()
        {
            MakeLegacyVoice();
            Write("GameData/Voice/Russian/A.ogg", "old");
            Write("GameData/Voice/English/B.ogg", "b");
            GameDataFiles.Move(GameDataLayout.LegacyVoiceRoot(_dir), Path.Combine(_dir, "GameData", "Voice"));
            Assert.That(File.ReadAllText(Path.Combine(_dir, "GameData/Voice/Russian/A.ogg")), Is.EqualTo("ogg"));
            Assert.That(File.Exists(Path.Combine(_dir, "GameData/Voice/English/B.ogg")));
            Assert.That(Directory.Exists(Path.Combine(_dir, "Assets/StreamingAssets/Voice")), Is.False);
        }

        [Test]
        public void Move_without_a_source_does_nothing()
        {
            Assert.That(GameDataFiles.Move(Path.Combine(_dir, "none"), Path.Combine(_dir, "GameData")), Does.StartWith("nothing"));
        }

        [Test]
        public void Mirror_copies_only_changed_files()
        {
            Write("GameData/Voice/Russian/A.ogg", "ogg");
            Write("GameData/Voice/Russian/B.ogg", "b");
            string from = Path.Combine(_dir, "GameData"), to = Path.Combine(_dir, "Build", "GameData");
            Assert.That(GameDataFiles.Mirror(from, to), Is.EqualTo((2, 0)));
            Assert.That(GameDataFiles.Mirror(from, to), Is.EqualTo((0, 2)));
            Write("GameData/Voice/Russian/B.ogg", "bb");
            Assert.That(GameDataFiles.Mirror(from, to), Is.EqualTo((1, 1)));
            Assert.That(File.ReadAllText(Path.Combine(to, "Voice/Russian/B.ogg")), Is.EqualTo("bb"));
        }
    }
}
