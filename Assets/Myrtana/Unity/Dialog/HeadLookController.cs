using System.Collections.Generic;
using G3.Unity.Characters;
using Myrtana.Ui.Dialog;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3 head look-at on one character (Docs/dialog-presentation.md §2, <see cref="HeadLook"/>): after the body animation
    /// each bone of the rig's look-at chain (<c>&lt;Rig&gt;_Head_Neck_1…3</c>, <c>_Head_Head_1</c>, neck first) turns
    /// towards the goal within its limits and follows it smoothly; <see cref="Release"/> fades back to the animation.
    /// Yaw is taken about the character's up axis, pitch towards it. The goal is a bone (the partner's
    /// <c>&lt;Rig&gt;_Head_Head_1</c>), followed every frame.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(290)] // after G3MotionPlayer (200), before G3TalkingFace (300)
    public sealed class HeadLookController : MonoBehaviour
    {
        Transform[] _bones;
        HeadLookBone[] _chain;
        Quaternion[] _animated, _written;
        float[] _yaw, _pitch, _weight;
        Transform _target;

        /// <summary>The goal now (null = released / fading out).</summary>
        public Transform Target => _target;

        /// <summary>The controller of <paramref name="character"/> (added on first use); null when its rig has no chain.</summary>
        public static HeadLookController On(Transform character)
        {
            if (character == null) return null;
            var existing = character.GetComponent<HeadLookController>();
            if (existing != null) return existing;
            var motion = character.GetComponentInChildren<G3MotionPlayer>();
            string rig = motion != null && motion.library != null ? motion.library.rig : null;
            var chain = HeadLook.ChainOf(rig);
            if (chain == null) return null;
            var byName = new Dictionary<string, Transform>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in character.GetComponentsInChildren<Transform>(true)) byName[t.name] = t;
            var bones = new Transform[chain.Count];
            int found = 0;
            for (int i = 0; i < chain.Count; i++)
                if (byName.TryGetValue(rig + "_" + chain[i].Suffix, out bones[i])) found++;
            if (found == 0) return null;
            var c = character.gameObject.AddComponent<HeadLookController>();
            c.Init(bones, chain);
            return c;
        }

        /// <summary>The bone a looker aims at on <paramref name="character"/>: its <c>…_Head_Head_1</c>, else the character.</summary>
        public static Transform AimPointOf(Transform character)
        {
            if (character == null) return null;
            foreach (var t in character.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith("_" + HeadLook.TargetBoneSuffix, System.StringComparison.OrdinalIgnoreCase)) return t;
            return character;
        }

        void Init(Transform[] bones, IReadOnlyList<HeadLookBone> chain)
        {
            _bones = bones;
            _chain = new HeadLookBone[chain.Count];
            for (int i = 0; i < chain.Count; i++) _chain[i] = chain[i];
            int n = bones.Length;
            _animated = new Quaternion[n];
            _written = new Quaternion[n];
            _yaw = new float[n];
            _pitch = new float[n];
            _weight = new float[n];
        }

        /// <summary>Starts (or retargets) the look-at.</summary>
        public void LookAt(Transform target)
        {
            if (target == null) { Release(); return; }
            if (_target == null)
                for (int i = 0; i < _weight.Length; i++)
                {
                    // Resume from where a fade-out stands.
                    _yaw[i] *= _weight[i];
                    _pitch[i] *= _weight[i];
                    _weight[i] = 1;
                }
            _target = target;
        }

        /// <summary>Lets go: the bones fade back to the animation (G3 ResetAll at the end of a talk).</summary>
        public void Release() => _target = null;

        void LateUpdate()
        {
            if (_bones == null) return;
            float dt = Time.deltaTime;
            var up = transform.up;
            var forward = transform.forward;
            for (int i = 0; i < _bones.Length; i++)
            {
                var bone = _bones[i];
                if (bone == null) continue;
                // The animation may leave a bone alone for a frame: take our last write back off first.
                if (_weight[i] > 0 && bone.localRotation == _written[i]) bone.localRotation = _animated[i];
                _animated[i] = bone.localRotation;
                if (_target == null)
                {
                    _weight[i] = HeadLook.FadeOut(_weight[i], _chain[i].Speed, dt);
                    if (_weight[i] <= 0) { _yaw[i] = _pitch[i] = 0; continue; }
                }
                else
                {
                    Aim(bone, i, up, forward, dt);
                }
                var q = Offset(bone, up, forward, _yaw[i] * _weight[i], _pitch[i] * _weight[i]);
                bone.localRotation = _animated[i] * q;
                _written[i] = bone.localRotation;
            }
        }

        void Aim(Transform bone, int i, Vector3 up, Vector3 forward, float dt)
        {
            Frame(bone, up, forward, out var axis, out var f, out var side);
            var d = Quaternion.Inverse(bone.rotation) * (_target.position - bone.position);
            if (d.sqrMagnitude < 1e-6f) return;
            d.Normalize();
            float df = Vector3.Dot(d, f), ds = Vector3.Dot(d, side);
            float yaw = Mathf.Atan2(ds, df);
            float pitch = Mathf.Atan2(Vector3.Dot(d, axis), Mathf.Sqrt(df * df + ds * ds));
            HeadLook.Clamp(_chain[i], ref yaw, ref pitch);
            float k = HeadLook.FollowStep(_chain[i].Speed, dt);
            _yaw[i] += (yaw - _yaw[i]) * k;
            _pitch[i] += (pitch - _pitch[i]) * k;
            _weight[i] = 1;
        }

        /// <summary>The bone's look frame in its local space: twist axis (the character's up), forward, side.</summary>
        static void Frame(Transform bone, Vector3 up, Vector3 forward, out Vector3 axis, out Vector3 f, out Vector3 side)
        {
            var inv = Quaternion.Inverse(bone.rotation);
            axis = (inv * up).normalized;
            f = Vector3.ProjectOnPlane(inv * forward, axis).normalized;
            side = Vector3.Cross(axis, f);
        }

        /// <summary>Local rotation turning the forward by <paramref name="yaw"/> about the axis, then <paramref name="pitch"/> towards it.</summary>
        static Quaternion Offset(Transform bone, Vector3 up, Vector3 forward, float yaw, float pitch)
        {
            Frame(bone, up, forward, out var axis, out var f, out _);
            var qYaw = Quaternion.AngleAxis(yaw * Mathf.Rad2Deg, axis);
            var turned = qYaw * f;
            var pitchAxis = Vector3.Cross(turned, axis);
            if (pitchAxis.sqrMagnitude < 1e-8f) return qYaw;
            return Quaternion.AngleAxis(pitch * Mathf.Rad2Deg, pitchAxis.normalized) * qYaw;
        }
    }
}
