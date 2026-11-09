using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// An item the hero holds during a use (G3 <c>_AI_HoldItems</c> / <c>_AI_HoldInventoryItems</c>): the pickaxe at an ore
    /// vein, the shovel at a treasure spot, the apple or potion he eats or drinks. A static mesh on the hero's
    /// <c>Slot_LeftHand_Weapon</c> / <c>Slot_RightHand_Weapon</c>, modelled in the slot frame like <see cref="NpcHandProps"/>;
    /// the mesh is the item template's hand prop or the item's own mesh (<see cref="ItemVisualCatalog"/>).
    /// </summary>
    public sealed class HeroHandProp
    {
        static ItemVisualCatalog _catalog;
        readonly Transform _hero;
        GameObject _shown;

        public HeroHandProp(Transform hero) => _hero = hero;

        /// <summary>Puts <paramref name="item"/> into a hand (replacing what this prop showed); false when it has no mesh.</summary>
        public bool Show(GameSession session, string item, bool leftHand)
        {
            Hide();
            if (_hero == null || string.IsNullOrEmpty(item)) return false;
            if (_catalog == null) _catalog = ItemVisualCatalog.Load();
            if (_catalog == null) return false;
            var slot = Find(_hero, leftHand ? "Slot_LeftHand_Weapon" : "Slot_RightHand_Weapon");
            if (slot == null) return false;
            if (!_catalog.TryGetMesh(item, out var entry))
            {
                string mesh = session != null && session.Content.Database.TryGet(item, out ItemDef def) ? def.Visual?.Mesh : null;
                if (mesh == null || !_catalog.TryGetMesh(mesh, out entry)) return false;
            }
            _shown = new GameObject("Hero use prop");
            _shown.transform.SetParent(slot, false); // identity: the item mesh is modelled in the slot frame
            _shown.AddComponent<MeshFilter>().sharedMesh = entry.mesh;
            _shown.AddComponent<MeshRenderer>().sharedMaterials = G3.Unity.G3CharacterRemaster.Map(entry.materials);
            return true;
        }

        public void Hide()
        {
            if (_shown != null) Object.Destroy(_shown);
            _shown = null;
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var found = Find(t.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
