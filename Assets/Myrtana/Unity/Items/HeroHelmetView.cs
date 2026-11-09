using System;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Puts the hero's worn helmet (<see cref="WornSlot.Helmet"/>) on his head through a <see cref="HelmetView"/> on the
    /// player (fix-220): on bind (new game, load) and on every helmet <see cref="EquipmentChanged"/>; taking it off or
    /// dropping it hides the helmet and brings the hair back. The view hangs under the player, so a transformation or the
    /// soul-travel body copy carries it like the rest of the hero model. fix-232 [owner]: with <see cref="ShowHelmet"/> off (the
    /// port option "Show helmet") nothing is drawn and the hair shows; switching it redraws at once.
    /// </summary>
    public sealed class HeroHelmetView : ISessionView
    {
        readonly Transform _hero;
        readonly ItemVisualCatalog _catalog;
        HelmetView _view;
        IDisposable _subscription;
        GameSession _session;
        static bool s_showHelmet = true;
        static event Action ShowHelmetChanged;

        /// <summary>fix-232: the port option "Show helmet" (<c>PortOptionsApplier</c>); default on = G3.</summary>
        public static bool ShowHelmet
        {
            get => s_showHelmet;
            set
            {
                if (s_showHelmet == value) return;
                s_showHelmet = value;
                ShowHelmetChanged?.Invoke();
            }
        }

        public HeroHelmetView(Transform hero, ItemVisualCatalog catalog)
        {
            _hero = hero;
            _catalog = catalog;
        }

        public void Bind(GameSession session)
        {
            _subscription?.Dispose();
            _session = session;
            _subscription = session.Events.Subscribe<EquipmentChanged>(e =>
            {
                if (e.Slot == WornSlot.Helmet) Show(e.Item);
            });
            ShowHelmetChanged -= Refresh;
            ShowHelmetChanged += Refresh;
            Show(session.Items.Equipment.In(WornSlot.Helmet));
        }

        void Refresh()
        {
            if (_hero == null) { ShowHelmetChanged -= Refresh; return; }
            if (_session != null) Show(_session.Items.Equipment.In(WornSlot.Helmet));
        }

        void Show(string helmet)
        {
            if (_hero == null) return;
            if (_view == null) _view = _hero.TryGetComponent<HelmetView>(out var v) ? v : _hero.gameObject.AddComponent<HelmetView>();
            ItemDef def = null;
            if (helmet != null) _session.Content.Database.TryGet(helmet, out def);
            string key = HelmetVisualRules.ShownMeshKey(def, ShowHelmet);
            if (key != null) _view.Show(key, _catalog);
            else _view.Hide();
        }
    }
}
