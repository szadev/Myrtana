using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-235: subtitles of shared (SVM) lines, with another voice set's text standing in and no blank text.</summary>
    public class SharedLineTextsTests
    {
        sealed class Strings : ILocalizer
        {
            readonly Dictionary<string, string> _texts;
            public Strings(Dictionary<string, string> texts) => _texts = texts;
            public string Text(TextId id) => TryText(id, out var t) ? t : "[" + id.Value + "]";
            public bool TryText(TextId id, out string text) => _texts.TryGetValue(id.Value ?? "", out text) && !string.IsNullOrEmpty(text);
            public string Stage(TextId id) => null;
        }

        static SharedLineTexts Texts() => new SharedLineTexts(new SharedLines(SharedLines.DefinitionId, "SVM_{voice}_{line}"),
            new Strings(new Dictionary<string, string>
            {
                ["SVM_Hum_Warrior_Hard_FirstMurder"] = "Ты убил одного из нас!",
                ["SVM_Hum_Warrior_FirstMurder"] = "short set",
                ["SVM_Gorn_Halt"] = "   ",
                ["SVM_Player_Sure"] = "Точно.",
            }),
            new[] { "Gorn", "Hum_Warrior_Hard", "Hum_Warrior", "Player" });

        [Test]
        public void OwnText_First()
        {
            Assert.That(Texts().TryText("Player", "Sure", out var t), Is.True);
            Assert.That(t, Is.EqualTo("Точно."));
            Assert.That(Texts().TryText(new TextId("SVM_Hum_Warrior_Hard_FirstMurder"), out t), Is.True);
            Assert.That(t, Is.EqualTo("Ты убил одного из нас!"));
        }

        [Test]
        public void MissingOwnText_TakesAnotherVoiceSetsText()
        {
            Assert.That(Texts().TryText(new TextId("SVM_Gorn_FirstMurder"), out var t), Is.True);
            Assert.That(t, Is.EqualTo("short set"), "the first set by name that has a text");
        }

        [Test]
        public void SplitsByTheLongestKnownVoiceSet()
        {
            Assert.That(Texts().TrySplit("SVM_Hum_Warrior_Hard_FirstMurder", out var set, out var line), Is.True);
            Assert.That(set, Is.EqualTo("Hum_Warrior_Hard"));
            Assert.That(line, Is.EqualTo("FirstMurder"));
            Assert.That(Texts().TrySplit("INFO_Gorn_Halt", out _, out _), Is.False);
        }

        [Test]
        public void NoNonBlankText_AnywhereIsNoSubtitle()
        {
            Assert.That(Texts().TryText(new TextId("SVM_Player_TooHard"), out var t), Is.False);
            Assert.That(t, Is.Null);
            Assert.That(Texts().TryText("Gorn", "Halt", out _), Is.False, "blank text is no text");
        }

        [Test]
        public void HeroRefusalLines_HavePatchTexts()
        {
            foreach (var line in new[] { "TooHard", "TooHeavy", "Impossible", "PickBroke", "NeedKey" })
                foreach (var language in new[] { "Russian", "English" })
                {
                    Assert.That(PatchStrings.TryGet(language, "SVM_Player_" + line, out var text), Is.True, line + " " + language);
                    Assert.That(text, Is.Not.Empty);
                }
            Assert.That(PatchStrings.TryGet("Russian", "svm_player_toohard", out var ru), Is.True, "ids are case-insensitive");
            Assert.That(ru, Is.EqualTo("Слишком сложно!"));
        }
    }
}
