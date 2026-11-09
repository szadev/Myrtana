using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Where the hero stands, sits or kneels to use an object: the NPC use slot of the same object he holds when the routine
    /// data has one (the free slot nearest to him, <see cref="StationClaims.TryOccupy"/>), else the slot an object with the
    /// same mesh has, carried over into this object's frame
    /// (G3 places every user at the mesh's own interaction offset: a bench seat 0.78 m in front of its origin, a stool's
    /// 0.53 m facing away, an ore vein's 1.56 m …). Null when neither exists.
    /// </summary>
    public static class HeroUseSpots
    {
        public readonly struct Spot
        {
            public Spot(Vector3 position, float yaw, string direction)
            {
                Position = position;
                Yaw = yaw;
                Direction = direction;
            }

            public Vector3 Position { get; }
            public float Yaw { get; }
            /// <summary>The slot's side ("Left", "Right", "Fwd").</summary>
            public string Direction { get; }
        }

        static Dictionary<string, (Vector3 local, float yaw)> _byVisual;
        static object _content;

        /// <param name="held">the object's slot the hero holds; None at an object with routine data means every slot is
        /// taken (a shared campfire): no spot, the caller places him at the object on his side</param>
        public static Spot? Find(GameSession session, InteractiveObjectDef def, Station held)
        {
            var db = session.Content.Database;
            if (db.TryGet(def.Id, out RoutinePointDef point) && point.Slots.Count > 0)
            {
                if (held.IsNone || held.Slot >= point.Slots.Count) return null;
                var slot = point.Slots[held.Slot];
                return new Spot(new Vector3(slot.X, slot.Y, slot.Z), slot.Yaw, point.DirectionOf(held.Slot));
            }
            if (def.Visual == null) return null;
            var offsets = Offsets(session);
            if (!offsets.TryGetValue(def.Visual, out var offset)) return null;
            Frame(def, out var origin, out var right, out var up, out var forward);
            var position = origin + right * offset.local.x + up * offset.local.y + forward * offset.local.z;
            return new Spot(position, YawOf(forward) + offset.yaw, "Fwd");
        }

        /// <summary>Per mesh: the first routine slot of an object with that mesh, in the object's frame.</summary>
        static Dictionary<string, (Vector3 local, float yaw)> Offsets(GameSession session)
        {
            if (_byVisual != null && ReferenceEquals(_content, session.Content)) return _byVisual;
            _content = session.Content;
            _byVisual = new Dictionary<string, (Vector3, float)>(System.StringComparer.OrdinalIgnoreCase);
            var db = session.Content.Database;
            foreach (var point in db.All<RoutinePointDef>())
            {
                if (point.Slots.Count == 0 || !db.TryGet(point.Id.Name, out InteractiveObjectDef def) || def.Visual == null ||
                    _byVisual.ContainsKey(def.Visual)) continue;
                Frame(def, out var origin, out var right, out var up, out var forward);
                var s = point.Slots[0];
                var d = new Vector3(s.X, s.Y, s.Z) - origin;
                _byVisual[def.Visual] = (new Vector3(Vector3.Dot(d, right), Vector3.Dot(d, up), Vector3.Dot(d, forward)), s.Yaw - YawOf(forward));
            }
            return _byVisual;
        }

        static void Frame(InteractiveObjectDef def, out Vector3 origin, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            origin = new Vector3(def.Position[0], def.Position[1], def.Position[2]);
            forward = new Vector3(def.Forward[0], def.Forward[1], def.Forward[2]).normalized;
            up = new Vector3(def.Up[0], def.Up[1], def.Up[2]).normalized;
            right = Vector3.Cross(up, forward).normalized;
        }

        static float YawOf(Vector3 forward) => Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }
}
