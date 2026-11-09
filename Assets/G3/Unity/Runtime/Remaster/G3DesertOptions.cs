using System;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// opts-1 (Docs/controls-options.md §9): the stored "World details" choices (<see cref="DesertOptions"/>) at runtime:
    /// PlayerPrefs, environment variables first. <see cref="G3WorldRemasterLook"/> lays them over the json (after the
    /// lighting quality) whenever it derives <see cref="WorldRemasterLook.Current"/>; a change here re-derives and
    /// re-applies only the systems involved (sand, print grid, desert atmosphere), live. The dunes row is
    /// <see cref="G3TerrainRelief.SetDunes"/>.
    /// </summary>
    public static class G3DesertOptions
    {
        sealed class Prefs : IRemasterPrefs
        {
            public bool Has(string key) => PlayerPrefs.HasKey(key);
            public int Get(string key) => PlayerPrefs.GetInt(key, 0);
            public void Set(string key, int value) => PlayerPrefs.SetInt(key, value);
            public void Delete(string key) => PlayerPrefs.DeleteKey(key);
        }

        static DesertChoices _choices;

        /// <summary>The effective choices (a copy: change them with the setters).</summary>
        public static DesertChoices Choices => Current.Copy();

        static DesertChoices Current
        {
            get
            {
                if (_choices == null)
                {
                    try { _choices = DesertOptions.Load(new Prefs(), Environment.GetEnvironmentVariable); }
                    catch (Exception) { _choices = new DesertChoices(); }   // PlayerPrefs off the main thread / before start
                }
                return _choices;
            }
        }

        /// <summary>The json with the choices on top (used by <see cref="G3WorldRemasterLook"/>).</summary>
        public static WorldLookSettings ApplyTo(WorldLookSettings s) => DesertOptions.Apply(s, Current);

        public static SandLook Sand => Current.Sand;
        public static bool SandFx => Current.SandFx;
        public static bool Atmo => Current.Atmo;
        public static bool ArchSand => Current.ArchSand;

        /// <summary>The saturation row's step: the chosen one, else the json's value (grade-1's field; 0 without it).</summary>
        public static int SaturationSteps
        {
            get
            {
                if (Current.Saturation != null) return Current.Saturation.Value;
                var f = DesertOptions.SaturationField;
                return f != null ? DesertOptions.SaturationSteps((float)f.GetValue(G3WorldRemasterLook.JsonSettings)) : 0;
            }
        }

        public static void SetSand(SandLook v) => Change(c => c.Sand = v);
        public static void SetSandFx(bool on) => Change(c => c.SandFx = on);
        public static void SetAtmo(bool on) => Change(c => c.Atmo = on);
        public static void SetArchSand(bool on) => Change(c => c.ArchSand = on);
        public static void SetSaturation(int steps) => Change(c => c.Saturation = steps);

        static void Change(Action<DesertChoices> edit)
        {
            edit(Current);
            try { DesertOptions.Save(new Prefs(), Current); PlayerPrefs.Save(); } catch (Exception e) { Debug.LogWarning("[Remaster] details not stored: " + e.Message); }
            G3WorldRemasterLook.ReapplyOptions();
            Debug.Log($"[Remaster] details: sand {Current.Sand}, wind+dust {Current.SandFx}, atmo {Current.Atmo}, arch sand {Current.ArchSand}, " +
                      $"saturation {(Current.Saturation?.ToString() ?? "json")} | {G3SandV2.Describe()} | {G3DesertAtmo.Describe()}");
        }
    }
}
