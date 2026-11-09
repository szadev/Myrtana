using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Unity.Ui.Components;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// CP's Alternative Balancing switch on the title's main page (<c>IDD_MENU_MAIN_2</c>, Docs/game-start.md): the
    /// heading, "On" left and "Off" right, and two 39×39 picture buttons; the chosen side shows the lit last frame of
    /// <c>G3_HUD_Alternative_Balancing_00…09</c>, the other side the dark first frame. A click animates the change over
    /// the ten frames (G3 does the same). The choice is a <see cref="NewGameOptions"/> setting, used by the next new game.
    /// </summary>
    public sealed class G3MenuBalancing
    {
        const int Frames = 10;
        const float FrameSeconds = 0.05f;
        static readonly TextId Heading = new TextId("HUD_Difficulty_Extended_Content"), On = new TextId("HUD_Slider_Value_On"),
                               Off = new TextId("HUD_Slider_Value_Off");

        readonly NewGameOptions _options;
        readonly VisualElement[] _parts;
        readonly VisualElement _onImage, _offImage;
        readonly IVisualElementScheduledItem _animation;
        float _animationStart = -1f;

        public G3MenuBalancing(VisualElement mainPage, G3MenuView view, NewGameOptions options)
        {
            _options = options;
            view.Label(mainPage, "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_2", Heading);
            view.Label(mainPage, "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_ON_2", On);
            view.Label(mainPage, "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_OFF_2", Off);
            _onImage = mainPage.Q("IDC_STATIC_MENU_ALTERNATIVE_BALANCING_IMAGE_ON_2");
            _offImage = mainPage.Q("IDC_STATIC_MENU_ALTERNATIVE_BALANCING_IMAGE_OFF_2");
            var onButton = mainPage.Q<G3Button>("IDC_BUTTON_MENU_ALTERNATIVE_BALANCING_ON_2");
            var offButton = mainPage.Q<G3Button>("IDC_BUTTON_MENU_ALTERNATIVE_BALANCING_OFF_2");
            if (onButton != null) { onButton.clicked += () => Choose(true); MakeTransparent(onButton); }
            if (offButton != null) { offButton.clicked += () => Choose(false); MakeTransparent(offButton); }
            _parts = new[]
            {
                mainPage.Q("IDC_STATIC_MENU_ALTERNATIVE_BALANCING_2"), mainPage.Q("IDC_STATIC_MENU_ALTERNATIVE_BALANCING_ON_2"),
                mainPage.Q("IDC_STATIC_MENU_ALTERNATIVE_BALANCING_OFF_2"), _onImage, _offImage, onButton, offButton,
            };
            // the buttons lie over the pictures and take the clicks
            onButton?.BringToFront();
            offButton?.BringToFront();
            _animation = mainPage.schedule.Execute(Animate).Every(16);
            _animation.Pause();
            ShowFrames(_options.AlternativeBalancing ? Frames - 1 : 0);
        }

        public void Show(bool visible)
        {
            foreach (var p in _parts) G3MenuView.Show(p, visible);
            if (visible && _animationStart < 0) ShowFrames(_options.AlternativeBalancing ? Frames - 1 : 0);
        }

        void Choose(bool on)
        {
            if (_options.AlternativeBalancing == on) return;
            _options.AlternativeBalancing = on;
            _animationStart = Time.unscaledTime;
            _animation.Resume();
        }

        void Animate()
        {
            int step = Mathf.Min(Frames - 1, (int)((Time.unscaledTime - _animationStart) / FrameSeconds));
            ShowFrames(_options.AlternativeBalancing ? step : Frames - 1 - step);
            if (step < Frames - 1) return;
            _animationStart = -1f;
            _animation.Pause();
        }

        /// <summary><paramref name="onFrame"/> lights the On side; the Off side shows the opposite frame.</summary>
        void ShowFrames(int onFrame)
        {
            SetFrame(_onImage, onFrame);
            SetFrame(_offImage, Frames - 1 - onFrame);
        }

        static void SetFrame(VisualElement image, int frame)
        {
            if (image == null) return;
            var sprite = G3Skin.Load()?.Sprite($"G3_HUD_Alternative_Balancing_{frame:00}.tga");
            image.style.backgroundImage = sprite != null ? new StyleBackground(sprite) : StyleKeyword.None;
        }

        /// <summary>The picture shows through: the button only takes the click.</summary>
        static void MakeTransparent(G3Button button)
        {
            button.RemoveFromClassList(ThemeUssWriter.PartClass(G3Button.Part));
            var front = button.Q("front");
            if (front != null) front.style.display = DisplayStyle.None;
            button.style.backgroundColor = Color.clear;
            button.style.backgroundImage = StyleKeyword.None;
            button.style.borderLeftWidth = button.style.borderRightWidth = button.style.borderTopWidth = button.style.borderBottomWidth = 0;
        }
    }
}
