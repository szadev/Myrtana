using System.Collections.Generic;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The worn helmet of a character (fix-220, <see cref="HelmetVisualRules"/>): the helmet's static mesh from the
    /// <see cref="ItemVisualCatalog"/> on the rig's <c>Slot_Helmet</c> node, modelled in the slot frame (identity attach, like
    /// hair, beard and <see cref="CarriedWeaponsView"/>), so it follows the head bone of whatever head the rig wears (the G3
    /// head or the remastered MetaHuman head, both bound to the same Hero_Head_* bones). A helmet that covers the skull hides
    /// the renderers under <c>Slot_Hair</c> (the remaster's hair shell) with <see cref="Renderer.forceRenderingOff"/>, a flag
    /// neither the character remaster (enabled, materials) nor the first-person view (shadow mode) touches; the hair slot is
    /// re-checked each frame while hidden because the remaster creates and destroys its hair renderers when it is toggled or
    /// suspended around an armour change. First person needs nothing here: a static mesh under Slot_Helmet is a head part
    /// for <c>G3HeroView</c> (FirstPersonBodyRules, fix-208), which re-classifies when the renderer set changes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HelmetView : MonoBehaviour
    {
        GameObject _prop;
        MeshFilter _filter;
        MeshRenderer _renderer;
        Transform _slot, _hairSlot;
        string _key;
        bool _hideHair;
        readonly List<Renderer> _hidden = new List<Renderer>();
        readonly List<Renderer> _scan = new List<Renderer>();

        /// <summary>The helmet mesh shown now; null = none.</summary>
        public string Shown => _prop != null && _prop.activeSelf ? _key : null;

        public bool HairHidden => _hideHair;

        /// <summary>"mesh@slot [hair hidden]" or "none", for tests and the editor checks.</summary>
        public string Describe() =>
            Shown == null ? "none" : $"{_key}@{(_slot != null ? _slot.name : "?")}{(_hideHair ? $" hair hidden ({_hidden.Count})" : "")}";

        /// <summary>Shows the helmet mesh <paramref name="key"/> (<see cref="HelmetVisualRules.MeshKey"/>); null or unknown = none.</summary>
        public bool Show(string key, ItemVisualCatalog catalog)
        {
            if (key == null || catalog == null || !catalog.TryGetMesh(key, out var entry) || entry.mesh == null)
            {
                if (key != null) Debug.LogWarning($"[Myrtana] no helmet visual '{key}' (menu Myrtana/Content/Build item visuals).");
                Hide();
                return false;
            }
            _slot = _slot != null ? _slot : FindSlot(transform, HelmetVisualRules.HelmetSlot);
            if (_slot == null)
            {
                Debug.LogWarning($"[Myrtana] {name}: no {HelmetVisualRules.HelmetSlot} node, helmet '{key}' not shown.");
                Hide();
                return false;
            }
            if (_prop == null)
            {
                _prop = new GameObject("Helmet") { layer = gameObject.layer };
                _filter = _prop.AddComponent<MeshFilter>();
                _renderer = _prop.AddComponent<MeshRenderer>();
            }
            if (_prop.transform.parent != _slot)
            {
                _prop.transform.SetParent(_slot, false);
                _prop.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity); // modelled in the slot frame
                _prop.transform.localScale = Vector3.one;
            }
            if (_key != key)
            {
                _filter.sharedMesh = entry.mesh;
                _renderer.sharedMaterials = G3.Unity.G3CharacterRemaster.Map(entry.materials);
                _key = key;
                _prop.name = "Helmet " + key;
            }
            if (!_prop.activeSelf) _prop.SetActive(true);
            SetHairHidden(HelmetVisualRules.HidesHair(entry.mesh.bounds.size.y));
            return true;
        }

        public void Hide()
        {
            if (_prop != null && _prop.activeSelf) _prop.SetActive(false);
            SetHairHidden(false);
        }

        void LateUpdate()
        {
            if (_hideHair) HideHair();
        }

        void OnDestroy()
        {
            SetHairHidden(false);
            if (_prop != null) Destroy(_prop);
        }

        void SetHairHidden(bool hide)
        {
            _hideHair = hide;
            if (hide) { HideHair(); return; }
            foreach (var r in _hidden) if (r != null) r.forceRenderingOff = false;
            _hidden.Clear();
        }

        void HideHair()
        {
            _hairSlot = _hairSlot != null ? _hairSlot : FindSlot(transform, HelmetVisualRules.HairSlot);
            if (_hairSlot == null) return;
            _hairSlot.GetComponentsInChildren(true, _scan);
            for (int i = _hidden.Count - 1; i >= 0; i--) if (_hidden[i] == null) _hidden.RemoveAt(i); // destroyed remaster extras
            foreach (var r in _scan)
            {
                if (r.forceRenderingOff) continue;
                r.forceRenderingOff = true;
                _hidden.Add(r);
            }
        }

        static Transform FindSlot(Transform t, string slot)
        {
            if (HelmetVisualRules.IsSlot(t.name, slot)) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindSlot(t.GetChild(i), slot);
                if (r != null) return r;
            }
            return null;
        }
    }
}
