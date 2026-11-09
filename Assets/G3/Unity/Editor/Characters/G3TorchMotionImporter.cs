using G3.Formats.Animation;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Adds the hero's torch motions (394 Hero_*_Torch_* clips: Stand/Sneak locomotion with the torch, HoldLeft_Begin/End to
    /// take it out and put it away, the Torch_1H combat set, reactions and down states) to the hero's motion library.
    /// Only the Hero rig has torch motions in G3. Full hero rebuilds include them as well.
    /// Menu <c>G3/Characters/Add Hero Torch Motions</c> or <c>G3.Unity.Editor.Characters.G3TorchMotionImporter.AddToHero()</c>.
    /// </summary>
    public static class G3TorchMotionImporter
    {
        public static bool IsTorchMotion(MotionName n) =>
            n != null && n.Rig == "Hero" && n.LeftItem == "Torch";

        [MenuItem("G3/Characters/Add Hero Torch Motions")]
        static void Menu() => Debug.Log(AddToHero());

        public static string AddToHero() => G3CombatMotionImporter.AddToLibraries("", true, IsTorchMotion, "torch");
    }
}
