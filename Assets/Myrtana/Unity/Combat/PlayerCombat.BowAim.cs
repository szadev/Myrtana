using Myrtana.Rules;
using UnityEngine;
using N = System.Numerics;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-228: the drawn bow points at what it aims at (<see cref="BowAimPose"/>). After the animation the spine turns the
    /// arrow line (draw hand → bow hand) onto the line from the bow hand to the aim point: the focus's body centre under the
    /// crosshair, else the screen-centre pick point; within the natural pitch range, eased in on the held aim and out after.
    /// </summary>
    public sealed partial class PlayerCombat
    {
        Transform _aimSpine2, _aimSpine3, _aimSpineVisual;
        float _aimCorrection, _aimWeight;

        /// <summary>The aim pose's spine correction now (deg, up positive) and its weight — dev runner / probes.</summary>
        public float AimPoseCorrection => _aimCorrection * _aimWeight;

        static N.Vector3 ToN(Vector3 v) => new N.Vector3(v.x, v.y, v.z);
        static Vector3 ToU(N.Vector3 v) => new Vector3(v.X, v.Y, v.Z);

        /// <summary>The screen-centre pick point; false when the ray hit nothing (the point is far along it).</summary>
        bool PickPoint(Vector3 from, out Vector3 point)
        {
            point = AimPoint(from, out bool hit);
            return hit;
        }

        /// <summary>What the bow points at (G3 OnGetAimAngle: the screen-centre point or the target centre).</summary>
        Vector3 PoseAimPoint(Vector3 from)
        {
            var cam = _camera != null ? _camera.transform : null;
            bool hit = PickPoint(from, out var pick);
            if (cam == null) return pick;
            N.Vector3? focus = null;
            var f = Focus;
            if (f != null && f.isActiveAndEnabled) focus = ToN(f.Body != null ? f.Body.Center : f.SpinePoint);
            return ToU(BowAimPose.AimPoint(ToN(cam.position), ToN(cam.forward), ToN(pick), hit, focus));
        }

        static Transform FindSuffix(Transform t, string suffix)
        {
            if (t == null) return null;
            if (t.name.EndsWith(suffix, System.StringComparison.Ordinal)) return t;
            for (int i = 0; i < t.childCount; i++)
                if (FindSuffix(t.GetChild(i), suffix) is Transform r) return r;
            return null;
        }

        /// <summary>LateUpdate (the animation has been evaluated this frame): turns the spine so the arrow meets the aim point.</summary>
        void ApplyBowAimPose()
        {
            float dt = Time.deltaTime;
            var a = _fighter != null ? _fighter.Animator : null;
            bool ranged = a != null && _fighter.RangedDrawn && !_dead && !_knockedOut && _slot != null && _leftSlot != null;
            // fix-253: with a bow / crossbow drawn the camera turns with the look only (no kick on a pull-in / relief lift)
            if (_camera != null) _camera.steadyAim = _fighter != null && _fighter.RangedDrawn && !_dead && !_knockedOut;
            if (!ranged)
            {
                _aimWeight = 0f;
                _aimCorrection = 0f;
                return;
            }
            if (_aimSpineVisual != _fighter.Visual || _aimSpine2 == null)
            {
                _aimSpineVisual = _fighter.Visual;
                _aimSpine2 = FindSuffix(_fighter.Visual, "Spine_Spine_2");
                _aimSpine3 = FindSuffix(_fighter.Visual, "Spine_Spine_3");
            }
            if (_aimSpine2 == null) return;

            var bowHand = _leftSlot.position;
            var line = bowHand - _slot.position; // the arrow line of the animated pose
            var flat = new Vector3(line.x, 0f, line.z);
            bool usable = a.AimHeld && flat.sqrMagnitude > 1e-4f && Vector3.Dot(flat.normalized, transform.forward) > 0.3f;
            float target = 0f;
            if (usable)
            {
                float targetPitch = BowAimPose.TargetPitch(ToN(bowHand), ToN(PoseAimPoint(bowHand)));
                target = BowAimPose.Correction(targetPitch, BowAimPose.PitchOf(ToN(line)));
                if (_aimWeight <= 0.01f) _aimCorrection = target; // no ease from a stale value
            }
            _aimWeight = BowAimPose.Ease(_aimWeight, usable ? 1f : 0f, BowAimPose.WeightRate, dt);
            if (usable) _aimCorrection = BowAimPose.Ease(_aimCorrection, target, BowAimPose.CorrectionRate, dt);
            float angle = _aimCorrection * _aimWeight;
            if (Mathf.Abs(angle) < 0.01f) return;

            // Tilt about the horizontal axis across the arrow line: positive raises it. Spread over two spine bones.
            var dir = flat.sqrMagnitude > 1e-4f ? flat.normalized : transform.forward;
            var axis = Vector3.Cross(dir, Vector3.up);
            if (_aimSpine3 != null && _aimSpine3.IsChildOf(_aimSpine2))
            {
                var q = Quaternion.AngleAxis(angle * 0.5f, axis);
                _aimSpine2.rotation = q * _aimSpine2.rotation;
                _aimSpine3.rotation = q * _aimSpine3.rotation;
            }
            else _aimSpine2.rotation = Quaternion.AngleAxis(angle, axis) * _aimSpine2.rotation;
        }
    }
}
