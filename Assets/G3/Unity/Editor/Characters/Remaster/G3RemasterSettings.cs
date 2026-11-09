using System;
using UnityEditor;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// The remaster override switch (Docs/remaster-pipeline.md). Off by default: the importer builds the original
    /// characters. On: parts with a <c>GameData/Remaster/Characters/&lt;stem&gt;.glb</c> use it. Stored per machine in
    /// EditorPrefs; the environment variable <see cref="EnvVar"/> (<c>1</c>/<c>0</c>) wins (batch runs, CI).
    /// Prefabs keep what they were built with: after toggling run <see cref="G3RemasterTools.RebuildCharacters"/>.
    /// </summary>
    public static class G3RemasterSettings
    {
        public const string PrefKey = "G3.Remaster.Enabled";
        public const string EnvVar = "G3_REMASTER";
        const string Menu = "G3/Remaster/Use Remaster Overrides";

        public static bool Enabled
        {
            get
            {
                string env = Environment.GetEnvironmentVariable(EnvVar);
                if (env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return EditorPrefs.GetBool(PrefKey, false);
            }
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        [MenuItem(Menu, priority = 200)]
        static void Toggle() => Enabled = !EditorPrefs.GetBool(PrefKey, false);

        [MenuItem(Menu, true)]
        static bool ToggleValidate()
        {
            UnityEditor.Menu.SetChecked(Menu, Enabled);
            return true;
        }
    }
}
