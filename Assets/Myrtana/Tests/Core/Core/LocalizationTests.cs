using System.Linq;
using System.Collections.Generic;
using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class LocalizationTests
    {
        sealed class Tables : IStringTableSource
        {
            readonly Dictionary<string, StringTable> _tables = new Dictionary<string, StringTable>();

            public Tables Add(string language, Dictionary<string, string> texts)
            {
                var t = new StringTable(language);
                t.Overlay(new StringTableFile(texts, new Dictionary<string, string> { ["A"] = language + " stage" }));
                _tables[language] = t;
                return this;
            }

            public StringTable Load(string language) => _tables.TryGetValue(language, out var t) ? t : null;
        }

        sealed class Clips : IVoiceClipCatalog
        {
            readonly HashSet<string> _clips = new HashSet<string>();
            public Clips Add(string language, string key) { _clips.Add(language + "/" + key); return this; }
            public bool Contains(string language, string clipKey) => _clips.Contains(language + "/" + clipKey);
        }

        static Tables RussianAndEnglish() => new Tables()
            .Add("Russian", new Dictionary<string, string> { ["A"] = "Стоять!", ["EMPTY"] = "" })
            .Add("English", new Dictionary<string, string> { ["A"] = "Halt!", ["B"] = "Only English", ["EMPTY"] = "Empty in Russian" });

        [Test]
        public void Text_UsesSelectedLanguage_ThenEnglish_ThenAVisibleMarker()
        {
            var settings = new LanguageSettings("Russian", "Russian");
            var loc = new Localizer(settings, RussianAndEnglish());

            Assert.That(loc.Text(new TextId("A")), Is.EqualTo("Стоять!"));
            Assert.That(loc.Text(new TextId("B")), Is.EqualTo("Only English"));
            Assert.That(loc.Text(new TextId("EMPTY")), Is.EqualTo("Empty in Russian"), "an empty translation counts as missing");
            Assert.That(loc.Text(new TextId("C")), Is.EqualTo("[C]"));
            Assert.That(loc.Stage(new TextId("A")), Is.EqualTo("Russian stage"));
        }

        [Test]
        public void Ids_AreLookedUpCaseInsensitively()
        {
            // Perk_OrcSlayer's learnt notice asks for FO_It_Perk_OrcSlayer; the table key is FO_It_Perk_Orcslayer.
            var t = new StringTable("Russian");
            t.Overlay(new StringTableFile(new Dictionary<string, string> { ["FO_It_Perk_Orcslayer"] = "Убийца орков" }, null));
            Assert.That(t.TryGetText("FO_It_Perk_OrcSlayer", out var text), Is.True);
            Assert.That(text, Is.EqualTo("Убийца орков"));
        }

        [Test]
        public void DevNameSearch_FindsRussianAndEnglishNames_WhateverTheTextLanguage()
        {
            var loc = new Localizer(new LanguageSettings("English", "Russian"), RussianAndEnglish());
            Assert.That(loc.TryTextIn("Russian", new TextId("A"), out var ru) ? ru : null, Is.EqualTo("Стоять!"));
            Assert.That(loc.TryTextIn("Russian", new TextId("B"), out _), Is.False, "no fallback");
            var entries = new[] { ("It_A", new TextId("A")), ("It_B", new TextId("B")) }
                .SelectMany(e => Myrtana.Session.Dev.DevLookup.Names(e.Item1, e.Item2, loc)).ToList();
            Assert.That(Myrtana.Session.Dev.DevLookup.Best(entries, "стоять", out _), Is.EqualTo("It_A"), "editor-runner-16: give меч in English");
            Assert.That(Myrtana.Session.Dev.DevLookup.Best(entries, "halt", out int others), Is.EqualTo("It_A"));
            Assert.That(others, Is.Zero, "one id once, whatever the number of its names");
        }

        [Test]
        public void Text_FollowsALanguageSwitch()
        {
            var settings = new LanguageSettings("Russian", "Russian");
            var loc = new Localizer(settings, RussianAndEnglish());
            int changes = 0;
            settings.Changed += () => changes++;

            settings.TextLanguage = "English";
            settings.TextLanguage = "English";

            Assert.That(loc.Text(new TextId("A")), Is.EqualTo("Halt!"));
            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public void UnknownLanguage_FallsBackToEnglish()
        {
            var loc = new Localizer(new LanguageSettings("Klingon", "Klingon"), RussianAndEnglish());
            Assert.That(loc.Text(new TextId("A")), Is.EqualTo("Halt!"));
        }

        [Test]
        public void Voice_UsesVoiceLanguage_ThenEnglish_ThenSilence()
        {
            var settings = new LanguageSettings("English", "Russian");
            var voices = new VoiceResolver(settings, new Clips().Add("Russian", "ru_and_en").Add("English", "ru_and_en").Add("English", "en_only"));

            Assert.That(voices.TryResolve("ru_and_en", out var a), Is.True);
            Assert.That(a.Language, Is.EqualTo("Russian"), "voice language is independent of the text language");
            Assert.That(voices.TryResolve("en_only", out var b), Is.True);
            Assert.That(b.Language, Is.EqualTo("English"));
            Assert.That(voices.TryResolve("missing", out _), Is.False);
            Assert.That(voices.TryResolve(null, out _), Is.False);
        }
    }
}
