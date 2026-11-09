using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3's focus for the HUD: the locked combat target, else the conversation partner, else what the hero would talk to or
    /// pick up (<see cref="PlayerInteraction"/>). Gives the focus bar its actor and the focus name its text and world anchor
    /// (G3 gCGUIEntityRenderer::CalcTextRect, <see cref="FocusNamePlacement"/>, fix-79): an object or item at its pivot plus
    /// its up axis times the height of its bounds (G3 FocusNameType Entity: the world tree boundary's height, the bind pose
    /// of an animated object such as a chest), a character 30 cm above the highest skeleton bone (G3 Skeleton: the animated
    /// box's height; skinned bounds are far too loose here); then G3's 10 cm along the camera's up axis.
    /// </summary>
    public sealed class HudFocusTracker
    {
        /// <summary>
        /// The highest bone is the head joint, not the top of the skull; the name goes clear above the hair or helmet (with
        /// G3's view lift of 10 cm, 40 cm above the joint as before fix-79).
        /// </summary>
        const float BoneNameLift = 0.3f;
        /// <summary>No renderer (a view still loading): about a man's height above the pivot.</summary>
        const float FallbackHeight = 1.9f;

        readonly PlayerInteraction _interaction;
        readonly PlayerCombat _combat;
        readonly NpcViewDirectory _npcs;
        readonly WorldItemViews _items;
        readonly List<Renderer> _renderers = new List<Renderer>();
        readonly List<Transform> _bones = new List<Transform>();
        GameObject _target;

        public HudFocusTracker(PlayerInteraction interaction, PlayerCombat combat, NpcViewDirectory npcs, WorldItemViews items)
        {
            _interaction = interaction;
            _combat = combat;
            _npcs = npcs;
            _items = items;
        }

        /// <summary>The focused NPC (empty when an item or nothing is in focus).</summary>
        public ActorId Actor { get; private set; }
        /// <summary>The focused world item, or null.</summary>
        public string WorldItem { get; private set; }
        /// <summary>The focused NPC is the locked combat target.</summary>
        public bool Locked { get; private set; }
        /// <summary>The focused interactive object (chest, door, bed, station), or null.</summary>
        public string WorldObject { get; private set; }
        public bool HasFocus => !Actor.IsEmpty || WorldItem != null || WorldObject != null;

        /// <summary>The interactive objects whose views carry the focus name (null = none).</summary>
        public InteractiveObjectViews Objects { get; set; }

        /// <summary>Resolves the focus of this frame; returns true when it changed.</summary>
        public bool Refresh(GameSession session)
        {
            var actor = default(ActorId);
            string item = null, obj = null;
            bool locked = false;
            if (_combat != null && _combat.Focus != null)
            {
                actor = _combat.Focus.Actor;
                locked = _combat.FocusIsOpponent; // neutral bystanders can be focused (GetFocusEntityPriority 1) and keep their colour
            }
            else if (session.Dialog.IsActive) actor = session.Dialog.Current.Partner;
            else if (_interaction != null)
            {
                actor = _interaction.Focus;
                item = _interaction.FocusItem;
                obj = _interaction.FocusObject;
            }
            if (actor.Equals(Actor) && item == WorldItem && obj == WorldObject && locked == Locked) return false;
            Actor = actor;
            WorldItem = item;
            WorldObject = obj;
            Locked = locked;
            _target = FindTarget();
            _renderers.Clear();
            _bones.Clear();
            if (_target == null) return true;
            _target.GetComponentsInChildren(_renderers);
            // G3 FocusNameType Skeleton for characters only; objects (an animated chest too) and items are Entity (fix-79)
            if (!Actor.IsEmpty)
                foreach (var r in _renderers)
                    if (r is SkinnedMeshRenderer skinned)
                        foreach (var bone in skinned.bones)
                            if (bone != null && !_bones.Contains(bone)) _bones.Add(bone);
            return true;
        }

        public FocusTarget Describe(GameSession session) =>
            !Actor.IsEmpty ? HudFocus.Npc(session, Actor, Locked) : WorldItem != null ? HudFocus.WorldItem(session, WorldItem)
            : WorldObject != null && session.Interaction.Objects.TryGet(WorldObject, out var def)
                ? HudFocus.Object(session, def) : default;

        /// <summary>The point the focus name sits on before G3's view lift: above the skeleton, or the pivot plus the bounds' height.</summary>
        public bool TryGetAnchor(out Vector3 anchor)
        {
            anchor = default;
            if (_target == null) return false;
            // a signpost board: G3's point above its origin (fix-80; the board itself is part of the cell scene). No lift
            // here: G3's 10 cm view lift is added by TryGetAnchor(camera) for every target (fix-79).
            if (WorldObject != null && _target.TryGetComponent<InteractiveObjectView>(out var objectView) && objectView.TryGetNameAnchor(out anchor))
                return true;
            if (_bones.Count > 0)
            {
                float top = float.MinValue;
                foreach (var bone in _bones)
                    if (bone != null && bone.position.y > top) top = bone.position.y;
                var p = _target.transform.position;
                anchor = new Vector3(p.x, top + BoneNameLift, p.z);
                return true;
            }
            var t = _target.transform;
            float height = TryGetBoundsHeight(out float h) ? h : FallbackHeight / Mathf.Max(0.01f, t.lossyScale.y);
            anchor = ToUnity(FocusNamePlacement.EntityPoint(ToNumerics(t.position), ToNumerics(t.up), t.lossyScale.y, height));
            return true;
        }

        /// <summary>
        /// Height (max.y − min.y) of the world box round the target's meshes, as G3's world tree boundary: mesh renderers by
        /// their bounds, skinned ones by their mesh's bind-pose bounds (the chest with its lid shut; the skinned renderer's own
        /// bounds are loose).
        /// </summary>
        bool TryGetBoundsHeight(out float height)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var r in _renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) continue;
                    var b = skinned.sharedMesh.bounds;
                    var m = skinned.transform.localToWorldMatrix;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y,
                                                                    (i & 4) == 0 ? b.min.z : b.max.z));
                        min = Mathf.Min(min, corner.y);
                        max = Mathf.Max(max, corner.y);
                    }
                }
                else if (r is MeshRenderer) // not the effects' particle, trail or line renderers
                {
                    var b = r.bounds;
                    min = Mathf.Min(min, b.min.y);
                    max = Mathf.Max(max, b.max.y);
                }
            }
            height = max - min;
            return max >= min;
        }

        /// <summary>The focus name's world anchor with G3's view lift (10 cm along the camera's up axis).</summary>
        public bool TryGetAnchor(Camera camera, out Vector3 anchor)
        {
            if (!TryGetAnchor(out var point))
            {
                anchor = default;
                return false;
            }
            anchor = camera != null ? ToUnity(FocusNamePlacement.Lift(ToNumerics(point), ToNumerics(camera.transform.up))) : point + Vector3.up * FocusNamePlacement.ViewLift;
            return true;
        }

        /// <summary>
        /// Runner probe (fix-79): the anchor's height over the target's pivot along its up axis before the view lift, metres;
        /// NaN without a focus.
        /// </summary>
        public float AnchorLift => _target != null && TryGetAnchor(out var p) ? Vector3.Dot(p - _target.transform.position, _target.transform.up) : float.NaN;

        static System.Numerics.Vector3 ToNumerics(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);

        static Vector3 ToUnity(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);

        GameObject FindTarget()
        {
            if (!Actor.IsEmpty) return _npcs != null && _npcs.TryGet(Actor, out var view) && view != null ? view.gameObject : null;
            if (WorldObject != null) return Objects != null && Objects.TryGet(WorldObject, out var obj) ? obj.gameObject : null;
            if (WorldItem == null || _items == null) return null;
            foreach (var kv in _items.Shown)
                if (kv.Key == WorldItem) return kv.Value;
            return null;
        }
    }
}
