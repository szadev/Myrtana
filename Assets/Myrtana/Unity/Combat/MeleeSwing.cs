using Myrtana.Rules;
using System.Collections.Generic;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Hit detection of one fighter's melee swings (Docs/combat.md §9). During the swing window (the motion's Hit phase)
    /// the weapon is a capsule from the hand's weapon slot (the right hand; a dual wielder's left blade has its own sweep) along the held item's mesh (fists: a short capsule at the
    /// hand; monsters without a hand slot: their striking bones, the jaw or the front limbs, <see cref="CreatureStrike"/>,
    /// fix-91; a rig without them a short capsule at the body's front). Every frame the capsule is
    /// swept from its last pose to the current one in steps of its radius (<see cref="MeleeHitGeometry.Steps"/>, no
    /// tunnelling) against the fighters' <see cref="CombatWeaponTrigger"/>s (G3 shape group 7: far taller than the body, so
    /// low and small creatures and targets downhill are reached); each body is reported once per swing.
    /// </summary>
    public sealed class MeleeSwing
    {
        const float FistLength = 0.25f, WeaponRadius = 0.08f, FistRadius = 0.12f;
        static readonly Collider[] Hits = new Collider[16];

        readonly Transform _root;
        readonly Transform _slot;
        readonly CombatBody _own;
        readonly float _reach, _height;
        // A creature's striking bones (fix-91): each END node and its parent joint make one capsule; null = none found.
        readonly Transform[] _strikeFrom, _strikeTo;
        Vector3[] _strikeLastA, _strikeLastB;
        readonly HashSet<CombatBody> _touched = new HashSet<CombatBody>();
        int _swing = -1;
        bool _hasLast, _levelTouched, _levelPending;
        RaycastHit _levelHit;
        Vector3 _lastA, _lastB;
        float _radius;
        Vector3 _localTip;

        /// <param name="root">The fighter's root (feet, facing).</param>
        /// <param name="visual">The animated character (searched for the right-hand weapon slot).</param>
        /// <param name="hands">Strikes with the hands (humanoids); monsters bite and claw in front of the body.</param>
        /// <param name="reach">Reach of a monster's bite or claw, m: only the fallback capsule of a rig without striking
        /// bones uses it, capped at the body's front (the AI's attack range is a decision distance, not the jaw's).</param>
        /// <param name="slotName">The hand slot of the weapon: the right hand, or the left one for a dual wielder's second blade.</param>
        public MeleeSwing(Transform root, Transform visual, CombatBody own, bool hands, float reach, string slotName = "Slot_RightHand_Weapon")
        {
            _root = root;
            _own = own;
            _reach = reach;
            // The body's top, not its capsule length: a wolf's capsule lies along the body (2 m long, 0.9 m high), and its
            // bite sat at 0.6 × length = 1.2 m, above every animal's back (wolf vs boar: misses by 0.01–0.06 m).
            _height = own != null ? own.Top : 1.8f;
            _slot = hands ? Find(visual, slotName) : null;
            if (!hands)
            {
                _reach = Mathf.Min(reach, CreatureStrike.FallbackReach(own != null ? own.Footprint : 0.4f));
                var bones = new List<Transform>();
                Collect(visual, bones);
                var names = new List<string>(bones.Count);
                foreach (var t in bones) names.Add(t.name);
                var picked = CreatureStrike.Select(names);
                if (picked.Count > 0)
                {
                    _strikeFrom = new Transform[picked.Count];
                    _strikeTo = new Transform[picked.Count];
                    for (int i = 0; i < picked.Count; i++)
                    {
                        var end = bones[names.IndexOf(picked[i])];
                        _strikeTo[i] = end;
                        _strikeFrom[i] = end.parent != null && end.parent != visual ? end.parent : end;
                    }
                    _strikeLastA = new Vector3[picked.Count];
                    _strikeLastB = new Vector3[picked.Count];
                }
            }
        }

        /// <summary>The creature strikes with its animated bones (jaw, claws), not a capsule in front of it.</summary>
        public bool HasStrikeBones => _strikeFrom != null;

        /// <summary>The hand slot was found on the skeleton.</summary>
        public bool HasSlot => _slot != null;

        /// <summary>Sweeps the current swing (<paramref name="swing"/> changes for each new one) and returns the newly touched bodies.</summary>
        public IEnumerable<CombatBody> Sweep(int swing)
        {
            if (swing != _swing)
            {
                _swing = swing;
                _touched.Clear();
                _hasLast = false;
                _levelTouched = _levelPending = false;
                MeasureWeapon();
            }
            var found = new List<CombatBody>();
            if (_strikeFrom != null)
            {
                bool first = !_hasLast;
                _hasLast = true;
                for (int i = 0; i < _strikeFrom.Length; i++)
                {
                    if (_strikeFrom[i] == null || _strikeTo[i] == null) continue;
                    var sa = _strikeFrom[i].position;
                    var sb = _strikeTo[i].position;
                    if (first) { _strikeLastA[i] = sa; _strikeLastB[i] = sb; }
                    SweepSegment(_strikeLastA[i], _strikeLastB[i], sa, sb, found);
                    _strikeLastA[i] = sa;
                    _strikeLastB[i] = sb;
                    Measure(sa, sb);
                }
                return found;
            }
            Segment(out var a, out var b);
            if (!_hasLast) { _lastA = a; _lastB = b; _hasLast = true; }
            SweepSegment(_lastA, _lastB, a, b, found);
            if (!_levelTouched && _radius == WeaponRadius) TouchLevel(a, b);
            _lastA = a;
            _lastB = b;
            Measure(a, b);
            return found;
        }

        void SweepSegment(Vector3 lastA, Vector3 lastB, Vector3 a, Vector3 b, List<CombatBody> found)
        {
            float travel = Mathf.Max((a - lastA).magnitude, (b - lastB).magnitude);
            int steps = MeleeHitGeometry.Steps(travel, _radius);
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                int n = Physics.OverlapCapsuleNonAlloc(Vector3.Lerp(lastA, a, t), Vector3.Lerp(lastB, b, t), _radius, Hits, ~0, QueryTriggerInteraction.Collide);
                for (int k = 0; k < n; k++)
                {
                    if (!Hits[k].isTrigger || !Hits[k].TryGetComponent<CombatWeaponTrigger>(out var trigger)) continue;
                    var body = trigger.Body;
                    if (body == null || body == _own) continue;
                    if (_touched.Add(body)) found.Add(body);
                }
            }
        }

        // ------------------------------------------------------------------------------------------- the level

        /// <summary>
        /// G3 OnItemTouchesLevel for a held weapon: the blade's tip path this frame or the blade itself meets the level (a
        /// static collider, not a character or the fighter); once per swing. Fists and monster bites never touch it.
        /// </summary>
        void TouchLevel(Vector3 a, Vector3 b)
        {
            if (LevelLine(_lastB, b, out var hit) || LevelLine(a, b, out hit))
            {
                _levelTouched = _levelPending = true;
                _levelHit = hit;
            }
        }

        static readonly RaycastHit[] LineHits = new RaycastHit[8];

        bool LevelLine(Vector3 from, Vector3 to, out RaycastHit hit)
        {
            hit = default;
            var d = to - from;
            float length = d.magnitude;
            if (length < 1e-4f) return false;
            int n = Physics.RaycastNonAlloc(from, d / length, LineHits, length, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = LineHits[i];
                if (h.distance >= best || !IsLevel(h.collider)) continue;
                best = h.distance;
                hit = h;
            }
            return best < float.MaxValue;
        }

        bool IsLevel(Collider c) =>
            c != null && !c.transform.IsChildOf(_root) && !(c is CharacterController) && c.attachedRigidbody == null &&
            c.GetComponentInParent<CombatBody>() == null && c.GetComponentInParent<CombatFighter>() == null;

        /// <summary>The level contact of the current swing, once (true the first time it is asked after the touch).</summary>
        public bool TakeLevelTouch(out RaycastHit hit)
        {
            hit = _levelHit;
            if (!_levelPending) return false;
            _levelPending = false;
            return true;
        }

        // ------------------------------------------------------------------------------------------- miss diagnostics

        /// <summary>The body whose closest approach this swing is measured (the fighter's opponent), or null.</summary>
        public CombatBody Watched { get; set; }
        float _closest = float.MaxValue, _lowY = float.MaxValue, _highY = float.MinValue;

        void Measure(Vector3 a, Vector3 b)
        {
            _lowY = Mathf.Min(_lowY, Mathf.Min(a.y, b.y));
            _highY = Mathf.Max(_highY, Mathf.Max(a.y, b.y));
            var trigger = Watched != null ? Watched.WeaponTrigger : null;
            if (trigger == null) return;
            float d = MeleeHitGeometry.Gap(Watched.Trigger, trigger.Local(a), trigger.Local(b), _radius);
            if (d < _closest) _closest = d;
        }

        /// <summary>
        /// After a swing: null when <see cref="Watched"/> was touched (or nothing was watched), else how close the weapon came
        /// (gap between the weapon capsule and the body's weapon trigger, m) and the heights the weapon covered vs the trigger's.
        /// Resets the measurement.
        /// </summary>
        public string MissReport()
        {
            string report = null;
            if (Watched != null && !_touched.Contains(Watched) && _closest < float.MaxValue)
            {
                var t = Watched.Trigger;
                float feet = Watched.transform.position.y;
                report = $"closest gap {_closest:0.00} m, weapon y {_lowY:0.00}..{_highY:0.00} (r {_radius:0.00}), " +
                         $"trigger y {feet + t.Bottom:0.00}..{feet + t.Top:0.00} ({t})";
            }
            _closest = float.MaxValue;
            _lowY = float.MaxValue;
            _highY = float.MinValue;
            return report;
        }

        /// <summary>Ends the swing (the next Hit phase starts a new one even with the same counter).</summary>
        public void Stop() => _swing = -1;

        void MeasureWeapon()
        {
            if (_strikeFrom != null)
            {
                _radius = CreatureStrike.Radius(_height);
                return;
            }
            _radius = FistRadius;
            _localTip = Vector3.up * FistLength;
            if (_slot == null) return;
            var filter = _slot.GetComponentInChildren<MeshFilter>();
            var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
            if (filter == null || filter.sharedMesh == null || renderer == null || !renderer.enabled) return;
            // The item is modelled in the slot frame (identity under the slot): its far end along the longest axis is the tip.
            var b = filter.sharedMesh.bounds;
            int axis = b.size.x >= b.size.y && b.size.x >= b.size.z ? 0 : b.size.y >= b.size.z ? 1 : 2;
            float min = b.min[axis], max = b.max[axis];
            var tip = Vector3.zero;
            tip[axis] = Mathf.Abs(max) >= Mathf.Abs(min) ? max : min;
            var local = filter.transform.localToWorldMatrix;
            _localTip = _slot.InverseTransformPoint(local.MultiplyPoint3x4(tip));
            _radius = WeaponRadius;
        }

        void Segment(out Vector3 a, out Vector3 b)
        {
            if (_slot != null)
            {
                a = _slot.position;
                b = _slot.TransformPoint(_localTip);
                return;
            }
            var chest = _root.position + Vector3.up * (_height * 0.6f);
            a = chest + _root.forward * 0.2f;
            b = chest + _root.forward * Mathf.Max(0.4f, _reach);
            _radius = Mathf.Max(FistRadius, _height * 0.25f);
        }

        static void Collect(Transform t, List<Transform> into)
        {
            if (t == null) return;
            into.Add(t);
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
        }

        static Transform Find(Transform t, string name)
        {
            if (t == null) return null;
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = Find(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
