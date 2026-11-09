using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using G3.Formats.World;

namespace G3.Unity.Editor
{
    /// <summary>
    /// The story-switchable world sectors for the world bakes (cells, world effects): read from the game content pack's
    /// <c>sectors/</c> folder (written by the content import from the infos' SetSectorStatus lines, the *_NPC_01/_02
    /// pairs and CP's liberation table), or, without a pack, every sector that is off in .wrldatasc. Name → on at the
    /// start of a new game. Run the content import before baking.
    /// </summary>
    public static class G3StorySectorList
    {
        public const string PackSectorFolder = "Assets/StreamingAssets/Content/Gothic3/sectors";

        static readonly Regex Entry = new Regex(@"\{[^{}]*""id""\s*:\s*""([^""]+)""[^{}]*\}", RegexOptions.Compiled);
        static readonly Regex OnAtStart = new Regex(@"""enabledAtStart""\s*:\s*true", RegexOptions.Compiled);

        /// <param name="layout">A layout built with includeDisabled (for the fallback).</param>
        public static Dictionary<string, bool> Load(WorldLayout layout)
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(PackSectorFolder))
                foreach (var file in Directory.GetFiles(PackSectorFolder, "*.json"))
                    foreach (Match m in Entry.Matches(File.ReadAllText(file)))
                        map[m.Groups[1].Value] = OnAtStart.IsMatch(m.Value);
            if (map.Count > 0 || layout == null) return map;
            foreach (var s in layout.Sectors)
                if (!s.Enabled) map[s.Name] = false;
            return map;
        }
    }
}
