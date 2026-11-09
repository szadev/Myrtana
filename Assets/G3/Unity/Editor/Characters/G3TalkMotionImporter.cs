using G3.Formats.Animation;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Adds the talk gestures (Docs/dialog-presentation.md §3: <c>&lt;Rig&gt;_Stand_*_Say_&lt;Phase&gt;_*</c> and the
    /// <c>Listen</c> loops) to the hero's and the NPC rigs' motion libraries without rebuilding them; full rebuilds
    /// include them as well. CLI: <c>G3.Unity.Editor.Characters.G3TalkMotionImporter.AddToLibraries("Hero,Orc", true)</c>.
    /// </summary>
    public static class G3TalkMotionImporter
    {
        public static bool IsTalkMotion(MotionName n) => n != null && n.Stance == "Stand" && (n.Action == "Say" || n.Action == "Listen");

        public static string AddToLibraries(string rigs, bool hero) => G3CombatMotionImporter.AddToLibraries(rigs, hero, IsTalkMotion, "talk");
    }
}
