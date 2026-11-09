using System.Collections.Generic;
using UnityEngine;

namespace Myrtana.Unity.Ui.Skin
{
    /// <summary>Sprites of one igl image group (or the theme crops), keyed by <see cref="Myrtana.Ui.Skin.GuiSkinCatalog.ImageKey"/>.</summary>
    public sealed class G3ImageLibrary : ScriptableObject
    {
        public string group;
        public string[] keys = new string[0];
        public Sprite[] sprites = new Sprite[0];

        Dictionary<string, Sprite> _map;

        public Sprite Get(string key)
        {
            if (_map == null)
            {
                _map = new Dictionary<string, Sprite>(keys.Length);
                for (int i = 0; i < keys.Length && i < sprites.Length; i++) _map[keys[i]] = sprites[i];
            }
            return _map.TryGetValue(key, out var s) ? s : null;
        }

        void OnValidate() => _map = null;
    }
}
