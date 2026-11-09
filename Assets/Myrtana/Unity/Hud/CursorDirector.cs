using System.Collections.Generic;
using G3.Unity;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// The one place that decides the cursor (fix-87): every frame, after the pages, menus and the dialog updated, it reads
    /// what is on screen into a <see cref="CursorUi"/>, asks <see cref="CursorPolicy"/> and applies the answer through
    /// <see cref="G3Cursor"/>. Nothing else writes <see cref="Cursor.visible"/> or <see cref="Cursor.lockState"/>.
    /// Before the game is wired (content loading) the cursor is hidden like under G3's loading screen.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class CursorDirector : MonoBehaviour
    {
        GameBootstrap _boot;
        G3Player _player;
        GameStartFlow _flow;
        GameMenu _menu;
        GameScreens _screens;
        DialogView _dialog;
        StoryCutsceneView _cutscene;
        G3TutorialView _tutorial;
        bool _wired, _refocused;

        /// <summary>The policy's answer of the last frame (dev runner probe <c>cursor</c>).</summary>
        public static CursorRole Mode { get; private set; } = CursorRole.Hidden;

        static readonly List<CursorRole> s_seen = new List<CursorRole>();
        const int SeenMax = 32;

        public void Init(GameBootstrap boot, G3Player player)
        {
            _boot = boot;
            _player = player;
        }

        /// <summary>The game's views, once they exist (GameBootstrap.StartGame).</summary>
        public void Wire(GameStartFlow flow, GameMenu menu, GameScreens screens, DialogView dialog, StoryCutsceneView cutscene,
                         G3TutorialView tutorial)
        {
            _flow = flow;
            _menu = menu;
            _screens = screens;
            _dialog = dialog;
            _cutscene = cutscene;
            _tutorial = tutorial;
            _wired = true;
        }

        /// <summary>What is on screen now.</summary>
        public CursorUi Read()
        {
            var controls = _boot != null ? _boot.Controls : null;
            return new CursorUi
            {
                Loading = !_wired || LoadingHold.IsLoading || _flow != null && _flow.InLoading,
                Video = _flow != null && _flow.InVideo,
                Slideshow = _cutscene != null && _cutscene.SlideshowShown,
                Menu = _menu != null && _menu.State.IsOpen,
                Page = _screens != null && _screens.IsPageOpen,
                DialogChoices = _dialog != null && _dialog.ChoicesShown,
                Tip = _tutorial != null && _tutorial.IsOpen,
                Dev = _player != null && _player.IsFlying || DevConsoleOpen,
                ControlsLocked = controls != null && controls.IsLocked,
            };
        }

        static bool DevConsoleOpen
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
                return Dev.DevConsole.IsOpen;
#else
                return false;
#endif
            }
        }

        void LateUpdate()
        {
            var mode = CursorPolicy.Evaluate(Read());
            if (mode != Mode || s_seen.Count == 0) Note(mode);
            Mode = mode;
            var mouse = Mouse.current;
            bool recapture = _refocused || mouse != null && mouse.leftButton.wasPressedThisFrame || global::G3.Unity.G3Pad.AnyPressed;
            _refocused = false;
            // gamepad-1: the pointer hides while the pad is the device in use (a mouse move brings it back)
            G3Cursor.Apply(CursorPolicy.Visible(mode) && !global::G3.Unity.G3Pad.PadIsLast, CursorPolicy.Locked(mode), CursorPolicy.Captured(mode), recapture);
        }

        void OnApplicationFocus(bool focus)
        {
            if (focus) _refocused = true; // the window is back: take the lock again
        }

        void OnDestroy() => G3Cursor.Release();

        static void Note(CursorRole mode)
        {
            if (s_seen.Count >= SeenMax) s_seen.RemoveAt(0);
            s_seen.Add(mode);
        }

        /// <summary>Dev runner: forget the modes seen so far (the current one stays as the first).</summary>
        public static void ResetSeen()
        {
            s_seen.Clear();
            s_seen.Add(Mode);
        }

        /// <summary>Dev runner: the modes seen since <see cref="ResetSeen"/>, "gameplay,hidden,pointer".</summary>
        public static string Seen()
        {
            var names = new string[s_seen.Count];
            for (int i = 0; i < names.Length; i++) names[i] = CursorPolicy.Name(s_seen[i]);
            return string.Join(",", names);
        }
    }
}
