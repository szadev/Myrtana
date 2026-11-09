using System;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// <see cref="PortOptions"/> as player preferences (keys <c>Myrtana.Port.*</c>, next to the other options of
    /// <see cref="OptionsPreferences"/>, which stores them with every change). Missing keys keep the original's defaults.
    /// </summary>
    public static class PortOptionsPreferences
    {
        const string Key = "Myrtana.Port.";

        public static void Load(PortOptions port)
        {
            port.FovOffset = PlayerPrefs.GetInt(Key + "FovOffset", 0);
            port.Upscaling = Enum.TryParse(PlayerPrefs.GetString(Key + "Upscaling", nameof(UpscalingMode.Off)), out UpscalingMode mode)
                ? mode : UpscalingMode.Off;
            port.CombatMusicThreat = PlayerPrefs.GetInt(Key + "CombatMusicThreat", 0) != 0;
            port.SmoothMusic = PlayerPrefs.GetInt(Key + "SmoothMusic", 0) != 0;
            port.CompanionsFollowTeleport = PlayerPrefs.GetInt(Key + "CompanionsFollowTeleport", 0) != 0;
            port.CompanionMarkers = PlayerPrefs.GetInt(Key + "CompanionMarkers", 0) != 0;
            port.TurnKeysOrbit = PlayerPrefs.GetInt(Key + "TurnKeysOrbit", 0) != 0;
            port.SkipDecorativeFocus = PlayerPrefs.GetInt(Key + "SkipDecorativeFocus", 0) != 0;
            port.CookOnAnyFire = PlayerPrefs.GetInt(Key + "CookOnAnyFire", 1) != 0;   // [owner] default on (fix-88)
            PlayerPrefs.DeleteKey(Key + "LockedChestMarkers"); // fix-116: the Video row went; the map page has its own filter
            port.ShieldBlocksCreatures = PlayerPrefs.GetInt(Key + "ShieldBlocksCreatures", 0) != 0; // default off = CP 1.75 (fix-109)
            port.StillWindowCamera = PlayerPrefs.GetInt(Key + "StillWindowCamera", 1) != 0; // [owner] default on: no zoom (fix-122)
            port.ContinueButton = PlayerPrefs.GetInt(Key + "ContinueButton", 1) != 0; // [owner] default on (fix-138)
            port.LateTrophies = PlayerPrefs.GetInt(Key + "LateTrophies", 1) != 0; // [owner] default on (fix-143)
            port.CrashRecovery = PlayerPrefs.GetInt(Key + "CrashRecovery", 1) != 0; // [owner] default on (fix-155)
            port.NpcShoutSubtitles = PlayerPrefs.GetInt(Key + "NpcShoutSubtitles", 1) != 0; // [owner] default on (fix-156)
            port.ForcedReleaseCounts = PlayerPrefs.GetInt(Key + "ForcedReleaseCounts", 1) != 0; // [owner] default on (fix-161)
            port.ForgiveAccidentalHits = PlayerPrefs.GetInt(Key + "ForgiveAccidentalHits", 1) != 0; // [owner] default on (fix-162)
            port.ShowFps = PlayerPrefs.GetInt(Key + "ShowFps", 0) != 0; // [owner] default off (fix-172)
            port.KeepWoundsAfterLoad = PlayerPrefs.GetInt(Key + "KeepWoundsAfterLoad", 1) != 0; // [owner] default on (fix-175)
            port.TorchGlow = PlayerPrefs.GetInt(Key + "TorchGlow", 0) != 0; // [owner] default off (fix-182)
            port.QuickLootChord = PlayerPrefs.GetInt(Key + "QuickLootChord", 1) != 0; // [owner] default on (fix-205)
            port.ShowHelmet = PlayerPrefs.GetInt(Key + "ShowHelmet", 1) != 0; // [owner] default on = G3 (fix-232)
            port.RangedFinishesDowned = PlayerPrefs.GetInt(Key + "RangedFinishesDowned", 1) != 0; // [owner] default on (fix-241)
        }

        /// <summary>Called by <see cref="OptionsPreferences"/> before its PlayerPrefs.Save.</summary>
        public static void Save(PortOptions port)
        {
            PlayerPrefs.SetInt(Key + "FovOffset", port.FovOffset);
            PlayerPrefs.SetString(Key + "Upscaling", port.Upscaling.ToString());
            PlayerPrefs.SetInt(Key + "CombatMusicThreat", port.CombatMusicThreat ? 1 : 0);
            PlayerPrefs.SetInt(Key + "SmoothMusic", port.SmoothMusic ? 1 : 0);
            PlayerPrefs.SetInt(Key + "CompanionsFollowTeleport", port.CompanionsFollowTeleport ? 1 : 0);
            PlayerPrefs.SetInt(Key + "CompanionMarkers", port.CompanionMarkers ? 1 : 0);
            PlayerPrefs.SetInt(Key + "TurnKeysOrbit", port.TurnKeysOrbit ? 1 : 0);
            PlayerPrefs.SetInt(Key + "SkipDecorativeFocus", port.SkipDecorativeFocus ? 1 : 0);
            PlayerPrefs.SetInt(Key + "CookOnAnyFire", port.CookOnAnyFire ? 1 : 0);
            PlayerPrefs.SetInt(Key + "ShieldBlocksCreatures", port.ShieldBlocksCreatures ? 1 : 0);
            PlayerPrefs.SetInt(Key + "StillWindowCamera", port.StillWindowCamera ? 1 : 0);
            PlayerPrefs.SetInt(Key + "ContinueButton", port.ContinueButton ? 1 : 0);
            PlayerPrefs.SetInt(Key + "LateTrophies", port.LateTrophies ? 1 : 0);
            PlayerPrefs.SetInt(Key + "CrashRecovery", port.CrashRecovery ? 1 : 0);
            PlayerPrefs.SetInt(Key + "NpcShoutSubtitles", port.NpcShoutSubtitles ? 1 : 0);
            PlayerPrefs.SetInt(Key + "ForcedReleaseCounts", port.ForcedReleaseCounts ? 1 : 0);
            PlayerPrefs.SetInt(Key + "ForgiveAccidentalHits", port.ForgiveAccidentalHits ? 1 : 0);
            PlayerPrefs.SetInt(Key + "ShowFps", port.ShowFps ? 1 : 0);
            PlayerPrefs.SetInt(Key + "KeepWoundsAfterLoad", port.KeepWoundsAfterLoad ? 1 : 0);
            PlayerPrefs.SetInt(Key + "TorchGlow", port.TorchGlow ? 1 : 0);
            PlayerPrefs.SetInt(Key + "QuickLootChord", port.QuickLootChord ? 1 : 0);
            PlayerPrefs.SetInt(Key + "ShowHelmet", port.ShowHelmet ? 1 : 0);
            PlayerPrefs.SetInt(Key + "RangedFinishesDowned", port.RangedFinishesDowned ? 1 : 0);
        }
    }
}
