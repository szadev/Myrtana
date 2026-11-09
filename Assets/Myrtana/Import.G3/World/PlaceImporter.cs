using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Story;
using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Named world entities the story refers to (Docs/party.md §5): every CondOwnerNearEntity of an info and the
    /// DestinationEntity of the quests whose delivery test measures a distance (BringNpc, FollowNpc, EnterArea), unless
    /// the name is an NPC (NPCs move; their position comes from the simulation). Position G3 cm → metres; the yaw from the
    /// entity's forward (row 2), clockwise from north (+z), as for the teleport helpers.
    /// </summary>
    public static class PlaceImporter
    {
        const float CentimetresToMetres = 0.01f;

        public static List<WorldPlaceDef> Places(RoutinePoints world, IEnumerable<Info> infos, IEnumerable<Quest> quests,
                                                 Func<string, bool> isActor, ImportReport report)
        {
            var wanted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in infos)
                if (!string.IsNullOrWhiteSpace(i.CondOwnerNearEntity)) wanted.Add(i.CondOwnerNearEntity.Trim());
            foreach (var q in quests)
                if ((q.TypeEnum == QuestType.BringNpc || q.TypeEnum == QuestType.FollowNpc || q.TypeEnum == QuestType.EnterArea) &&
                    !string.IsNullOrWhiteSpace(q.DestinationEntity))
                    wanted.Add(q.DestinationEntity.Trim());

            var places = new List<WorldPlaceDef>();
            foreach (var name in wanted)
            {
                if (isActor(name)) continue;
                if (!world.Named.TryGetValue(name, out var m))
                {
                    report.Count("named entity of a story condition not found in the world", name);
                    continue;
                }
                float yaw = (float)(Math.Atan2(m[8], m[10]) * 180.0 / Math.PI);
                places.Add(new WorldPlaceDef(name, new[] { m[12] * CentimetresToMetres, m[13] * CentimetresToMetres, m[14] * CentimetresToMetres }, yaw));
            }
            report.Note($"{places.Count} named places of story conditions");
            return places;
        }
    }
}
