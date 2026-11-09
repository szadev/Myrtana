#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Globalization;
using System.Linq;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-125 probes and ops (Docs/dev-scenarios.md): the station recipe list ([owner] dimmed rows the hero cannot make,
    /// "×n" on the others) and its whole-row viewport (no half row at the bottom).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        G3SynthScreen OpenSynth() =>
            FindAnyObjectByType<GameScreens>() is GameScreens screens && screens.Current is G3SynthScreen synth ? synth : null;

        /// <summary>'synthselect n=': selects that row of the open craft page (the list scrolls to it).</summary>
        void SynthSelect(ScenarioStep st)
        {
            var page = OpenSynth();
            if (page == null) { Fail("synthselect: no craft page open"); return; }
            page.List.Select(st.Int("n", 0));
            Log($"synthselect {page.List.SelectedIndex}: first row {page.List.FirstRow}, {page.List.VisibleRows} shown");
        }

        /// <summary>
        /// synth (rows), synthcraftable / synthdimmed (rows the hero can make / dimmed rows), synthvisible (whole rows shown),
        /// synthfirst (top row), synthhalfrows (rows cut by the viewport edge), synthrow:&lt;recipe&gt; ("×n", "dim", "-" = not
        /// listed; "!" appended when the drawn row disagrees with the view model).
        /// </summary>
        string SynthProbe(string kind, string arg)
        {
            var page = OpenSynth();
            if (page == null) return "-";
            var list = page.List;
            var rows = page.RecipeRows;
            var inv = CultureInfo.InvariantCulture;
            switch (kind)
            {
                case "synthcraftable": return rows.Count(r => r.Craftable).ToString(inv);
                case "synthdimmed": return list.Rows.Count(r => r.Unavailable).ToString(inv);
                case "synthvisible": return list.VisibleRows.ToString(inv);
                case "synthfirst": return list.FirstRow.ToString(inv);
                case "synthhalfrows":
                {
                    // fix-136: a row counts as cut when more than 1.5 px of it is hidden and more than 1.5 px shown. Layout
                    // and the content offset snap to device pixels (scale 1080/768: an odd row's 48 px pitch lands on half a
                    // pixel), up to ~0.7 px together, so the old 0.5 px tolerance reported whole rows as cut.
                    const float Tolerance = 1.5f;
                    var view = list.ScrollView.contentViewport.worldBound;
                    int cut = 0;
                    foreach (var row in list.Rows)
                    {
                        var b = row.worldBound;
                        float shown = Mathf.Min(b.yMax, view.yMax) - Mathf.Max(b.yMin, view.yMin);
                        if (shown > Tolerance && shown < b.height - Tolerance) cut++;
                    }
                    return cut.ToString(inv);
                }
                case "synthrow":
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].Recipe.Id != arg) continue;
                        var ui = i < list.Rows.Count ? list.Rows[i] : null;
                        string value = rows[i].Craftable ? rows[i].CountText : "dim";
                        bool agrees = ui != null && ui.Unavailable == !rows[i].Craftable && (ui.Count?.text ?? string.Empty) == rows[i].CountText;
                        return agrees ? value : value + "!";
                    }
                    return "-";
                }
                default: return rows.Count.ToString(inv);
            }
        }
    }
}
#endif
