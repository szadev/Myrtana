using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The character page in the original look (<c>IDD_PAGE_SKILLS_2</c>, Docs/formats-gui.md §6.8): the character view
    /// (progress, attributes, armour protection) and the skills view with seven tile grids by skill group, unlearned
    /// skills grey. One selection across the grids shows the skill box (name, description, requirements).
    /// </summary>
    public sealed class G3CharacterScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_SKILLS_2";
        const int HeaderFont = 24, RequirementFont = 14, LabelGap = 6;

        /// <summary>Grid and header controls → skill group of the content and its title string.</summary>
        static readonly (string control, string group, TextId title)[] Groups =
        {
            ("COMBAT", "Skill0", new TextId("HL_Skills_STR")), ("HUNT", "Skill1", new TextId("HL_Skills_DEX")),
            ("MAGIC", "Skill2", new TextId("HL_Skills_INT")), ("SMITHING", "Skill3", new TextId("HL_Skills_SMT")),
            ("THEFT", "Skill4", new TextId("HL_Skills_THF")), ("ALCHEMY", "Skill5", new TextId("HL_Skills_ALC")),
            ("MISC", "Skill6", new TextId("HL_Skills_Misc")),
        };

        readonly G3CharacterView _character;
        readonly List<(G3ItemGrid grid, string group)> _grids = new List<(G3ItemGrid, string)>();
        readonly VisualElement _skillBox;
        SkillSheet _skills;
        (int grid, int index) _selected = (-1, -1);

        public G3CharacterScreen(VisualElement pagesRoot) : base(pagesRoot, G3Pages.Build(Layout))
        {
            _character = new G3CharacterView(Root.Q("IDD_VIEW_CHARACTER_2") ?? Root.Q("IDC_VIEW_CHARACTER_2"));
            foreach (var (control, group, _) in Groups)
            {
                var grid = G3Pages.Grid(Root, "IDC_LISTCTRL_" + control + "_2", scrollable: false);
                if (grid == null) continue;
                int gridIndex = _grids.Count;
                grid.SelectionChanged += index => Select(gridIndex, index);
                _grids.Add((grid, group));
            }
            _skillBox = Root.Q("IDC_VIEW_SKILL_2");
        }

        protected override void OnOpen() => _selected = (-1, -1);

        public override void Render()
        {
            var text = Session.Localizer;
            _character.Render(CharacterSheet.Of(Session));
            _skills = SkillSheet.Of(Session);
            foreach (var (control, _, title) in Groups) G3Pages.SetText(Root, "IDC_LABEL_" + control + "_2", text.Text(title));
            Root.schedule.Execute(() =>
            {
                foreach (var (control, _, _) in Groups)
                    if (Root.Q("IDC_LABEL_" + control + "_2") is Label header && header.userData is WindowData w)
                        G3Text.FitWidth(header, w.Width, HeaderFont);
            });
            for (int i = 0; i < _grids.Count; i++)
            {
                var (grid, group) = _grids[i];
                grid.SetItems(_skills.Of(group).Select(s => new ItemTileModel
                {
                    Texture = s.Icon, Category = ItemIconCategory.Skill, Learned = s.Learned, CountOnly = true,
                }).ToList());
                grid.Select(i == _selected.grid ? _selected.index : -1, notify: false);
            }
            ShowSkill();
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Character;

        public override void Tick()
        {
            var keys = PageKeys.Now; // gamepad-1: keyboard or pad (LT/RT or LB/RB = the grids)
            if (_grids.Count == 0) return;
            int g = _selected.grid < 0 ? 0 : _selected.grid;
            var grid = _grids[g].grid;
            int s = _selected.grid < 0 ? -1 : _selected.index;
            if (keys.GridMove(s, grid.Count, grid.Columns) is int moved) grid.Select(moved);
            if (keys.PageNext || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabNext)) _grids[(g + 1) % _grids.Count].grid.Select(0);
            if (keys.PagePrev || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabPrev)) _grids[(g + _grids.Count - 1) % _grids.Count].grid.Select(0);
        }

        void Select(int grid, int index)
        {
            if (index < 0 && grid != _selected.grid) return;
            _selected = (grid, index);
            for (int i = 0; i < _grids.Count; i++)
                if (i != grid) _grids[i].grid.Select(-1, notify: false);
            ShowSkill();
        }

        void ShowSkill()
        {
            SkillSheet.Skill skill = null;
            if (_selected.grid >= 0 && _selected.index >= 0)
            {
                var list = _skills?.Of(_grids[_selected.grid].group);
                if (list != null && _selected.index < list.Count) skill = list[_selected.index];
            }
            if (_skillBox == null) return;
            _skillBox.style.visibility = skill == null ? Visibility.Hidden : Visibility.Visible;
            if (skill == null) return;
            G3Pages.SetText(_skillBox, "IDC_LABEL_SKILL_2", skill.Name);
            G3Pages.SetText(_skillBox, "IDC_LABEL_SKILL_DESC_2", skill.Description);
            ShowRequirements(skill.Requirements);
        }

        /// <summary>
        /// The requirement table (G3 gCHUDTableSkillStats in IDC_LABEL_SKILL_REQ_2, TNR 14 regular): label left, value
        /// right, one row per font height, all rows #FFE6E6, a 4 px gap after each section; the last section ([port]) is the
        /// learning-point cost.
        /// </summary>
        void ShowRequirements(IReadOnlyList<IReadOnlyList<LabelledValue>> sections)
        {
            var slot = _skillBox.Q("IDC_LABEL_SKILL_REQ_2");
            if (slot == null) return;
            if (slot is Label text) text.text = string.Empty;
            slot.style.overflow = Overflow.Visible; // up to 2 attributes + skill + [port] LP + gaps overflow the 50 px rect; G3 draws unclipped
            var table = slot.Q("rows");
            if (table == null)
            {
                table = new VisualElement { name = "rows", pickingMode = PickingMode.Ignore };
                table.AddToClassList("g3-fill-parent");
                slot.Add(table);
            }
            table.Clear();
            var color = G3Palette.SkillStats.ToColor();
            foreach (var section in sections)
            {
                foreach (var r in section)
                {
                    var row = new VisualElement { pickingMode = PickingMode.Ignore };
                    row.AddToClassList("g3-stat-row");
                    row.style.height = G3Text.LineHeight(RequirementFont);
                    row.style.flexShrink = 0;
                    row.style.fontSize = RequirementFont;
                    row.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Normal;
                    var label = new Label(r.Label) { pickingMode = PickingMode.Ignore };
                    label.AddToClassList("g3-stat-row__label");
                    var value = new Label(r.Value) { pickingMode = PickingMode.Ignore };
                    value.AddToClassList("g3-stat-row__value");
                    foreach (var cell in new[] { label, value })
                    {
                        cell.style.color = color;
                        G3Text.Shadow(cell);
                        row.Add(cell);
                    }
                    table.Add(row);
                    label.schedule.Execute(() => G3Text.FitWidth(label, row.layout.width - G3Text.TextWidth(value) - LabelGap, RequirementFont));
                }
                var gap = new VisualElement { pickingMode = PickingMode.Ignore };
                gap.style.height = ItemStatColors.SectionGap;
                gap.style.flexShrink = 0;
                table.Add(gap);
            }
        }
    }
}
