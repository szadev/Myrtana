using UnityEditor;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// Menu Myrtana/Start: the developer quick start (Play → new game where the scene's hero stands, Faring) or the
    /// game's real start (logos, title screen, New Game at Ardea, intro). Docs/game-start.md.
    /// </summary>
    static class DevStartMenu
    {
        const string Item = "Myrtana/Start/Dev quick start at Faring";

        [MenuItem(Item, priority = 200)]
        static void Toggle() => EditorPrefs.SetBool(DevStart.PrefKey, !EditorPrefs.GetBool(DevStart.PrefKey, true));

        [MenuItem(Item, true)]
        static bool Validate()
        {
            Menu.SetChecked(Item, EditorPrefs.GetBool(DevStart.PrefKey, true));
            return true;
        }
    }
}
