using Unity.Profiling;
using UnityEngine;
using NV3 = System.Numerics.Vector3;
using NQ = System.Numerics.Quaternion;

namespace G3.Unity
{
    /// <summary>
    /// A character standing on the drawn relief (Docs/remaster-world.md section 14): physics keeps G3's collision ground
    /// (controller, NavMesh, every ray and every saved position are unchanged); after the animation (LateUpdate, order 270:
    /// after the motion player and the hero's foot-plant sampling, before head look) the skeleton root is moved onto the visual
    /// ground (<see cref="G3TerrainRelief.TryGround"/>) and, for upright two-legged rigs near the camera, each leg reaches the
    /// ground under its own foot by two-bone IK (<see cref="ReliefFootingRules"/>); four-legged rigs pitch along the ground.
    /// Off the terrain (a floor, a bridge, in the air) or with the relief off the offset fades to 0. A pose written here is
    /// re-based whenever the animation wrote the bone again (paused or culled animators do not), so nothing accumulates.
    /// </summary>
    [DefaultExecutionOrder(270)]
    public sealed class G3ReliefFooting : MonoBehaviour
    {
        /// <summary>The hero's footing (camera: <see cref="HeroOffset"/>).</summary>
        public static G3ReliefFooting Hero { get; private set; }
        /// <summary>The hero model's current visual offset above his collision feet (m): the third-person camera follows it.</summary>
        public static float HeroOffset => Hero != null && Hero.isActiveAndEnabled ? Hero.Offset : 0f;

        /// <summary>Legs reach the ground by IK within this distance of the camera, m.</summary>
        public const float IkDistance = 40f;
        const float BodySeconds = 0.1f, PitchSeconds = 0.25f, MaxPitch = 25f, WeightUpSeconds = 0.3f, WeightDownSeconds = 0.08f, SpeedSeconds = 0.15f, MaxSpeedDt = 0.1f, RaiseSeconds = 0.05f;
        static readonly ProfilerMarker Marker = new ProfilerMarker("G3ReliefFooting");

        struct Leg
        {
            public Transform hip, knee, ankle;
            public Quaternion baseHip, baseKnee, wroteHip, wroteKnee;
            public bool wrote;
            public float rest, raise;   // fix-173: lowest animated ankle height above the root (learnt), smoothed IK raise
            public bool Valid => hip != null && knee != null && ankle != null;
        }

        Animator _animator;
        Transform _skeleton;
        Renderer _probe;
        SkinnedMeshRenderer[] _skins;
        float _skinsAt;
        Leg _left, _right;
        bool _biped, _isHero;
        float _halfLength = 0.4f;
        Vector3 _basePos, _wrotePos;
        Quaternion _baseRot, _wroteRot;
        bool _wrote;
        float _body = float.NaN, _pitch;
        // fix-173: gait (leg IK only while slow), from the root's own horizontal speed
        Vector3 _lastFeet; bool _hasLast; float _speed, _legW;

        /// <summary>Last frame's numbers for the runner (body, ground deltas under the feet, IK raises, flags).</summary>
        public string Diagnostics => $"biped {_biped} ik {_ik} body {Offset:F2} dL {_dl:F2} dR {_dr:F2} raiseL {_raiseL:F2} raiseR {_raiseR:F2} pitch {_pitch:F1} speed {_speed:F2} legW {_legW:F2} cL {_cl:F2} cR {_cr:F2}";
        bool _ik; float _dl, _dr, _raiseL, _raiseR, _cl, _cr;

        /// <summary>The applied visual offset of the model above the collision feet (m, smoothed).</summary>
        public float Offset => float.IsNaN(_body) ? 0f : _body;

        /// <summary>The visual offset of the character whose model root is <paramref name="visual"/> (0 without footing).</summary>
        public static float OffsetOf(Transform visual) =>
            visual != null && visual.TryGetComponent<G3ReliefFooting>(out var f) && f.isActiveAndEnabled ? f.Offset : 0f;

        /// <summary>Adds (or returns) the footing of a character model root (the GameObject with its Animator).</summary>
        public static G3ReliefFooting Attach(GameObject visual, bool hero = false)
        {
            if (visual == null) return null;
            if (!visual.TryGetComponent<G3ReliefFooting>(out var f)) f = visual.AddComponent<G3ReliefFooting>();
            f._isHero = hero;
            if (hero) Hero = f;
            return f;
        }

        /// <summary>Puts the model back on its collision pose at once (a ragdoll is built from the bones next).</summary>
        public static void Release(Transform visual)
        {
            if (visual != null && visual.TryGetComponent<G3ReliefFooting>(out var f)) f.Restore();
        }

        void Awake() => Find();

        /// <summary>Every footing runs in one batched LateUpdate (perf-3, <see cref="G3FrameBatch"/>; order 270 as before).</summary>
        static readonly G3FrameBatch.Batch<G3ReliefFooting> s_lateBatch =
            new G3FrameBatch.Batch<G3ReliefFooting>(G3FrameBatch.Slot.LateUpdate270, "G3ReliefFooting", f => f.FrameLateUpdate());

        void OnEnable() { if (_isHero) Hero = this; s_lateBatch.Add(this); }

        void OnDisable()
        {
            s_lateBatch.Remove(this);
            Restore();
            if (Hero == this && !_isHero) Hero = null;
        }

        void OnDestroy() { if (Hero == this) Hero = null; }

        void Find()
        {
            _animator = GetComponent<Animator>();
            _skeleton = null;
            foreach (Transform c in transform)
                if (c.name.EndsWith("_ROOT", System.StringComparison.Ordinal) && c.childCount > 0) { _skeleton = c; break; }
            Transform hipL = null, kneeL = null, endL = null, hipR = null, kneeR = null, endR = null, spine1 = null, spineEnd = null;
            if (_skeleton != null)
                foreach (var t in _skeleton.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name;
                    // G3 rigs: Leg_1 = hip joint, Leg_2 = knee (Leg_END sits on it), Foot_Foot_1 = ankle (Toe_1 the ball)
                    if (n.EndsWith("_Left_Leg_Leg_1")) hipL = t; else if (n.EndsWith("_Left_Leg_Leg_2")) kneeL = t; else if (n.EndsWith("_Left_Foot_Foot_1")) endL = t;
                    else if (n.EndsWith("_Right_Leg_Leg_1")) hipR = t; else if (n.EndsWith("_Right_Leg_Leg_2")) kneeR = t; else if (n.EndsWith("_Right_Foot_Foot_1")) endR = t;
                    else if (n.EndsWith("_Spine_Spine_1")) spine1 = t; else if (n.EndsWith("_Spine_Spine_END")) spineEnd = t;
                }
            _left = new Leg { hip = hipL, knee = kneeL, ankle = endL };
            _right = new Leg { hip = hipR, knee = kneeR, ankle = endR };
            // upright two-legged rigs (humans, orcs, the hero): legs hang below the hips, the spine rises; others pitch
            _biped = _left.Valid && _right.Valid && spine1 != null && spineEnd != null &&
                     spineEnd.position.y - spine1.position.y > 0.25f && hipL.position.y - endL.position.y > 0.35f;
            _skins = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _probe = _skins.Length > 0 ? _skins[0] : null;
            if (_probe != null && !_biped)
            {
                // half the body length along the facing (front/back ground samples of a four-legged body)
                var smr = (SkinnedMeshRenderer)_probe;
                float min = 0, max = 0;
                foreach (var b in smr.bones)
                {
                    if (b == null) continue;
                    float z = transform.InverseTransformPoint(b.position).z;
                    min = Mathf.Min(min, z); max = Mathf.Max(max, z);
                }
                _halfLength = Mathf.Clamp((max - min) * 0.4f, 0.15f, 2.5f);
            }
        }

        void Restore()
        {
            if (_wrote && _skeleton != null)
            {
                if (_skeleton.localPosition == _wrotePos) _skeleton.localPosition = _basePos;
                if (_skeleton.localRotation == _wroteRot) _skeleton.localRotation = _baseRot;
            }
            RestoreLeg(ref _left); RestoreLeg(ref _right);
            _wrote = false;
            _body = float.NaN; _pitch = 0;
            _left.raise = _right.raise = 0;
            _hasLast = false;
        }

        static void RestoreLeg(ref Leg l)
        {
            if (!l.wrote || !l.Valid) return;
            if (l.hip.localRotation == l.wroteHip) l.hip.localRotation = l.baseHip;
            if (l.knee.localRotation == l.wroteKnee) l.knee.localRotation = l.baseKnee;
            l.wrote = false;
        }

        void FrameLateUpdate()
        {
            if (_skeleton == null) return;
            using var _ = Marker.Auto();
            if (_animator != null && !_animator.enabled) { _wrote = false; _left.wrote = _right.wrote = false; return; }   // frozen corpse / ragdoll
            if (!G3TerrainRelief.Active) { if (_wrote) Restore(); return; }
            // snow3 perf (Nordmar: 0.5 ms/frame for ~120 footings in the editor): a model no camera sees is left on its
            // animated pose (put back once if we wrote it); it is re-based the frame it shows again. The hero always runs
            // (the camera follows HeroOffset).
            if (!_isHero && !Visible())
            {
                if (_wrote || _left.wrote || _right.wrote || !float.IsNaN(_body)) Restore();
                _ik = false; _hasLast = false;
                return;
            }

            // the animated pose (re-based if the animation did not write the bones since our last write)
            var lp = _skeleton.localPosition; var lr = _skeleton.localRotation;
            // each channel on its own (fix-173): a motion that writes only one of them must not let the other accumulate our
            // offset / tilt frame after frame (a creature tumbling into the ground)
            bool posOurs = _wrote && lp == _wrotePos, rotOurs = _wrote && lr == _wroteRot;
            if (posOurs) lp = _basePos;
            if (rotOurs) lr = _baseRot;
            if (posOurs || rotOurs) _skeleton.SetLocalPositionAndRotation(lp, lr);
            _basePos = lp; _baseRot = lr;
            Rebase(ref _left); Rebase(ref _right);

            var root = transform;
            var feet = root.position;
            float dt = Time.deltaTime;
            // gait: legs reach the ground only at stand / walk speeds, smoothly faded out towards run (fix-173)
            // (a hitch frame says little: the mover may cap its step below Time.deltaTime -> a false 'slow' sample)
            if (_hasLast && dt > 0 && dt <= MaxSpeedDt)
            {
                float v = new Vector2(feet.x - _lastFeet.x, feet.z - _lastFeet.z).magnitude / dt;
                if (v < 20f) _speed = ReliefFootingRules.Follow(_speed, v, dt, SpeedSeconds);   // not a teleport
            }
            _lastFeet = feet; _hasLast = true;
            // the legs let go of the ground fast when the body speeds up and take it again only once it stays slow
            float wTarget = ReliefFootingRules.LegWeight(_speed);
            _legW = ReliefFootingRules.Follow(_legW, wTarget, dt, wTarget > _legW ? WeightUpSeconds : WeightDownSeconds);
            float w = 0, d0 = 0;
            bool hasGround = G3TerrainRelief.TryGround(feet, out float col, out float vis);
            if (hasGround)
            {
                w = G3TerrainRelief.OnTerrain(feet.y - col);
                d0 = vis - feet.y;
            }
            var cam = Camera.main;
            bool near = cam != null && (cam.transform.position - feet).sqrMagnitude < IkDistance * IkDistance && (!_isHero || Visible());
            bool ik = _biped && near && w > 0.01f && _legW > 0.01f;
            float dl = float.NaN, dr = float.NaN, cl = 0, cr = 0;
            if (_biped)
            {
                // the animated feet (before any write of ours this frame): height over the root plane and the learnt rest
                Learn(ref _left, feet.y, dt); Learn(ref _right, feet.y, dt);
                cl = ReliefFootingRules.Contact(_left.ankle.position.y - feet.y, _left.rest);
                cr = ReliefFootingRules.Contact(_right.ankle.position.y - feet.y, _right.rest);
            }
            if (ik)
            {
                dl = Delta(_left.ankle.position, feet.y);
                dr = Delta(_right.ankle.position, feet.y);
            }
            else _left.raise = _right.raise = 0;
            _ik = ik; _dl = dl; _dr = dr; _cl = cl; _cr = cr; _raiseL = _raiseR = 0;
            float target = w > 0 ? w * (ik ? ReliefFootingRules.BodyOffset(d0, dl, dr, cl, cr, _legW) : Mathf.Clamp(d0, -ReliefFootingRules.MaxOffset, ReliefFootingRules.MaxOffset)) : 0f;
            // fix-181: an NPC whose collision feet are inside the ground (under the nearest ground layer) is never drawn there:
            // its model stands on the drawn ground until the body's own guard (NpcView) puts it onto the floor
            if (!_isHero && hasGround) target = ReliefFootingRules.BuriedLift(feet.y, col, vis, target);
            _body = ReliefFootingRules.Follow(_body, target, dt, BodySeconds);

            float pitchTarget = 0;
            if (!_biped && w > 0.01f && near)
            {
                var fwd = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized * _halfLength;
                float gf = Delta(feet + fwd, feet.y), gb = Delta(feet - fwd, feet.y);
                if (!float.IsNaN(gf) && !float.IsNaN(gb)) pitchTarget = Mathf.Clamp(Mathf.Atan2(gf - gb, 2 * _halfLength) * Mathf.Rad2Deg, -MaxPitch, MaxPitch) * w;
            }
            _pitch = ReliefFootingRules.Follow(_pitch, pitchTarget, dt, PitchSeconds);

            if (Mathf.Abs(_body) < 0.0005f && Mathf.Abs(_pitch) < 0.05f) { _wrote = false; return; }
            // body: the skeleton root moved up / down (and pitched about the feet for four legs)
            var parent = _skeleton.parent;
            var wp = parent.TransformPoint(lp); var wr = parent.rotation * lr;
            var pivot = feet;
            var q = Mathf.Abs(_pitch) > 0.01f ? Quaternion.AngleAxis(-_pitch, root.right) : Quaternion.identity;
            var np = pivot + Vector3.up * _body + q * (wp - pivot);
            var nr = q * wr;
            _skeleton.SetPositionAndRotation(np, nr);
            _wrotePos = _skeleton.localPosition; _wroteRot = _skeleton.localRotation; _wrote = true;

            if (ik)
            {
                _raiseL = Reach(ref _left, dl, cl, feet.y, root, dt);
                _raiseR = Reach(ref _right, dr, cr, feet.y, root, dt);
            }
        }

        /// <summary>Any of the model's skins is seen by a camera (the first one may be a hidden part: armour swaps, LODs).</summary>
        bool Visible()
        {
            if (_skins == null || _skins.Length == 0) return true;
            bool live = false;
            foreach (var r in _skins)
            {
                if (r == null || !r.enabled) continue;
                if (r.isVisible) return true;
                live = true;
            }
            // the remaster may have swapped the skins (none of ours drawn any more): look again now and then
            if (!live && Time.unscaledTime >= _skinsAt) { _skinsAt = Time.unscaledTime + 2f; _skins = GetComponentsInChildren<SkinnedMeshRenderer>(true); }
            return false;
        }

        /// <summary>The drawn ground under <paramref name="p"/> relative to the plane the animation stands on (NaN: none).</summary>
        static float Delta(Vector3 p, float feetY) => G3TerrainRelief.TryGround(new Vector3(p.x, feetY, p.z), out _, out float vis) ? vis - feetY : float.NaN;

        static void Rebase(ref Leg l)
        {
            if (!l.Valid) return;
            var h = l.hip.localRotation; var k = l.knee.localRotation;
            if (l.wrote && h == l.wroteHip) { h = l.baseHip; l.hip.localRotation = h; }
            if (l.wrote && k == l.wroteKnee) { k = l.baseKnee; l.knee.localRotation = k; }
            l.baseHip = h; l.baseKnee = k; l.wrote = false;
        }

        /// <summary>Learns a foot's rest height (its lowest animated ankle height above the root; re-learnt slowly upwards).</summary>
        static void Learn(ref Leg l, float feetY, float dt)
        {
            float h = l.ankle.position.y - feetY;
            l.rest = !(l.rest > 0) || h < l.rest ? Mathf.Max(h, 0.001f) : l.rest + dt * 0.02f;
        }

        float Reach(ref Leg l, float dFoot, float contact, float feetY, Transform root, float dt)
        {
            // the ankle has moved with the body by now: its animated lift above the rest is relative to the moved root plane
            float lift = l.ankle.position.y - (feetY + _body) - l.rest;
            float want = _legW * ReliefFootingRules.FootRaise(dFoot, _body, contact, lift);
            l.raise = ReliefFootingRules.Follow(l.raise, want, dt, RaiseSeconds);
            float raise = l.raise;
            if (Mathf.Abs(raise) < 0.004f) return 0;
            Vector3 a = l.hip.position, b = l.knee.position, c = l.ankle.position;
            var t = c + Vector3.up * raise;
            if (!ReliefFootingRules.TwoBone(N(a), N(b), N(c), N(t), N(root.right), out var hq, out var kq)) return 0;
            var kneeWorld = l.knee.rotation;
            l.hip.rotation = U(hq) * l.hip.rotation;
            l.knee.rotation = U(kq) * kneeWorld;
            l.wroteHip = l.hip.localRotation; l.wroteKnee = l.knee.localRotation; l.wrote = true;
            return raise;
        }

        static NV3 N(Vector3 v) => new NV3(v.x, v.y, v.z);
        static Quaternion U(NQ q) => new Quaternion(q.X, q.Y, q.Z, q.W);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Hero = null;
    }
}
