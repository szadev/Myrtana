using System.Collections.Generic;
using Myrtana.Unity.Ui.Skin;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>Where a dragged tile was let go.</summary>
    public readonly struct ItemDrop
    {
        public ItemDrop(G3ItemGrid source, int sourceIndex, G3ItemGrid target, int targetIndex, Vector2 screen = default)
        {
            Source = source;
            SourceIndex = sourceIndex;
            Target = target;
            TargetIndex = targetIndex;
            Screen = screen;
        }

        public G3ItemGrid Source { get; }
        public int SourceIndex { get; }
        /// <summary>The grid under the mouse that accepts drops (<see cref="G3ItemGrid.DropRole"/>), or null.</summary>
        public G3ItemGrid Target { get; }
        /// <summary>The cell under the mouse in <see cref="Target"/> (also an empty cell), or −1.</summary>
        public int TargetIndex { get; }
        /// <summary>Where the mouse let go, screen pixels from the top left (fix-85: dropped beside the page's views).</summary>
        public Vector2 Screen { get; }
    }

    /// <summary>
    /// Drag and drop between G3 list controls (gCGUIListCtrl drag, gCHUDListCtrlInventory::OnDrop): a tile with an item is
    /// picked up once the mouse moves 4 px with the left button down, its icon follows the mouse on a top document, and
    /// letting go reports the grid and cell under the mouse to the source grid's <see cref="G3ItemGrid.Dropped"/>. Grids
    /// on different documents (the pages and the HUD's quick bar) are found through their screen positions.
    /// </summary>
    static class G3ItemDrag
    {
        const float StartDistance = 4f;
        const int SortingOrder = 125; // above the menu: the icon is always on top
        static readonly List<G3ItemGrid> Targets = new List<G3ItemGrid>();
        static VisualElement s_layer;
        static G3Image s_ghost;

        // the tile being pressed or dragged
        static G3ItemGrid s_grid;
        static G3ItemTile s_tile;
        static Vector2 s_pressedAt;
        static bool s_dragging;

        /// <summary>A tile is being dragged (hover selection pauses meanwhile).</summary>
        public static bool Dragging => s_dragging;

        public static void Register(G3ItemGrid grid)
        {
            if (!Targets.Contains(grid)) Targets.Add(grid);
        }

        public static void Unregister(G3ItemGrid grid) => Targets.Remove(grid);

        /// <summary>Lets the tile start drags for its grid.</summary>
        public static void Attach(G3ItemGrid grid, G3ItemTile tile)
        {
            tile.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || !grid.DragEnabled || tile.Model == null || tile.Index >= grid.Count) return;
                s_grid = grid;
                s_tile = tile;
                s_pressedAt = MouseScreen();
                s_dragging = false;
            });
            tile.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (s_tile != tile) return;
                if (!s_dragging)
                {
                    if ((e.pressedButtons & 1) == 0) { s_tile = null; return; }
                    if (Vector2.Distance(MouseScreen(), s_pressedAt) < StartDistance) return;
                    s_dragging = true;
                    tile.CapturePointer(e.pointerId);
                    ShowGhost(tile);
                }
                MoveGhost();
            });
            tile.RegisterCallback<PointerUpEvent>(e =>
            {
                if (s_tile != tile) return;
                bool dragged = s_dragging;
                var grid0 = s_grid;
                s_tile = null;
                s_grid = null;
                s_dragging = false;
                if (tile.HasPointerCapture(e.pointerId)) tile.ReleasePointer(e.pointerId);
                HideGhost();
                if (!dragged) return;
                e.StopPropagation(); // not a click
                var at = MouseScreen();
                var (target, index) = Find(at);
                grid0.RaiseDropped(new ItemDrop(grid0, tile.Index, target, index, at));
            });
            tile.RegisterCallback<PointerCaptureOutEvent>(_ =>
            {
                if (s_tile != tile || !s_dragging) return;
                s_tile = null;
                s_grid = null;
                s_dragging = false;
                HideGhost();
            });
        }

        /// <summary>Mouse position in screen pixels, origin top left (as UI Toolkit panels expect).</summary>
        static Vector2 MouseScreen()
        {
            var mouse = Mouse.current;
            var p = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
            return new Vector2(p.x, Screen.height - p.y);
        }

        static (G3ItemGrid grid, int index) Find(Vector2 screen)
        {
            for (int t = Targets.Count - 1; t >= 0; t--)
            {
                var grid = Targets[t];
                if (grid.panel == null || grid.resolvedStyle.display == DisplayStyle.None || !IsShown(grid)) continue;
                var p = RuntimePanelUtils.ScreenToPanel(grid.panel, screen);
                if (!grid.worldBound.Contains(p)) continue;
                var tiles = grid.Tiles;
                for (int i = 0; i < tiles.Count; i++)
                    if (tiles[i].resolvedStyle.display != DisplayStyle.None && tiles[i].worldBound.Contains(p)) return (grid, i);
                return (grid, -1);
            }
            return (null, -1);
        }

        /// <summary>The drop grid and cell under a screen point (as a release finds them), or (null, −1).</summary>
        public static (G3ItemGrid grid, int index) TargetAt(Vector2 screen) => Find(screen);

        /// <summary>
        /// The screen point lies on UI of any shown document (pages, HUD sidebar, quick bar, bars, dialogs …): the topmost
        /// mouse-pickable, displayed element there, besides the drag icon's own layer [owner, fix-92: only a release over
        /// the game world drops a stack]. <paramref name="what"/> names the element found (log).
        /// </summary>
        public static bool OverUi(Vector2 screen, out string what)
        {
            what = null;
            var seen = new HashSet<IPanel>();
            foreach (var doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (doc == null || !doc.isActiveAndEnabled) continue;
                var root = doc.rootVisualElement;
                var panel = root?.panel;
                if (panel == null || (s_layer != null && panel == s_layer.panel && root == s_layer) || !seen.Add(panel)) continue;
                var hit = panel.Pick(RuntimePanelUtils.ScreenToPanel(panel, screen));
                for (var e = hit; e != null; e = e.parent)
                    if (e == s_layer) { hit = null; break; }
                if (hit == null || !IsShown(hit)) continue;
                what = doc.name + "/" + (string.IsNullOrEmpty(hit.name) ? hit.GetType().Name : hit.name);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The screen point lies on a displayed child view of <paramref name="page"/> (the page's panels, not the free
        /// space between them, fix-85).
        /// </summary>
        public static bool OverViews(VisualElement page, Vector2 screen)
        {
            if (page?.panel == null) return false;
            var p = RuntimePanelUtils.ScreenToPanel(page.panel, screen);
            foreach (var child in page.Children())
                if (child.userData is WindowData && IsShown(child) && child.worldBound.Contains(p)) return true;
            return false;
        }

        /// <summary>Every ancestor is displayed (a hidden page keeps its grid in the tree).</summary>
        static bool IsShown(VisualElement e)
        {
            for (; e != null; e = e.parent)
                if (e.resolvedStyle.display == DisplayStyle.None || e.resolvedStyle.opacity <= 0f) return false;
            return true;
        }

        static void ShowGhost(G3ItemTile tile)
        {
            if (s_layer == null || s_layer.panel == null)
            {
                var doc = G3PanelSettings.CreateDocument("G3 drag", GuiPanelKind.Page, null, SortingOrder);
                Object.DontDestroyOnLoad(doc.gameObject);
                s_layer = doc.rootVisualElement;
                s_ghost = new G3Image { pickingMode = PickingMode.Ignore };
                s_ghost.style.position = Position.Absolute;
                s_ghost.style.width = s_ghost.style.height = G3ItemTile.Size;
                s_ghost.style.opacity = 0.75f;
                s_layer.Add(s_ghost);
            }
            var model = tile.Model;
            s_ghost.SetSprite(G3Skin.Load()?.ItemIcon(model.Texture, model.Category, model.Learned));
            s_ghost.style.display = DisplayStyle.Flex;
            MoveGhost();
        }

        static void MoveGhost()
        {
            if (s_ghost == null || s_ghost.panel == null) return;
            var p = RuntimePanelUtils.ScreenToPanel(s_ghost.panel, MouseScreen());
            s_ghost.style.left = p.x - G3ItemTile.Size / 2f;
            s_ghost.style.top = p.y - G3ItemTile.Size / 2f;
        }

        static void HideGhost()
        {
            if (s_ghost != null) s_ghost.style.display = DisplayStyle.None;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()   // domain reload is off in the editor
        {
            Targets.Clear();
            s_layer = null;
            s_ghost = null;
            s_tile = null;
            s_grid = null;
            s_dragging = false;
        }
    }
}
