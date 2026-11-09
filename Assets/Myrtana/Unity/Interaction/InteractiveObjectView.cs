using G3.Unity.Characters;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// One interactive object in the scene. Doors play their G3 open/close motions (<see cref="G3Character"/> clips of the
    /// door actor); animated objects (grindstone, chests, barbecue, water pipe, orc boulder) play their own part of the
    /// hero's use motions (<see cref="ObjectUseParts"/>); a chest built without those parts swings its lid (bone
    /// <c>Chest_Top</c>) open by hand as before.
    /// Chests and doors get a solid box collider (the door's moves with its leaf bone).
    /// </summary>
    public sealed class InteractiveObjectView : MonoBehaviour
    {
        const string LidBone = "Chest_Top", DoorBone = "Door_Move";
        /// <summary>The temple doors of Mora Sul and Al Shedim (G3_TempleDoor_01) move their leaf on this bone.</summary>
        const string TempleDoorBone = "Temple_Bone_Move";
        const float LidOpenAngle = 75f, LidSpeed = 160f; // degrees, degrees per second

        Transform _lid;
        Quaternion _lidClosed;
        float _lidAngle, _lidTarget;
        G3Character _character;
        bool _doorOpen;
        Renderer _renderer;

        public InteractiveObjectDef Def { get; private set; }
        public string Id => Def.Id;

        public void Init(InteractiveObjectDef def, bool animated)
        {
            Def = def;
            if (!animated) return;
            _renderer = GetComponentInChildren<Renderer>();
            _character = GetComponent<G3Character>();
            _lid = Find(transform, LidBone);
            if (_lid != null) _lidClosed = _lid.localRotation;
            AddCollider();
        }

        /// <summary>Where the hero aims his focus and the HUD hangs the name.</summary>
        public Vector3 FocusPoint => _renderer != null ? _renderer.bounds.center : transform.position + Vector3.up * 0.5f;

        /// <summary>
        /// Where the focus ray ends (fix-104, <see cref="FocusLineOfSight"/>): G3's point 3/4 up the entity's world box — the
        /// mesh box of a prefab view; for an object of the cell scene (no prefab, only a focus point) the box of its own mesh
        /// colliders at its pivot (fix-105: a signpost board 2.8 m up, a hearth whose pivot+0.5 m lay inside its stone), else
        /// <see cref="FocusPoint"/>. Its own mesh does not block the ray.
        /// </summary>
        public Vector3 SightPoint
        {
            get
            {
                if (_renderer != null) return FocusLineOfSight.Point(_renderer.bounds);
                return OwnBox(out var box) ? FocusLineOfSight.Point(box) : FocusPoint;
            }
        }

        /// <summary>The world box of the own mesh colliders of an object of the cell scene (no prefab view), when there are any.</summary>
        bool OwnBox(out Bounds box)
        {
            if (!_ownBoxKnown && Time.time >= _ownBoxRetry)
            {
                // the cell's colliders stream in after the view: look again a little later when none is there yet
                _ownBoxKnown = FocusLineOfSight.TryOwnMeshBox(transform.position, out _ownBox);
                if (!_ownBoxKnown) _ownBoxRetry = Time.time + OwnBoxRetrySeconds;
            }
            box = _ownBox;
            return _ownBoxKnown;
        }

        const float OwnBoxRetrySeconds = 2f;
        Bounds _ownBox;
        bool _ownBoxKnown;
        float _ownBoxRetry;

        /// <summary>An object of the cell scene: its view has no mesh, only a focus point (fix-104).</summary>
        public bool IsFocusPointOnly => _renderer == null;

        /// <summary>
        /// G3's focus-name point of an object whose mesh height is known (<see cref="InteractiveObjectDef.FocusHeight"/>:
        /// signposts, fix-80): the local point (0, bounds height, 0) above its origin — just above the board, centred on it;
        /// an object of the cell scene: its own mesh colliders' box height above the pivot (fix-184).
        /// False for the rest (the HUD uses the renderer bounds or a default height).
        /// </summary>
        public bool TryGetNameAnchor(out Vector3 anchor)
        {
            anchor = default;
            if (Def == null) return false;
            if (Def.FocusHeight > 0f)
            {
                anchor = transform.TransformPoint(0f, Def.FocusHeight, 0f);
                return true;
            }
            // fix-184 (regress-1021b fix79_focus: a stool's name 1.90 m over it, G3 0.66): an object of the cell scene has no
            // renderer of its own, so the HUD fell back to a person's height; its own mesh box gives G3's bounds height
            if (_renderer == null && OwnBox(out var box))
            {
                anchor = transform.position + transform.up * box.size.y;
                return true;
            }
            return false;
        }

        /// <summary>Shows the state of the object (a door opens or closes, animated when it changed during play).</summary>
        public void Show(InteractiveObjects objects, bool animate)
        {
            if (Def.Door == null) return;
            bool open = objects.IsOpen(Def.Id);
            if (open == _doorOpen && animate) return;
            _doorOpen = open;
            PlayDoor(open ? Def.Door.OpenMotion : Def.Door.CloseMotion, animate, open);
        }

        /// <summary>
        /// Plays the object's own part of a hero use motion (<see cref="ObjectUseParts.PartOf"/>: the <c>_I_</c> variant, e.g.
        /// the grindstone's wheel turning, the chest's lid opening, the orc boulder's stone tilting, in step with the hero).
        /// False when the object has no such part.
        /// </summary>
        public bool PlayUsePart(string heroKey, float fade)
        {
            var c = FindPart(ObjectUseParts.PartOf(heroKey));
            return c != null && _character.Play(c.Motion, fade);
        }

        /// <summary>Shows the last frame of an object part at once (the boulder lying where it lay after a let-go).</summary>
        public void RestUsePart(string heroKey) => RestPart(ObjectUseParts.PartOf(heroKey));

        /// <summary>
        /// Where the user of an animated object stands exactly (fix-189): its actor's <see cref="ObjectUseParts.ExactDummy"/>
        /// (the object parts' keys on it are dropped, so it stays at its bind pose), else the object's origin. The water
        /// pipe's lies 0.99 m in front of the pipe; standing on the origin put the hero inside the pipe.
        /// </summary>
        public Vector3 ExactUsePoint
        {
            get
            {
                if (!_exactLooked)
                {
                    _exact = Find(transform, ObjectUseParts.ExactDummy);
                    _exactLooked = true;
                }
                return _exact != null ? _exact.position : transform.position;
            }
        }

        /// <summary>The actor's placement dummy when its visual has one (fix-243: a chest without it must not put the hero
        /// on its origin, inside it).</summary>
        public bool TryExactUsePoint(out Vector3 point)
        {
            point = ExactUsePoint;
            return _exact != null;
        }

        Transform _exact;
        bool _exactLooked;

        /// <summary>The object has its own part of this hero motion (the orc boulder, the animated water pipe).</summary>
        public bool HasUsePart(string heroKey) => FindPart(ObjectUseParts.PartOf(heroKey)) != null;

        /// <summary>The object's visual has this part (an <c>_I_</c> key, <see cref="ObjectPartState"/>).</summary>
        public bool HasPart(string partKey) => FindPart(partKey) != null;

        /// <summary>Carries out the object's side of a use (<see cref="ObjectPartState"/>).</summary>
        public void Apply(ObjectPartCommand command, float fade)
        {
            var c = FindPart(command.Part);
            var anim = GetComponent<Animation>();
            switch (command.Action)
            {
                case ObjectPartAction.Play:
                    if (c != null) _character.Play(c.Motion, fade);
                    break;
                case ObjectPartAction.Rest:
                    RestPart(command.Part);
                    break;
                case ObjectPartAction.Stop:
                    if (anim != null) anim.Stop(); // the pose stays where the part left it (the wheel stands still)
                    break;
            }
        }

        void RestPart(string partKey)
        {
            var c = FindPart(partKey);
            var anim = GetComponent<Animation>();
            if (c == null || anim == null || !_character.Play(c.Motion, 0f)) return;
            var state = anim[c.Clip.name];
            state.normalizedTime = 1f;
            anim.Sample();
            anim.Stop();
        }

        G3Character.ClipInfo FindPart(string partKey)
        {
            if (_character == null || partKey == null) return null;
            foreach (var c in _character.Clips)
                if (c?.Clip != null && string.Equals(G3MotionKey.KeyOf(c.Motion), partKey, System.StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        /// <summary>Opens or closes a chest lid.</summary>
        public void SetLid(bool open) => _lidTarget = open ? LidOpenAngle : 0f;

        void Update()
        {
            if (_lid == null || Mathf.Approximately(_lidAngle, _lidTarget)) return;
            _lidAngle = Mathf.MoveTowards(_lidAngle, _lidTarget, LidSpeed * Time.deltaTime);
            // The lid hinges at the back: it turns about the chest's own right axis, opening up and backwards.
            var axis = _lid.parent != null ? _lid.parent.InverseTransformDirection(transform.right) : Vector3.right;
            _lid.localRotation = Quaternion.AngleAxis(-_lidAngle, axis) * _lidClosed;
        }

        void PlayDoor(string motion, bool animate, bool open)
        {
            var anim = GetComponent<Animation>();
            var clip = _character != null && motion != null ? _character.Find(motion) : null;
            if (anim == null || clip == null) return;
            if (!animate && !open) return; // the prefab rests closed
            var state = anim[clip.Clip.name];
            state.wrapMode = WrapMode.ClampForever;
            anim.Play(clip.Clip.name);
            if (animate) return;
            state.normalizedTime = 1f;
            anim.Sample();
        }

        void AddCollider()
        {
            var skinned = GetComponentInChildren<SkinnedMeshRenderer>();
            if (skinned == null || skinned.sharedMesh == null) return;
            var mesh = skinned.sharedMesh;
            // fix-261 [owner]: the temple door's leaf is Temple_Bone_Move; unmatched, the door got one still box over the whole
            // mesh, which stayed in the doorway after the leaf moved away (the opened Mora Sul temple could not be entered).
            int leaf = System.Array.FindIndex(skinned.bones, b => b != null && (b.name == DoorBone || b.name == TempleDoorBone));
            var holder = new GameObject("Collider");
            var box = holder.AddComponent<BoxCollider>();
            if (leaf < 0)
            {
                holder.transform.SetParent(skinned.transform, false); // a chest: the whole mesh
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;
                return;
            }
            // A door: only the vertices of the leaf, in the leaf bone's space, so the frame stays walkable when open.
            holder.transform.SetParent(skinned.bones[leaf], false);
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            var bind = mesh.bindposes[leaf];
            var bounds = new Bounds();
            bool any = false;
            for (int i = 0; i < vertices.Length && i < weights.Length; i++)
            {
                if (weights[i].boneIndex0 != leaf || weights[i].weight0 < 0.5f) continue;
                var p = bind.MultiplyPoint3x4(vertices[i]);
                if (!any) bounds = new Bounds(p, Vector3.zero);
                else bounds.Encapsulate(p);
                any = true;
            }
            box.center = bounds.center;
            box.size = Vector3.Max(bounds.size, Vector3.one * 0.05f);
        }

        static Transform Find(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var found = Find(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
