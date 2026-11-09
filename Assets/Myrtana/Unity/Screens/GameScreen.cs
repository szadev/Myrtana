using System.Collections.Generic;
using Myrtana.Session;
using Myrtana.Unity.Ui.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// A full-screen page over the game (character, quest log, inventory, trade). The layout comes from a UXML template
    /// in Resources/Myrtana/Screens (the plain look) or from the original G3 layouts (Screens/G3); the subclass only
    /// fills it from a view model.
    /// </summary>
    public abstract class GameScreen
    {
        const string TemplateFolder = "Myrtana/Screens/";

        protected GameScreen(VisualElement hudRoot, string template)
        {
            var tree = Resources.Load<VisualTreeAsset>(TemplateFolder + template);
            var container = tree.Instantiate();
            container.style.position = Position.Absolute;
            container.style.left = 0;
            container.style.top = 0;
            container.style.right = 0;
            container.style.bottom = 0;
            container.pickingMode = PickingMode.Ignore;
            hudRoot.Add(container);
            Root = container;
            Root.style.display = DisplayStyle.None;
        }

        /// <summary>A page whose tree the subclass built itself (the G3 skin); it is added to <paramref name="parent"/> hidden.</summary>
        protected GameScreen(VisualElement parent, VisualElement page)
        {
            parent.Add(page);
            Root = page;
            Root.style.display = DisplayStyle.None;
        }

        protected VisualElement Root { get; }
        protected GameSession Session { get; private set; }

        bool _open;
        G3Fade _fade;

        /// <summary>Open (it may still be fading in); false as soon as it closes, while it fades out.</summary>
        public bool IsOpen => _open;

        /// <summary>G3 fades its pages in and out over 200 ms (gCHUDPage::Open/Close, AnimateWindow AW_BLEND).</summary>
        G3Fade Fade => _fade ??= new G3Fade(Root);

        public void Open(GameSession session)
        {
            Session = session;
            _open = true;
            Root.BringToFront(); // above a page that is still fading out
            Fade.Show(true);
            OnOpen();
            Render();
        }

        public void Close()
        {
            if (!IsOpen) return;
            _open = false;
            Fade.Show(false);
            OnClose();
            Session = null;
        }

        /// <summary>Rebuilds the content from the current state (after a change or a language switch).</summary>
        /// <summary>
        /// What the page is to the HUD (G3 OnPageChanged): pages 0–4 (inventory, spells, documents/map, character, quests)
        /// are sheets with the page sidebar; trade, loot and the crafting stations take the screen alone.
        /// </summary>
        public virtual Myrtana.Ui.Skin.HudPage HudPage => Myrtana.Ui.Skin.HudPage.Sheet;

        public abstract void Render();

        /// <summary>Called every frame while open (keyboard navigation).</summary>
        public virtual void Tick() { }

        /// <summary>
        /// The page uses this key itself while open, so a page key bound to it does not close the page (G3: the page's
        /// list controls see a key before the session keys; e.g. Backspace on the inventory list).
        /// </summary>
        public virtual bool ConsumesKey(UnityEngine.InputSystem.Key key) => false;

        /// <summary>
        /// Escape while the page is open: true when the page used it itself (a modal of the page, e.g. the amount dialog,
        /// which G3 runs as a modal loop where Escape is IDCANCEL), so the page stays open.
        /// </summary>
        public virtual bool HandleEscape() => false;

        /// <summary>gamepad-1: the entries of the pad hint bar on this page (Docs/gamepad.md §4).</summary>
        public virtual G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Generic;

        /// <summary>gamepad-1: where the pad hint bar goes (the page's root).</summary>
        internal VisualElement HintHost => Root;

        protected virtual void OnOpen() { }
        protected virtual void OnClose() { }

        protected static void AddSection(VisualElement parent, string title, IEnumerable<LabelledValue> rows)
        {
            parent.Add(Label(title, "screen-section-title"));
            foreach (var row in rows)
            {
                var line = new VisualElement();
                line.AddToClassList("screen-row");
                line.Add(Label(row.Label, "screen-row-label"));
                line.Add(Label(row.Value, "screen-row-value"));
                parent.Add(line);
            }
        }

        protected static Label Label(string text, string cssClass)
        {
            var label = new Label(text);
            label.AddToClassList(cssClass);
            return label;
        }
    }
}
