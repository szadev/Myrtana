using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Ui.Dialog;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace Myrtana.Unity
{
    /// <summary>
    /// The dialog camera (Docs/dialog-presentation.md §1): takes the camera from <see cref="G3PlayerCamera"/> for a
    /// conversation and gives it back afterwards. Each shot is a <see cref="DialogShotDirector"/> preset placed like G3
    /// does it (<see cref="DialogShotGeometry"/>: around the listener's head, beside the pair, or a dolly move) and aimed
    /// at the pair's midpoint or the speaker's head. Close shots cut, wide ("Lerp") shots glide in from the previous
    /// camera; within a shot the camera follows the heads smoothly and is pulled in when the world blocks its view.
    /// Presets whose view is mostly blocked are skipped.
    /// </summary>
    public sealed class DialogCameraRig
    {
        /// <summary>A shot is rejected when the world leaves less than this share of its distance free.</summary>
        const float MinFreeShare = 0.75f;
        const float CollisionRadius = 0.15f;
        /// <summary>The camera keeps at least this distance from the point it looks at, m.</summary>
        const float MinViewDistance = 0.4f;
        /// <summary>Smoothing of the followed heads inside a shot, s, and of a glide to a wide shot, s.</summary>
        const float FollowTime = 0.25f, GlideTime = 1.4f;
        /// <summary>fix-76: the first shot glides in from the player camera over about this long, s (G3's camera moves
        /// towards each wished pose with its VeloTrans/VeloRot; a cut from gameplay into the conversation read as a jump).</summary>
        const float EnterTime = 0.35f;

        readonly G3PlayerCamera _playerCamera;
        readonly Camera _camera;
        readonly DialogShotDirector _director;
        float _savedFov;
        bool _active;
        /// <summary>Optional rim/fill for the pair (<see cref="G3CharacterLightRig.Enabled"/>, off by default).</summary>
        G3CharacterLightRig _lights;

        Transform _speakerHead, _listenerHead;
        Transform _speakerRoot, _listenerRoot;
        DialogShotPreset _shot;
        Vector3 _focus, _focusVel, _position, _positionVel;
        bool _glide, _entering;
        float _fov, _fovVel;

        public DialogCameraRig(G3PlayerCamera playerCamera, System.Random random)
        {
            _playerCamera = playerCamera;
            _camera = playerCamera != null ? playerCamera.GetComponent<Camera>() : null;
            _director = new DialogShotDirector(random);
        }

        public bool IsActive => _active;
        public DialogShotPreset Shot => _shot;
        /// <summary>Distance camera → speaker's head now, m (diagnostics).</summary>
        public float SpeakerDistance => _active && _camera != null && _speakerRoot != null
            ? Vector3.Distance(_camera.transform.position, HeadPosition(_speakerRoot, _speakerHead)) : 0;
        /// <summary>
        /// Distance camera → speaker's head the preset itself gives (G3 formula, no collision pull-in, no smoothing), m.
        /// <see cref="SpeakerDistance"/> ≈ this means the rig drives the camera and nothing pulled it in (diagnostics).
        /// </summary>
        public float PresetSpeakerDistance => _active && _shot != null && _speakerRoot != null && _listenerRoot != null
            ? DialogShotGeometry.SpeakerDistance(ToS(HeadPosition(_speakerRoot, _speakerHead)), ToS(HeadPosition(_listenerRoot, _listenerHead)),
                                                 _shot, _director.CurrentAge) : 0;
        /// <summary>Distance between the two heads, m: listener-anchored shots stand this much farther from the speaker.</summary>
        public float HeadDistance => _active && _speakerRoot != null && _listenerRoot != null
            ? Vector3.Distance(HeadPosition(_speakerRoot, _speakerHead), HeadPosition(_listenerRoot, _listenerHead)) : 0;
        /// <summary>
        /// fix-84 diagnostics: how far (m) the camera stands beyond what the level leaves free between it and the point
        /// it looks at (0 = clear view; &gt; 0 = inside or behind rock), and its height above the higher head, m.
        /// </summary>
        public float Obstruction => _active && _camera != null && _shot != null ? Vector3.Distance(_camera.transform.position, Clear(_focus, _camera.transform.position)) : 0;
        public float HeightAboveHeads => _active && _camera != null && _speakerRoot != null && _listenerRoot != null
            ? _camera.transform.position.y - Mathf.Max(HeadPosition(_speakerRoot, _speakerHead).y, HeadPosition(_listenerRoot, _listenerHead).y) : 0;
        /// <summary>Lines in a row by the current speaker after his first (the director's monologue counter).</summary>
        public int SameSpeakerRun => _director.SameSpeakerRun;

        /// <summary>Takes the camera; the hero is drawn whole (no first-person head) until <see cref="End"/>.</summary>
        public void Begin(G3Player hero)
        {
            if (_camera == null || _active) return;
            _active = true;
            _director.Reset();
            _shot = null;
            _savedFov = _camera.fieldOfView;
            _playerCamera.enabled = false;
            foreach (var v in hero.GetComponentsInChildren<IG3BodyView>(true))
            {
                v.SetFirstPerson(false);
                v.SetHidden(false);
            }
            if (G3CharacterLightRig.Enabled)
            {
                _lights ??= new G3CharacterLightRig(_camera);
                _lights.Begin(hero.transform);
            }
        }

        /// <summary>Gives the camera back to the player camera (which re-applies its own view on enable).</summary>
        public void End()
        {
            if (!_active) return;
            _active = false;
            _shot = null;
            _speakerRoot = _listenerRoot = null;
            _lights?.End();
            if (_camera != null) _camera.fieldOfView = _savedFov; // gone when Play stops mid-conversation
            if (_playerCamera != null) _playerCamera.enabled = true;
        }

        /// <summary>A new line: frame <paramref name="speaker"/> talking to <paramref name="listener"/> (roots of both characters).</summary>
        public void Frame(Transform speaker, Transform listener, bool speakerIsHero)
        {
            if (!_active || speaker == null || listener == null) return;
            bool sameSpeaker = _speakerRoot == speaker;
            _speakerRoot = speaker;
            _listenerRoot = listener;
            _speakerHead = FindHead(speaker);
            _listenerHead = FindHead(listener);
            if (_lights != null && _lights.IsActive)
            {
                _lights.AddSubject(speaker);
                _lights.AddSubject(listener);
                _lights.Focus(speaker);
            }
            var s = HeadPosition(_speakerRoot, _speakerHead);
            var l = HeadPosition(_listenerRoot, _listenerHead);
            float partnerDistance = Vector3.Distance(speaker.position, listener.position);
            bool big = IsBigCreature(speaker) || IsBigCreature(listener);
            bool changed = _director.NextShot(speakerIsHero, sameSpeaker, p => Usable(p, s, l), partnerDistance, big);
            if (!changed && _shot != null) return;
            bool first = _shot == null;
            _shot = _director.Current;
            var target = DialogShotGeometry.LookTarget(ToS(s), ToS(l), _shot).ToUnity();
            if (first || _entering)
            {
                // fix-76: the first shot (and a new one while still entering) eases in from the player camera's pose: the
                // position and the look point move onto the shot, no cut and no turn snap
                if (first)
                {
                    var cam = _camera.transform;
                    _position = cam.position;
                    _focus = cam.position + cam.forward * Mathf.Max(MinViewDistance, Vector3.Distance(cam.position, target));
                    _focusVel = _positionVel = Vector3.zero;
                    _fov = _camera.fieldOfView;
                    _fovVel = 0f;
                }
                _entering = true;
                _glide = false;
                return;
            }
            _glide = _shot.Transition == ShotTransition.Glide;
            _focus = target;
            _focusVel = _positionVel = Vector3.zero;
            if (!_glide) _position = Desired(s, l, _focus, 0);
            _camera.fieldOfView = _fov = _director.Fov;
        }

        /// <summary>Places the camera; call from LateUpdate after the characters moved.</summary>
        public void Update(float dt)
        {
            if (!_active || _shot == null || _speakerRoot == null || _listenerRoot == null) return;
            _director.Advance(dt);
            var s = HeadPosition(_speakerRoot, _speakerHead);
            var l = HeadPosition(_listenerRoot, _listenerHead);
            var target = DialogShotGeometry.LookTarget(ToS(s), ToS(l), _shot).ToUnity();
            float followTime = _entering ? EnterTime : FollowTime;
            _focus = Vector3.SmoothDamp(_focus, target, ref _focusVel, followTime, Mathf.Infinity, dt);
            var desired = Desired(s, l, _entering ? target : _focus, _director.CurrentAge);
            _position = Vector3.SmoothDamp(_position, desired, ref _positionVel, _glide ? GlideTime : followTime, Mathf.Infinity, dt);
            // fix-84 (G3 MaintainWallDistance every frame): a glide or ease-in never passes through rock on its way
            var cleared = Clear(_focus, _position);
            if ((cleared - _position).sqrMagnitude > 1e-6f)
            {
                _position = cleared;
                _positionVel = Vector3.zero;
            }
            if (_entering)
            {
                _fov = Mathf.SmoothDamp(_fov, _director.Fov, ref _fovVel, EnterTime, Mathf.Infinity, dt);
                _camera.fieldOfView = _fov;
                if ((_position - desired).sqrMagnitude < 1e-4f && (_focus - target).sqrMagnitude < 1e-4f) _entering = false;
            }
            var look = Quaternion.LookRotation(_focus - _position, Vector3.up);
            look *= Quaternion.Euler(-DialogShotPresets.PitchOffset, 0, 0);
            _camera.transform.SetPositionAndRotation(_position, look);
            _lights?.Place();
        }

        /// <summary>The preset's camera position, pulled towards the target where the world blocks the view.</summary>
        Vector3 Desired(Vector3 speaker, Vector3 listener, Vector3 target, float age) =>
            Clear(target, DialogShotGeometry.CameraPosition(ToS(speaker), ToS(listener), _shot, age).ToUnity());

        /// <summary>
        /// <paramref name="wished"/> moved towards <paramref name="target"/> until the way between them is free of level
        /// geometry, traced both ways (<see cref="ShotClearance"/>; fix-84: never inside rock or outside an enclosed cave).
        /// </summary>
        Vector3 Clear(Vector3 target, Vector3 wished)
        {
            var dir = wished - target;
            float range = dir.magnitude;
            if (range < 1e-3f) return wished;
            dir /= range;
            float d = ShotClearance.Distance(range, OutwardFree(target, dir, range), InwardHit(wished, -dir, range), MinViewDistance);
            return target + dir * d;
        }

        bool Usable(DialogShotPreset preset, Vector3 speaker, Vector3 listener)
        {
            var target = DialogShotGeometry.LookTarget(ToS(speaker), ToS(listener), preset).ToUnity();
            var p = DialogShotGeometry.CameraPosition(ToS(speaker), ToS(listener), preset).ToUnity();
            var dir = p - target;
            float range = dir.magnitude;
            if (range < 1e-3f) return true;
            dir /= range;
            return ShotClearance.FreeShare(range, OutwardFree(target, dir, range), InwardHit(p, -dir, range)) >= MinFreeShare;
        }

        /// <summary>Free distance from <paramref name="from"/> along <paramref name="dir"/>, ignoring both characters.</summary>
        float OutwardFree(Vector3 from, Vector3 dir, float range)
        {
            float free = range;
            int n = Physics.SphereCastNonAlloc(from, CollisionRadius, dir, _hits, range, _playerCamera.collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.distance <= 0 || IsActor(h.collider)) continue; // a cast starting inside a collider: the inward trace sees it
                free = Mathf.Min(free, h.distance);
            }
            // a thin ray as well, which also meets the back of one-sided rock (a sphere that starts touching a wall
            // reports nothing, and mesh colliders are hit from their front side only by default)
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            n = Physics.RaycastNonAlloc(from, dir, _hits, range, _playerCamera.collisionMask, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = backfaces;
            for (int i = 0; i < n; i++)
                if (!IsActor(_hits[i].collider)) free = Mathf.Min(free, _hits[i].distance);
            return free;
        }

        /// <summary>
        /// Distance from the wished camera position back towards the target to the first level surface (−1 = none, 0 =
        /// the position is inside geometry): catches one-sided rock whose faces point away from the target.
        /// </summary>
        float InwardHit(Vector3 from, Vector3 dir, float range)
        {
            if (Physics.CheckSphere(from, CollisionRadius * 0.5f, _playerCamera.collisionMask, QueryTriggerInteraction.Ignore))
            {
                int k = Physics.OverlapSphereNonAlloc(from, CollisionRadius * 0.5f, _overlaps, _playerCamera.collisionMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < k; i++)
                    if (!IsActor(_overlaps[i])) return 0;
            }
            float hit = -1;
            int n = Physics.RaycastNonAlloc(from, dir, _hits, range, _playerCamera.collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (IsActor(_hits[i].collider)) continue;
                if (hit < 0 || _hits[i].distance < hit) hit = _hits[i].distance;
            }
            return hit;
        }

        readonly RaycastHit[] _hits = new RaycastHit[32];
        readonly Collider[] _overlaps = new Collider[16];

        bool IsActor(Collider c) => c != null && (IsPart(c.transform, _speakerRoot) || IsPart(c.transform, _listenerRoot) ||
                                                  c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic);

        /// <summary>G3 FindPreset: a character whose bounding box exceeds 343 m³ (3.43e8 cm³) is a big creature.</summary>
        const float BigCreatureVolume = 343f;

        static bool IsBigCreature(Transform root)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any && b.size.x * b.size.y * b.size.z > BigCreatureVolume;
        }

        /// <summary>The character's own colliders: its hierarchy, or a capsule on its direct parent (never the level the
        /// character happens to be parented under — fix-84).</summary>
        static bool IsPart(Transform t, Transform root) => root != null && (t == root || t.IsChildOf(root) || root.parent == t);

        static Vector3 HeadPosition(Transform root, Transform head) =>
            head != null ? head.position : root.position + Vector3.up * 1.7f;

        /// <summary>The skeleton's head bone (<c>&lt;Rig&gt;_Head_Head_1</c>), or null.</summary>
        static Transform FindHead(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>())
                if (t.name.EndsWith("_Head_Head_1", System.StringComparison.Ordinal)) return t;
            return null;
        }

        static SVector3 ToS(Vector3 v) => new SVector3(v.x, v.y, v.z);
    }

    static class DialogVectorExtensions
    {
        public static Vector3 ToUnity(this SVector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
