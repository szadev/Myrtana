using System;
using System.Collections.Generic;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>The map filters' states for this install (PlayerPrefs; fix-126, <see cref="MapFilterState"/>).</summary>
    public static class MapFilters
    {
        static MapFilterState _state;

        static MapFilterState State
        {
            get
            {
                if (_state != null) return _state;
                _state = new MapFilterState((key, fallback) => PlayerPrefs.GetInt(key, fallback), (key, value) =>
                {
                    PlayerPrefs.SetInt(key, value);
                    PlayerPrefs.Save();
                });
                _state.Changed += f => Changed?.Invoke(f);
                return _state;
            }
        }

        /// <summary>Raised when a filter changes (the bars' looks and the map layers follow).</summary>
        public static event Action<MapFilter> Changed;

        public static bool IsOn(MapFilter filter) => State.IsOn(filter);

        public static void Set(MapFilter filter, bool on) => State.Set(filter, on);

        public static bool Toggle(MapFilter filter) => State.Toggle(filter);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() // domain reload is off in the editor
        {
            _state = null;
            Changed = null;
        }
    }

    /// <summary>
    /// fix-126 [owner]: the map filter bar inside the map's top-right corner (<see cref="MapFilterBarLayout"/>): G3 icon
    /// buttons on a dark brown plate, a filter that is off has its icon dimmed, the hovered button brightens (G3's icon
    /// hover tint) and its caption shows under the bar (the map pages have no tooltips). A click flips the filter for every
    /// map view (<see cref="MapFilters"/>).
    /// </summary>
    public sealed class MapFilterBar : VisualElement
    {
        static readonly Color HoverBack = new Color(1f, 0.92f, 0.86f, 0.22f), OnBack = new Color(1f, 0.92f, 0.86f, 0.10f);
        static readonly Color Rim = new Color(0.78f, 0.65f, 0.28f, 0.55f);

        readonly Dictionary<MapFilter, G3Button> _buttons = new Dictionary<MapFilter, G3Button>();
        readonly Func<MapFilter, string> _caption;
        readonly Label _captionLabel;
        readonly VisualElement _plate;
        MapFilter? _hovered;

        /// <param name="filters">The view's filters, shown in <see cref="MapFilterBarLayout.Order"/>.</param>
        /// <param name="caption">The caption of a filter in the text language.</param>
        /// <param name="changed">Called after a click flipped a filter (the view redraws).</param>
        public MapFilterBar(IEnumerable<MapFilter> filters, Func<MapFilter, string> caption, Action<MapFilter> changed)
        {
            _caption = caption;
            name = "map-filter-bar";
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.top = MapFilterBarLayout.Inset;
            style.right = MapFilterBarLayout.Inset;
            style.alignItems = Align.FlexEnd;

            _plate = new VisualElement { name = "map-filter-plate", pickingMode = PickingMode.Position };
            _plate.style.flexDirection = FlexDirection.Row;
            float pad = MapFilterBarLayout.Padding;
            _plate.style.paddingLeft = _plate.style.paddingRight = _plate.style.paddingTop = _plate.style.paddingBottom = pad;
            _plate.style.backgroundColor = new Color(MapFilterBarLayout.PlateR, MapFilterBarLayout.PlateG, MapFilterBarLayout.PlateB, MapFilterBarLayout.PlateAlpha);
            _plate.style.borderTopLeftRadius = _plate.style.borderTopRightRadius = _plate.style.borderBottomLeftRadius = _plate.style.borderBottomRightRadius = 3f;
            _plate.style.borderLeftWidth = _plate.style.borderRightWidth = _plate.style.borderTopWidth = _plate.style.borderBottomWidth = 1f;
            _plate.style.borderLeftColor = _plate.style.borderRightColor = _plate.style.borderTopColor = _plate.style.borderBottomColor = Rim;
            Add(_plate);

            var wanted = new HashSet<MapFilter>(filters ?? Array.Empty<MapFilter>());
            var skin = G3Skin.Load();
            foreach (var filter in MapFilterBarLayout.Order)
            {
                if (!wanted.Contains(filter)) continue;
                var f = filter;
                string icon = MapFilterBarLayout.Icon(f);
                G3Button button;
                if (skin?.Sprite(icon) != null) button = new G3Button(null, () => Click(f, changed)).WithIcon(icon);
                else
                {
                    // without the GUI data: the caption's first letter on the plate
                    button = new G3Button(Initial(f), () => Click(f, changed));
                    G3Text.Font(button, 14, bold: true);
                }
                button.name = "map-filter-" + f.ToString().ToLowerInvariant();
                button.focusable = false;
                button.pickingMode = PickingMode.Position;
                button.style.position = Position.Relative;
                button.style.width = button.style.height = MapFilterBarLayout.ButtonSize;
                button.style.marginLeft = _buttons.Count == 0 ? 0f : MapFilterBarLayout.Gap;
                button.style.borderTopLeftRadius = button.style.borderTopRightRadius = button.style.borderBottomLeftRadius = button.style.borderBottomRightRadius = 2f;
                button.RegisterCallback<PointerEnterEvent>(_ => Hover(f));
                button.RegisterCallback<PointerLeaveEvent>(_ => Hover(null));
                _plate.Add(button);
                _buttons[f] = button;
            }

            _captionLabel = new Label { name = "map-filter-caption", pickingMode = PickingMode.Ignore };
            _captionLabel.style.marginTop = 2f;
            _captionLabel.style.color = Color.white;
            _captionLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _captionLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _captionLabel.style.paddingLeft = _captionLabel.style.paddingRight = _captionLabel.style.paddingTop = _captionLabel.style.paddingBottom = 0;
            G3Text.Font(_captionLabel, MapFilterBarLayout.CaptionSize, bold: true);
            G3Text.Shadow(_captionLabel);
            _captionLabel.style.display = DisplayStyle.None;
            Add(_captionLabel);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                MapFilters.Changed -= OnChanged;
                MapFilters.Changed += OnChanged;
                Refresh();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => MapFilters.Changed -= OnChanged);
            Refresh();
        }

        /// <summary>The filters on this bar.</summary>
        public IEnumerable<MapFilter> Filters => _buttons.Keys;

        /// <summary>The filter's button is on this bar and shown (the dev probe <c>mapfilter</c>).</summary>
        public bool Shows(MapFilter filter) =>
            _buttons.TryGetValue(filter, out var b) && b.resolvedStyle.display != DisplayStyle.None && b.style.display != DisplayStyle.None
            && panel != null && resolvedStyle.display != DisplayStyle.None;

        /// <summary>Shows or hides one button (the companions' while the option is off).</summary>
        public void SetAvailable(MapFilter filter, bool available)
        {
            if (!_buttons.TryGetValue(filter, out var b)) return;
            var display = available ? DisplayStyle.Flex : DisplayStyle.None;
            if (b.style.display != display) b.style.display = display;
        }

        /// <summary>A button's click (also the dev op <c>mapfilter</c>): the filter flips, then the view redraws.</summary>
        public static void Click(MapFilter filter, Action<MapFilter> changed)
        {
            MapFilters.Toggle(filter);
            changed?.Invoke(filter);
        }

        void OnChanged(MapFilter _) => Refresh();

        void Hover(MapFilter? filter)
        {
            _hovered = filter;
            Refresh();
        }

        void Refresh()
        {
            foreach (var kv in _buttons)
            {
                bool on = MapFilters.IsOn(kv.Key), hover = _hovered == kv.Key;
                if (kv.Value.Icon != null) kv.Value.Icon.style.opacity = on ? 1f : MapFilterBarLayout.OffOpacity;
                else kv.Value.style.opacity = on ? 1f : MapFilterBarLayout.OffOpacity;
                kv.Value.style.backgroundColor = hover ? HoverBack : on ? OnBack : new Color(0f, 0f, 0f, 0f);
            }
            string text = _hovered != null ? CaptionOf(_hovered.Value) : null;
            _captionLabel.text = text ?? string.Empty;
            _captionLabel.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            _plate.style.backgroundColor = new Color(MapFilterBarLayout.PlateR, MapFilterBarLayout.PlateG, MapFilterBarLayout.PlateB,
                                                     _hovered != null ? 1f : MapFilterBarLayout.PlateAlpha);
        }

        string CaptionOf(MapFilter filter)
        {
            string text = null;
            try { text = _caption?.Invoke(filter); }
            catch (Exception) { }
            return string.IsNullOrEmpty(text) ? filter.ToString() : text;
        }

        string Initial(MapFilter filter)
        {
            string text = CaptionOf(filter);
            return string.IsNullOrEmpty(text) ? "?" : text.Substring(0, 1);
        }
    }
}
