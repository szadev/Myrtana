#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Unity.Ui.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-85/92 ops [owner]: the hero drops a stack by dragging it out of the inventory page (<c>invdrop</c>); a click on
    /// the selected category tab cycles the sort mode (<c>invsort</c>).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        /// <summary>
        /// `invdrop item=X [amount=1] [at=world|sidebar] [expect=dropped|refused|kept] [close=1]` — opens the inventory,
        /// shows the stack's tab and lets the dragged tile go at a screen point found on the live UI: `world` = outside
        /// every UI element (the only place a drop happens), `sidebar` = on the HUD's page sidebar (must keep the stack).
        /// A stack then gets the amount dialog (set to `amount`, OK). Checks the carried count and the new dropped world
        /// item in front of the hero. The item is given first (amount of it) when the hero lacks it.
        /// </summary>
        IEnumerator InvDropStep(ScenarioStep st)
        {
            var screens = FindAnyObjectByType<GameScreens>();
            var page = screens != null ? screens.ByAction(G3.Player.SessionKey.InventoryMode) as G3InventoryScreen : null;
            if (page == null) { Fail("invdrop: no G3 inventory page (G3 GUI not imported?)"); yield break; }
            string item = ResolveItem(st.Get("item"));
            if (item == null) yield break;
            int amount = st.Int("amount", 1);
            string at = st.Get("at", "world");
            var inv = _session.Items.Inventories.Of(ActorId.Player);
            if (inv.Count(item) == 0) _session.Items.Inventories.Give(ActorId.Player, item, amount);
            HideTips();
            if (screens.Current != page) screens.Toggle(G3.Player.SessionKey.InventoryMode);
            yield return new WaitForSecondsRealtime(0.4f);
            if (screens.Current != page) { Fail("invdrop: the inventory did not open"); yield break; }
            if (!page.ShowItem(item)) { Fail($"invdrop: {item} is on no inventory tab"); yield break; }
            yield return null;
            Vector2? point = at == "sidebar" ? ScreenPointOn("IDC_VIEW_BAR_PAGES_2") : ScreenPoint(page.IsWorldPoint);
            if (point == null)
            {
                Fail($"invdrop: no screen point {(at == "sidebar" ? "on the sidebar" : "over the world (UI everywhere?)")}");
                yield break;
            }
            G3ItemGrid.OverUi(point.Value, out string under);
            int before = inv.Count(item);
            var known = _session.Items.World.AllDropped.Select(d => d.Id).ToList();
            page.ReleaseDrag(item, point.Value);
            yield return null;
            bool dialog = page.AmountOpen;
            if (dialog)
            {
                page.SetAmount(amount);
                yield return new WaitForSecondsRealtime(0.2f);
                page.ConfirmAmount();
            }
            yield return new WaitForSecondsRealtime(0.3f);
            int after = inv.Count(item);
            var fresh = _session.Items.World.AllDropped.FirstOrDefault(d => !known.Contains(d.Id));
            var hero = Hero != null ? Hero.transform.position : Vector3.zero;
            float dist = fresh != null ? Vector2.Distance(new Vector2(fresh.Position[0], fresh.Position[2]), new Vector2(hero.x, hero.z)) : -1f;
            Log($"invdrop {item} at {at} ({point.Value.x:0},{point.Value.y:0}{(under != null ? " on " + under : "")}): dialog {(dialog ? "yes" : "no")}, " +
                $"carried {before} -> {after}, dropped {(fresh != null ? $"{fresh.Id} x{fresh.Amount} {dist:0.00} m from the hero" : "-")}");
            string expect = st.Get("expect", at == "sidebar" ? "kept" : "dropped");
            if (expect == "refused" || expect == "kept")
            {
                if (dialog || after != before || fresh != null) Fail($"invdrop: {item} should have stayed in the inventory ({expect})");
            }
            else if (fresh == null || fresh.Item != item || fresh.Amount != amount || after != before - amount)
                Fail($"invdrop: expected {amount} {item} on the ground and {before - amount} carried");
            else if (dist > 2f) Fail($"invdrop: the item landed {dist:0.00} m from the hero");
            if (st.Bool("close", true) && screens.Current == page) screens.CloseCurrent();
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>
        /// `invsort [clicks=4] [close=1]` (fix-92) — opens the inventory and clicks the selected category tab `clicks` times
        /// (a real click event): each click moves the sort mode on (G3, name, value, newest, G3), the tab carries the mode's
        /// theme arrow (none in G3 order) and the grid lists the tab's rows in that mode. 4 clicks end where they began.
        /// </summary>
        IEnumerator InvSortStep(ScenarioStep st)
        {
            var screens = FindAnyObjectByType<GameScreens>();
            var page = screens != null ? screens.ByAction(G3.Player.SessionKey.InventoryMode) as G3InventoryScreen : null;
            if (page == null) { Fail("invsort: no G3 inventory page (G3 GUI not imported?)"); yield break; }
            HideTips();
            if (screens.Current != page) screens.Toggle(G3.Player.SessionKey.InventoryMode);
            yield return new WaitForSecondsRealtime(0.4f);
            if (screens.Current != page) { Fail("invsort: the inventory did not open"); yield break; }
            int clicks = st.Int("clicks", 4);
            var start = G3SortToggle.Mode;
            if (page.Tabs.SortMark != ItemSorting.GlyphPart(start))
                Fail($"invsort: mark {page.Tabs.SortMark ?? "none"} on open, expected {ItemSorting.GlyphPart(start) ?? "none"}");
            for (int i = 0; i < clicks; i++)
            {
                var was = G3SortToggle.Mode;
                page.Tabs.ClickSelected();
                yield return null;
                yield return null;
                var now = G3SortToggle.Mode;
                var expected = page.Tabs.Filter(ItemLists.Of(_session, ActorId.Player, sort: now)).Select(r => r.Item).ToList();
                var listed = page.Listed;
                bool inOrder = listed.SequenceEqual(expected);
                Log($"invsort click {i + 1}: {was} -> {now}, mark {page.Tabs.SortMark ?? "none"}, {listed.Count} rows " +
                    $"{(inOrder ? "in order" : "OUT OF ORDER")}: {string.Join(",", listed.Take(6))}");
                if (now != ItemSorting.Next(was)) Fail($"invsort: click {i + 1} left the mode at {now}, expected {ItemSorting.Next(was)}");
                if (page.Tabs.SortMark != ItemSorting.GlyphPart(now))
                    Fail($"invsort: mark {page.Tabs.SortMark ?? "none"} in {now}, expected {ItemSorting.GlyphPart(now) ?? "none"}");
                if (!inOrder) Fail($"invsort: the grid is not in {now} order");
            }
            if (st.Bool("close", true) && screens.Current == page) screens.CloseCurrent();
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>The first point of a coarse screen raster (centre rows first) that <paramref name="ok"/> accepts.</summary>
        static Vector2? ScreenPoint(Func<Vector2, bool> ok, int cols = 32, int rows = 24)
        {
            for (int k = 0; k < rows; k++)
            {
                int r = rows / 2 + ((k & 1) == 0 ? k / 2 : -(k / 2 + 1)); // 12, 11, 13, 10 ...
                for (int c = 0; c < cols; c++)
                {
                    var p = new Vector2((c + 0.5f) * Screen.width / cols, (r + 0.5f) * Screen.height / rows);
                    if (ok(p)) return p;
                }
            }
            return null;
        }

        /// <summary>A screen point well inside the displayed element <paramref name="name"/> of any UI document, or null.</summary>
        static Vector2? ScreenPointOn(string name)
        {
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                var e = doc != null && doc.isActiveAndEnabled ? doc.rootVisualElement?.Q(name) : null;
                if (e?.panel == null || e.resolvedStyle.display == DisplayStyle.None) continue;
                var b = e.worldBound;
                var inner = new Rect(b.x + b.width * 0.3f, b.y + b.height * 0.3f, b.width * 0.4f, b.height * 0.4f);
                var panel = e.panel;
                var p = ScreenPoint(s => inner.Contains(RuntimePanelUtils.ScreenToPanel(panel, s)), 128, 96);
                if (p != null) return p;
            }
            return null;
        }
    }
}
#endif
