using G3.Unity;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Gives <see cref="G3LoadingOverlay"/> (start-up and load-game waits) the original G3 loading screen
    /// (<see cref="G3LoadingScreen"/>) in its own document above every other UI. Without the GUI import the overlay
    /// keeps its plain placeholder.
    /// </summary>
    public sealed class G3LoadingScreenView : IG3LoadingScreenView
    {
        public const int SortingOrder = 1000;

        UIDocument _document;
        G3LoadingScreen _screen;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register() => G3LoadingOverlay.ViewFactory = () => G3Skin.Load() != null ? new G3LoadingScreenView() : null;

        public void Begin()
        {
            if (_document == null)
            {
                _document = G3PanelSettings.CreateDocument("Loading screen", GuiPanelKind.Screen, null, SortingOrder);
                Object.DontDestroyOnLoad(_document.gameObject);
                _screen = new G3LoadingScreen();
                _document.rootVisualElement.Add(_screen);
            }
            else _screen.Begin(System.Environment.TickCount);
            _document.rootVisualElement.style.display = DisplayStyle.Flex;
        }

        public void SetProgress(float progress) => _screen?.SetProgress(progress);

        public void SetOpacity(float alpha)
        {
            if (_document != null) _document.rootVisualElement.style.opacity = alpha;
        }

        public void End()
        {
            if (_document != null) _document.rootVisualElement.style.display = DisplayStyle.None;
        }
    }
}
