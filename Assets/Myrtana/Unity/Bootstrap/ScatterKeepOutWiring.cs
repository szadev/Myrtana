using System.Collections.Generic;
using G3.Unity;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The ground cover scatter (Docs/remaster-world.md section 18.2) keeps NPC places clear: every routine point slot
    /// (where an NPC stands, sits, sleeps or works, 1.1 m) and every station / seat / bed / campfire object (1.6 m) of the
    /// content. Handed to <see cref="G3GroundScatter.SetKeepOut"/> when a session begins.
    /// </summary>
    public static class ScatterKeepOutWiring
    {
        public const float SlotRadius = 1.1f, ObjectRadius = 1.6f;

        public static int Apply(GameSession session)
        {
            if (session?.Content?.Database == null) return 0;
            var db = session.Content.Database;
            var discs = new List<Vector4>();
            foreach (var point in db.All<RoutinePointDef>())
                foreach (var s in point.Slots) discs.Add(new Vector4(s.X, s.Y, s.Z, SlotRadius));
            foreach (var o in db.All<InteractiveObjectDef>())
            {
                if (o.Position == null || o.Position.Length < 3) continue;
                if (o.Kind != InteractiveKind.Station && o.Kind != InteractiveKind.Seat && o.Kind != InteractiveKind.Bed) continue;
                discs.Add(new Vector4(o.Position[0], o.Position[1], o.Position[2], ObjectRadius));
            }
            return G3GroundScatter.SetKeepOut(discs);
        }
    }
}
