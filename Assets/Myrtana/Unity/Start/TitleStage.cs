using System.Collections.Generic;
using G3.Unity;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Keeps the world out of the start-up logos and the title. G3 has no world behind its main menu: the title shows
    /// the static picture <c>G3_HUD_View_Menu_Main_Back</c> (gCHUDMenuMain2), and a world is loaded only by New Game or
    /// Load. Here the master scene already holds the hero and the streamer, so until the program flow leaves the title:
    /// <list type="bullet">
    /// <item>the streamer loads no cells (<see cref="G3WorldStreamer.LoadOnly"/> = none) and no start-up loading screen
    /// is made for the scene's hero position (Faring);</item>
    /// <item>the camera renders nothing but black (the menu and the videos cover it anyway);</item>
    /// <item>the world is held like behind a loading screen (<see cref="LoadingHold"/>): no game time, no physics, the
    /// world's sounds muted (the title music plays on), the hero's controls locked.</item>
    /// </list>
    /// Created before the streamer's Start when the game will show the title (not the developer quick start).
    /// </summary>
    public sealed class TitleStage : MonoBehaviour
    {
        static readonly HashSet<int> NoCells = new HashSet<int>();

        G3WorldStreamer _streamer;
        LoadingHold _hold;
        Camera _camera;
        int _cullingMask;
        CameraClearFlags _clearFlags;
        Color _background;

        /// <summary>True from the start until the first game is started or loaded.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Takes the world out of view before the streamer starts (call from AfterSceneLoad).</summary>
        public static TitleStage Begin(GameObject host, G3WorldStreamer streamer)
        {
            var stage = host.AddComponent<TitleStage>();
            stage._streamer = streamer;
            if (streamer != null)
            {
                streamer.loadingScreenAtStart = false; // the title comes first; the area waits come with New Game / Load
                streamer.LoadOnly = NoCells;
            }
            stage._camera = Camera.main;
            if (stage._camera != null)
            {
                stage._cullingMask = stage._camera.cullingMask;
                stage._clearFlags = stage._camera.clearFlags;
                stage._background = stage._camera.backgroundColor;
                stage._camera.cullingMask = 0;
                stage._camera.clearFlags = CameraClearFlags.SolidColor;
                stage._camera.backgroundColor = Color.black;
            }
            stage.IsActive = true;
            return stage;
        }

        /// <summary>Holds the world (game time, sounds, controls) while the title is up.</summary>
        public void Attach(LoadingHold hold)
        {
            _hold = hold;
            _hold.Title = this; // the streamer's hold on the hero under the title is no load (fix-98: menu pointer)
            if (IsActive) _hold.Acquire(this);
        }

        /// <summary>
        /// A game is being started or loaded: the streamer may load again (the loading screen follows the hero's new
        /// position), the camera renders the world, the hold is released (the flow's loading phase holds it on).
        /// </summary>
        public void End()
        {
            if (!IsActive) return;
            IsActive = false;
            if (_streamer != null && ReferenceEquals(_streamer.LoadOnly, NoCells)) _streamer.LoadOnly = null;
            if (_camera != null)
            {
                _camera.cullingMask = _cullingMask;
                _camera.clearFlags = _clearFlags;
                _camera.backgroundColor = _background;
            }
            _hold?.Release(this);
        }

        void OnDestroy() => End();
    }
}
