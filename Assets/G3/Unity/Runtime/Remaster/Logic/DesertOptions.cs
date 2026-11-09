using System;
using System.Reflection;

namespace G3.Unity
{
    /// <summary>opts-1: the "Desert sand" row's three looks (Docs/controls-options.md §9).</summary>
    public enum SandLook
    {
        /// <summary>G3's sand ground (json <c>sand</c> false: every sand global zero).</summary>
        Original = 0,
        /// <summary>sand-2: the procedural sand (grain, ripples, glints; <c>sandScan</c> 0).</summary>
        Simple = 1,
        /// <summary>sand-3: the scan-based sand (the json's own <c>sandScan</c>; default).</summary>
        Scans = 2,
    }

    /// <summary>The stored choices of the "World details" page (opts-1); defaults = the current look.</summary>
    public sealed class DesertChoices
    {
        public SandLook Sand = SandLook.Scans;
        /// <summary>Sand streamers / wisps (sand-2/3), footstep dust (G3SandDust) and the tent-edge wisps (arch-2).</summary>
        public bool SandFx = true;
        /// <summary>The desert atmosphere and light (atmo-1, section 24 of the options doc; json <c>atmo</c>).</summary>
        public bool Atmo = true;
        /// <summary>arch-2 (25.7): the sandstorm deposit on cloth and ledges; off = pinned at the resting deposit.</summary>
        public bool ArchSand = true;
        /// <summary>grade-1's global <c>saturation</c> in menu steps (<see cref="DesertOptions.SaturationMin"/>..Max);
        /// null = not chosen (the json's value stays).</summary>
        public int? Saturation;

        public DesertChoices Copy() => (DesertChoices)MemberwiseClone();
    }

    /// <summary>
    /// opts-1 (Docs/controls-options.md §9): the "World details" page's switches on top of G3WorldLook.json — the desert
    /// sand look, its wind and dust, the desert atmosphere, the storm sand on buildings and (when grade-1's field exists)
    /// the saturation. Every switch only flips the json keys the systems already have for their A/B (off = the look before
    /// them); the dunes row is <c>G3TerrainRelief.SetDunes</c> (its own pref). Stored as ints under <c>G3.Remaster.*</c>;
    /// environment variables win (<see cref="Load"/>). Engine-free (G3Tests: DesertOptionsTests).
    /// </summary>
    public static class DesertOptions
    {
        public const string SandKey = "G3.Remaster.Sand";
        public const string SandFxKey = "G3.Remaster.SandFx";
        public const string AtmoKey = "G3.Remaster.DesertAtmo";
        public const string ArchSandKey = "G3.Remaster.ArchSand";
        public const string SaturationKey = "G3.Remaster.Saturation";

        /// <summary>G3SandV2's A/B variable: 0/off = Original (G3's sand), 1/on = the stored look (Scans if Original was stored).</summary>
        public const string SandEnvVar = "G3_REMASTER_SAND";
        public const string SandFxEnvVar = "G3_REMASTER_SANDFX";
        /// <summary>G3DesertAtmo's A/B variable.</summary>
        public const string AtmoEnvVar = "G3_REMASTER_ATMO";
        public const string ArchSandEnvVar = "G3_REMASTER_ARCHSAND";

        /// <summary>The saturation row: steps of 0.1 on grade-1's -1..1 offset, shown as -50 %..+50 %.</summary>
        public const int SaturationMin = -5, SaturationMax = 5;
        public const float SaturationStep = 0.1f;

        /// <summary>grade-1's json field (<c>saturation</c>, -1..1 offset); null on a build without it (the row is hidden).</summary>
        public static readonly FieldInfo SaturationField =
            typeof(WorldLookSettings).GetField("saturation", BindingFlags.Public | BindingFlags.Instance) is FieldInfo f && f.FieldType == typeof(float) ? f : null;

        public static bool HasSaturation => SaturationField != null;

        /// <summary>Reads the stored choices; a variable (<paramref name="env"/>: name → value, may be null) wins over the store.</summary>
        public static DesertChoices Load(IRemasterPrefs prefs, Func<string, string> env = null)
        {
            var c = new DesertChoices();
            if (prefs != null)
            {
                if (prefs.Has(SandKey)) c.Sand = ToLook(prefs.Get(SandKey));
                if (prefs.Has(SandFxKey)) c.SandFx = prefs.Get(SandFxKey) != 0;
                if (prefs.Has(AtmoKey)) c.Atmo = prefs.Get(AtmoKey) != 0;
                if (prefs.Has(ArchSandKey)) c.ArchSand = prefs.Get(ArchSandKey) != 0;
                if (prefs.Has(SaturationKey)) c.Saturation = ClampSat(prefs.Get(SaturationKey));
            }
            if (env != null)
            {
                var sand = WorldRemasterSwitch.ParseFlag(env(SandEnvVar));
                if (sand == false) c.Sand = SandLook.Original;
                else if (sand == true && c.Sand == SandLook.Original) c.Sand = SandLook.Scans;
                c.SandFx = WorldRemasterSwitch.ParseFlag(env(SandFxEnvVar)) ?? c.SandFx;
                c.Atmo = WorldRemasterSwitch.ParseFlag(env(AtmoEnvVar)) ?? c.Atmo;
                c.ArchSand = WorldRemasterSwitch.ParseFlag(env(ArchSandEnvVar)) ?? c.ArchSand;
            }
            return c;
        }

        /// <summary>Stores every choice (an unset saturation stays unset).</summary>
        public static void Save(IRemasterPrefs prefs, DesertChoices c)
        {
            if (prefs == null || c == null) return;
            prefs.Set(SandKey, (int)c.Sand);
            prefs.Set(SandFxKey, c.SandFx ? 1 : 0);
            prefs.Set(AtmoKey, c.Atmo ? 1 : 0);
            prefs.Set(ArchSandKey, c.ArchSand ? 1 : 0);
            if (c.Saturation != null) prefs.Set(SaturationKey, ClampSat(c.Saturation.Value));
            else prefs.Delete(SaturationKey);
        }

        public static SandLook ToLook(int v) => v <= 0 ? SandLook.Original : v == 1 ? SandLook.Simple : SandLook.Scans;

        static int ClampSat(int v) => Math.Max(SaturationMin, Math.Min(SaturationMax, v));

        /// <summary>The json value as a menu step (rounded, clamped).</summary>
        public static int SaturationSteps(float value) => ClampSat((int)Math.Round(value / SaturationStep));

        /// <summary>
        /// A copy of <paramref name="s"/> with the choices: Original sand = <c>sand</c> false; Simple = <c>sandScan</c> 0
        /// (the sand-2 look); no wind and dust = <c>sandWisps</c>, <c>sandDust</c>, <c>archSandWisps</c> 0; no desert
        /// atmosphere = <c>atmo</c> false; no storm sand = the deposit pinned at <c>archSandRest</c> (a pinned json deposit
        /// stays); a chosen saturation sets grade-1's <c>saturation</c>. Defaults change nothing.
        /// </summary>
        public static WorldLookSettings Apply(WorldLookSettings s, DesertChoices c)
        {
            var o = (s ?? WorldLookSettings.Default).Clamped();
            if (c == null) return o;
            if (c.Sand == SandLook.Original) o.sand = false;
            else if (c.Sand == SandLook.Simple) o.sandScan = 0f;
            if (!c.SandFx) { o.sandWisps = 0f; o.sandDust = 0f; o.archSandWisps = 0; }
            if (!c.Atmo) o.atmo = false;
            if (!c.ArchSand && o.archSandDeposit < 0) o.archSandDeposit = o.archSandRest;
            if (c.Saturation != null && SaturationField != null)
                SaturationField.SetValue(o, c.Saturation.Value * SaturationStep);
            return o;
        }

        // ------------------------------------------------------------------ texts

        static bool Russian(string language) => language == "Russian";
        static string OnOff(bool on, bool ru) => ru ? (on ? "Вкл." : "Выкл.") : (on ? "On" : "Off");
        static string Needs(string v, bool on, bool ok, string ru, string en, bool r) => on && !ok ? v + (r ? " (" + ru + ")" : " (" + en + ")") : v;

        /// <summary>The options button (on the Remaster page) and this page's name.</summary>
        public static string PageName(string language) => Russian(language) ? "Детали мира" : "World details";

        public static string SandName(string language) => Russian(language) ? "Песок пустыни" : "Desert sand";

        /// <summary>The sand row's value; "needs Ground" while not Original with the Ground part off.</summary>
        public static string SandValue(SandLook look, bool groundOn, string language)
        {
            bool ru = Russian(language);
            string v = look == SandLook.Scans ? (ru ? "Сканы" : "Scans") : look == SandLook.Simple ? (ru ? "Простой" : "Simple") : (ru ? "Выкл. (оригинал)" : "Off (original)");
            return Needs(v, look != SandLook.Original, groundOn, "с Землёй", "with Ground", ru);
        }

        public static string SandFxName(string language) => Russian(language) ? "Ветер и пыль песка" : "Sand wind and dust";

        public static string SandFxValue(bool on, bool sandOn, string language)
        {
            bool ru = Russian(language);
            return Needs(OnOff(on, ru), on, sandOn, "с Песком", "with Sand", ru);
        }

        public static string DunesName(string language) => Russian(language) ? "Дюны" : "Dunes";

        public static string DunesValue(bool on, bool reliefOn, string language)
        {
            bool ru = Russian(language);
            return Needs(OnOff(on, ru), on, reliefOn, "с Рельефом", "with Relief", ru);
        }

        public static string AtmoName(string language) => Russian(language) ? "Атмосфера пустыни" : "Desert atmosphere";

        public static string AtmoValue(bool on, bool lightingOn, string language)
        {
            bool ru = Russian(language);
            return Needs(OnOff(on, ru), on, lightingOn, "с Освещением", "with Lighting", ru);
        }

        public static string ArchSandName(string language) => Russian(language) ? "Песок бурь на постройках" : "Storm sand on buildings";

        public static string ArchSandValue(bool on, bool buildingsOn, string language)
        {
            bool ru = Russian(language);
            return Needs(OnOff(on, ru), on, buildingsOn, "со Зданиями", "with Buildings", ru);
        }

        public static string SaturationName(string language) => Russian(language) ? "Насыщенность" : "Saturation";

        /// <summary>"0 %", "+20 %", "-10 %" (the step x 10).</summary>
        public static string SaturationValue(int steps, bool lightingOn, string language)
        {
            bool ru = Russian(language);
            int pct = steps * 10;
            string v = (pct > 0 ? "+" : "") + pct + "%";
            return Needs(v, true, lightingOn, "с Освещением", "with Lighting", ru);
        }

        /// <summary>The hint line of the page.</summary>
        public static string Hint(string language) => Russian(language)
            ? "Пустыня Варанта и цвет: каждая настройка применяется сразу. Выкл. — вид до этих улучшений."
            : "Varant's desert and colour: every row applies at once. Off: the look before these refinements.";
    }
}
