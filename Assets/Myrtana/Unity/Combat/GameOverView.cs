using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Game over on the player's death (Docs/combat.md §5.2): a dark overlay with G3's "The End" (GO_GameOver) over the
    /// HUD. The game stays where it is; the game menu (Esc) loads a save, which hides the overlay again.
    /// </summary>
    public sealed class GameOverView : ISessionView
    {
        const string TextKey = "GO_GameOver";

        readonly VisualElement _overlay;
        readonly Label _label;
        GameSession _session;

        public GameOverView(VisualElement hudRoot)
        {
            _overlay = new VisualElement { name = "game-over", pickingMode = PickingMode.Ignore };
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = _overlay.style.right = _overlay.style.top = _overlay.style.bottom = 0;
            _overlay.style.backgroundColor = new Color(0, 0, 0, 0.55f);
            _overlay.style.justifyContent = Justify.Center;
            _overlay.style.alignItems = Align.Center;
            _label = new Label { pickingMode = PickingMode.Ignore };
            _label.AddToClassList("hud-message"); // the HUD's own message look
            _label.style.position = Position.Relative;
            _label.style.top = StyleKeyword.Auto;
            _label.style.fontSize = 48;
            _overlay.Add(_label);
            _overlay.style.display = DisplayStyle.None;
            hudRoot.Add(_overlay);
        }

        public bool Shown => _overlay.style.display == DisplayStyle.Flex;

        public void Bind(GameSession session)
        {
            _session = session;
            Hide();
        }

        public void Show()
        {
            _label.text = _session != null && _session.Localizer.TryText(new TextId(TextKey), out var text) ? text : "The End";
            _overlay.style.display = DisplayStyle.Flex;
        }

        public void Hide() => _overlay.style.display = DisplayStyle.None;
    }
}
