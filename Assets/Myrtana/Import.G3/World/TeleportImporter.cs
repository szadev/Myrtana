using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Teleport destinations (Docs/map-teleport.md §1.5): the AI helpers named <c>Teleport_&lt;X&gt;</c> that the teleport
    /// spells look up by name. Position G3 cm → metres; the yaw comes from the helper's forward (row 2 of the matrix),
    /// clockwise from north (+z), no axis flip.
    /// </summary>
    public static class TeleportImporter
    {
        public const string Prefix = "Teleport_";
        const float CentimetresToMetres = 0.01f;

        public static List<TeleportDestinationDef> Destinations(RoutinePoints world, ImportReport report)
        {
            var destinations = world.Labels
                .Where(l => l.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                .Select(l => Destination(l.Key, l.Value))
                .OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            if (destinations.Count == 0) report.Count("no teleport destinations in the world", "Teleport_*");
            else report.Note($"{destinations.Count} teleport destinations");
            return destinations;
        }

        static TeleportDestinationDef Destination(string name, float[] m)
        {
            float yaw = (float)(Math.Atan2(m[8], m[10]) * 180.0 / Math.PI);
            return new TeleportDestinationDef(name, new[] { m[12] * CentimetresToMetres, m[13] * CentimetresToMetres, m[14] * CentimetresToMetres }, yaw);
        }
    }
}
