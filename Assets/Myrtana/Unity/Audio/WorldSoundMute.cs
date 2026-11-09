using G3.Unity;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Silences the world (the mixer's Ambience and Effects groups) while the game is paused by the game menu, the
    /// intro or a loading screen, and on the title screen; music and voices go on. G3 mutes ambience in its menus
    /// (Docs/ui-skin.md open issues) and the sound channel group while the intro video plays. Installs itself
    /// next to <see cref="SessionMusicSituation"/>; does nothing without the mixer.
    /// </summary>
    [AddComponentMenu("Myrtana/World Sound Mute")]
    public sealed class WorldSoundMute : MonoBehaviour
    {
        [SerializeField] bool muted;
        G3MusicSituationOverride _override;
        float _nextSearch;

        void Update()
        {
            // FindAnyObjectByType walks every script instance; in the game scene there is usually no override, so the
            // search is retried twice a second instead of every frame (perf-7).
            if (_override == null && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 0.5f;
                _override = FindAnyObjectByType<G3MusicSituationOverride>();
            }
            bool title = _override != null && _override.situation == TitleMusic.Situation;
            bool mute = GamePause.IsPaused || title;
            if (mute == muted) return;
            muted = mute;
            G3AudioMixer.SetMuted(G3AudioBus.Ambience, mute);
            G3AudioMixer.SetMuted(G3AudioBus.Effects, mute);
        }

        void OnDisable()
        {
            if (!muted) return;
            muted = false;
            G3AudioMixer.SetMuted(G3AudioBus.Ambience, false);
            G3AudioMixer.SetMuted(G3AudioBus.Effects, false);
        }
    }
}
