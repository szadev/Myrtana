using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero's HP, SP and MP bars and the locked target's HP bar (Docs/combat.md §9), using the existing G3 status bar
    /// element (<see cref="G3StatusBar"/>) as it is, at the bar slots of G3's HUD layout (<c>IDD_PAGE_MAIN_2</c>:
    /// <c>IDC_VIEW_BAR_HEALTH_2</c>, <c>…ENDURANCE_2</c>, <c>…MANA_2</c>, <c>…FOCUS_2</c>) on a G3-skinned panel. Without the
    /// imported G3 GUI the bars sit unskinned at the bottom left of the plain HUD. Values come from <see cref="ActorVitals"/>.
    /// The focus bar shows while a target is locked.
    /// </summary>
    public sealed class VitalsHud : MonoBehaviour, ISessionView
    {
        const string HudLayout = "IDD_PAGE_MAIN_2";
        const int SortingOrder = 101; // above the plain HUD (100), below the dialog (110)

        G3StatusBar _hp, _sp, _mp, _focus;
        PlayerCombat _player;
        GameSession _session;
        int _lastHp = -1, _lastSp = -1, _lastMp = -1, _lastFocus = -1;
        ActorId _lastFocusActor;
        bool _vitalsShown = true;

        /// <summary>
        /// The actor whose HP the focus bar shows (empty = none). Default: the locked combat target. The G3 HUD replaces it
        /// with G3's focus (locked target, dialog partner or talk focus).
        /// </summary>
        public System.Func<ActorId> FocusSource { get; set; }

        /// <summary>The hero's HP/SP/MP bars may show (the G3 HUD hides them during dialogs and on trade).</summary>
        public bool VitalsVisible { get; set; } = true;

        /// <summary>The focus bar may show (the G3 HUD hides it on pages).</summary>
        public bool FocusVisible { get; set; } = true;

        public void Init(VisualElement plainHud, PlayerCombat player)
        {
            _player = player;
            var skin = G3Skin.Load();
            var layout = skin?.Catalog.Screen(HudLayout);
            if (layout != null)
            {
                var root = G3PanelSettings.CreateDocument("Vitals HUD", GuiPanelKind.Hud, transform, SortingOrder).rootVisualElement;
                _hp = Slot(root, layout, "IDC_VIEW_BAR_HEALTH_2", StatusBarKind.Health);
                _sp = Slot(root, layout, "IDC_VIEW_BAR_ENDURANCE_2", StatusBarKind.Stamina);
                _mp = Slot(root, layout, "IDC_VIEW_BAR_MANA_2", StatusBarKind.Mana);
                _focus = Slot(root, layout, "IDC_VIEW_BAR_FOCUS_2", StatusBarKind.Focus);
            }
            if (_hp == null)
            {
                var column = new VisualElement { name = "vitals", pickingMode = PickingMode.Ignore };
                column.style.position = Position.Absolute;
                column.style.left = 24;
                column.style.bottom = 24;
                _hp = Add(column, StatusBarKind.Health);
                _sp = Add(column, StatusBarKind.Stamina);
                _mp = Add(column, StatusBarKind.Mana);
                _focus = Add(column, StatusBarKind.Focus);
                plainHud.Add(column);
            }
            Show(_focus, false);
        }

        static G3StatusBar Slot(VisualElement root, WindowData layout, string slot, StatusBarKind kind)
        {
            var w = layout.Find(slot);
            if (w == null) return null;
            var host = new VisualElement { pickingMode = PickingMode.Ignore };
            G3LayoutBuilder.Place(host, w, layout.Width, layout.Height);
            var bar = new G3StatusBar(kind);
            host.Add(bar);
            root.Add(host);
            return bar;
        }

        static G3StatusBar Add(VisualElement parent, StatusBarKind kind)
        {
            var bar = new G3StatusBar(kind);
            bar.style.marginTop = 4;
            parent.Add(bar);
            return bar;
        }

        readonly System.Collections.Generic.Dictionary<G3StatusBar, G3Fade> _fades = new System.Collections.Generic.Dictionary<G3StatusBar, G3Fade>();

        /// <summary>G3 grows the bars out of their centre and shrinks them back over 200 ms (AnimateWindow AW_CENTER).</summary>
        void Show(G3StatusBar bar, bool on)
        {
            if (bar == null) return;
            if (!_fades.TryGetValue(bar, out var fade))
            {
                fade = new G3Fade(bar.parent ?? bar, G3FadeKind.Center, shown: on);
                _fades.Add(bar, fade);
                return;
            }
            fade.Show(on);
        }

        public void Bind(GameSession session)
        {
            _session = session;
            _lastHp = _lastSp = _lastMp = _lastFocus = -1;
            Refresh(immediate: true);
        }

        void Update() => Refresh(immediate: false);

        void Refresh(bool immediate)
        {
            if (_session == null || _hp == null) return;
            var v = _session.Combat.Vitals;
            var p = ActorId.Player;
            Set(_hp, ref _lastHp, v.HitPoints(p), v.HitPointsMax(p), immediate);
            Set(_sp, ref _lastSp, v.Stamina(p), v.StaminaMax(p), immediate);
            Set(_mp, ref _lastMp, v.Mana(p), v.ManaMax(p), immediate);
            // G3 HUD colour override [0x100a5a50]: HP teal while poisoned (status bit 4), SP grey while diseased (bit 8) (fix-127)
            var status = _session.Magic.Status;
            _hp.Poisoned = status.IsPoisoned(p);
            if (_sp != null) _sp.Exhausted = status.Has(p, ActorStatus.Diseased);
            if (VitalsVisible != _vitalsShown)
            {
                _vitalsShown = VitalsVisible;
                Show(_hp, _vitalsShown);
                Show(_sp, _vitalsShown);
                Show(_mp, _vitalsShown);
            }

            if (_focus == null) return;
            var focus = FocusSource != null ? FocusSource() : _player != null && _player.Focus != null ? _player.Focus.Actor : default;
            bool shown = FocusVisible && !focus.IsEmpty && v.HitPointsMax(focus) > 0;
            Show(_focus, shown);
            if (!shown) return;
            if (!focus.Equals(_lastFocusActor)) { _lastFocusActor = focus; _lastFocus = -1; immediate = true; }
            Set(_focus, ref _lastFocus, v.HitPoints(focus), v.HitPointsMax(focus), immediate);
            _focus.Poisoned = status.IsPoisoned(focus);
        }

        static void Set(G3StatusBar bar, ref int last, int value, int max, bool immediate)
        {
            if (bar == null || (value == last && !immediate)) return;
            last = value;
            bar.SetValue(value, max, immediate);
        }
    }
}
