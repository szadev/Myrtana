using System.Collections.Generic;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The weapons a fighter carries on its body while they are not in the hand (<see cref="WeaponHold.WornItems"/>: melee and
    /// ranged weapons on their carry slots, the shield on the back or in the left hand), as static meshes on the skeleton's
    /// Slot_* nodes, modelled in the slot frame (identity attach, like <see cref="NpcHandProps"/>). Meshes and materials are
    /// the shared ones of the <see cref="ItemVisualCatalog"/>; the props are created once per (pooled) view and reused, and
    /// nothing runs per frame — the owner calls <see cref="Show"/> when the loadout or the drawn weapon changes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CarriedWeaponsView : MonoBehaviour
    {
        sealed class Prop
        {
            public GameObject go;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public Transform slot;
            public string key;
        }

        readonly List<Prop> _props = new List<Prop>();
        readonly List<WeaponHold.Worn> _worn = new List<WeaponHold.Worn>();
        readonly Dictionary<string, Transform> _slots = new Dictionary<string, Transform>();

        /// <summary>What is shown now ("item@slot …"), for tests and the editor checks.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            foreach (var p in _props)
                if (p.go != null && p.go.activeSelf) parts.Add($"{p.key}@{(p.slot != null ? p.slot.name : "?")}");
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Shows <paramref name="loadout"/>'s weapons except <paramref name="drawn"/> (the item in the hand, null = sheathed)
        /// and those <paramref name="lying"/> on the ground (fix-149, <see cref="WeaponHold.WornItems(CombatLoadout, ItemDef, System.Func{ItemDef, bool}, List{WeaponHold.Worn})"/>).
        /// </summary>
        public void Show(CombatLoadout loadout, ItemDef drawn, System.Func<ItemDef, bool> lying, ItemVisualCatalog catalog)
        {
            WeaponHold.WornItems(loadout, drawn, lying, _worn);
            ShowWorn(catalog);
        }

        void ShowWorn(ItemVisualCatalog catalog)
        {
            int used = 0;
            foreach (var w in _worn)
            {
                string key = CombatFighter.VisualKey(w.Item);
                var slot = Slot(w.Slot);
                if (key == null || slot == null || catalog == null || !catalog.TryGetMesh(key, out var entry)) continue;
                var p = used < _props.Count ? _props[used] : NewProp();
                used++;
                if (p.slot != slot)
                {
                    p.go.transform.SetParent(slot, false);
                    p.go.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity); // modelled in the slot frame
                    p.go.transform.localScale = Vector3.one;
                    p.slot = slot;
                }
                if (p.key != key)
                {
                    p.filter.sharedMesh = entry.mesh;
                    p.renderer.sharedMaterials = G3.Unity.G3CharacterRemaster.Map(entry.materials);
                    p.key = key;
                    p.go.name = "Carried " + key;
                }
                if (!p.go.activeSelf) p.go.SetActive(true);
                G3.Unity.G3ItemEffect.Sync(p.go, w.Item?.Id); // a flame sword burns on the back too (effects-2)
            }
            for (int i = used; i < _props.Count; i++)
                if (_props[i].go != null && _props[i].go.activeSelf) _props[i].go.SetActive(false);
        }

        public void Hide()
        {
            foreach (var p in _props)
                if (p.go != null && p.go.activeSelf) p.go.SetActive(false);
        }

        Prop NewProp()
        {
            var go = new GameObject("Carried");
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);
            var p = new Prop { go = go, filter = go.AddComponent<MeshFilter>(), renderer = go.AddComponent<MeshRenderer>() };
            _props.Add(p);
            return p;
        }

        Transform Slot(string name)
        {
            if (name == null) return null;
            if (_slots.TryGetValue(name, out var t) && t != null) return t;
            t = FindDeep(transform, name);
            _slots[name] = t;
            return t;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
