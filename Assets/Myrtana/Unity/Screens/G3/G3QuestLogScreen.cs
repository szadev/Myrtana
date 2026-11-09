using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Story;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The quest log in the original look (<c>IDD_PAGE_QUESTS_2</c> → <c>IDD_VIEW_QUESTS_2</c>, Docs/formats-gui.md §6.9):
    /// world map, the six faction fame values, the time-of-day strip with the game time; on the right the three status
    /// tabs (current, successful, failed), the quest list and the selected quest's log, speaker lines hero blue and
    /// others red. ←/→ switch the tab, ↑/↓ the quest. The map's location buttons filter the list (<see cref="QuestLocations"/>):
    /// a place shows its quests and its name and the hero's fame there in the location box, Main the main story, World all.
    /// fix-126 [owner]: the world map also carries the port's layers under the place buttons — where the hero talked to the
    /// people of the listed quests (<see cref="QuestTalkMapMarks"/>, the selected quest's marks larger and named), the
    /// companions (fix-70) and the locked chests (fix-102) — placed by a projection fitted from the place buttons and the
    /// world map's town markers (<see cref="QuestLocations.TryProjection"/>), with the maps' filter bar inside the map's
    /// top-right corner (<see cref="MapFilterBar"/>; G3's map has no place names, so no names button here).
    /// fix-225 [owner, QoL; G3 has none]: the world map zooms and pans (<see cref="MapZoomView"/>) — the map picture, the
    /// layers and the 23 place buttons (kept at their size, placed by their centre's percent of the map) in the zoomed
    /// content; Main, World and the filter bar stay where they are. A press on a place button that does not drag stays
    /// its click.
    /// fix-223 [owner]: the cartouche's captions of Main and World are filled (<c>HUD_Main</c>, <c>HUD_All</c>), and the hovered,
    /// else picked, place is named beside its icon (<see cref="QuestLocations.NamedOnMap"/>); with fix-225 the name rides in the zoomed content at its fixed size.
    /// gamepad-1: the pad moves a focus over the places (named as hovered), A picks, LB/RB tabs, Y/X the quest, LT/RT
    /// zoom, the right stick pans (scrolls the text at 1x), R3 resets (<see cref="PadMap"/>).
    /// </summary>
    public sealed class G3QuestLogScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_QUESTS_2";
        static readonly TextId Title = new TextId("HUD_SessionKey_JournalModeLog"), ReputationText = new TextId("HUD_Reputation");
        const float PlaceNameFont = 13f, PlaceNameGap = 3f;
        const int TextSize = 14, TabFont = 16, TabPadding = 16, FameFont = 18, FameLabelLeft = 42, FameValueRight = 306;

        /// <summary>Fame controls of the layout → fame group of the content.</summary>
        static readonly (string control, string group)[] Factions =
        {
            ("NORDMAR", "Nrd"), ("ORCS", "Orc"), ("REBELS", "Reb"), ("DRUIDS", "Mid"), ("ASSASSINS", "Ass"), ("NOMADS", "Nom"),
        };
        static readonly QuestJournal.Group[] TabGroups = { QuestJournal.Group.Running, QuestJournal.Group.Succeeded, QuestJournal.Group.Failed };

        readonly G3TabBar _tabs;
        readonly G3ListView _list;
        readonly Label _empty;
        readonly ScrollView _text;
        readonly G3Image _daytime;
        readonly Label _time;
        IReadOnlyList<QuestJournal.Quest> _shown = new List<QuestJournal.Quest>();
        Id<QuestDef> _selected;
        int _location = QuestLocations.World;
        readonly VisualElement _locationBox;
        readonly VisualElement _worldView;
        readonly LockedChestMapMarks _chests;
        readonly QuestTalkMapMarks _questTalks;
        readonly CompanionMapDots _companions;
        readonly MapFilterBar _filterBar;
        readonly MapZoomView _zoom;
        MapProjection? _projection;
        readonly Label _placeName;
        int _hovered = -1;
        readonly Dictionary<Label, Color> _captionColors = new Dictionary<Label, Color>();
        bool _projectionTried;
        int _padFocus = -1; // gamepad-1: the place the pad's focus is on (-1 none yet)
        readonly VisualElement _padRing;
        readonly List<G3.Player.NavRect> _navRects = new List<G3.Player.NavRect>();
        readonly List<int> _navIndex = new List<int>();

        public G3QuestLogScreen(VisualElement pagesRoot) : base(pagesRoot, G3Pages.Build(Layout))
        {
            var tabSlot = Root.Q("IDC_TABCTRL_QUESTS_2");
            _tabs = tabSlot?.userData is WindowData w ? G3TabBar.FromLayout(w) : new G3TabBar(false);
            for (int i = 0; i < TabGroups.Length; i++) _tabs.AddTab(text: " ");
            _tabs.Select(0, notify: false);
            _tabs.SelectionChanged += _ => Render();
            if (tabSlot != null)
            {
                tabSlot.pickingMode = PickingMode.Position;
                tabSlot.Add(_tabs);
            }

            var listSlot = Root.Q("IDC_LISTCTRL_QUESTS_2");
            _list = new G3ListView { ActivateOnSingleClick = false, AlwaysShowScrollBar = true };
            _list.AddToClassList("g3-fill-parent");
            _list.SelectionChanged += index =>
            {
                if (index >= 0 && index < _shown.Count) _selected = _shown[index].Id;
                ShowText();
            };
            _empty = new Label { pickingMode = PickingMode.Ignore };
            _empty.AddToClassList("g3-fill-parent");
            G3Text.Font(_empty, 16, bold: true);
            _empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            _empty.style.color = G3Palette.Text.ToColor();
            if (listSlot != null)
            {
                listSlot.pickingMode = PickingMode.Position;
                listSlot.Add(_list);
                listSlot.Add(_empty);
            }

            var textSlot = Root.Q("IDC_STATIC_INFOS_2");
            _text = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden, verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible };
            _text.AddToClassList("g3-fill-parent");
            if (textSlot != null)
            {
                textSlot.pickingMode = PickingMode.Position;
                textSlot.Add(_text);
            }

            _daytime = Root.Q<G3Image>("IDC_STATIC_INGAMETIME");
            _time = new Label { pickingMode = PickingMode.Ignore };
            _time.AddToClassList("g3-fill-parent");
            G3Text.Font(_time, 18);
            _time.style.unityTextAlign = TextAnchor.MiddleCenter;
            _time.style.color = G3Palette.Label.ToColor();
            Root.Q("IDC_VALUE_INGAMETIME")?.Add(_time);
            _locationBox = Root.Q("IDC_VIEW_LOCATION_2");
            if (_locationBox != null) _locationBox.style.display = DisplayStyle.None; // shown only for a picked location
            for (int i = 0; i < QuestLocations.All.Count; i++)
            {
                int index = i;
                var button = Root.Q<G3Button>(QuestLocations.All[i].Control);
                if (button == null) continue;
                button.pickingMode = PickingMode.Position;
                button.clicked += () => Pick(index);
                Hoverable(button, index);
                // fix-223 [owner]: Main and World have their captions in the map's cartouche; the captions pick too
                if (QuestLocations.CaptionControl(index) is string caption && Root.Q<Label>(caption) is Label label)
                {
                    label.pickingMode = PickingMode.Position;
                    label.RegisterCallback<ClickEvent>(_ => Pick(index));
                    Hoverable(label, index);
                    G3Text.Shadow(label);
                    _captionColors[label] = label.style.color.value;
                }
            }

            // fix-126 [owner]: the port's layers on the world map, under the place buttons, and the filter bar over them
            _worldView = Root.Q<G3Button>(QuestLocations.All[QuestLocations.World].Control)?.parent; // IDC_VIEW_WORLD_2 (the built view)
            if (_worldView != null)
            {
                var layers = new VisualElement { name = "quest-map-layers", pickingMode = PickingMode.Ignore };
                layers.AddToClassList("g3-fill-parent");
                var background = _worldView.Q("background");
                // fix-225: a zoom viewport where the background was; the background, the layers and the place buttons in it
                bool ownBackground = background != null && background.parent == _worldView;
                int at = ownBackground ? _worldView.IndexOf(background) : 0;
                _zoom = new MapZoomView(null);
                _worldView.Insert(at, _zoom.Viewport);
                if (ownBackground) _zoom.Content.Add(background);
                _zoom.Content.Add(layers);
                MovePlaceButtons(_zoom.Content);
                _zoom.Changed += PlaceLayers;
                _chests = new LockedChestMapMarks(layers);
                _questTalks = new QuestTalkMapMarks(layers);
                _companions = new CompanionMapDots(layers);
                _filterBar = new MapFilterBar(new[] { MapFilter.Quests, MapFilter.Companions, MapFilter.Chests },
                                              f => Session?.Localizer.Text(new TextId(MapFilterBarLayout.TextId(f))), _ => PlaceLayers());
                _filterBar.SetAvailable(MapFilter.Companions, CompanionMapDots.Enabled);
                _worldView.Add(_filterBar);

                // fix-223 [owner]: the hovered (else picked) place's name beside its icon
                _placeName = new Label { name = "quest-location-name", pickingMode = PickingMode.Ignore };
                _placeName.style.position = Position.Absolute;
                _placeName.style.whiteSpace = WhiteSpace.NoWrap;
                _placeName.style.paddingLeft = _placeName.style.paddingRight = _placeName.style.paddingTop = _placeName.style.paddingBottom = 0;
                _placeName.style.marginLeft = _placeName.style.marginRight = _placeName.style.marginTop = _placeName.style.marginBottom = 0;
                _placeName.style.translate = new Translate(0f, Length.Percent(-50f));
                _placeName.style.color = Color.white;
                _placeName.style.display = DisplayStyle.None;
                G3Text.Font(_placeName, PlaceNameFont, bold: true);
                G3Text.Shadow(_placeName);
                _zoom.Content.Add(_placeName); // fix-225: moves with the zoomed map, keeps its pixel size
                _zoom.Changed += PlaceName;
                _worldView.RegisterCallback<GeometryChangedEvent>(_ => PlaceName());

                // gamepad-1: the pad's focus ring over the map view (not clipped by the zoom viewport)
                _padRing = new VisualElement { name = "quest-map-pad-focus", pickingMode = PickingMode.Ignore };
                _padRing.style.position = Position.Absolute;
                var gold = new Color(0.85f, 0.69f, 0.38f, 0.95f);
                _padRing.style.borderTopWidth = _padRing.style.borderBottomWidth = _padRing.style.borderLeftWidth = _padRing.style.borderRightWidth = 2f;
                _padRing.style.borderTopColor = _padRing.style.borderBottomColor = _padRing.style.borderLeftColor = _padRing.style.borderRightColor = gold;
                _padRing.style.borderTopLeftRadius = _padRing.style.borderTopRightRadius = _padRing.style.borderBottomLeftRadius =
                    _padRing.style.borderBottomRightRadius = 3f;
                _padRing.style.display = DisplayStyle.None;
                _worldView.Add(_padRing);
            }
        }

        void Pick(int index)
        {
            _location = index;
            Render();
        }

        void Hoverable(VisualElement e, int index)
        {
            e.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _hovered = index;
                PlaceName();
            });
            e.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (_hovered != index) return;
                _hovered = -1;
                PlaceName();
            });
        }

        /// <summary>The place named beside its icon on the map (−1 none).</summary>
        public int NamedPlace { get; private set; } = -1;

        /// <summary>
        /// fix-223 [owner]: Main/World's captions in the cartouche (<c>HUD_Main</c>, <c>HUD_All</c>; the picked one
        /// highlighted) and the hovered, else picked, place's name beside its icon — right of it, or left near the
        /// view's right edge (fix-225: the zoom viewport's, not the zoomed content's).
        /// </summary>
        void PlaceName()
        {
            if (Session == null) return;
            var text = Session.Localizer;
            foreach (int index in new[] { QuestLocations.Main, QuestLocations.World })
            {
                if (!(Root.Q<Label>(QuestLocations.CaptionControl(index)) is Label caption)) continue;
                caption.text = text.Text(QuestLocations.All[index].Name);
                bool lit = index == _location || index == _hovered;
                caption.style.color = lit ? G3Palette.TextHighlight.ToColor()
                    : _captionColors.TryGetValue(caption, out var normal) ? normal : G3Palette.Label.ToColor();
            }
            if (_placeName == null || _worldView == null || _zoom == null) return;
            NamedPlace = QuestLocations.NamedOnMap(_location, _hovered);
            var button = NamedPlace >= 0 ? Root.Q<G3Button>(QuestLocations.All[NamedPlace].Control) : null;
            var r = button?.layout ?? Rect.zero;
            float viewWidth = _zoom.Viewport.layout.width, contentWidth = _zoom.Content.layout.width;
            if (button == null || button.parent == null || float.IsNaN(r.width) || r.width <= 0f || float.IsNaN(viewWidth)
                || float.IsNaN(contentWidth) || viewWidth <= 0f)
            {
                _placeName.style.display = DisplayStyle.None;
                return;
            }
            // fix-225: the button's rect in the zoomed content (where the name sits) and in the view (where it must fit)
            var inContent = button.parent.ChangeCoordinatesTo(_zoom.Content, r);
            var inView = button.parent.ChangeCoordinatesTo(_zoom.Viewport, r);
            string name = text.Text(QuestLocations.All[NamedPlace].Name);
            if (_placeName.text != name) _placeName.text = name;
            _placeName.style.display = DisplayStyle.Flex;
            _placeName.style.top = inContent.center.y;
            float width = G3Text.TextWidth(_placeName);
            if (inView.xMax + PlaceNameGap + width > viewWidth - PlaceNameGap)
            {
                _placeName.style.left = StyleKeyword.Null;
                _placeName.style.right = contentWidth - inContent.xMin + PlaceNameGap;
            }
            else
            {
                _placeName.style.right = StyleKeyword.Null;
                _placeName.style.left = inContent.xMax + PlaceNameGap;
            }
            _placeName.BringToFront();
        }

        protected override void OnOpen()
        {
            _tabs.Select(0, notify: false);
            _location = QuestLocations.World;
            _hovered = -1;
            _padFocus = -1;
            if (_padRing != null) _padRing.style.display = DisplayStyle.None;
            _zoom?.ResetZoom(immediate: true);
        }

        /// <summary>fix-225: the world map's zoom and pan (the dev probe <c>mapzoom</c>; gamepad-1 binds its actions).</summary>
        public MapZoomView Zoom => _zoom;

        /// <summary>
        /// fix-225: the place buttons into the zoomed content, each at its layout centre as percent of the world view and
        /// its layout size (so it stays on its place at any zoom and keeps its size); Main and World stay put.
        /// </summary>
        void MovePlaceButtons(VisualElement content)
        {
            float w = _worldView.userData is WindowData v ? v.Width : 442f, h = _worldView.userData is WindowData v2 ? v2.Height : 392f;
            foreach (var location in QuestLocations.All)
            {
                if (location.IsAll || location.Enclave == null) continue; // World, Main
                if (!(Root.Q<G3Button>(location.Control) is G3Button button) || button.parent != _worldView) continue;
                if (!(button.userData is WindowData b) || b.Rect == null || b.Rect.Length < 4) continue;
                content.Add(button);
                button.style.position = Position.Absolute;
                button.style.left = Length.Percent((b.Left + b.Width / 2f) / w * 100f);
                button.style.top = Length.Percent((b.Top + b.Height / 2f) / h * 100f);
                button.style.right = StyleKeyword.Null;
                button.style.bottom = StyleKeyword.Null;
                button.style.width = b.Width;
                button.style.height = b.Height;
                button.style.marginLeft = -b.Width / 2f;
                button.style.marginTop = -b.Height / 2f;
            }
        }

        public override void Render()
        {
            var text = Session.Localizer;
            var journal = QuestJournal.Of(Session);
            G3Pages.SetText(Root, "IDC_LABEL_QUESTS_2", text.Text(Title));
            for (int i = 0; i < TabGroups.Length; i++) _tabs.Tabs[i].Label.text = journal.GroupTitles[TabGroups[i]];
            _tabs.schedule.Execute(() =>
            {
                foreach (var tab in _tabs.Tabs) G3Text.FitWidth(tab.Label, _tabs.ItemWidth - TabPadding, TabFont);
            });
            RenderFame();
            RenderTime();

            _shown = journal.Groups[TabGroups[Mathf.Max(0, _tabs.SelectedIndex)]].Where(q => QuestLocations.Matches(_location, q.Folder)).ToList();
            RenderLocation();
            PlaceName();
            _list.SetItems(_shown.Select(q => q.Title).ToList());
            _empty.text = _shown.Count == 0 ? journal.EmptyText : string.Empty;
            int index = _shown.ToList().FindIndex(q => q.Id == _selected);
            if (index < 0 && _shown.Count > 0) _selected = _shown[index = 0].Id;
            _list.Select(index, notify: false);
            ShowText();
        }

        /// <summary>Quest talks drawn on the world map (the dev probe <c>mapquestmarks</c>).</summary>
        public int QuestTalksShown => _questTalks?.Talks ?? 0;

        /// <summary>Marks of the selected quest (the dev probe <c>mapquestselected</c>).</summary>
        public int QuestTalksSelected => _questTalks?.Selected ?? 0;

        /// <summary>Locked chests drawn on the world map (the dev probe <c>mapchestmarks</c>).</summary>
        public int LockedChestMarksShown => _chests?.Chests ?? 0;

        /// <summary>Companion dots drawn (the dev probe <c>mapcompanions</c>).</summary>
        public int CompanionsShown => _companions?.Shown ?? 0;

        /// <summary>The map is placed (its projection fitted; the dev probe <c>mapfit</c>).</summary>
        public bool MapFitted => _projection != null;

        /// <summary>The filter's button shows on the bar (the dev probe <c>mapfilter</c>).</summary>
        public bool FilterVisible(MapFilter filter) => _filterBar != null && _filterBar.Shows(filter);

        /// <summary>Clicks a filter's button (the dev ops <c>mapfilter</c> / <c>chestfilter</c>): the player's path.</summary>
        public void ClickFilter(MapFilter filter) => MapFilterBar.Click(filter, _ => PlaceLayers());

        /// <summary>The world map's layers: the listed quests' talks (the selected one's highlighted), companions, chests.</summary>
        void PlaceLayers()
        {
            if (_worldView == null || Session == null) return;
            if (!_projectionTried)
            {
                _projectionTried = true;
                _projection = FitProjection();
            }
            var listed = new HashSet<Id<QuestDef>>(_shown.Select(q => q.Id));
            _questTalks.Place(Session, _projection, listed.Contains, _selected);
            _companions.Place(Session, _projection);
            _chests.Place(Session, _projection);
        }

        MapProjection? FitProjection()
        {
            if (!Session.Content.Database.TryGet(QuestLocations.WorldMapItem, out MapDef world)) return null;
            float w = _worldView.userData is WindowData v ? v.Width : 442f, h = _worldView.userData is WindowData v2 ? v2.Height : 392f;
            bool ok = QuestLocations.TryProjection(world, control =>
            {
                if (!(Root.Q<G3Button>(control)?.userData is WindowData b) || b.Rect == null || b.Rect.Length < 4) return null;
                return (b.Left + b.Width / 2f, b.Top + b.Height / 2f);
            }, w, h, out var projection);
            if (!ok) Debug.LogWarning("[Myrtana] quest log map: no projection (the place buttons do not match the world map's markers)");
            return ok ? projection : (MapProjection?)null;
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.QuestLog;

        public override void Tick()
        {
            _filterBar?.SetAvailable(MapFilter.Companions, CompanionMapDots.Enabled);
            _companions?.Place(Session, _projection);
            var keys = PageKeys.Now;
            var kb = keys.Keyboard;
            _zoom?.Tick(kb);
            // keyboard: left/right tabs, up/down the quest; pad: LB/RB tabs, Y/X the quest, the map below (gamepad-1)
            if (keys.Key(Key.LeftArrow) || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabPrev)) _tabs.Select((_tabs.SelectedIndex + TabGroups.Length - 1) % TabGroups.Length);
            if (keys.Key(Key.RightArrow) || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabNext)) _tabs.Select((_tabs.SelectedIndex + 1) % TabGroups.Length);
            if (keys.Key(Key.UpArrow) || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Tertiary)) _list.Select(Mathf.Max(0, _list.SelectedIndex - 1));
            if (keys.Key(Key.DownArrow) || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Secondary)) _list.Select(Mathf.Min(_shown.Count - 1, _list.SelectedIndex + 1));
            PadMap();
        }

        /// <summary>
        /// gamepad-1 (Docs/gamepad.md section 4): the d-pad / left stick moves a focus over the map's places (the 23 place
        /// buttons in the zoomed content and Main/World in the cartouche, spatially), named like a hovered place; A picks it.
        /// LT/RT zoom around the focused place, the right stick pans while zoomed (at 1x it scrolls the quest text), R3
        /// resets the zoom. A focused place outside the zoomed view is panned into it.
        /// </summary>
        void PadMap()
        {
            bool pad = G3.Unity.G3Pad.PadIsLast;
            if (_worldView == null || _zoom == null)
            {
                if (pad) PadScroll.Apply(_text);
                return;
            }
            if (!pad)
            {
                if (_padRing != null) _padRing.style.display = DisplayStyle.None;
                return;
            }
            var dir = G3.Unity.G3Pad.UiMove;
            if (dir != G3.Player.NavDir.None)
            {
                if (_padFocus < 0 || PlaceButton(_padFocus) == null) _padFocus = _location;
                else
                {
                    _navRects.Clear();
                    _navIndex.Clear();
                    int at = -1;
                    for (int i = 0; i < QuestLocations.All.Count; i++)
                    {
                        var b = PlaceButton(i);
                        if (b == null) continue;
                        var r = b.worldBound;
                        if (float.IsNaN(r.width) || r.width <= 0f) continue;
                        if (i == _padFocus) at = _navRects.Count;
                        _navRects.Add(new G3.Player.NavRect(r.x, r.y, r.width, r.height));
                        _navIndex.Add(i);
                    }
                    if (at >= 0)
                    {
                        int next = G3.Player.SpatialNav.Pick(_navRects[at], _navRects, dir, at);
                        if (next >= 0) _padFocus = _navIndex[next];
                    }
                }
                _hovered = _padFocus;
                RevealFocus();
                PlaceName();
            }
            if (_padFocus >= 0 && G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Accept)) Pick(_padFocus);

            Vector2? anchor = null;
            var focused = _padFocus >= 0 ? PlaceButton(_padFocus) : null;
            if (focused != null && focused.parent != null && focused.parent != _worldView)
                anchor = focused.parent.ChangeCoordinatesTo(_zoom.Viewport, focused.layout).center;
            if (!_zoom.TickPad(anchor)) PadScroll.Apply(_text);
            PlaceRing(focused);
        }

        G3Button PlaceButton(int index)
        {
            if (index < 0 || index >= QuestLocations.All.Count) return null;
            var b = Root.Q<G3Button>(QuestLocations.All[index].Control);
            return b != null && b.resolvedStyle.display != DisplayStyle.None ? b : null;
        }

        /// <summary>A focused place in the zoomed content outside the view: pan it in.</summary>
        void RevealFocus()
        {
            var b = PlaceButton(_padFocus);
            if (b == null || b.parent == null || b.parent == _worldView) return;
            _zoom.Reveal(b.parent.ChangeCoordinatesTo(_zoom.Viewport, b.layout));
        }

        /// <summary>The pad's gold ring around the focused place (over the map view; Main/World in the cartouche too).</summary>
        void PlaceRing(VisualElement focused)
        {
            if (_padRing == null) return;
            if (focused == null || float.IsNaN(focused.worldBound.width))
            {
                _padRing.style.display = DisplayStyle.None;
                return;
            }
            var r = focused.worldBound;
            var tl = _worldView.WorldToLocal(new Vector2(r.xMin, r.yMin));
            var br = _worldView.WorldToLocal(new Vector2(r.xMax, r.yMax));
            _padRing.style.display = DisplayStyle.Flex;
            _padRing.style.left = tl.x - 2f;
            _padRing.style.top = tl.y - 2f;
            _padRing.style.width = br.x - tl.x + 4f;
            _padRing.style.height = br.y - tl.y + 4f;
            _padRing.BringToFront();
        }

        /// <summary>The location box: the picked place's icon, name and the hero's fame with its enclave (none for World).</summary>
        void RenderLocation()
        {
            if (_locationBox == null) return;
            var location = QuestLocations.All[_location];
            _locationBox.style.display = location.IsAll ? DisplayStyle.None : DisplayStyle.Flex;
            if (location.IsAll) return;
            var text = Session.Localizer;
            if (_locationBox.Q<G3Image>("IDC_STATIC_ICON_LOCATION_2") is G3Image icon) icon.Image = location.IconImage;
            G3Pages.SetText(_locationBox, "IDC_LABEL_LOCATION_2", text.Text(location.Name));
            bool fame = location.Enclave != null;
            var fameLabel = _locationBox.Q("IDC_LABEL_FAME_2");
            var fameValue = _locationBox.Q("IDC_VALUE_FAME_2");
            if (fameLabel != null) fameLabel.style.display = fame ? DisplayStyle.Flex : DisplayStyle.None;
            if (fameValue != null) fameValue.style.display = fame ? DisplayStyle.Flex : DisplayStyle.None;
            if (!fame) return;
            var groups = Session.Fame.All(Rules.FameKind.Enclave).Select(kv => kv.Key);
            int value = Session.Fame.Of(Rules.FameKind.Enclave, QuestLocations.FameGroup(location, groups));
            G3Pages.SetText(_locationBox, "IDC_LABEL_FAME_2", text.Text(ReputationText));
            G3Pages.SetText(_locationBox, "IDC_VALUE_FAME_2", value + "%");
        }

        void RenderFame()
        {
            var sheet = CharacterSheet.Of(Session);
            foreach (var (control, group) in Factions)
            {
                bool known = sheet.ByFameGroup.TryGetValue(group, out var fame);
                G3Pages.SetText(Root, "IDC_LABEL_FAME_" + control + "_2", known ? fame.Label : group);
                G3Pages.SetText(Root, "IDC_VALUE_FAME_" + control + "_2", known ? fame.Value : "0");
            }
            Root.schedule.Execute(() =>
            {
                foreach (var (control, _) in Factions)
                    if (Root.Q("IDC_LABEL_FAME_" + control + "_2") is Label label && Root.Q("IDC_VALUE_FAME_" + control + "_2") is Label value)
                        G3Text.FitWidth(label, FameValueRight - FameLabelLeft - G3Text.TextWidth(value) - 8, FameFont);
            });
        }

        /// <summary>Time-of-day picture (night before 6 h and from 22 h, dawn 6–7, day 8–19, dusk 20–21) and the game time.</summary>
        void RenderTime()
        {
            var clock = Session.Clock;
            float hour = clock.HourOfDay;
            string strip = hour < 6 || hour >= 22 ? "Night" : hour < 8 ? "Dawn" : hour < 20 ? "Day" : "Dusk";
            if (_daytime != null) _daytime.Image = $"G3_HUD_View_Quests_Time_{strip}.tga";
            int h = (int)hour, m = (int)((hour - h) * 60);
            _time.text = $"{clock.Day + 1}. {h:00}:{m:00}";
        }

        void ShowText()
        {
            PlaceLayers();
            _text.Clear();
            var quest = _shown.FirstOrDefault(q => q.Id == _selected);
            if (quest == null) return;
            var header = Line(quest.Title, G3Palette.TextSelected, bold: true);
            header.style.marginBottom = 6;
            _text.Add(header);
            foreach (var line in quest.Lines)
            {
                if (!string.IsNullOrEmpty(line.Speaker)) _text.Add(Line(line.Speaker, QuestLogColors.Speaker(line.ByHero), bold: true));
                var entry = Line(line.Text, G3Palette.Text, bold: false);
                entry.style.marginBottom = 6;
                _text.Add(entry);
            }
        }

        static Label Line(string text, SkinColor color, bool bold)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            G3Text.Font(label, TextSize, bold);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.UpperLeft;
            label.style.color = color.ToColor();
            G3Text.Shadow(label);
            return label;
        }
    }
}
