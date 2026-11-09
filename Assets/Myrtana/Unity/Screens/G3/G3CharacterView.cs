using System.Collections.Generic;
using Myrtana.Session;
using Myrtana.Unity.Ui.Skin;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Fills the character view (<c>IDD_VIEW_CHARACTER_2</c>: progress, attributes, armour protection) from a
    /// <see cref="CharacterSheet"/>. Used by the character page and the left column of the inventory.
    /// </summary>
    public sealed class G3CharacterView
    {
        /// <summary>Layout control suffix → attribute id.</summary>
        static readonly (string control, string attribute)[] Attributes =
        {
            ("STR", "STR"), ("HNT", "DEX"), ("INT", "INT"), ("SMT", "SMT"), ("THF", "THF"), ("ALC", "ALC"),
            ("HP", "HP"), ("SP", "SP"), ("MP", "MP"),
            ("PRO_BLD", "PROT_BLADE"), ("PRO_IMP", "PROT_IMPACT"), ("PRO_MIS", "PROT_MISSILE"),
            ("PRO_FIR", "PROT_FIRE"), ("PRO_ICE", "PROT_ICE"), ("PRO_LTG", "PROT_LIGHTNING"),
        };

        /// <summary>Row font (16 px bold), the value column's right edge and the labels' left edge in the layout.</summary>
        const float RowFont = 16, ValueRight = 290, LabelLeft = 24, Gap = 6, MinFont = 11, HeaderFont = 24;
        static readonly string[] Headers = { "IDC_LABEL_PROGRESS_2", "IDC_LABEL_ATTRIBUTES_2", "IDC_LABEL_PROTECTIONS_2" };

        readonly VisualElement _view;
        readonly List<(Label label, Label value)> _rows = new List<(Label, Label)>();

        public G3CharacterView(VisualElement view) => _view = view;

        public void Render(CharacterSheet sheet)
        {
            if (_view == null) return;
            G3Pages.SetText(_view, "IDC_LABEL_PROGRESS_2", sheet.ExperienceTitle);
            G3Pages.SetText(_view, "IDC_LABEL_ATTRIBUTES_2", sheet.AttributesTitle);
            G3Pages.SetText(_view, "IDC_LABEL_PROTECTIONS_2", sheet.ProtectionTitle);
            Row("LEVEL", sheet.Level);
            Row("XP", sheet.Xp);
            Row("TNL", sheet.NextLevel);
            Row("LP", sheet.LearningPoints);
            foreach (var (control, attribute) in Attributes)
                Row(control, sheet.ByAttribute.TryGetValue(attribute, out var value) ? value : new LabelledValue(attribute, string.Empty));
            _view.schedule.Execute(FitLabels);
        }

        void Row(string control, LabelledValue value)
        {
            var label = _view.Q<Label>("IDC_LABEL_" + control + "_2");
            var number = _view.Q<Label>("IDC_VALUE_" + control + "_2");
            if (label == null || number == null) return;
            label.text = value.Label;
            number.text = value.Value;
            if (!_rows.Contains((label, number))) _rows.Add((label, number));
        }

        /// <summary>
        /// The layout lets label and value columns overlap (the German labels are short); a longer label (Russian
        /// "Жизненная энергия") is drawn smaller so it ends before its value.
        /// </summary>
        void FitLabels()
        {
            foreach (var (label, number) in _rows)
                G3Text.FitWidth(label, ValueRight - LabelLeft - G3Text.TextWidth(number) - Gap, RowFont, MinFont);
            foreach (var name in Headers)
                if (_view.Q(name) is Label header && header.userData is Myrtana.Ui.Skin.WindowData w) G3Text.FitWidth(header, w.Width, HeaderFont);
        }
    }
}
