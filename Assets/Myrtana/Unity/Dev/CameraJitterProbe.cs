#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using G3.Player;
using G3.Unity;
using G3.Unity.Characters;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-76: the camera jitter probe (Docs/dev-scenarios.md "camprobe"): records the player camera every frame after it
    /// was placed (execution order 2000, after G3PlayerCamera's 1000) into <see cref="CameraJitterStats"/>: the camera
    /// relative to the hero's feet in his yaw frame, its height, its yaw relative to his look and its pitch, and while he
    /// runs on the ground in a locomotion set the pelvis height above the feet with the set's phase.
    /// </summary>
    [DefaultExecutionOrder(2000)]
    public sealed class CameraJitterProbe : MonoBehaviour
    {
        public G3Player hero;
        public readonly CameraJitterStats Stats = new CameraJitterStats();
        G3PlayerCamera _camera;
        G3MotionPlayer _motion;
        Transform _pelvis;
        int _cutSerial = -1;
        float _searched = -10f;

        /// <summary>The hero runs at least this fast (m/s) for a pelvis sample: the run cycle the metric compares.</summary>
        const float RunSpeed = 2.5f;

        void LateUpdate()
        {
            if (hero == null) return;
            if ((_camera == null || _motion == null || _pelvis == null) && Time.unscaledTime - _searched > 1f)
            {
                _searched = Time.unscaledTime;
                if (_camera == null) _camera = FindAnyObjectByType<G3PlayerCamera>();
                if (_motion == null) _motion = hero.GetComponentInChildren<G3MotionPlayer>();
                if (_pelvis == null)
                    foreach (var t in hero.GetComponentsInChildren<Transform>(false))
                        if (t.name == "Hero_Spine_Spine_1") { _pelvis = t; break; }
            }
            if (_camera == null || !_camera.isActiveAndEnabled) return;
            var cam = _camera.transform;
            var feet = hero.transform.position;
            var rel = Quaternion.Euler(0f, -hero.Yaw, 0f) * (cam.position - feet);
            float relYaw = Mathf.DeltaAngle(hero.Yaw, cam.eulerAngles.y);
            float pitch = Mathf.DeltaAngle(0f, cam.eulerAngles.x);
            bool cut = _cutSerial >= 0 && _camera.CutSerial != _cutSerial;
            _cutSerial = _camera.CutSerial;
            float pelvis = float.NaN, phase = float.NaN;
            var v = hero.Velocity;
            if (_pelvis != null && _motion != null && hero.IsGrounded && _motion.CurrentIsLocomotion && !_motion.Claimed &&
                new Vector2(v.x, v.z).magnitude >= RunSpeed)
            {
                pelvis = _pelvis.position.y - feet.y;
                phase = _motion.CurrentPhase;
            }
            Stats.Add(Time.deltaTime, rel.x, rel.y, rel.z, cam.position.y, relYaw, pitch, cut, pelvis, phase);
        }
    }
}
#endif
