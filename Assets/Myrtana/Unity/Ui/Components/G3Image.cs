using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// A packed G3 image (view background, HUD icon, item icon) by its original name. View panels are complete bitmaps
    /// at their final 1024×768-space size, so <see cref="UseNativeSize"/> gives the original panel size.
    /// </summary>
    public class G3Image : VisualElement
    {
        string _image;

        public G3Image() { AddToClassList("g3-image"); pickingMode = PickingMode.Ignore; }

        public G3Image(string imageName, bool nativeSize = false) : this()
        {
            Image = imageName;
            if (nativeSize) UseNativeSize();
        }

        /// <summary>Image name as in layouts and templates ("G3_HUD_View_Talk_Back.tga", "IT_Apple"); null clears it.</summary>
        public string Image
        {
            get => _image;
            set
            {
                _image = value;
                SetSprite(string.IsNullOrEmpty(value) ? null : G3Skin.Load()?.Sprite(value));
            }
        }

        public void SetSprite(Sprite sprite)
        {
            style.backgroundImage = sprite != null ? new StyleBackground(sprite) : new StyleBackground(StyleKeyword.None);
        }

        /// <summary>Sets width/height to the image size from the index (reference pixels).</summary>
        public G3Image UseNativeSize()
        {
            var info = G3Skin.Load()?.Catalog.Image(_image);
            if (info != null)
            {
                style.width = info.Width;
                style.height = info.Height;
            }
            return this;
        }
    }
}
