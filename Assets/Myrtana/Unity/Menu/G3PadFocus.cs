using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using G3.Player;
using G3.Unity;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// gamepad-1 (Docs/gamepad.md §4): pad focus for button pages — the game menu and its option pages, which G3 drives by
    /// mouse only. The candidates are the visible, enabled buttons and sliders under a root, plus elements registered with
    /// <see cref="MakeFocusable"/> (radio rows, key list cells) or <see cref="MakeComposite"/> (the save list: it moves its
    /// own selection first). The d-pad / left stick moves the focus to the nearest candidate in that direction
    /// (<see cref="SpatialNav"/>), left/right change a focused slider, A presses. A visible element with
    /// <see cref="ModalClass"/> (the confirm box) keeps the focus inside it. A gold ring marks the focus while the pad is
    /// the device in use.
    /// </summary>
    public sealed class G3PadFocus
    {
        public const string FocusableClass = "g3-pad-focusable", ModalClass = "g3-pad-modal";

        /// <summary>A widget with its own inner selection (the save list).</summary>
        public interface IComposite
        {
            /// <summary>Moves the inner selection; false at its edge (the focus leaves the widget then).</summary>
            bool Move(NavDir dir);
            void Accept();
            /// <summary>Y: the widget's third action (delete a save).</summary>
            void Tertiary();
        }

        sealed class Actions
        {
            public Action Accept, Secondary, Tertiary;
        }

        static readonly ConditionalWeakTable<VisualElement, Actions> s_actions = new ConditionalWeakTable<VisualElement, Actions>();
        static readonly ConditionalWeakTable<VisualElement, IComposite> s_composites = new ConditionalWeakTable<VisualElement, IComposite>();

        /// <summary>
        /// Lets the pad focus and press a custom clickable (A = <paramref name="accept"/>, X = <paramref name="secondary"/>,
        /// Y = <paramref name="tertiary"/>).
        /// </summary>
        public static void MakeFocusable(VisualElement element, Action accept, Action secondary = null, Action tertiary = null)
        {
            if (element == null) return;
            element.AddToClassList(FocusableClass);
            s_actions.Remove(element);
            s_actions.Add(element, new Actions { Accept = accept, Secondary = secondary, Tertiary = tertiary });
        }

        public static void MakeComposite(VisualElement element, IComposite composite)
        {
            if (element == null) return;
            element.AddToClassList(FocusableClass);
            s_composites.Remove(element);
            s_composites.Add(element, composite);
        }

        readonly VisualElement _root;
        readonly VisualElement _ring;
        readonly List<VisualElement> _candidates = new List<VisualElement>();
        readonly List<NavRect> _rects = new List<NavRect>();
        VisualElement _focus;

        public G3PadFocus(VisualElement root)
        {
            _root = root;
            _ring = new VisualElement { name = "g3-pad-focus-ring", pickingMode = PickingMode.Ignore };
            _ring.style.position = Position.Absolute;
            var gold = new Color(0.85f, 0.69f, 0.38f, 0.95f);
            _ring.style.borderTopWidth = _ring.style.borderBottomWidth = _ring.style.borderLeftWidth = _ring.style.borderRightWidth = 2f;
            _ring.style.borderTopColor = _ring.style.borderBottomColor = _ring.style.borderLeftColor = _ring.style.borderRightColor = gold;
            _ring.style.borderTopLeftRadius = _ring.style.borderTopRightRadius = _ring.style.borderBottomLeftRadius =
                _ring.style.borderBottomRightRadius = 3f;
            _ring.style.display = DisplayStyle.None;
            root?.Add(_ring);
        }

        /// <summary>The focused element (null before the pad was used on the page).</summary>
        public VisualElement Focused => _focus;

        /// <summary>
        /// One frame: <paramref name="open"/> = the page takes input now. Moves and presses act only when the pad is in
        /// use; the ring shows only then.
        /// </summary>
        public void Tick(bool open)
        {
            if (_root == null) return;
            bool pad = open && G3Pad.PadIsLast;
            if (!pad)
            {
                _ring.style.display = DisplayStyle.None;
                return;
            }
            Collect();
            if (_candidates.Count == 0)
            {
                _focus = null;
                _ring.style.display = DisplayStyle.None;
                return;
            }
            int at = _focus != null ? _candidates.IndexOf(_focus) : -1;
            if (at < 0)
            {
                at = SpatialNav.First(_rects);
                _focus = _candidates[at];
            }
            var dir = G3Pad.UiMove;
            if (dir != NavDir.None) Move(dir, at);
            if (G3Pad.Ui(UiCommand.Accept)) Press(_focus);
            if (G3Pad.Ui(UiCommand.Secondary) && _focus != null && s_actions.TryGetValue(_focus, out var a)) a.Secondary?.Invoke();
            if (G3Pad.Ui(UiCommand.Tertiary) && _focus != null)
            {
                if (s_composites.TryGetValue(_focus, out var c)) c.Tertiary();
                else if (s_actions.TryGetValue(_focus, out var t)) t.Tertiary?.Invoke();
            }
            Place();
        }

        void Move(NavDir dir, int at)
        {
            if (_focus != null && s_composites.TryGetValue(_focus, out var composite) && composite.Move(dir)) return;
            bool sideways = dir == NavDir.Left || dir == NavDir.Right;
            int step = dir == NavDir.Right ? 1 : -1;
            if (sideways && _focus is SliderInt si)
            {
                si.value = Mathf.Clamp(si.value + step, Mathf.Min(si.lowValue, si.highValue), Mathf.Max(si.lowValue, si.highValue));
                return;
            }
            if (sideways && _focus is Slider sf)
            {
                float range = Mathf.Abs(sf.highValue - sf.lowValue);
                sf.value = Mathf.Clamp(sf.value + step * range / 20f, Mathf.Min(sf.lowValue, sf.highValue), Mathf.Max(sf.lowValue, sf.highValue));
                return;
            }
            int next = SpatialNav.Pick(_rects[at], _rects, dir, at);
            if (next < 0) return;
            _focus = _candidates[next];
            Reveal(_focus);
        }

        static void Press(VisualElement e)
        {
            if (e == null) return;
            if (s_composites.TryGetValue(e, out var composite))
            {
                composite.Accept();
                return;
            }
            if (s_actions.TryGetValue(e, out var a))
            {
                a.Accept?.Invoke();
                return;
            }
            if (e is Button b)
            {
                // Button presses on a submit (Clickable handles NavigationSubmitEvent) — the same as a click
                using (var ev = NavigationSubmitEvent.GetPooled())
                {
                    ev.target = b;
                    b.SendEvent(ev);
                }
            }
        }

        /// <summary>Scrolls a focused element inside a ScrollView into view.</summary>
        static void Reveal(VisualElement e)
        {
            for (var p = e.parent; p != null; p = p.parent)
                if (p is ScrollView sv)
                {
                    sv.ScrollTo(e);
                    return;
                }
        }

        void Collect()
        {
            _candidates.Clear();
            _rects.Clear();
            VisualElement scope = _root;
            // a visible modal (the confirm box) keeps the focus inside it
            _root.Query(className: ModalClass).ForEach(m => { if (scope == _root && Visible(m, _root)) scope = m; });
            scope.Query<VisualElement>().ForEach(e =>
            {
                if (e == _ring || e == scope) return;
                bool kind = e is Button || e is SliderInt || e is Slider || e.ClassListContains(FocusableClass);
                if (!kind || !e.enabledInHierarchy || InsideComposite(e, scope)) return;
                if (!Visible(e, _root)) return;
                var r = e.worldBound;
                if (r.width < 2f || r.height < 2f || float.IsNaN(r.x)) return;
                _candidates.Add(e);
                _rects.Add(new NavRect(r.x, r.y, r.width, r.height));
            });
        }

        static bool InsideComposite(VisualElement e, VisualElement scope)
        {
            for (var p = e.parent; p != null && p != scope; p = p.parent)
                if (s_composites.TryGetValue(p, out _)) return true;
            return false;
        }

        static bool Visible(VisualElement e, VisualElement root)
        {
            for (var p = e; p != null; p = p.parent)
            {
                var s = p.resolvedStyle;
                if (s.display == DisplayStyle.None || s.visibility == Visibility.Hidden || s.opacity <= 0.01f) return false;
                if (p == root) break;
            }
            return true;
        }

        void Place()
        {
            if (_focus == null)
            {
                _ring.style.display = DisplayStyle.None;
                return;
            }
            var r = _focus.worldBound;
            var local = _root.WorldToLocal(new Vector2(r.x, r.y));
            var scale = _root.worldTransform.lossyScale;
            float sx = Mathf.Abs(scale.x) > 1e-4f ? scale.x : 1f, sy = Mathf.Abs(scale.y) > 1e-4f ? scale.y : 1f;
            const float pad = 3f;
            _ring.style.left = local.x - pad;
            _ring.style.top = local.y - pad;
            _ring.style.width = r.width / sx + 2 * pad;
            _ring.style.height = r.height / sy + 2 * pad;
            _ring.style.display = DisplayStyle.Flex;
            _ring.BringToFront();
        }

        /// <summary>Forgets the focus (the page changed; the next pad use starts at the top).</summary>
        public void Reset() => _focus = null;
    }
}
