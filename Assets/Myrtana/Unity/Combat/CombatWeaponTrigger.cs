using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The shape melee weapons touch (G3 shape group 7, WeaponTrigger; <see cref="MeleeHitGeometry"/>): an upright trigger
    /// box or capsule far taller than the body. A sibling of the <see cref="CombatBody"/> under the fighter, so the queries
    /// that look for bodies (arrows, spells, the camera, spacing) never find it; only <see cref="MeleeSwing"/> does.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatWeaponTrigger : MonoBehaviour
    {
        BoxCollider _box;
        CapsuleCollider _capsule;

        public CombatBody Body { get; private set; }

        public static CombatWeaponTrigger Create(CombatBody body)
        {
            var go = new GameObject("Weapon trigger");
            go.transform.SetParent(body.transform.parent != null ? body.transform.parent : body.transform, false);
            var t = go.AddComponent<CombatWeaponTrigger>();
            t.Body = body;
            return t;
        }

        public void Fit(WeaponTrigger s)
        {
            if (s.IsBox)
            {
                if (_box == null) { _box = gameObject.AddComponent<BoxCollider>(); _box.isTrigger = true; }
                _box.enabled = true;
                _box.center = new Vector3(0, s.CenterY, s.CenterZ);
                _box.size = new Vector3(s.HalfX * 2, s.Height, s.HalfZ * 2);
                if (_capsule != null) _capsule.enabled = false;
            }
            else
            {
                if (_capsule == null) { _capsule = gameObject.AddComponent<CapsuleCollider>(); _capsule.isTrigger = true; }
                _capsule.enabled = true;
                _capsule.direction = 1;
                _capsule.radius = s.Radius;
                _capsule.height = s.Height;
                _capsule.center = new Vector3(0, s.CenterY, s.CenterZ);
                if (_box != null) _box.enabled = false;
            }
        }

        /// <summary>World point → the trigger's frame (feet of the fighter, metres).</summary>
        public HitPoint Local(Vector3 world)
        {
            var p = transform.InverseTransformPoint(world);
            return new HitPoint(p.x, p.y, p.z);
        }
    }
}
