using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Dresses the hero model in the worn armour (formats-items.md §2.1: armour is a body actor): the hero's body
    /// skinned mesh gets the armour body's mesh, materials and bones from <see cref="ItemVisualCatalog"/>, further meshes of
    /// the body actor (King's fur collar) get renderers of their own next to it; without armour the body he wore at the
    /// start comes back. Works on any skinned "_Body_" mesh under the player and needs no
    /// change to the character pipeline. The runtime character remaster (<c>G3CharacterRemaster</c>) is suspended around a change.
    /// </summary>
    public sealed class HeroArmorView : ISessionView
    {
        readonly Transform _hero;
        readonly ItemVisualCatalog _catalog;
        SkinnedMeshRenderer _body;
        Mesh _startMesh;
        Material[] _startMaterials;
        Transform[] _startBones;
        Transform _startRoot;
        Dictionary<string, Transform> _bonesByName;
        readonly List<SkinnedMeshRenderer> _extraParts = new List<SkinnedMeshRenderer>();
        IDisposable _subscription;
        GameSession _session;

        public HeroArmorView(Transform hero, ItemVisualCatalog catalog)
        {
            _hero = hero;
            _catalog = catalog;
        }

        public void Bind(GameSession session)
        {
            _subscription?.Dispose();
            _session = session;
            _subscription = session.Events.Subscribe<EquipmentChanged>(e =>
            {
                if (e.Slot == WornSlot.Armor) Show(e.Item);
            });
            Show(session.Items.Equipment.In(WornSlot.Armor));
        }

        void Show(string armor)
        {
            // the runtime character remaster (Options → Remaster) puts the originals back first and re-applies afterwards,
            // so the start body is the original one and the new armour gets its remastered mesh when there is one
            global::G3.Unity.G3CharacterRemaster.Suspend(_hero.gameObject);
            try { ShowArmor(armor); }
            finally { global::G3.Unity.G3CharacterRemaster.Resume(_hero.gameObject); }
        }

        void ShowArmor(string armor)
        {
            if (!FindBody()) return;
            if (armor != null && _session.Content.Database.TryGet(armor, out ItemDef def) && def.Visual?.Body != null && _catalog != null &&
                _catalog.TryGetBody(ItemVisualCatalog.BodyKey(def.Visual.Body, def.Visual.MaterialSwitch), out var entry) && entry.parts.Count > 0)
            {
                Dress(_body, entry.parts[0]);
                for (int i = 1; i < entry.parts.Count; i++) Dress(ExtraPart(i - 1), entry.parts[i]);
                HideExtraParts(entry.parts.Count - 1);
            }
            else
            {
                if (armor != null) Debug.LogWarning($"[Myrtana] no body visual for armour '{armor}' (menu Myrtana/Content/Build item visuals).");
                _body.sharedMesh = _startMesh;
                _body.sharedMaterials = _startMaterials;
                _body.bones = _startBones;
                _body.rootBone = _startRoot;
                HideExtraParts(0);
            }
        }

        void Dress(SkinnedMeshRenderer renderer, ItemVisualCatalog.BodyPart part)
        {
            renderer.sharedMesh = part.mesh;
            renderer.sharedMaterials = part.materials;
            renderer.bones = part.bones.Select(Bone).ToArray();
            renderer.rootBone = Bone(part.rootBone) ?? _startRoot;
            renderer.enabled = true;
        }

        /// <summary>The renderer of the body's (index+2)-th mesh: a sibling of the body renderer with its settings.</summary>
        SkinnedMeshRenderer ExtraPart(int index)
        {
            while (_extraParts.Count <= index)
            {
                var go = new GameObject($"{_body.name} Armor Part {_extraParts.Count + 2}") { layer = _body.gameObject.layer };
                go.transform.SetParent(_body.transform.parent, false);
                go.transform.SetLocalPositionAndRotation(_body.transform.localPosition, _body.transform.localRotation);
                go.transform.localScale = _body.transform.localScale;
                var r = go.AddComponent<SkinnedMeshRenderer>();
                r.updateWhenOffscreen = _body.updateWhenOffscreen;
                r.localBounds = _body.localBounds;
                r.shadowCastingMode = _body.shadowCastingMode;
                r.receiveShadows = _body.receiveShadows;
                _extraParts.Add(r);
            }
            return _extraParts[index];
        }

        void HideExtraParts(int used)
        {
            for (int i = used; i < _extraParts.Count; i++)
            {
                _extraParts[i].enabled = false;
                _extraParts[i].sharedMesh = null;
            }
        }

        /// <summary>The hero's body renderer, remembered with what it showed at the start.</summary>
        bool FindBody()
        {
            if (_body != null) return true;
            _body = _hero.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                         .FirstOrDefault(r => r.sharedMesh != null && r.sharedMesh.name.IndexOf("_Body_", StringComparison.OrdinalIgnoreCase) >= 0);
            if (_body == null) return false;
            _startMesh = _body.sharedMesh;
            _startMaterials = _body.sharedMaterials;
            _startBones = _body.bones;
            _startRoot = _body.rootBone;
            _bonesByName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            var skeleton = _startRoot != null ? _startRoot.root : _hero;
            foreach (var t in skeleton.GetComponentsInChildren<Transform>(true))
                if (!_bonesByName.ContainsKey(t.name)) _bonesByName[t.name] = t;
            return true;
        }

        /// <summary>A bone of the hero rig by name; a bone the hero lacks falls back to the root bone.</summary>
        Transform Bone(string name) => name != null && _bonesByName.TryGetValue(name, out var t) ? t : _startRoot;
    }
}
