using G3.Unity;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Myrtana.Unity
{
    /// <summary>
    /// Applies the menu's <see cref="PortOptions"/> (player feedback items 5, 12, 15) at start, after every change and
    /// when a session binds:
    /// <list type="bullet">
    /// <item>field of view → <see cref="G3PlayerCamera.FovOffset"/>; camera in windows → <see cref="G3PlayerCamera.OriginalWindowCamera"/> (fix-122);</item>
    /// <item>upscaling → the URP asset's render scale and upscaling filter (FSR 1 / STP), restored on quit;</item>
    /// <item>music → the session's <c>MusicSituationRule.ThreatOnly</c> and <see cref="G3MusicDirector.SmoothTransitions"/>;</item>
    /// <item>companions → <c>PlayerParty.FollowsHeroOnTeleport</c> and <see cref="CompanionMapDots.Enabled"/>;</item>
    /// <item>turn keys → <see cref="G3Player.TurnKeysOrbitCamera"/> (fix-73);</item>
    /// <item>decorative objects → <see cref="PlayerInteraction.FocusDecorativeObjects"/> (fix-79);</item>
    /// <item>cooking fires → <see cref="PlayerInteraction.CookOnAnyFire"/> (fix-88, [owner]);</item>
    /// <item>shield against beasts → the session's <c>CombatService.Port.ShieldBlocksCreatures</c> (fix-109, [owner]);</item>
    /// <item>Continue button → <see cref="GameMenu.ContinueOffered"/> (fix-138, [owner]);</item>
    /// <item>late trophies → the session's <c>NpcLoot.LateTrophies</c> (fix-143, [owner]);</item>
    /// <item>crash recovery → <see cref="SaveGameController.CrashRecoveryEnabled"/> (fix-155, [owner]).</item>
    /// <item>NPC shout subtitles → <see cref="NpcReactionView.NpcShoutSubtitles"/> (fix-156, [owner]).</item>
    /// <item>freeing by force → the session's <c>ForcedReleases.Enabled</c> (fix-161, [owner]; switching on, also at a load,
    /// re-judges release quests the kill failed).</item>
    /// <item>forgive accidental hits → the session's <c>CombatService.Port.ForgiveAccidentalHits</c> (fix-162, [owner]).</item>
    /// <item>show FPS → <see cref="FpsOverlay.Option"/> (fix-172, [owner]).</item>
    /// <item>keep wounds after load → the session's <c>NpcLifeServices.KeepWoundsAfterLoad</c> (fix-175, [owner]).</item>
    /// <item>torch glow → <see cref="G3EffectPlayer.TorchGlow"/> (fix-182, [owner]).</item>
    /// <item>take all chord → <see cref="PlayerInteraction.QuickLootChord"/> (fix-205, [owner]).</item>
    /// <item>arrows finish downed foes → the session's <c>CombatService.Port.RangedFinishesDowned</c> (fix-241, [owner]).</item>
    /// <item>show helmet → <see cref="HeroHelmetView.ShowHelmet"/> (fix-232, [owner]; the hero's helmet view redraws at once).</item>
    /// </list>
    /// </summary>
    public sealed class PortOptionsApplier : ISessionView
    {
        readonly PortOptions _port;
        GameSession _session;
        UniversalRenderPipelineAsset _urp;
        float _baseScale = -1f;
        UpscalingFilterSelection _baseFilter;

        public PortOptionsApplier(PortOptions port)
        {
            _port = port;
            port.Changed += Apply;
            Application.quitting += RestoreAsset;
            Apply();
        }

        public void Bind(GameSession session)
        {
            _session = session;
            Apply();
        }

        public void Apply()
        {
            G3PlayerCamera.FovOffset = _port.FovOffset;
            G3PlayerCamera.OriginalWindowCamera = !_port.StillWindowCamera;
            ApplyUpscaling(_port.Upscaling);
            G3MusicDirector.SmoothTransitions = _port.SmoothMusic;
            CompanionMapDots.Enabled = _port.CompanionMarkers;
            G3Player.TurnKeysOrbitCamera = _port.TurnKeysOrbit;
            PlayerInteraction.FocusDecorativeObjects = !_port.SkipDecorativeFocus;
            PlayerInteraction.CookOnAnyFire = _port.CookOnAnyFire;
            GameMenu.ContinueOffered = _port.ContinueButton;
            SaveGameController.CrashRecoveryEnabled = _port.CrashRecovery;
            NpcReactionView.NpcShoutSubtitles = _port.NpcShoutSubtitles;
            FpsOverlay.Option = _port.ShowFps;
            G3EffectPlayer.TorchGlow = _port.TorchGlow;
            PlayerInteraction.QuickLootChord = _port.QuickLootChord;
            HeroHelmetView.ShowHelmet = _port.ShowHelmet;
            if (_session == null) return;
            _session.Music.ThreatOnly = _port.CombatMusicThreat;
            _session.Party.Members.FollowsHeroOnTeleport = _port.CompanionsFollowTeleport;
            _session.Combat.Port.ShieldBlocksCreatures = _port.ShieldBlocksCreatures;
            _session.Combat.Port.ForgiveAccidentalHits = _port.ForgiveAccidentalHits;
            _session.Combat.Port.RangedFinishesDowned = _port.RangedFinishesDowned;
            if (_session.Items.NpcLoot != null) _session.Items.NpcLoot.LateTrophies = _port.LateTrophies;
            _session.ForcedReleases.Enabled = _port.ForcedReleaseCounts;
            _session.Life.KeepWoundsAfterLoad = _port.KeepWoundsAfterLoad;
        }

        /// <summary>
        /// Render scale and filter of the active URP asset (Unity 6 URP: FidelityFX Super Resolution 1.0 or Spatial-Temporal
        /// Post-processing; STP falls back to automatic on devices without compute shaders). Off restores the asset's own values.
        /// </summary>
        void ApplyUpscaling(UpscalingMode mode)
        {
            _urp ??= GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (_urp == null) return;
            if (_baseScale < 0f)
            {
                _baseScale = _urp.renderScale;
                // fix-168: the world remaster may already have switched the asset to STP (it applies before this view
                // exists); that is not the asset's own filter, else "Off" would put STP back after the remaster let go
                _baseFilter = G3WorldV2.OwnUpscalingFilter(_urp, _urp.upscalingFilter);
                G3StpGuard.Failed += Apply;   // STP broke while rendering: re-apply (STP rows -> FSR)
            }
            if (mode == UpscalingMode.Off)
            {
                _urp.renderScale = _baseScale;
                // the remaster's STP (100 % scale, remaster AA) keeps the asset while it holds it
                _urp.upscalingFilter = G3WorldV2.HoldsStp(_urp) && G3StpGuard.Usable ? UpscalingFilterSelection.STP : _baseFilter;
                return;
            }
            _urp.renderScale = PortOptions.RenderScale(mode);
            bool stp = PortOptions.IsStp(mode);
            if (stp && !G3StpGuard.Usable)
            {
                // fix-168: no STP compute shaders in this build (or STP failed): the same scale with FSR 1
                if (!_stpFallbackLogged) { _stpFallbackLogged = true; Debug.LogWarning("[PortOptions] STP unusable (" + G3StpGuard.Reason + "): upscaling uses FSR"); }
                stp = false;
            }
            _urp.upscalingFilter = stp ? UpscalingFilterSelection.STP : UpscalingFilterSelection.FSR;
        }

        bool _stpFallbackLogged;

        /// <summary>The asset is shared with the editor: its own values come back on quit (as DisplayOptionsApplier's shadows).</summary>
        void RestoreAsset()
        {
            if (_urp == null || _baseScale < 0f) return;
            _urp.renderScale = _baseScale;
            _urp.upscalingFilter = _baseFilter;
        }
    }
}
