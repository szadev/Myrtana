using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The port's locked-chest marks on the map views (fix-102 [owner]; G3 has none), shown only by the maps' chest filter
    /// (fix-116 [owner]: <see cref="Shown"/>, default hidden, kept in PlayerPrefs; fix-126: the chest button of the filter
    /// bar inside the map's top-right corner, <see cref="MapFilterBar"/>). The chests of <see cref="LockedChestMarks.Visible"/>
    /// at their map point (<see cref="MapProjection.TryToMap"/>, as the hero arrow), drawn with G3's chest icon
    /// <c>IT_Chest_Normal</c> 13 px at 80 % opacity (without the GUI data a small rust-red dot with a dark rim); chests a
    /// few pixels apart are one mark with a small count (<see cref="MapChestMarksLayout.Group"/>). Above the map's
    /// markers, under the companions' dots and the hero arrow. No hover text: the map page has no tooltips.
    /// </summary>
    public sealed class LockedChestMapMarks
    {
        public const string Icon = "IT_Chest_Normal.tga";
        const float DotSize = 8f, CountFontSize = 10f;
        static readonly Color DotFill = new Color(0.72f, 0.27f, 0.16f), Rim = new Color(0.08f, 0.06f, 0.03f);

        /// <summary>The maps' chest filter: the marks show (PlayerPrefs; hidden until the player turns them on).</summary>
        public static bool Shown
        {
            get => MapFilters.IsOn(MapFilter.Chests);
            set => MapFilters.Set(MapFilter.Chests, value);
        }

        readonly VisualElement _layer;
        readonly List<(VisualElement mark, Label count)> _marks = new List<(VisualElement, Label)>();
        readonly List<(float x, float y)> _points = new List<(float, float)>();
        bool? _hasIcon;

        public LockedChestMapMarks(VisualElement picture)
        {
            _layer = new VisualElement { name = "map-locked-chests", pickingMode = PickingMode.Ignore };
            _layer.AddToClassList("g3-fill-parent");
            picture.Add(_layer);
        }

        /// <summary>Chests drawn by the last <see cref="Place"/> (for the dev probe <c>mapchestmarks</c>).</summary>
        public int Chests { get; private set; }

        /// <summary>Marks (groups) drawn by the last <see cref="Place"/> (the dev probe <c>mapchestgroups</c>).</summary>
        public int Groups { get; private set; }

        /// <summary>
        /// Places the marks of the discovered locked chests on <paramref name="map"/> (none while the filter hides them,
        /// without a map or a session), grouped in the picture's pixels.
        /// </summary>
        public void Place(GameSession session, MapDef map) => Place(session, map?.Projection);

        /// <summary>The same on a map view whose projection is known without a map item (the quest log's world map, fix-126).</summary>
        public void Place(GameSession session, MapProjection? projection)
        {
            _points.Clear();
            var size = _layer.contentRect.size;
            float w = size.x > 1f && !float.IsNaN(size.x) ? size.x : MapChestMarksLayout.MapWidth;
            float h = size.y > 1f && !float.IsNaN(size.y) ? size.y : MapChestMarksLayout.MapHeight;
            if (Shown && session != null && projection != null)
            {
                foreach (var chest in session.Interaction.ChestMarks.Visible.OrderBy(m => m.Id, StringComparer.Ordinal))
                    if (LockedChestMarks.OnMap(projection.Value, chest, out float u, out float v)) _points.Add((u * w, v * h));
            }
            var groups = MapChestMarksLayout.Group(_points);
            for (int i = 0; i < groups.Count; i++)
            {
                var (mark, count) = Mark(i);
                mark.style.left = Length.Percent(groups[i].X / w * 100f);
                mark.style.top = Length.Percent(groups[i].Y / h * 100f);
                mark.style.display = DisplayStyle.Flex;
                string text = MapChestMarksLayout.CountText(groups[i].Count);
                if (count.text != text) count.text = text;
                count.style.display = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            for (int i = groups.Count; i < _marks.Count; i++) _marks[i].mark.style.display = DisplayStyle.None;
            Chests = _points.Count;
            Groups = groups.Count;
        }

        (VisualElement mark, Label count) Mark(int index)
        {
            if (index < _marks.Count) return _marks[index];
            _hasIcon ??= G3Skin.Load()?.Sprite(Icon) != null;
            VisualElement mark;
            float size;
            if (_hasIcon.Value)
            {
                mark = new G3Image(Icon);
                size = MapChestMarksLayout.IconSize;
            }
            else
            {
                mark = new VisualElement { pickingMode = PickingMode.Ignore };
                size = DotSize;
                mark.style.backgroundColor = DotFill;
                mark.style.borderTopLeftRadius = mark.style.borderTopRightRadius =
                    mark.style.borderBottomLeftRadius = mark.style.borderBottomRightRadius = size / 2f;
                mark.style.borderLeftWidth = mark.style.borderRightWidth = mark.style.borderTopWidth = mark.style.borderBottomWidth = 1f;
                mark.style.borderLeftColor = mark.style.borderRightColor = mark.style.borderTopColor = mark.style.borderBottomColor = Rim;
            }
            mark.pickingMode = PickingMode.Ignore;
            mark.style.position = Position.Absolute;
            mark.style.width = mark.style.height = size;
            mark.style.marginLeft = mark.style.marginTop = -size / 2f;
            mark.style.opacity = MapChestMarksLayout.Opacity;
            mark.style.overflow = Overflow.Visible;

            // the count: small white digits with G3's black shadow at the icon's bottom-right corner
            var count = new Label { pickingMode = PickingMode.Ignore, name = "chest-count" };
            count.style.position = Position.Absolute;
            count.style.left = Length.Percent(70f);
            count.style.top = Length.Percent(45f);
            count.style.whiteSpace = WhiteSpace.NoWrap;
            count.style.paddingLeft = count.style.paddingRight = count.style.paddingTop = count.style.paddingBottom = 0;
            count.style.marginLeft = count.style.marginTop = 0;
            count.style.color = Color.white;
            G3Text.Font(count, CountFontSize, bold: true);
            G3Text.Shadow(count);
            count.style.display = DisplayStyle.None;
            mark.Add(count);

            _layer.Add(mark);
            _marks.Add((mark, count));
            return (mark, count);
        }
    }
}
