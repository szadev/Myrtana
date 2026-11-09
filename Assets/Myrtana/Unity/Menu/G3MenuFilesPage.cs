using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The save and load list (<c>IDD_MENU_FILES_2</c>) in G3's page (background, Load/Save, Delete and Back buttons, the
    /// list control's rect and scroll bar). The rows are the port's own (fix-107 / fix-112 [owner], <see cref="G3SaveList"/>,
    /// <see cref="SaveListLayout"/>: seven instead of G3's five 530×106 items): picture, name and real date, then game
    /// time and place (<see cref="SaveGameList"/>); loading puts the quicksave and the autosave side by side on the
    /// first line, "New Savegame" is the first line when saving. One click selects; Load/Save, a
    /// double click or Enter act on the selected row; Delete (button: any save; Del key: numbered saves only) and
    /// overwriting ask first (G3's confirm texts and box).
    /// </summary>
    public sealed class G3MenuFilesPage
    {
        static readonly TextId LoadText = new TextId("HUD_File_Load"), SaveText = new TextId("HUD_File_Save"),
                               DeleteText = new TextId("HUD_File_Delete"), BackText = new TextId("HUD_Menu_Back"),
                               OverwriteText = new TextId("HUD_Confirm_Overwrite"), DeleteConfirmText = new TextId("HUD_Confirm_Delete");

        readonly VisualElement _page;
        readonly G3MenuView _view;
        readonly G3SaveList _list;
        readonly G3Button _act, _delete;
        List<SaveGameList.Row> _rows = new List<SaveGameList.Row>();
        bool _saving, _visible;

        public G3MenuFilesPage(VisualElement page, G3MenuView view)
        {
            _page = page;
            _view = view;
            G3MenuView.Hide(page, "IDC_VIEW_MENU_FILES_REQUEST_2");
            _act = view.Button(page, "IDC_BUTTON_MENU_FILES_LOAD_SAVE_2", LoadText, Act);
            _delete = view.Button(page, "IDC_BUTTON_MENU_FILES_DELETE_2", DeleteText, Delete);
            view.Button(page, "IDC_BUTTON_MENU_FILES_BACK_2", BackText, () => view.Menu.State.Back());

            _list = new G3SaveList { KeysBlocked = () => _view.Menu == null || _view.Menu.State.Request != null };
            _list.Activated += _ => Act();
            _list.SelectionChanged += _ => UpdateButtons();
            _list.DeleteRequested += _ => DeleteByKey();
            _list.Scrolled += _ => RequestVisiblePictures();
            var slot = page.Q("IDC_LISTCTRL_MENU_FILES_2");
            if (slot != null)
            {
                slot.pickingMode = PickingMode.Position;
                slot.Add(_list);
            }
        }

        public IReadOnlyList<SaveGameList.Row> Rows => _rows;
        public G3SaveList List => _list;

        public SaveGameList.Row Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count ? _rows[_list.SelectedIndex] : null;

        /// <summary>
        /// Shows the page as the save list or the load list; the rows are brought up to date with the disk each time it
        /// opens (fix-222: from the save index, only changed files are opened; the pictures of the rows in view follow,
        /// read in the background).
        /// </summary>
        public void Show(bool visible, bool saving)
        {
            bool reopen = visible && (!_visible || saving != _saving);
            _visible = visible;
            _saving = saving;
            G3MenuView.Show(_page, visible);
            if (!visible) return;
            // the button text is a binding of the view; switch it between Load and Save
            if (_act != null) _view.Rebind(_act, saving ? SaveText : LoadText);
            if (reopen) Reload(null, 0);
            else Relabel();
            if (_view.Menu?.State.Request == null) _list.Focus(); // keys go to the list again after a confirm box
        }

        /// <summary>Reads the rows; keeps <paramref name="keepSlot"/> selected, else the row at <paramref name="fallbackIndex"/>.</summary>
        void Reload(string keepSlot, int fallbackIndex)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            HookPictures();
            _rows = SaveGameList.For(_saving, _view.Menu.Saves.Slots(rescan: true), _view.Localizer, _view.Languages?.TextLanguage);
            // fix-112 [owner]: quicksave (left) and autosave (right) share the first line of the Load page
            _list.SetLayout(_rows.ConvertAll(r => r.Kind == SaveSlotKind.Quick ? 0 : r.Kind == SaveSlotKind.Auto ? 1 : -1));
            _list.ShowRow(0);
            ApplyTexts();
            int keep = keepSlot != null ? _rows.FindIndex(r => r.Slot == keepSlot) : -1;
            _list.Select(keep >= 0 ? keep : System.Math.Min(fallbackIndex, _rows.Count - 1), notify: false);
            UpdateButtons();
            _list.Focus();
            UnityEngine.Debug.Log($"[Myrtana] {(_saving ? "save" : "load")} page: {_rows.Count} rows in {watch.Elapsed.TotalMilliseconds:0.0} ms");
        }

        /// <summary>Row texts in the current language (the rows themselves stay).</summary>
        void Relabel()
        {
            var fresh = SaveGameList.For(_saving, _view.Menu.Saves.Slots(), _view.Localizer, _view.Languages?.TextLanguage);
            if (fresh.Count != _rows.Count)
            {
                Reload(Selected?.Slot, _list.SelectedIndex);
                return;
            }
            _rows = fresh;
            ApplyTexts();
            UpdateButtons();
        }

        void ApplyTexts()
        {
            var saves = _view.Menu.Saves;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                // fix-222: only a picture already read; the rows in view ask for theirs (RequestVisiblePictures)
                var picture = r.Kind == SaveSlotKind.New || !saves.HasThumbnail(r.Slot) ? null : saves.Thumbnail(r.Slot);
                _list.Bind(i, r.Name, r.Detail, r.Date, picture, showFrame: r.Kind != SaveSlotKind.New);
            }
            RequestVisiblePictures();
        }

        /// <summary>Lines of pictures read beyond the visible ones (a wheel step or a page key shows them at once).</summary>
        const int PictureMargin = 7;
        SaveGameController _hooked;

        void HookPictures()
        {
            var saves = _view.Menu?.Saves;
            if (saves == null || ReferenceEquals(saves, _hooked)) return;
            if (_hooked != null) _hooked.ThumbnailReady -= OnPicture;
            _hooked = saves;
            saves.ThumbnailReady += OnPicture;
        }

        /// <summary>fix-222: asks for the pictures of the rows in view (and a page around them) not read yet.</summary>
        void RequestVisiblePictures()
        {
            if (!_visible || _list.Grid == null || _view.Menu == null) return;
            var saves = _view.Menu.Saves;
            foreach (int i in _list.Grid.ItemsOnLines(_list.FirstRow, _list.VisibleRows, PictureMargin))
            {
                if (i >= _rows.Count || _rows[i].Kind == SaveSlotKind.New || saves.HasThumbnail(_rows[i].Slot)) continue;
                saves.Thumbnail(_rows[i].Slot); // requests it
            }
        }

        void OnPicture(string slot)
        {
            if (!_visible || _hooked == null) return;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Slot == slot && _rows[i].Kind != SaveSlotKind.New)
                    _list.SetPicture(i, _hooked.Thumbnail(slot), showFrame: true);
        }

        void UpdateButtons()
        {
            var row = Selected;
            G3MenuView.SetDisabled(_act, row == null);
            G3MenuView.SetDisabled(_delete, row == null || row.Kind == SaveSlotKind.New);
        }

        /// <summary>Load or save the selected row (Load/Save button, double click, Enter).</summary>
        public void Act()
        {
            var row = Selected;
            if (row == null) return;
            var menu = _view.Menu;
            if (!_saving) menu.Load(row.Slot);
            else if (row.Kind == SaveSlotKind.New) menu.SaveTo(row.Slot);
            else menu.State.Ask(new MenuRequest(OverwriteText, () => menu.SaveTo(row.Slot)));
        }

        /// <summary>The Delete button: any listed save (not the new slot), after G3's question.</summary>
        void Delete()
        {
            var row = Selected;
            if (row == null || row.Kind == SaveSlotKind.New) return;
            AskDelete(row);
        }

        /// <summary>The Del key: numbered saves only (fix-107 [owner]: the quicksave and autosave stay).</summary>
        void DeleteByKey()
        {
            var row = Selected;
            if (row == null || !row.DeletableByKey) return;
            AskDelete(row);
        }

        void AskDelete(SaveGameList.Row row)
        {
            var menu = _view.Menu;
            int index = _list.SelectedIndex;
            menu.State.Ask(new MenuRequest(DeleteConfirmText, () =>
            {
                menu.Saves.Delete(row.Slot);
                Reload(null, index); // the next row moves up into the selection
            }));
        }
    }
}
