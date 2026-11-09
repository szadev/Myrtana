#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using G3.Player;
using G3.Unity;
using G3.Unity.Characters;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-89: the torso probe (Docs/dev-scenarios.md "torsoprobe"): every frame the hero runs on the ground (≥ 2.5 m/s)
    /// records his pelvis (<c>Spine_1</c>), chest (<c>Spine_3</c>) and head (<c>Head_1</c>) into <see cref="TorsoPoseStats"/>,
    /// marked as an overlay frame while an upper-body overlay (a draw or sheathe while running) plays. Logs one line of
    /// bone positions per overlay frame with <c>trace</c>.
    /// </summary>
    [DefaultExecutionOrder(2000)]
    public sealed class TorsoProbe : MonoBehaviour
    {
        public G3Player hero;
        public bool trace;
        public readonly TorsoPoseStats Stats = new TorsoPoseStats();
        G3MotionPlayer _motion;
        Transform _pelvis, _chest, _head;
        float _searched = -10f;

        const float RunSpeed = 2.5f;

        void LateUpdate()
        {
            if (hero == null) return;
            if ((_motion == null || _pelvis == null || _chest == null || _head == null) && Time.unscaledTime - _searched > 1f)
            {
                _searched = Time.unscaledTime;
                _motion = hero.GetComponentInChildren<G3MotionPlayer>();
                foreach (var t in hero.GetComponentsInChildren<Transform>(false))
                {
                    if (t.name == "Hero_Spine_Spine_1") _pelvis = t;
                    else if (t.name == "Hero_Spine_Spine_3") _chest = t;
                    else if (t.name == "Hero_Head_Head_1") _head = t;
                }
            }
            if (_motion == null || _pelvis == null || _chest == null || _head == null) return;
            var v = hero.Velocity;
            if (!hero.IsGrounded || new Vector2(v.x, v.z).magnitude < RunSpeed) return;
            bool overlay = _motion.HasOverlay;
            Vector3 p = _pelvis.position, c = _chest.position, h = _head.position;
            Stats.Add(p.x, p.y, p.z, c.x, c.y, c.z, h.x, h.y, h.z, overlay);
            if (trace && overlay)
            {
                var feet = hero.transform.position;
                Debug.Log($"[scn] torso {_motion.OverlayKey} {_motion.OverlayPhase:0.00}: pelvis {(p - feet).y:0.000} chest {(c - feet).y:0.000} " +
                          $"head {(h - feet).y:0.000} dist {(h - p).magnitude:0.000} bend {TorsoPoseStats.Bend(p.x, p.y, p.z, c.x, c.y, c.z, h.x, h.y, h.z):0.0}");
            }
        }
    }
}
#endif
