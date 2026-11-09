using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Story;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-126 [owner]: where the hero talked to the people of the quests (<see cref="QuestTalkMarks"/>) on a map view,
    /// shown by the maps' quest filter (<see cref="MapFilter.Quests"/>, default on). G3's main-quest location icon, small
    /// (14 px, 75 %); talks a few pixels apart are one mark (<see cref="MapChestMarksLayout.GroupIndices"/>). The selected
    /// quest's marks are larger, opaque and named (the NPC's name under the mark); any mark names its people while hovered.
    /// </summary>
    public sealed class QuestTalkMapMarks
    {
        public const string Icon = "G3_HUD_Icon_Location_Main.tga";
        const float Size = 14f, SelectedSize = 18f, Opacity = 0.75f, NameFontSize = 12f, DotSize = 9f;
        static readonly Color DotFill = new Color(0x0C / 255f, 0x70 / 255f, 0x97 / 255f), Rim = new Color(0.08f, 0.06f, 0.03f);

        readonly VisualElement _layer;
        readonly List<(VisualElement mark, Label name)> _marks = new List<(VisualElement, Label)>();
        GameSession _session;
        MapProjection? _projection;
        Func<Id<QuestDef>, bool> _quests;
        Id<QuestDef> _selected;
        bool? _hasIcon;

        public QuestTalkMapMarks(VisualElement picture)
        {
            _layer = new VisualElement { name = "map-quest-talks", pickingMode = PickingMode.Ignore };
            _layer.AddToClassList("g3-fill-parent");
            picture.Add(_layer);
            _layer.RegisterCallback<GeometryChangedEvent>(_ => Redraw());
        }

        /// <summary>Talks drawn by the last <see cref="Place"/> (the dev probe <c>mapquestmarks</c>).</summary>
        public int Talks { get; private set; }

        /// <summary>Marks drawn (talks grouped; the dev probe <c>mapquestgroups</c>).</summary>
        public int Groups { get; private set; }

        /// <summary>Marks of the selected quest (the dev probe <c>mapquestselected</c>).</summary>
        public int Selected { get; private set; }

        /// <summary>
        /// Places the marks of the quests <paramref name="quests"/> accepts on the view (none while the filter hides them,
        /// without a projection or a session); <paramref name="selected"/>'s are highlighted.
        /// </summary>
        public void Place(GameSession session, MapProjection? projection, Func<Id<QuestDef>, bool> quests, Id<QuestDef> selected = default)
        {
            _session = session;
            _projection = projection;
            _quests = quests;
            _selected = selected;
            Redraw();
        }

        void Redraw()
        {
            var size = _layer.contentRect.size;
            float w = size.x > 1f && !float.IsNaN(size.x) ? size.x : MapChestMarksLayout.MapWidth;
            float h = size.y > 1f && !float.IsNaN(size.y) ? size.y : MapChestMarksLayout.MapHeight;
            var talks = new List<QuestTalkMark>();
            var points = new List<(float x, float y)>();
            if (MapFilters.IsOn(MapFilter.Quests) && _session?.QuestTalks != null && _projection != null)
            {
                foreach (var talk in _session.QuestTalks.Of(_quests))
                {
                    if (!_projection.Value.TryToMap(talk.X, talk.Z, out float u, out float v) || u < 0f || u > 1f || v < 0f || v > 1f) continue;
                    talks.Add(talk);
                    points.Add((u * w, v * h));
                }
            }
            var groups = MapChestMarksLayout.GroupIndices(points);
            int selectedMarks = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                var (group, members) = groups[i];
                bool selected = !_selected.IsEmpty && members.Any(m => talks[m].Quest == _selected);
                if (selected) selectedMarks++;
                var (mark, name) = Mark(i);
                float edge = selected ? SelectedSize : Size;
                mark.style.width = mark.style.height = edge;
                mark.style.marginLeft = mark.style.marginTop = -edge / 2f;
                mark.style.opacity = selected ? 1f : Opacity;
                mark.style.left = Length.Percent(group.X / w * 100f);
                mark.style.top = Length.Percent(group.Y / h * 100f);
                mark.style.display = DisplayStyle.Flex;
                // the selected quest's people first, then the others of the group
                var people = members.OrderBy(m => talks[m].Quest == _selected ? 0 : 1).Select(m => talks[m].Npc).Distinct().ToList();
                string text = Names(people);
                if (name.text != text) name.text = text;
                mark.userData = selected;
                name.style.display = selected ? DisplayStyle.Flex : DisplayStyle.None;
                if (selected) mark.BringToFront();
            }
            for (int i = groups.Count; i < _marks.Count; i++) _marks[i].mark.style.display = DisplayStyle.None;
            Talks = talks.Count;
            Groups = groups.Count;
            Selected = selectedMarks;
        }

        string Names(IReadOnlyList<ActorId> people)
        {
            if (_session == null || people.Count == 0) return string.Empty;
            var names = people.Take(2).Select(p => QuestJournal.SpeakerName(_session, p)).ToList();
            string text = string.Join(", ", names);
            return people.Count > 2 ? text + " +" + (people.Count - 2).ToString(System.Globalization.CultureInfo.InvariantCulture) : text;
        }

        (VisualElement mark, Label name) Mark(int index)
        {
            if (index < _marks.Count) return _marks[index];
            _hasIcon ??= G3Skin.Load()?.Sprite(Icon) != null;
            VisualElement mark;
            if (_hasIcon.Value) mark = new G3Image(Icon);
            else
            {
                mark = new VisualElement();
                mark.style.backgroundColor = DotFill;
                mark.style.borderTopLeftRadius = mark.style.borderTopRightRadius =
                    mark.style.borderBottomLeftRadius = mark.style.borderBottomRightRadius = DotSize;
                mark.style.borderLeftWidth = mark.style.borderRightWidth = mark.style.borderTopWidth = mark.style.borderBottomWidth = 1f;
                mark.style.borderLeftColor = mark.style.borderRightColor = mark.style.borderTopColor = mark.style.borderBottomColor = Rim;
            }
            mark.name = "quest-talk";
            mark.pickingMode = PickingMode.Position; // hover names the people
            mark.style.position = Position.Absolute;
            mark.style.overflow = Overflow.Visible;

            // the name: small white text with G3's black shadow, centred under the mark
            var name = new Label { pickingMode = PickingMode.Ignore, name = "quest-talk-name" };
            name.style.position = Position.Absolute;
            name.style.left = Length.Percent(50f);
            name.style.top = Length.Percent(100f);
            name.style.translate = new Translate(Length.Percent(-50f), 0f);
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.paddingLeft = name.style.paddingRight = name.style.paddingTop = name.style.paddingBottom = 0;
            name.style.marginLeft = name.style.marginTop = 0;
            name.style.color = Color.white;
            G3Text.Font(name, NameFontSize, bold: true);
            G3Text.Shadow(name);
            name.style.display = DisplayStyle.None;
            mark.Add(name);
            mark.RegisterCallback<PointerEnterEvent>(_ =>
            {
                name.style.display = DisplayStyle.Flex;
                mark.BringToFront();
            });
            mark.RegisterCallback<PointerLeaveEvent>(_ =>
                name.style.display = mark.userData is bool selected && selected ? DisplayStyle.Flex : DisplayStyle.None);

            _layer.Add(mark);
            _marks.Add((mark, name));
            return (mark, name);
        }
    }
}
