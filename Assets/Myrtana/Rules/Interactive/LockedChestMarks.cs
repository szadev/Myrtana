using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>A locked chest the hero found but could not open: its object id and where it stands (world metres).</summary>
    public readonly struct LockedChestMark
    {
        public LockedChestMark(string id, float x, float y, float z)
        {
            Id = id;
            X = x;
            Y = y;
            Z = z;
        }

        public string Id { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
    }

    /// <summary>
    /// The port's map marks of locked chests (fix-102, [owner]; G3 has none, Docs/map-teleport.md §1.7): a chest is
    /// "discovered" when the hero focuses it or tries to open it while its lock refuses him (no key, no lockpick, no perk:
    /// every <see cref="LockVerdict"/> but Open / Key / Pick). The mark goes once the lock opens (key, pick, Open Locks
    /// spell, script: <see cref="LockOpened"/>) or the chest is opened (<see cref="ContainerOpened"/>). Recorded whatever the
    /// map option says (the option only hides the marks); saved in the section "world.chestmarks".
    /// </summary>
    public sealed class LockedChestMarks
    {
        readonly InteractiveObjects _objects;
        readonly Dictionary<string, LockedChestMark> _marks = new Dictionary<string, LockedChestMark>(StringComparer.Ordinal);

        public LockedChestMarks(InteractiveObjects objects, IEventBus events)
        {
            _objects = objects;
            if (events == null) return;
            events.Subscribe<LockOpened>(e => Forget(e.ObjectId));
            events.Subscribe<ContainerOpened>(e => Forget(e.ObjectId));
        }

        /// <summary>Raised when a mark is added or removed.</summary>
        public event Action Changed;

        /// <summary>Every recorded mark (also those whose chest is not in the world now).</summary>
        public IReadOnlyCollection<LockedChestMark> All => _marks.Values;

        /// <summary>The marks to draw: the chest is still locked and in the world (its story sector on).</summary>
        public IEnumerable<LockedChestMark> Visible => _marks.Values.Where(m => _objects.IsLocked(m.Id) && _objects.IsInWorld(m.Id));

        public bool Contains(string id) => id != null && _marks.ContainsKey(id);

        /// <summary>
        /// The mark's point on a map picture (u from the left, v from the top, 0…1; G3 <c>gCMap_PS::CalcUV</c> as the hero
        /// arrow): false when the chest lies off that map (another region's map) or the map is degenerate.
        /// </summary>
        public static bool OnMap(MapProjection projection, LockedChestMark mark, out float u, out float v) =>
            projection.TryToMap(mark.X, mark.Z, out u, out v) && u >= 0f && u <= 1f && v >= 0f && v <= 1f;

        /// <summary>The lock refuses the hero (he lacks the key, a lockpick or the perk).</summary>
        public static bool Refuses(LockVerdict verdict) =>
            verdict != LockVerdict.Open && verdict != LockVerdict.Key && verdict != LockVerdict.Pick;

        /// <summary>
        /// The hero focused object <paramref name="id"/> or tried its lock: a locked chest whose lock refuses him is marked.
        /// True when a new mark was added.
        /// </summary>
        public bool Discover(string id)
        {
            if (id == null || _marks.ContainsKey(id) || !_objects.TryGet(id, out var def)) return false;
            if (def.Kind != InteractiveKind.Chest || def.Container == null || !_objects.IsInWorld(def) || !_objects.IsLocked(id)) return false;
            if (!Refuses(_objects.CheckLock(id))) return false;
            var p = def.Position ?? new float[3];
            _marks[id] = new LockedChestMark(id, p[0], p[1], p[2]);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Drops the mark of <paramref name="id"/> (its lock or lid was opened).</summary>
        public bool Forget(string id)
        {
            if (id == null || !_marks.Remove(id)) return false;
            Changed?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------------------------------------- save state

        internal void Restore(IEnumerable<LockedChestMark> marks)
        {
            _marks.Clear();
            foreach (var m in marks)
                if (m.Id != null && _objects.TryGet(m.Id, out InteractiveObjectDef _)) _marks[m.Id] = m;
            Changed?.Invoke();
        }
    }

    /// <summary>Save section "world.chestmarks": the marked chests (id and position), sorted by id.</summary>
    public sealed class LockedChestMarksSaveSection : ISaveParticipant
    {
        readonly LockedChestMarks _marks;

        public LockedChestMarksSaveSection(LockedChestMarks marks) => _marks = marks;

        public string Section => "world.chestmarks";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var marks = _marks.All.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            w.WriteInt32(marks.Count);
            foreach (var m in marks)
            {
                w.WriteString(m.Id);
                w.WriteDouble(m.X);
                w.WriteDouble(m.Y);
                w.WriteDouble(m.Z);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int count = r.ReadInt32();
            var marks = new List<LockedChestMark>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                string id = r.ReadString();
                float x = (float)r.ReadDouble(), y = (float)r.ReadDouble(), z = (float)r.ReadDouble();
                marks.Add(new LockedChestMark(id, x, y, z));
            }
            _marks.Restore(marks);
        }
    }
}
