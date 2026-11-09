using System;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// G3 push button: theme <c>button_button</c> (stone plate tinted brown #3C2819 α200 → α255 on hover, glow front
    /// layer), centred text in parchment → #FFEBDC on hover. Menu buttons are 252×32 with 19 px bold text. An optional
    /// icon image replaces the text (sidebar close button, trash buttons).
    /// </summary>
    public class G3Button : Button
    {
        public const string Part = "button_button";

        public G3Image Icon { get; private set; }

        public G3Button() : this(null, null) { }

        public G3Button(string text, Action clicked = null) : base(clicked)
        {
            this.text = text ?? string.Empty;
            AddToClassList(ThemeUssWriter.PartClass(Part));
            AddToClassList("g3-button");
            var front = new VisualElement { pickingMode = PickingMode.Ignore, name = "front" };
            front.AddToClassList(ThemeUssWriter.FrontClass);
            Add(front);
            G3Text.Font(this, 19, bold: true);
        }

        /// <summary>Shows an image (e.g. "G3_HUD_Icon_Page_Exit.tga") instead of the theme plate.</summary>
        public G3Button WithIcon(string imageName)
        {
            RemoveFromClassList(ThemeUssWriter.PartClass(Part));
            AddToClassList("g3-button--icon");
            Icon = new G3Image(imageName);
            Icon.AddToClassList("g3-button__icon");
            Add(Icon);
            return this;
        }
    }
}
