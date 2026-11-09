using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace G3.Unity
{
    // Engine-free part of the Options → Remaster page (Docs/build.md §3): the independent switches, their stored keys,
    // overrides, the F7 master switch, the migration of the old single "World" switch and the texts. Compiled into
    // G3.Unity and into Tools/G3Tests.

    /// <summary>One switch (row) of the Remaster options page. The first six are parts of the world (F7 flips them together).</summary>
    public enum RemasterFeature
    {
        /// <summary>Light and air: sky clouds, aerial perspective, clarity, grade/shoulder, SSAO boost (r3 look, section 7) and
        /// the v2 light (sun/ambient balance, long shadows, height fog + sun shafts, volumetric clouds; section 10).</summary>
        Lighting,
        /// <summary>Ground: the landscape cells' materials (r3 + v2 terrain, incl. their steep rock blends) and ground materials on meshes.</summary>
        Terrain,
        /// <summary>Grass and small plants: the vegetation layer's materials (r3), plant meshes, the v2 dense grass and the grass waves.</summary>
        Grass,
        /// <summary>Rock meshes: cliffs, mountains, boulders, cave walls (r3 + v2 triplanar rock materials).</summary>
        Rocks,
        /// <summary>Buildings and props: architecture, wood, planks, objects (r3 + v2 surface materials).</summary>
        Buildings,
        /// <summary>Trees: SpeedTree bark/leaf materials (r3), the v2 foliage terms, leaf rock and the v2 replacement trees (<c>G3TreeV2</c>).</summary>
        Trees,
        /// <summary>Character remaster swapped at runtime (<c>G3CharacterRemaster</c>; hero v2, Docs/remaster-characters.md §5.6).</summary>
        Characters,
        /// <summary>Snow (remaster-snow, Docs/remaster-world.md section 12): a sub-switch of the v2 surfaces (snow BRDF, top-face
        /// snow, footprints; <c>G3SnowV2</c>), default on, its own row above the seven (<see cref="ExtraRow"/>); not a world
        /// part (no material pairs, F7 leaves it).</summary>
        Snow,
    }

    /// <summary>The world parts as flags (the material pairs carry one of them, <see cref="WorldRemasterParts.Classify"/>).</summary>
    [Flags]
    public enum WorldParts
    {
        None = 0,
        Lighting = 1,
        Terrain = 2,
        Grass = 4,
        Rocks = 8,
        Buildings = 16,
        Trees = 32,
        /// <summary>Parts whose materials are G3/Remaster/WorldV2 copies (v2 globals _G3V2Surface etc.).</summary>
        Surfaces = Terrain | Rocks | Buildings,
        All = Lighting | Terrain | Grass | Rocks | Buildings | Trees,
    }

    /// <summary>Minimal key/value store of the switches (PlayerPrefs at runtime, a dictionary in the tests).</summary>
    public interface IRemasterPrefs
    {
        bool Has(string key);
        int Get(string key);
        void Set(string key, int value);
        void Delete(string key);
    }

    /// <summary>
    /// Keys, defaults and texts of the remaster switches. Every switch is off by default (the faithful port), stored in
    /// PlayerPrefs under <see cref="PrefKey"/> and can be forced by environment variables (<see cref="EnvChain"/>):
    /// <c>1/true/on</c> or <c>0/false/off</c>, the first one set wins.
    /// </summary>
    public static class RemasterOptions
    {
        /// <summary>The page's rows, top to bottom (the seven row places of the video layout).</summary>
        public static readonly RemasterFeature[] Rows =
        {
            RemasterFeature.Lighting, RemasterFeature.Terrain, RemasterFeature.Grass, RemasterFeature.Rocks,
            RemasterFeature.Buildings, RemasterFeature.Trees, RemasterFeature.Characters,
        };

        /// <summary>The world parts (F7 / the master switch flips all of them).</summary>
        public static readonly RemasterFeature[] WorldFeatures =
        {
            RemasterFeature.Lighting, RemasterFeature.Terrain, RemasterFeature.Grass, RemasterFeature.Rocks,
            RemasterFeature.Buildings, RemasterFeature.Trees,
        };

        /// <summary>Overrides every runtime switch (not the editor's import switch G3_REMASTER of the characters).</summary>
        public const string SharedEnvVar = "G3_REMASTER_RUNTIME";

        /// <summary>The old single world switch (fix-69 and before); read once by <see cref="Migrate"/>, then deleted.</summary>
        public const string LegacyWorldKey = WorldRemasterSwitch.PrefKey;   // "G3.Remaster.World"

        public static bool IsWorld(RemasterFeature f) => f != RemasterFeature.Characters && f != RemasterFeature.Snow;

        /// <summary>The row above the seven (remaster-snow): placed one 28 px row above Lighting.</summary>
        public const RemasterFeature ExtraRow = RemasterFeature.Snow;

        /// <summary>The Snow row's value: on/off, and "needs Ground/Rocks/Buildings" while it is on but no v2 surface part is.</summary>
        public static string SnowValue(bool on, bool surfacesOn, string language)
        {
            bool ru = Russian(language);
            string v = ru ? (on ? "Вкл." : "Выкл.") : (on ? "On" : "Off");
            if (on && !surfacesOn) v += ru ? " (с Землёй/Скалами)" : " (with Ground/Rocks)";
            return v;
        }

        /// <summary>remaster-gi2 (Docs/remaster-world.md section 17): the "Modern lighting" row, a sub-switch of Lighting two rows
        /// above it (default off; not a world part, F7 leaves it).</summary>
        public static string ModernName(string language) => Russian(language) ? "Современное освещение" : "Modern lighting";

        /// <summary>The Modern lighting row's value: on/off, and "needs Lighting" while it is on but the Lighting part is off.</summary>
        public static string ModernValue(bool on, bool lightingOn, string language)
        {
            bool ru = Russian(language);
            string v = ru ? (on ? "Вкл." : "Выкл.") : (on ? "On" : "Off");
            if (on && !lightingOn) v += ru ? " (с Освещением)" : " (with Lighting)";
            return v;
        }

        /// <summary>fix-171 [owner]: the terrain relief row (G3TerrainRelief, pref G3.Remaster.Relief, default on), next to Ground:
        /// the relief belongs to the landscape cells (Ground), not to the rock meshes.</summary>
        public static string ReliefName(string language) => Russian(language) ? "Рельеф земли" : "Terrain relief";

        /// <summary>The relief row's value: on/off, and "needs Ground" while it is on but the Ground part is off.</summary>
        public static string ReliefValue(bool on, bool groundOn, string language)
        {
            bool ru = Russian(language);
            string v = ru ? (on ? "Вкл." : "Выкл.") : (on ? "On" : "Off");
            if (on && !groundOn) v += ru ? " (с Землёй)" : " (with Ground)";
            return v;
        }

        /// <summary>fix-178: the "Persistent tracks" row (snow prints stay until snowfall covers them; default on; a sub-switch
        /// of Snow, not a world part).</summary>
        public static string TracksName(string language) => Russian(language) ? "Постоянные следы" : "Persistent tracks";

        /// <summary>The Persistent tracks row's value: on/off, and "needs Snow" while it is on but the snow is not running.</summary>
        public static string TracksValue(bool on, bool snowOn, string language)
        {
            bool ru = Russian(language);
            string v = ru ? (on ? "Вкл." : "Выкл.") : (on ? "On" : "Off");
            if (on && !snowOn) v += ru ? " (со Снегом)" : " (with Snow)";
            return v;
        }

        /// <summary>weather-opt: the "Weather" row (G3.Weather.WeatherMode; 0 Original = G3, default; 1 Fair), its pref and variable.</summary>
        public const string WeatherPrefKey = "G3.Remaster.Weather";
        /// <summary><c>G3_REMASTER_WEATHER</c>: 1/on = Fair, 0/off = Original (overrides the stored choice).</summary>
        public const string WeatherEnvVar = "G3_REMASTER_WEATHER";

        public static string WeatherName(string language) => Russian(language) ? "Погода" : "Weather";

        /// <summary>The Weather row's value: 0 Original (G3's chances), 1 Fair (less rain/snow/sandstorm, shorter grey tail).</summary>
        public static string WeatherValue(int mode, string language)
        {
            bool ru = Russian(language);
            return mode == 1 ? (ru ? "Ясная" : "Fair") : (ru ? "Оригинал" : "Original");
        }

        /// <summary>The effective Weather mode (0/1): the variable first, then the stored value (anything but 1 = Original).</summary>
        public static int WeatherMode(string envValue, int stored)
        {
            var f = WorldRemasterSwitch.ParseFlag(envValue);
            if (f != null) return f.Value ? 1 : 0;
            return stored == 1 ? 1 : 0;
        }

        /// <summary>
        /// lighting-new (section 21): the three-way value of the Modern lighting row: 0 Off / 1 Soft (sections 17 + 19) / 2 New
        /// (physically motivated light, auto-exposure, filmic tonemap); "needs Lighting" while on with the Lighting part off.
        /// integrate: <paramref name="giAllowed"/> false (Lighting quality Medium / Low set v2GiStrength 0, fix-171) shows that
        /// Soft / New wait for High: the frame stays the plain remaster light (WorldGi.Modern / New are false), never black.
        /// </summary>
        public static string ModernModeValue(int mode, bool lightingOn, string language, bool giAllowed = true)
        {
            bool ru = Russian(language);
            string v = mode >= 2 ? (ru ? "Новое" : "New") : mode == 1 ? (ru ? "Мягкое" : "Soft") : (ru ? "Выкл." : "Off");
            if (mode > 0 && !lightingOn) v += ru ? " (с Освещением)" : " (with Lighting)";
            else if (mode > 0 && !giAllowed) v += ru ? " (с качеством Высокое)" : " (with High quality)";
            return v;
        }

        /// <summary>The flag of a world feature (<see cref="WorldParts.None"/> for the characters).</summary>
        public static WorldParts Part(RemasterFeature f)
        {
            switch (f)
            {
                case RemasterFeature.Lighting: return WorldParts.Lighting;
                case RemasterFeature.Terrain: return WorldParts.Terrain;
                case RemasterFeature.Grass: return WorldParts.Grass;
                case RemasterFeature.Rocks: return WorldParts.Rocks;
                case RemasterFeature.Buildings: return WorldParts.Buildings;
                case RemasterFeature.Trees: return WorldParts.Trees;
                default: return WorldParts.None;
            }
        }

        static string Name(RemasterFeature f) => f.ToString();   // Lighting, Terrain, ... (stable: used in keys)

        /// <summary><c>G3.Remaster.&lt;Name&gt;</c>: G3.Remaster.Lighting/Terrain/Grass/Rocks/Buildings/Trees/Characters.</summary>
        public static string PrefKey(RemasterFeature f) => "G3.Remaster." + Name(f);

        /// <summary>The feature's own variable: G3_REMASTER_LIGHTING, ..._TREES, ..._CHARACTERS.</summary>
        public static string EnvVar(RemasterFeature f) => "G3_REMASTER_" + Name(f).ToUpperInvariant();

        /// <summary>
        /// Variables in priority order: the own one; for the world parts then <c>G3_REMASTER_WORLD</c> (all world parts),
        /// <c>G3_REMASTER_RUNTIME</c> and the old world switch's <c>G3_REMASTER</c>; for the characters <c>G3_REMASTER_RUNTIME</c>.
        /// </summary>
        public static string[] EnvChain(RemasterFeature f) => IsWorld(f)
            ? new[] { EnvVar(f), WorldRemasterSwitch.EnvVar, SharedEnvVar, WorldRemasterSwitch.SharedEnvVar }
            : new[] { EnvVar(f), SharedEnvVar };

        /// <summary>Every switch starts off: the original look.</summary>
        public const bool Default = false;

        /// <summary>The effective state: own environment variable, then the shared one, then the stored choice.</summary>
        public static bool Resolve(string ownEnv, string sharedEnv, bool stored) =>
            WorldRemasterSwitch.ParseFlag(ownEnv) ?? WorldRemasterSwitch.ParseFlag(sharedEnv) ?? stored;

        /// <summary>The effective state: the first parseable value (<see cref="EnvChain"/> order), else the stored choice.</summary>
        public static bool Resolve(IEnumerable<string> envValues, bool stored)
        {
            if (envValues != null)
                foreach (var v in envValues)
                {
                    var f = WorldRemasterSwitch.ParseFlag(v);
                    if (f != null) return f.Value;
                }
            return stored;
        }

        /// <summary>
        /// One-time migration of the old single world switch: <c>G3.Remaster.World</c> = 1 turns every world part on (a part
        /// that already has its own stored value keeps it, e.g. Trees chosen on fix-69's page); the old key is deleted
        /// either way, so it runs once. Returns the number of part keys written.
        /// </summary>
        public static int Migrate(IRemasterPrefs prefs)
        {
            if (prefs == null || !prefs.Has(LegacyWorldKey)) return 0;
            bool on = prefs.Get(LegacyWorldKey) == 1;
            int n = 0;
            if (on)
                foreach (var f in WorldFeatures)
                {
                    string key = PrefKey(f);
                    if (prefs.Has(key)) continue;
                    prefs.Set(key, 1);
                    n++;
                }
            prefs.Delete(LegacyWorldKey);
            return n;
        }

        /// <summary>rebrand: the demo build's one-time first-run preset (<see cref="ApplyDemoPreset"/>); its value is the preset version.</summary>
        public const string DemoPresetKey = "G3.Remaster.DemoPreset";

        /// <summary>The preset version: raise it to apply a changed preset once more on machines that ran an older one.</summary>
        public const int DemoPresetVersion = 1;

        /// <summary>
        /// rebrand (owner, 2026-10-21): the demo build presents the remaster first. Once per machine (until
        /// <see cref="DemoPresetKey"/> holds <see cref="DemoPresetVersion"/>) every world part is switched on and the
        /// "Modern lighting" sub-switch (<paramref name="modernKey"/>) off; the characters keep their stored choice (default
        /// off). Afterwards the player's own choices are kept. Returns the number of keys written (0: already applied).
        /// </summary>
        public static int ApplyDemoPreset(IRemasterPrefs prefs, string modernKey)
        {
            if (prefs == null || (prefs.Has(DemoPresetKey) && prefs.Get(DemoPresetKey) >= DemoPresetVersion)) return 0;
            int n = 0;
            foreach (var f in WorldFeatures) { prefs.Set(PrefKey(f), 1); n++; }
            if (!string.IsNullOrEmpty(modernKey)) { prefs.Set(modernKey, 0); n++; }
            prefs.Set(DemoPresetKey, DemoPresetVersion);
            return n;
        }

        /// <summary>F7 / the master switch: everything on → all world parts off; anything off → all on.</summary>
        public static bool MasterTarget(WorldParts current) => (current & WorldParts.All) != WorldParts.All;

        /// <summary>The master state: on while any world part is on.</summary>
        public static bool MasterOn(WorldParts current) => (current & WorldParts.All) != WorldParts.None;

        /// <summary>
        /// The procedural v2 replacement trees (Docs/remaster-world.md 10.15) are no longer behind the Trees option (trees-v3,
        /// 10.17: the option switches G3's own trees between their original and their remastered materials). They stay as an
        /// experiment for the runner's JSON A/B only: the world v2 layer active and <c>"v2TreeReplace": true</c> (default false).
        /// </summary>
        public static bool TreesWanted(bool worldV2Active, bool jsonTreeReplace) => worldV2Active && jsonTreeReplace;

        static bool Russian(string language) => language == "Russian";

        /// <summary>The options button and page name.</summary>
        public static string PageName(string language) => Russian(language) ? "Ремастер" : "Remaster";

        /// <summary>The row label.</summary>
        public static string RowName(RemasterFeature f, string language)
        {
            bool ru = Russian(language);
            switch (f)
            {
                case RemasterFeature.Lighting: return ru ? "Освещение и атмосфера" : "Lighting and atmosphere";
                case RemasterFeature.Terrain: return ru ? "Земля" : "Ground";
                case RemasterFeature.Grass: return ru ? "Трава и растения" : "Grass and plants";
                case RemasterFeature.Rocks: return ru ? "Скалы и горы" : "Rocks and mountains";
                case RemasterFeature.Buildings: return ru ? "Здания и предметы" : "Buildings and props";
                case RemasterFeature.Trees: return ru ? "Деревья" : "Trees";
                case RemasterFeature.Snow: return ru ? "Снег и следы" : "Snow and footprints";
                default: return ru ? "Персонажи" : "Characters";   // fix-168: remaster-chars covers NPCs too
            }
        }

        /// <summary>
        /// The row value. <paramref name="available"/> false: the switch is on but the build has no data for it (the set was
        /// not built), so nothing changes on screen.
        /// </summary>
        public static string RowValue(RemasterFeature f, bool on, bool available, string language)
        {
            bool ru = Russian(language);
            string v = ru ? (on ? "Вкл." : "Выкл.") : (on ? "On" : "Off");
            if (on && !available) v += ru ? " (нет данных)" : " (not built)";
            return v;
        }

        /// <summary>The hint line under the rows (the page's reset-warning place).</summary>
        public static string Hint(string language) => Russian(language)
            ? "Ремастер Gothic 3: каждая часть применяется сразу. F7 в игре: весь мир — ремастер или оригинал."
            : "Gothic 3 Remaster: every part applies at once. F7 in game: the whole world, remastered or original.";
    }

    /// <summary>
    /// Which world part a remaster material pair belongs to (Docs/remaster-world.md 10.15.3). The builders store it in the
    /// set (<c>part</c>); sets built before carry 0 and are classified at runtime from the shader, the material name and
    /// the v2 copy's kind/surface flags.
    /// </summary>
    public static class WorldRemasterParts
    {
        // material families (the material half of G3.Formats.Remaster.WorldRemasterCategories.ClassifyMaterial)
        static readonly Regex PlantRe = new Regex(@"^G3_Nature_(Decal|Plant|Tree|OldTree|Leaf|Bush|Misc)|Mushroom|_Leaves?_|_Bark_", RegexOptions.IgnoreCase);
        static readonly Regex RockRe = new Regex(@"_Wall_Stone|Rock|Mountain|Cliff|_Cave|Glacier|_Ice_|_Stone_\d|Lava", RegexOptions.IgnoreCase);
        static readonly Regex ArchRe = new Regex(@"^G3_Architecture_|_Dungeon_|_Building|_Bricks|_WallHouse|_Roof|_Temple|_Ruin", RegexOptions.IgnoreCase);
        static readonly Regex PropRe = new Regex(@"^G3_(Objects?|Items|Weapons)_", RegexOptions.IgnoreCase);
        static readonly Regex GroundRe = new Regex(@"_Ground_|_PlainSand|_Desert_(Ground|Plain|Wave|Dune)|_Snow_|_Grass_|_Dirt|_Gravel|_Mud", RegexOptions.IgnoreCase);
        static readonly Regex TreeRe = new Regex(@"Tree|_Bark", RegexOptions.IgnoreCase);

        /// <summary>
        /// The part of one material: SpeedTree shader → trees, the vegetation layer's shader → grass; else the inventory
        /// category (<paramref name="category"/>, first entry of materials.tsv's list: terrain/water → ground, rock → rocks,
        /// plant → grass (tree-named pieces: trees), architecture/prop/other → buildings); else the v2 copy (kind 1 terrain,
        /// rock surface, building surface); else the material family by name; G3/LitBlend → ground; else buildings.
        /// </summary>
        public static WorldParts Classify(string category, string shader, string material, int v2Kind = 0, bool v2Arch = false, bool v2Rock = false)
        {
            if (shader == "G3/SpeedTree") return WorldParts.Trees;
            if (shader == "G3/Vegetation") return WorldParts.Grass;
            string n = material ?? "";
            if (!string.IsNullOrWhiteSpace(category))
            {
                int comma = category.IndexOf(',');
                string cat = (comma >= 0 ? category.Substring(0, comma) : category).Trim().ToLowerInvariant();
                switch (cat)
                {
                    case "terrain":
                    case "water": return WorldParts.Terrain;
                    case "rock": return WorldParts.Rocks;
                    case "plant": return TreeRe.IsMatch(n) ? WorldParts.Trees : WorldParts.Grass;
                    case "architecture":
                    case "prop":
                    case "other":
                    case "hidden": return WorldParts.Buildings;
                }
            }
            if (v2Kind == 1) return WorldParts.Terrain;
            if (v2Rock) return WorldParts.Rocks;
            if (v2Arch) return WorldParts.Buildings;
            if (PlantRe.IsMatch(n)) return TreeRe.IsMatch(n) ? WorldParts.Trees : WorldParts.Grass;
            if (RockRe.IsMatch(n) && !ArchRe.IsMatch(n)) return WorldParts.Rocks;
            if (ArchRe.IsMatch(n) || PropRe.IsMatch(n)) return WorldParts.Buildings;
            if (GroundRe.IsMatch(n) || shader == "G3/LitBlend") return WorldParts.Terrain;
            return WorldParts.Buildings;
        }

        /// <summary>A stored part value when it is exactly one world part, else null (0 = set built before fix-71).</summary>
        public static WorldParts? Stored(int part)
        {
            if (part <= 0 || (part & (part - 1)) != 0 || ((WorldParts)part & ~WorldParts.All) != 0) return null;
            return (WorldParts)part;
        }

        /// <summary>The flags as lower-case names ("lighting, terrain") or "none".</summary>
        public static string Describe(WorldParts parts)
        {
            if ((parts & WorldParts.All) == WorldParts.None) return "none";
            var l = new List<string>();
            foreach (var p in new[] { WorldParts.Lighting, WorldParts.Terrain, WorldParts.Grass, WorldParts.Rocks, WorldParts.Buildings, WorldParts.Trees })
                if ((parts & p) != 0) l.Add(p.ToString().ToLowerInvariant());
            return string.Join(", ", l);
        }
    }
}
