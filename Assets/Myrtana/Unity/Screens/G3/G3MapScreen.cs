using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The documents page on its Maps tab (<c>IDD_PAGE_DOCUMENTS_2</c>, Docs/map-teleport.md §1.2): the carried maps in
    /// the list on the right, the chosen map stretched over the reading area with the hero arrow at his position, turned
    /// to his heading, and the map's visible markers under it (picture <c>G3_HUD_Marker</c> centred on the point — not in
    /// the CP 1.75 GUI data, so only the label shows, as in G3 — and the label text white with a black shadow, centred
    /// above the point). The map is the one picked in the list, else the one shown last, else the largest carried map
    /// around the hero (<see cref="WorldMaps.Choose"/>); without one the map view stays hidden, as in G3. The original has
    /// no zoom or pan; fix-225 [owner, QoL] adds them (<see cref="MapZoomView"/>: wheel around the cursor, drag, +/−,
    /// double-click / Home resets; back to the whole map on opening and on another map). ↑/↓ pick a map. fix-126 [owner]: the map's filter bar inside its top-right corner
    /// (<see cref="MapFilterBar"/>): place names (G3's labels, on; overlapping labels nudged apart, <see cref="MapLabelLayout"/>),
    /// quest people (where the hero talked about his running quests, <see cref="QuestTalkMapMarks"/>), companions (fix-70,
    /// while that option is on) and the port's locked-chest marks (fix-102/116, default hidden; <see cref="LockedChestMapMarks"/>).
    /// The other tabs list the carried letters and books (both on the Books tab,
    /// as in G3) and the carried recipes of each craft; the chosen one shows in the letter, book or recipe view
    /// (Docs/interactive-objects.md §6.4). What to show comes from the view model <see cref="DocumentsPage"/>.
    /// </summary>
    public sealed class G3MapScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_DOCUMENTS_2";
        static readonly string[] OtherDocuments = { "IDC_VIEW_LETTER_2", "IDC_VIEW_BOOK_2", "IDC_VIEW_RECIPE_2" };
        const int MapsTab = 0, ArrowSize = 32, MarkerSize = 32, ListRowHeight = 46;
        const string MarkerImage = "G3_HUD_Marker.tga";

        readonly Transform _hero;
        readonly G3TabBar _tabs;
        readonly G3ListView _list;
        readonly VisualElement _mapView;
        readonly G3Image _picture;
        readonly G3Image _arrow;
        readonly VisualElement _markers;
        readonly CompanionMapDots _companions;
        readonly LockedChestMapMarks _lockedChests;
        readonly QuestTalkMapMarks _questTalks;
        readonly MapFilterBar _filterBar;
        readonly MapZoomView _zoom;
        readonly List<(Label label, float u, float v)> _labels = new List<(Label, float, float)>();
        readonly VisualElement _letter, _book;
        readonly G3RecipeView _recipe;
        DocumentsPage _page;
        MapDef _map;

        public G3MapScreen(VisualElement pagesRoot, Transform hero) : base(pagesRoot, G3Pages.Build(Layout))
        {
            _hero = hero;
            foreach (var name in OtherDocuments)
            {
                var view = Root.Q(name);
                if (view != null) view.style.display = DisplayStyle.None;
            }
            G3Pages.BuildInto(Root.Q("IDC_VIEW_DOCUMENT_LIST_2"), "IDD_VIEW_DOCUMENTS_2");

            var tabSlot = Root.Q("IDC_TABCTRL_DOCUMENTS_2");
            _tabs = tabSlot?.userData is WindowData w ? G3TabBar.FromLayout(w) : new G3TabBar(false);
            foreach (var icon in DocumentsPage.TabIcons) _tabs.AddTab(icon);
            _tabs.Select(MapsTab, notify: false);
            _tabs.SelectionChanged += _ => Show(null);
            if (tabSlot != null)
            {
                tabSlot.pickingMode = PickingMode.Position;
                tabSlot.Add(_tabs);
            }

            _list = new G3ListView { ActivateOnSingleClick = false, RowHeight = ListRowHeight, AlwaysShowScrollBar = true };
            _list.AddToClassList("g3-fill-parent");
            _list.SelectionChanged += index => Show(_page != null && index >= 0 && index < _page.Rows.Count ? _page.Rows[index].Item : null, rebuildList: false);
            var listSlot = Root.Q("IDC_LISTCTRL_DOCUMENTS_2");
            if (listSlot != null)
            {
                listSlot.pickingMode = PickingMode.Position;
                listSlot.Add(_list);
            }

            _mapView = Root.Q("IDC_VIEW_MAP_2");
            var mapSlot = Root.Q("IDC_STATIC_MAP_2") ?? _mapView;
            _picture = new G3Image { name = "map-picture" };
            _picture.AddToClassList("g3-fill-parent");
            _zoom = new MapZoomView(mapSlot); // fix-225: the picture and its layers zoom and pan inside the slot
            // G3 draws the markers first, then the arrow.
            _markers = new VisualElement { name = "map-markers", pickingMode = PickingMode.Ignore };
            _markers.AddToClassList("g3-fill-parent");
            _picture.Add(_markers);
            _lockedChests = new LockedChestMapMarks(_picture); // fix-102, the port's marks; above the markers
            _questTalks = new QuestTalkMapMarks(_picture); // fix-126: where the hero talked about his running quests
            _companions = new CompanionMapDots(_picture); // the port's option; between the markers and the arrow
            _arrow = new G3Image("G3_HUD_Arrow.tga") { name = "map-arrow" };
            _arrow.style.position = Position.Absolute;
            _arrow.style.width = ArrowSize;
            _arrow.style.height = ArrowSize;
            _arrow.style.marginLeft = -ArrowSize / 2f;
            _arrow.style.marginTop = -ArrowSize / 2f;
            _picture.Add(_arrow);
            _zoom.Content.Add(_picture);
            _zoom.Changed += PlaceQuestTalks; // their groups are made in view pixels: finer when zoomed in
            _markers.RegisterCallback<GeometryChangedEvent>(_ => Declutter());
            // fix-126 [owner]: the filter bar inside the map's top-right corner (fix-116's toggle under the map was not seen)
            _filterBar = new MapFilterBar(new[] { MapFilter.Names, MapFilter.Quests, MapFilter.Companions, MapFilter.Chests },
                                          FilterCaption, _ => PlaceLayers());
            _filterBar.SetAvailable(MapFilter.Companions, CompanionMapDots.Enabled);
            mapSlot?.Add(_filterBar);

            _letter = Root.Q("IDC_VIEW_LETTER_2");
            _book = Root.Q("IDC_VIEW_BOOK_2");
            G3TextViews.PrepareText(_letter, "IDC_LABEL_LETTER_HEADER_2", "IDC_LABEL_LETTER_TEXT_2");
            G3TextViews.PrepareText(_book, "IDC_LABEL_BOOK_HEADER_2", "IDC_LABEL_BOOK_LEFT_2", "IDC_LABEL_BOOK_RIGHT_2");
            _recipe = new G3RecipeView(Root.Q("IDC_VIEW_RECIPE_2"));
        }

        protected override void OnOpen()
        {
            _tabs.Select(MapsTab, notify: false);
            _zoom.ResetZoom(immediate: true);
        }

        /// <summary>fix-225: the map's zoom and pan (the dev probe <c>mapzoom</c>; gamepad-1 binds its actions).</summary>
        public MapZoomView Zoom => _zoom;

        /// <summary>Switches to a documents tab (the tab bar's click; also for the editor checks).</summary>
        public void ShowTab(DocumentTab tab) => _tabs.Select((int)tab);

        public override void Render() => Show(_page?.SelectedItem);

        /// <summary>Locked chests drawn on the shown map (fix-102; the dev probe <c>mapchestmarks</c>).</summary>
        public int LockedChestMarksShown => _lockedChests.Chests;

        /// <summary>Chest marks drawn (nearby chests grouped into one, fix-116; the dev probe <c>mapchestgroups</c>).</summary>
        public int LockedChestGroupsShown => _lockedChests.Groups;

        /// <summary>The chest filter shows (the map view is shown with a map; the dev probe <c>mapchestfilter</c>).</summary>
        public bool ChestFilterVisible => FilterVisible(MapFilter.Chests);

        /// <summary>The filter's button shows on the bar (the map view is shown with a map; the dev probe <c>mapfilter</c>).</summary>
        public bool FilterVisible(MapFilter filter) => _filterBar.Shows(filter) && (_mapView == null || _mapView.resolvedStyle.display != DisplayStyle.None);

        /// <summary>Clicks the chest filter (the dev op <c>chestfilter</c>): the same path as the player's click.</summary>
        public void ClickChestFilter() => ClickFilter(MapFilter.Chests);

        /// <summary>Clicks a filter's button (the dev op <c>mapfilter</c>): the same path as the player's click.</summary>
        public void ClickFilter(MapFilter filter) => MapFilterBar.Click(filter, _ => PlaceLayers());

        /// <summary>Place labels shown (the dev probe <c>mapnames</c>).</summary>
        public int NamesShown => _labels.Count(l => l.label.style.display != DisplayStyle.None);

        /// <summary>Labels the declutter moved (the dev probe <c>mapnamesnudged</c>).</summary>
        public int NamesNudged { get; private set; }

        /// <summary>Pairs of shown labels that still overlap (the dev probe <c>mapnamesoverlap</c>; 0 after the declutter).</summary>
        public int NamesOverlapping
        {
            get
            {
                var boxes = _labels.Where(l => l.label.style.display != DisplayStyle.None).Select(l => l.label.worldBound).ToList();
                int n = 0;
                for (int i = 0; i < boxes.Count; i++)
                    for (int j = i + 1; j < boxes.Count; j++)
                        if (boxes[i].width > 0f && boxes[j].width > 0f && boxes[i].Overlaps(boxes[j])) n++;
                return n;
            }
        }

        /// <summary>Quest talks drawn (the dev probe <c>mapquestmarks</c>).</summary>
        public int QuestTalksShown => _questTalks.Talks;

        /// <summary>Companion dots drawn (the dev probe <c>mapcompanions</c>).</summary>
        public int CompanionsShown => _companions.Shown;

        string FilterCaption(MapFilter filter) => Session?.Localizer.Text(new TextId(MapFilterBarLayout.TextId(filter)));

        /// <summary>The map's layers after a filter changed.</summary>
        void PlaceLayers()
        {
            ShowNames();
            if (_map == null) return;
            _lockedChests.Place(Session, _map);
            _companions.Place(Session, _map);
            PlaceQuestTalks();
        }

        /// <summary>The marks of the running quests (the documents map has no quest selected).</summary>
        void PlaceQuestTalks()
        {
            var session = Session;
            _questTalks.Place(session, _map?.Projection,
                              q => session != null && QuestJournal.GroupOf(session.Quests.StatusOf(q)) == QuestJournal.Group.Running);
        }

        /// <summary>
        /// Builds the page of the selected tab with <paramref name="picked"/> selected (null = G3's default) and shows it; a
        /// click in the list keeps the list (and its scroll position) as it is.
        /// </summary>
        void Show(string picked, bool rebuildList = true)
        {
            int tab = Mathf.Clamp(_tabs.SelectedIndex, 0, DocumentsPage.TabIcons.Count - 1);
            var p = _hero != null ? _hero.position : Vector3.zero;
            if (_page != null && _page.Tab != (DocumentTab)tab) picked = null;
            _page = DocumentsPage.Of(Session, (DocumentTab)tab, picked, p.x, p.z);
            G3Pages.SetText(Root, "IDC_LABEL_DOCUMENTS_2", _page.Title);
            if (rebuildList)
            {
                _list.SetItems(_page.Rows.Select(r => r.Name).ToList());
                for (int i = 0; i < _page.Rows.Count; i++) _list.Rows[i].SetIcon(_page.Rows[i].Icon);
                _list.Select(_page.Selected, notify: false);
            }
            ShowDocument();
            ShowMap();
        }

        /// <summary>G3 <c>gCHUDViewDocument2::SetStack</c>: the letter, book or recipe view of the chosen item, the others hidden.</summary>
        void ShowDocument()
        {
            bool letter = _page.View == DocumentView.Letter, book = _page.View == DocumentView.Book;
            if (_letter != null) _letter.style.display = letter ? DisplayStyle.Flex : DisplayStyle.None;
            if (_book != null) _book.style.display = book ? DisplayStyle.Flex : DisplayStyle.None;
            if (letter) G3TextViews.BindLetter(_letter, _page.Document);
            if (book) G3TextViews.BindBook(_book, _page.Document);
            _recipe.Bind(_page.Recipe);
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Map;

        public override void Tick()
        {
            if (_map != null) PlaceArrow();
            _zoom.Tick(global::G3.Unity.G3Input.GameKeyboard);
            _zoom.TickPad(); // gamepad-1: LT/RT zoom, right stick pans, R3 resets
            _lockedChests.Place(Session, _map);
            _filterBar.SetAvailable(MapFilter.Companions, CompanionMapDots.Enabled);
            _companions.Place(Session, _map);
            var keys = PageKeys.Now; // gamepad-1: keyboard or pad (LB/RB = the document tabs)
            int tabs = DocumentsPage.TabIcons.Count;
            if (G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabPrev)) _tabs.Select((_tabs.SelectedIndex + tabs - 1) % tabs);
            if (G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabNext)) _tabs.Select((_tabs.SelectedIndex + 1) % tabs);
            int count = _page?.Rows.Count ?? 0;
            if (count == 0) return;
            if (keys.Up) _list.Select(Mathf.Max(0, _list.SelectedIndex - 1));
            if (keys.Down) _list.Select(Mathf.Min(count - 1, _list.SelectedIndex + 1));
        }

        /// <summary>Shows the map the page chose (G3's pick) or hides the map view.</summary>
        void ShowMap()
        {
            if (_page.Map?.Id != _map?.Id) _zoom.ResetZoom(immediate: true);
            _map = _page.Map;
            if (_mapView != null) _mapView.style.display = _map != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (_map == null) return;
            _picture.Image = _map.Bitmap + ".tga";
            ShowMarkers();
            PlaceArrow();
            _lockedChests.Place(Session, _map);
            _companions.Place(Session, _map);
            PlaceQuestTalks();
        }

        /// <summary>
        /// The map's visible markers (Docs/map-teleport.md §1.2): the marker picture 32×32 centred on the marker's map point
        /// (when the GUI data has it), the label's text box centred horizontally on the point with its bottom on the point,
        /// one line, white with a black shadow. A label without text (no string-table entry) draws nothing, as in G3.
        /// </summary>
        void ShowMarkers()
        {
            _markers.Clear();
            _labels.Clear();
            NamesNudged = 0;
            if (_map == null) return;
            bool hasPicture = G3Skin.Load()?.Sprite(MarkerImage) != null;
            int shown = 0;
            foreach (var marker in _map.VisibleMarkers)
            {
                if (!_map.Projection.TryToMap(marker.X, marker.Z, out float u, out float v)) continue;
                if (hasPicture)
                {
                    var icon = new G3Image(MarkerImage);
                    icon.style.position = Position.Absolute;
                    icon.style.width = MarkerSize;
                    icon.style.height = MarkerSize;
                    icon.style.left = Length.Percent(u * 100f);
                    icon.style.top = Length.Percent(v * 100f);
                    icon.style.marginLeft = -MarkerSize / 2f;
                    icon.style.marginTop = -MarkerSize / 2f;
                    _markers.Add(icon);
                }
                if (!Session.Localizer.TryText(marker.Label, out var text) || string.IsNullOrEmpty(text)) continue;
                var label = new Label(text) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("g3-static");
                label.AddToClassList("map-marker-label");
                label.style.position = Position.Absolute;
                label.style.left = Length.Percent(u * 100f);
                label.style.top = Length.Percent(v * 100f);
                label.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-100f));
                label.style.whiteSpace = WhiteSpace.NoWrap;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.paddingLeft = 0;
                label.style.paddingRight = 0;
                label.style.paddingTop = 0;
                label.style.paddingBottom = 0;
                label.style.marginLeft = 0;
                label.style.marginTop = 0;
                label.style.color = Color.white;
                G3Text.Shadow(label);
                // fix-136: the declutter needs the labels' laid-out sizes; the scheduled run came before the first layout
                // (all sizes 0: Ben Sala / Bakaresh stayed on each other), so it runs again whenever a label's size changes
                // (its own nudges move it without resizing it: no loop)
                label.RegisterCallback<GeometryChangedEvent>(e =>
                {
                    if (e.oldRect.size != e.newRect.size) Declutter();
                });
                _markers.Add(label);
                _labels.Add((label, u, v));
                shown++;
            }
            ShowNames();
            Debug.Log($"[Myrtana] map {_map.Id}: {shown} marker labels (picture {(hasPicture ? "yes" : "none")})");
        }

        /// <summary>The place names follow the names filter (fix-126).</summary>
        void ShowNames()
        {
            var display = MapFilters.IsOn(MapFilter.Names) ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var (label, _, _) in _labels)
                if (label.style.display != display) label.style.display = display;
            if (display == DisplayStyle.Flex && _labels.Count > 0) _markers.schedule.Execute(Declutter); // sizes once laid out
        }

        /// <summary>
        /// fix-126 [owner]: overlapping labels nudged apart (<see cref="MapLabelLayout.Declutter"/>), the others where G3 puts
        /// them; once the labels have their size, again when the map's size changes.
        /// </summary>
        void Declutter()
        {
            if (_labels.Count == 0) return;
            var size = _markers.contentRect.size;
            if (!(size.x > 1f) || !(size.y > 1f)) return;
            var boxes = new List<MapLabelBox>(_labels.Count);
            foreach (var (label, u, v) in _labels)
            {
                var r = label.layout;
                float lw = float.IsNaN(r.width) ? 0f : r.width, lh = float.IsNaN(r.height) ? 0f : r.height;
                boxes.Add(new MapLabelBox(u * size.x, v * size.y, lw, lh));
            }
            var offsets = MapLabelLayout.Declutter(boxes);
            int nudged = 0;
            for (int i = 0; i < _labels.Count; i++)
            {
                var label = _labels[i].label;
                if (offsets[i].dx != 0f || offsets[i].dy != 0f) nudged++;
                label.style.marginLeft = offsets[i].dx;
                label.style.marginTop = offsets[i].dy;
            }
            NamesNudged = nudged;
        }

        /// <summary>The hero arrow at his map point, turned clockwise from map-up by his heading; hidden off the map.</summary>
        void PlaceArrow()
        {
            if (_hero == null || !_map.Projection.TryToMap(_hero.position.x, _hero.position.z, out float u, out float v)
                || u < 0f || u > 1f || v < 0f || v > 1f)
            {
                _arrow.style.display = DisplayStyle.None;
                return;
            }
            var forward = _hero.forward;
            _arrow.style.display = DisplayStyle.Flex;
            _arrow.style.left = Length.Percent(u * 100f);
            _arrow.style.top = Length.Percent(v * 100f);
            _arrow.style.rotate = new Rotate(Angle.Degrees(MapProjection.HeadingDegrees(forward.x, forward.z)));
        }
    }
}
