using System.Collections.Generic;
using G3.Unity.Characters;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Lays a dead NPC's body on the ground once it is still (fix-80, <see cref="CorpseRestRules"/>; G3 ForceGroundAlignment
    /// for the dead): the ragdoll frozen (<see cref="Ragdoll.Settled"/>) or the dying motion in its held LieDead pose
    /// (<see cref="G3CombatAnimator.LiesDead"/>). The pose is frozen (Animator off, as the ragdoll's), the skin is sampled
    /// (baked skinned meshes) against a small grid of ground heights under the body; a pose corpse is tilted onto the
    /// ground plane and moved until it touches the ground, a ragdoll is only lifted out when it sank in. The origin moves
    /// to the middle of the lying body without moving the body (its children keep their world pose), so the focus name, the
    /// loot reach and the saved pose follow the corpse. A corpse shown again (<see cref="Begin"/> with <c>restored</c>:
    /// a loaded game, a view streaming in) is first centred on its saved spot. <see cref="RemoveFrom"/> undoes it all
    /// (revived, pooled view reused).
    /// </summary>
    public sealed class CorpseRest : MonoBehaviour
    {
        /// <summary>A dying motion that never reaches a held pose is laid down after this long, s.</summary>
        const float FallbackSeconds = 5f;
        /// <summary>The held pose is cross-faded into: it is sampled this long after it started, s.</summary>
        const float FadeSeconds = 0.3f;
        /// <summary>A fresh corpse settles onto the ground over this time (no snap), s.</summary>
        const float BlendSeconds = 0.25f;
        /// <summary>Most skin samples per body.</summary>
        const int MaxSamples = 1500;
        /// <summary>Ground rays start this far above the body (pose corpses: above its origin too), m.</summary>
        const float RayAbove = 0.3f, PoseRayAbove = 1.2f, RayBelow = 3f;
        /// <summary>fix-232: a pose corpse's ground ray that found nothing is cast again from this much higher, m.</summary>
        const float RetryAbove = 3f;

        static readonly RaycastHit[] Hits = new RaycastHit[16];
        static Mesh s_bake;

        readonly List<(Transform t, Vector3 position, Quaternion rotation)> _children = new List<(Transform, Vector3, Quaternion)>();
        readonly List<Vector3> _samples = new List<Vector3>();
        readonly List<SkinnedMeshRenderer> _skins = new List<SkinnedMeshRenderer>();
        G3CombatAnimator _combat;
        G3MotionPlayer _motion;
        Animator _mecanim;
        bool _mecanimWasEnabled, _frozeMecanim;
        AnimatorCullingMode _culling;
        bool _cullingChanged;
        bool _pending, _restored;
        float _since, _liesSince = -1f;
        int _startFrame;
        float _blend = -1f;
        Vector3 _fromPosition, _toPosition;
        Quaternion _fromRotation, _toRotation;
        // fix-163: a settling without ground (the collider not there yet) is tried again; a rested body is checked again
        float _retryAt = -1f, _verifyAt = -1f;
        int _retries, _verifies;
        bool _putBack;
        static int s_logCount;
        static float s_logWindow = -100f;
        /// <summary>Settlings retried this often while the ground under the body is missing, s, at most this many times.</summary>
        const float RetrySeconds = 0.5f;
        const int MaxRetries = 20;
        /// <summary>A rested body is measured again after these delays (s): its pose may change after the sampling.</summary>
        static readonly float[] VerifyDelays = { 1f, 4f };

        /// <summary>The body lies on the ground (counts each settling: the view reports the new pose once per value).</summary>
        public int Serial { get; private set; }
        public bool Rested { get; private set; }
        /// <summary>Last settling, for logs and runner checks: tilt degrees, vertical move m, ground samples found.</summary>
        public string Info { get; private set; } = "-";
        /// <summary>The last settling's vertical move, m (fix-163: a corpse shown again after a save lies where it was: ~0).</summary>
        public float LastOffset { get; private set; }

        /// <summary>Starts waiting for the body under <paramref name="visual"/> to be still, then lays it on the ground.</summary>
        public static CorpseRest Begin(Transform visual, bool restored)
        {
            if (visual == null) return null;
            var rest = visual.TryGetComponent<CorpseRest>(out var r) ? r : visual.gameObject.AddComponent<CorpseRest>();
            rest.Restart(restored);
            return rest;
        }

        /// <summary>Gives the body back to the Animator, upright, with its own child poses (revived, view reused).</summary>
        public static void RemoveFrom(Transform visual)
        {
            if (visual != null && visual.TryGetComponent<CorpseRest>(out var r)) r.Release();
        }

        void Restart(bool restored)
        {
            Release();
            _restored = restored;
            _pending = true;
            _since = Time.time;
            _liesSince = -1f;
            _retryAt = _verifyAt = -1f;
            _retries = _verifies = 0; _putBack = false;
            _reshown = false;
            _startFrame = Time.frameCount;
            _combat = GetComponent<G3CombatAnimator>();
            _mecanim = GetComponent<Animator>();
            if (_mecanim != null && _mecanim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
            {
                // the pose must be written even off screen (a corpse streaming in behind the camera) before it is sampled
                _culling = _mecanim.cullingMode;
                _cullingChanged = true;
                _mecanim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            enabled = true;
        }

        void Release()
        {
            _pending = false;
            _blend = -1f;
            _retryAt = _verifyAt = -1f;
            if (Rested || _children.Count > 0)
            {
                foreach (var c in _children)
                    if (c.t != null && c.t.parent == transform) c.t.SetLocalPositionAndRotation(c.position, c.rotation);
                _children.Clear();
                var e = transform.eulerAngles;
                transform.rotation = Quaternion.Euler(0f, e.y, 0f);
            }
            if (_mecanim != null)
            {
                if (_frozeMecanim) _mecanim.enabled = _mecanimWasEnabled || _mecanim.enabled;
                if (_cullingChanged) _mecanim.cullingMode = _culling;
            }
            _frozeMecanim = _cullingChanged = false;
            if (_motion != null) _motion.ReleasePose(this);
            Rested = false;
            enabled = false;
        }

        static readonly HashSet<CorpseRest> s_rested = new HashSet<CorpseRest>();
        static bool s_hooked;

        /// <summary>The drawn ground changed as a whole (relief on / off): every corpse at rest lies down again on its saved spot.</summary>
        static void OnGroundReset()
        {
            var list = new List<CorpseRest>(s_rested);
            s_rested.Clear();
            foreach (var r in list)
                if (r != null && r.Rested && r.gameObject.activeInHierarchy) r.Restart(true);
        }

        void MarkRested()
        {
            if (!s_hooked) { s_hooked = true; global::G3.Unity.G3TerrainRelief.GroundReset += OnGroundReset; }
            s_rested.Add(this);
        }

        void OnDestroy() => s_rested.Remove(this);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_rested.Clear(); s_hooked = false; }

        void LateUpdate()
        {
            if (_blend >= 0f) Blend();
            if (_pending && Ready())
            {
                _pending = false;
                Settle();
            }
            else if (_retryAt >= 0f && Time.time >= _retryAt && _blend < 0f)
            {
                _retryAt = -1f;
                Settle(); // fix-163: the ground was missing (a neighbour cell's collider not active yet)
            }
            else if (_verifyAt >= 0f && Time.time >= _verifyAt && _blend < 0f) Verify();
            if (!_pending && _blend < 0f && _retryAt < 0f && _verifyAt < 0f) enabled = false; // at rest: nothing to do per frame
        }

        bool Ready()
        {
            if (TryGetComponent<Ragdoll>(out var ragdoll)) return ragdoll.Settled;
            if (_combat != null && _combat.IsDead)
            {
                // fix-180: laid down only in its own dead pose, faded in fully: a body frozen half-way between standing and
                // lying (a fade that never ran: killed frozen, the player taken over) stood "half-upright" on its legs
                if (_combat.DeadPoseShown)
                {
                    if (_liesSince < 0f) _liesSince = Time.time;
                    // the pose is written by this frame's animation pass (restored: the frame after it was shown)
                    return _restored ? Time.frameCount > _startFrame : Time.time - _liesSince >= FadeSeconds;
                }
                _liesSince = -1f;
                if (Time.time - _since <= FallbackSeconds) return false;
                if (!_reshown && _combat.ReshowDeadPose())
                {
                    _reshown = true;
                    _since = Time.time - FallbackSeconds + ReshowSeconds; // one more short wait for the pose
                    Note($"{name}: dead pose not shown after {FallbackSeconds:F0} s: shown again");
                    return false;
                }
                return true; // a dying motion that never reaches a held pose: laid down as it is
            }
            if (Time.time - _since > FallbackSeconds) return true;
            if (_combat == null || !_combat.LiesDead) return false;
            if (_liesSince < 0f) _liesSince = Time.time;
            return _restored ? Time.frameCount > _startFrame : Time.time - _liesSince >= FadeSeconds;
        }

        bool _reshown;
        /// <summary>After showing the dead pose again, the body waits this long for it at most, s.</summary>
        const float ReshowSeconds = 1f;

        static void Note(string text)
        {
            if (G3.Player.BodySinkRules.MayLog(Time.unscaledTime, ref s_logCount, ref s_logWindow)) Debug.Log("[corpse] fix-180: " + text);
        }

        /// <summary>
        /// fix-180: the most upright leg's angle from straight down, degrees (G3 rigs: <c>_Leg_Leg_1</c> hip to
        /// <c>_Foot_Foot_1</c> ankle; four-legged rigs' hind legs): a standing body ~0-25°, a lying one 45-90°. NaN without legs.
        /// </summary>
        public static float UprightLegAngle(Transform visual) => UprightLegAngle(visual, Vector3.down);

        /// <summary>
        /// fix-232: the most upright leg's angle from <paramref name="down"/> (the ground's inward normal under the body), degrees:
        /// measured against world down, a wolf lying on a 35° slope with its legs downhill read 27° (its LieDead legs are ~52°
        /// off its body's down, tilted 35° with the ground), a body standing on that slope reads the slope angle.
        /// </summary>
        public static float UprightLegAngle(Transform visual, Vector3 down)
        {
            if (visual == null) return float.NaN;
            Transform hipL = null, hipR = null, footL = null, footR = null;
            foreach (var t in visual.GetComponentsInChildren<Transform>())
            {
                string n = t.name;
                if (n.EndsWith("_Left_Leg_Leg_1", System.StringComparison.Ordinal)) hipL = t;
                else if (n.EndsWith("_Right_Leg_Leg_1", System.StringComparison.Ordinal)) hipR = t;
                else if (n.EndsWith("_Left_Foot_Foot_1", System.StringComparison.Ordinal)) footL = t;
                else if (n.EndsWith("_Right_Foot_Foot_1", System.StringComparison.Ordinal)) footR = t;
            }
            float best = float.NaN;
            if (hipL != null && footL != null) best = Vector3.Angle(footL.position - hipL.position, down);
            if (hipR != null && footR != null)
            {
                float a = Vector3.Angle(footR.position - hipR.position, down);
                best = float.IsNaN(best) ? a : Mathf.Min(best, a);
            }
            return best;
        }

        void Blend()
        {
            _blend += Time.deltaTime / BlendSeconds;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_blend));
            transform.SetPositionAndRotation(Vector3.Lerp(_fromPosition, _toPosition, t), Quaternion.Slerp(_fromRotation, _toRotation, t));
            if (_blend >= 1f) _blend = -1f;
        }

        void Settle()
        {
            bool ragdoll = TryGetComponent<Ragdoll>(out _);
            if (_mecanim != null && _mecanim.enabled)
            {
                _mecanimWasEnabled = true;
                _mecanim.enabled = false; // the pose stays as it is (the ragdoll does the same)
                _frozeMecanim = true;
            }
            // fix-202: and the motion player's graph stops writing the bones (a disabled Animator alone does not stop it)
            if (_motion == null) TryGetComponent(out _motion);
            if (_motion != null) _motion.HoldPose(this);
            if (!Sample()) { Info = "no skin"; Rested = true; Serial++; return; }
            Bounds(out var min, out var max);
            var centre = new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f);
            if (_restored)
            {
                // a corpse met again lies around its saved spot (the origin it had when it came to rest)
                var shift = new Vector3(transform.position.x - centre.x, 0f, transform.position.z - centre.z);
                transform.position += shift;
                for (int i = 0; i < _samples.Count; i++) _samples[i] += shift;
                min += shift; max += shift; centre += shift;
            }
            // fix-188: a ragdoll that went through the ground (or out of the world) lies down again as a pose corpse on the
            // floor under its death spot: its frozen pose is partly inside the hill, and lifted by its lowest limbs it was
            // propped 1.2-2.9 m over a 33° slope (fix180 Nordmar, b1022b) or found no ground at all (fell to y -122)
            if (!_putBack && TryGetComponent<Ragdoll>(out var fallen) && fallen.FellThrough)
            {
                _putBack = true;
                var spot = fallen.DeathPosition;
                if (NpcWalker.TryFloorHeight(spot, out float floorY, 2f)) spot.y = floorY; // it may have stumbled off a ledge
                Note($"{name}: fell through the ground at {fallen.PelvisPosition}: lies down in its dead pose at its death spot {spot}");
                fallen.Remove(); // the skeleton goes back to the Animator
                transform.position = spot;
                Restart(false);
                return;
            }
            float start = RayStart(max);
            if (!Survey(min, max, start, out var g))
            {
                // fix-163: no ground under the body yet (its cell's collider streams in later): tried again; the origin is
                // not reported meanwhile (a save keeps the last good spot)
                if (_retries++ < MaxRetries)
                {
                    Info = $"ground samples {g.Found}/{g.Nx * g.Nz}: retry {_retries}";
                    _retryAt = Time.time + RetrySeconds;
                    return;
                }
                Info = $"ground samples {g.Found}/{g.Nx * g.Nz}: left as it lies";
                Rested = true;
                Serial++;
                return;
            }
            var normal = g.Normal;
            float a = g.A, b = g.B, c = g.C;
            // 1. the origin moves under the middle of the body; the body itself stays (children keep their world pose)
            var pivot = new Vector3(centre.x, a * centre.x + b * centre.z + c, centre.z);
            var move = pivot - transform.position;
            // fix-163: a pose corpse's children keep only the horizontal part of the move: the pose sits over the origin
            // as it does whenever the corpse is shown again, so the origin the simulation keeps (ReportCorpse) puts a
            // restored body where it lay. Compensating the height too left the saved origin up to ~0.8 m off the body
            // (each load lifted or dropped it again; owner saves 218-226), and a settling that then failed showed the
            // body half in the ground (owner, Vengard yard). A ragdoll's pose is physics: it keeps its world pose.
            var keep = ragdoll ? move : new Vector3(move.x, 0f, move.z);
            RecordChildren();
            transform.position = pivot;
            foreach (var child in _children) if (child.t != null) child.t.position -= keep;
            if (!ragdoll)
                for (int i = 0; i < _samples.Count; i++) _samples[i] += new Vector3(0f, move.y, 0f);
            // 2. a pose corpse leans onto the ground plane; then up or down until the skin rests on the ground
            var tilt = Quaternion.identity;
            if (!ragdoll)
            {
                var n = CorpseRestRules.LimitTilt(normal);
                tilt = Quaternion.FromToRotation(transform.up, new Vector3(n.x, n.y, n.z));
            }
            float offset;
            // fix-221: a ragdoll is lifted only by its collision parts' real penetration (its feet / toes / back slots have
            // no collider and lie in an uphill slope: lifted by its skin it was propped 0.35-0.58 m over an 18-33° slope)
            if (ragdoll && PartClearances(g, _clearances) > 0)
                offset = CorpseRestRules.RagdollLift(_clearances, g.Normal.y);
            else
            {
                var gaps = new List<float>(_samples.Count);
                foreach (var s in _samples)
                {
                    var p = pivot + tilt * (s - pivot);
                    gaps.Add(p.y - g.Height(p.x, p.z));
                }
                offset = CorpseRestRules.RestOffset(gaps, ragdoll);
            }
            LastOffset = offset;
            // a fresh pose corpse blends from where its body is (the origin's height move above is not yet shown)
            _fromPosition = ragdoll || _restored ? transform.position : transform.position - Vector3.up * move.y;
            _fromRotation = transform.rotation;
            _toPosition = pivot + Vector3.up * offset;
            _toRotation = tilt * transform.rotation;
            // fix-202: a fresh ragdoll lifted out of the ground blends up too (it jumped up to 0.4 m in one frame after
            // lying still for seconds); only a corpse shown again lies down at once
            if (CorpseRestRules.SettlesAtOnce(_restored)) transform.SetPositionAndRotation(_toPosition, _toRotation);
            else
            {
                transform.position = _fromPosition;
                _blend = 0f;
            }
            Info = $"{(ragdoll ? "ragdoll" : _restored ? "restored pose" : "pose")}: tilt {Quaternion.Angle(Quaternion.identity, tilt):F0}°, " +
                   $"moved {offset:+0.00;-0.00} m, origin moved {new Vector2(move.x, move.z).magnitude:F2} m, ground {g.Found}/{g.Nx * g.Nz}" +
                   (g.Walls > 0 ? $" ({g.Walls} on a wall)" : "");
            Rested = true;
            Serial++;
            MarkRested();
            if (_verifies < VerifyDelays.Length) _verifyAt = Time.time + BlendSeconds + VerifyDelays[_verifies];
        }

        /// <summary>
        /// fix-163 safety net after the settling: the lying body is measured again (its pose may have changed after it
        /// was sampled, a collider may have arrived); sunk deeper than <see cref="G3.Player.BodySinkRules.Tolerance"/> it
        /// is lifted onto the ground and the new spot reported (Serial). Logged, rate-limited.
        /// </summary>
        void Verify()
        {
            _verifyAt = -1f;
            int done = ++_verifies;
            if (Measure(out float low, out _, out _))
            {
                float lift = G3.Player.BodySinkRules.CorpseLift(low, CorpseRestRules.MaxLift);
                if (lift > 0f)
                {
                    transform.position += Vector3.up * (lift + CorpseRestRules.Contact);
                    Info += $"; sunk {-low:F2} m after rest: lifted";
                    Serial++;
                    if (G3.Player.BodySinkRules.MayLog(Time.unscaledTime, ref s_logCount, ref s_logWindow))
                        Debug.Log($"[corpse] fix-163: {name} lay {-low:F2} m in the ground at {transform.position} after its rest: lifted");
                }
            }
            if (done < VerifyDelays.Length) _verifyAt = Time.time + VerifyDelays[done];
        }

        /// <summary>
        /// Where the ground rays start: a ragdoll just above its skin (a body under a table rests on the floor), one that
        /// fell through above its death height, a pose corpse above its origin too. fix-166: <see cref="Measure"/> (the rest
        /// check, the runner's corpsegap) starts where the settling did: from +1.2 m it saw the uphill slope over a ragdoll
        /// that the settling (rays from inside the hill) had not, and called a resting body 0.14 m sunk (fix80 Nordmar).
        /// </summary>
        float RayStart(Vector3 max)
        {
            if (TryGetComponent<Ragdoll>(out var rd))
                return rd.FellThrough ? Mathf.Max(max.y, rd.DeathHeight) + 0.5f : max.y + RayAbove;
            return Mathf.Max(max.y, transform.position.y) + PoseRayAbove;
        }

        /// <summary>Ground heights on a grid over the body's footprint and the plane through them.</summary>
        struct GroundSurvey
        {
            public float[] Heights;
            public int Nx, Nz, Found, Walls;
            public float MinX, MinZ, Step, A, B, C;
            public (float x, float y, float z) Normal;
            public float Height(float x, float z) => CorpseRestRules.GridHeight(Heights, Nx, Nz, MinX, MinZ, Step, x, z, A, B, C);
        }

        bool Survey(Vector3 min, Vector3 max, float start, out GroundSurvey g)
        {
            g = new GroundSurvey { MinX = min.x, MinZ = min.z };
            g.Step = Mathf.Max(0.2f, Mathf.Max(max.x - min.x, max.z - min.z) / 4f);
            g.Nx = Mathf.Clamp(Mathf.CeilToInt((max.x - min.x) / g.Step) + 1, 2, 9);
            g.Nz = Mathf.Clamp(Mathf.CeilToInt((max.z - min.z) / g.Step) + 1, 2, 9);
            g.Heights = new float[g.Nx * g.Nz];
            float reach = start - (min.y - RayBelow);
            // fix-232: a pose corpse's ray that found nothing began inside the uphill slope (the 35° Nordmar spot: 8 of 20 missed,
            // the plane through the rest lay under the hill, the rest check a second later found the body 0.76 m in it and lifted
            // it 1 m off the downhill ground): it starts again higher. Ragdolls keep their rays (a body under a table).
            bool pose = !TryGetComponent<Ragdoll>(out _);
            for (int iz = 0; iz < g.Nz; iz++)
            for (int ix = 0; ix < g.Nx; ix++)
            {
                float x = min.x + ix * g.Step, z = min.z + iz * g.Step;
                if (TryGround(new Vector3(x, start, z), reach, out float y) ||
                    pose && TryGround(new Vector3(x, start + RetryAbove, z), reach + RetryAbove, out y))
                {
                    g.Heights[iz * g.Nx + ix] = y;
                }
                else g.Heights[iz * g.Nx + ix] = float.NaN;
            }
            // fix-232: a pose corpse against a rock face: the face is no ground under it (its origin is on its floor)
            if (pose)
            {
                var o = transform.position;
                g.Walls = CorpseRestRules.DropWallSamples(g.Heights, g.Nx, g.Nz, g.MinX, g.MinZ, g.Step, o.x, o.y, o.z);
            }
            // fix-166: the top of a barrel / wall beside the body is no ground under it (the rays start above the body)
            CorpseRestRules.DropRaisedOutliers(g.Heights, g.Nx, g.Nz, g.MinX, g.MinZ, g.Step);
            var ground = new List<(float x, float y, float z)>(g.Nx * g.Nz);
            for (int iz = 0; iz < g.Nz; iz++)
            for (int ix = 0; ix < g.Nx; ix++)
            {
                float y = g.Heights[iz * g.Nx + ix];
                if (!float.IsNaN(y)) ground.Add((min.x + ix * g.Step, y, min.z + iz * g.Step));
            }
            g.Found = ground.Count;
            return CorpseRestRules.FitPlane(ground, out g.Normal, out g.A, out g.B, out g.C);
        }

        /// <summary>
        /// Measures the lying body now (dev runner, fix-80): the low skin percentile's height above the ground (m, negative =
        /// sunk in), the pelvis bone's height above the ground (NaN without a <c>Spine_Spine_1</c>), and how far the origin
        /// (the focus name's horizontal anchor) is from the middle of the body (m). False without skin or ground.
        /// </summary>
        public bool Measure(out float lowGap, out float pelvisHeight, out float centreOffset)
        {
            lowGap = pelvisHeight = centreOffset = float.NaN;
            if (!Sample()) return false;
            Bounds(out var min, out var max);
            if (!Survey(min, max, RayStart(max), out var g)) return false;
            // fix-221: a ragdoll's contact with the ground is its collision parts' clearance (the skin's feet lie in the slope)
            if (TryGetComponent<Ragdoll>(out _) && PartClearances(g, _clearances) > 0)
                lowGap = CorpseRestRules.LowestClearance(_clearances);
            else
            {
                var gaps = new List<float>(_samples.Count);
                foreach (var p in _samples) gaps.Add(p.y - g.Height(p.x, p.z));
                lowGap = CorpseRestRules.Percentile(gaps, CorpseRestRules.LowPercentile);
            }
            centreOffset = new Vector2(transform.position.x - (min.x + max.x) * 0.5f, transform.position.z - (min.z + max.z) * 0.5f).magnitude;
            foreach (var t in GetComponentsInChildren<Transform>())
                if (t.name.EndsWith("Spine_Spine_1", System.StringComparison.OrdinalIgnoreCase))
                {
                    pelvisHeight = t.position.y - g.Height(t.position.x, t.position.z);
                    break;
                }
            return true;
        }

        /// <summary>fix-232 (dev runner): the up normal of the ground plane under the lying body, as the settling surveys it.</summary>
        public bool GroundNormal(out Vector3 up)
        {
            up = Vector3.up;
            if (!Sample()) return false;
            Bounds(out var min, out var max);
            if (!Survey(min, max, RayStart(max), out var g)) return false;
            up = new Vector3(g.Normal.x, g.Normal.y, g.Normal.z);
            return true;
        }

        readonly List<float> _clearances = new List<float>();
        readonly List<Collider> _parts = new List<Collider>();

        /// <summary>
        /// fix-221: the clearance over the surveyed ground of every ragdoll collision sphere (a capsule's two end spheres, a
        /// sphere part), <see cref="CorpseRestRules.PartClearance"/>, into <paramref name="into"/>; returns how many.
        /// </summary>
        int PartClearances(GroundSurvey g, List<float> into)
        {
            into.Clear();
            GetComponentsInChildren(false, _parts);
            foreach (var c in _parts)
            {
                if (c == null || !c.enabled || c.isTrigger || c.attachedRigidbody == null) continue;
                var t = c.transform;
                var s = t.lossyScale;
                float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                if (c is SphereCollider sphere)
                    Add(t.TransformPoint(sphere.center), sphere.radius * scale);
                else if (c is CapsuleCollider capsule)
                {
                    var axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                    float half = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
                    Add(t.TransformPoint(capsule.center + axis * half), capsule.radius * scale);
                    Add(t.TransformPoint(capsule.center - axis * half), capsule.radius * scale);
                }
            }
            return into.Count;

            void Add(Vector3 p, float radius) =>
                into.Add(CorpseRestRules.PartClearance(p.y - g.Height(p.x, p.z), radius, g.Normal.y));
        }

        void RecordChildren()
        {
            if (_children.Count > 0) return;
            for (int i = 0; i < transform.childCount; i++)
            {
                var t = transform.GetChild(i);
                _children.Add((t, t.localPosition, t.localRotation));
            }
        }

        /// <summary>World skin points of the posed body (every n-th vertex of each baked skinned mesh), else its bones.</summary>
        bool Sample()
        {
            _samples.Clear();
            GetComponentsInChildren(false, _skins);
            int total = 0;
            foreach (var s in _skins) if (s != null && s.enabled && s.sharedMesh != null) total += s.sharedMesh.vertexCount;
            if (total > 0)
            {
                int stride = Mathf.Max(1, total / MaxSamples);
                if (s_bake == null) s_bake = new Mesh { name = "CorpseRestBake" };
                var vertices = new List<Vector3>();
                foreach (var s in _skins)
                {
                    if (s == null || !s.enabled || s.sharedMesh == null) continue;
                    s.BakeMesh(s_bake, true);
                    s_bake.GetVertices(vertices);
                    var m = Matrix4x4.TRS(s.transform.position, s.transform.rotation, Vector3.one);
                    for (int i = 0; i < vertices.Count; i += stride) _samples.Add(m.MultiplyPoint3x4(vertices[i]));
                }
            }
            if (_samples.Count == 0)
                foreach (var t in GetComponentsInChildren<Transform>())
                    if (t != transform) _samples.Add(t.position);
            return _samples.Count > 0;
        }

        void Bounds(out Vector3 min, out Vector3 max)
        {
            min = max = _samples[0];
            foreach (var p in _samples)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        /// <summary>The first solid surface below <paramref name="from"/>: no triggers, bodies (ragdolls, items) or characters, nothing of this corpse.</summary>
        bool TryGround(Vector3 from, float reach, out float y)
        {
            y = 0f;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, Hits, reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.collider is CharacterController || h.rigidbody != null || h.collider.transform.IsChildOf(transform)) continue;
                if (h.distance < best)
                {
                    best = h.distance;
                    y = h.point.y;
                }
            }
            // remaster-relief: on the terrain the body lies on the drawn relief, not on G3's coarser collision under it
            // (Docs/remaster-world.md section 14); the settling runs again when the relief goes on or off (GroundReset)
            if (!float.IsPositiveInfinity(best) && global::G3.Unity.G3TerrainRelief.TryGround(new Vector3(from.x, y, from.z), out float col, out float vis) &&
                Mathf.Abs(col - y) < 0.08f)
                y = vis;
            return !float.IsPositiveInfinity(best);
        }
    }
}
