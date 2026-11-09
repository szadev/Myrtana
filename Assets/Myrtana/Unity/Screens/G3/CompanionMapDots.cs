using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The port's companion markers on the map pages (option <see cref="PortOptions.CompanionMarkers"/>, player feedback
    /// item 5; off = G3, which shows only the hero arrow): a small gold dot with a dark rim at each party member's
    /// position (members waiting somewhere included), on top of the map's markers and under the hero arrow. fix-126: the
    /// maps' filter bar can hide them too (<see cref="Myrtana.Ui.Skin.MapFilter.Companions"/>, its button only while the
    /// option is on).
    /// </summary>
    public sealed class CompanionMapDots
    {
        /// <summary>Set by the options (<see cref="PortOptionsApplier"/>).</summary>
        public static bool Enabled { get; set; }

        const float Size = 10f;
        static readonly Color Fill = new Color(0xC7 / 255f, 0xA6 / 255f, 0x48 / 255f), Rim = new Color(0.08f, 0.06f, 0.03f);

        readonly VisualElement _layer;
        readonly List<VisualElement> _dots = new List<VisualElement>();

        public CompanionMapDots(VisualElement picture)
        {
            _layer = new VisualElement { name = "map-companions", pickingMode = PickingMode.Ignore };
            _layer.AddToClassList("g3-fill-parent");
            picture.Add(_layer);
        }

        /// <summary>Places a dot per party member on <paramref name="map"/> (none when off, without a map or a session).</summary>
        public void Place(GameSession session, MapDef map) => Place(session, map?.Projection);

        /// <summary>The dots drawn by the last <see cref="Place"/> (the dev probe <c>mapcompanions</c>).</summary>
        public int Shown { get; private set; }

        /// <summary>The same on a map view whose projection is known without a map item (the quest log's world map, fix-126).</summary>
        public void Place(GameSession session, MapProjection? projection)
        {
            int used = 0;
            if (Enabled && MapFilters.IsOn(Myrtana.Ui.Skin.MapFilter.Companions) && session != null && projection != null)
            {
                foreach (var member in session.Party.Members.Members)
                {
                    if (member == null || !session.Npcs.TryGet(member.Id, out var agent)) continue;
                    var pose = session.Npcs.PoseOf(agent);
                    if (!projection.Value.TryToMap(pose.X, pose.Z, out float u, out float v) || u < 0f || u > 1f || v < 0f || v > 1f)
                        continue;
                    var dot = Dot(used++);
                    dot.style.left = Length.Percent(u * 100f);
                    dot.style.top = Length.Percent(v * 100f);
                    dot.style.display = DisplayStyle.Flex;
                }
            }
            for (int i = used; i < _dots.Count; i++) _dots[i].style.display = DisplayStyle.None;
            Shown = used;
        }

        VisualElement Dot(int index)
        {
            if (index < _dots.Count) return _dots[index];
            var dot = new VisualElement { pickingMode = PickingMode.Ignore };
            dot.style.position = Position.Absolute;
            dot.style.width = dot.style.height = Size;
            dot.style.marginLeft = dot.style.marginTop = -Size / 2f;
            dot.style.backgroundColor = Fill;
            dot.style.borderTopLeftRadius = dot.style.borderTopRightRadius =
                dot.style.borderBottomLeftRadius = dot.style.borderBottomRightRadius = Size / 2f;
            dot.style.borderLeftWidth = dot.style.borderRightWidth = dot.style.borderTopWidth = dot.style.borderBottomWidth = 1.5f;
            dot.style.borderLeftColor = dot.style.borderRightColor = dot.style.borderTopColor = dot.style.borderBottomColor = Rim;
            _layer.Add(dot);
            _dots.Add(dot);
            return dot;
        }
    }
}
