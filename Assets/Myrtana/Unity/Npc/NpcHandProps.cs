using G3.Unity.Characters;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Shows the items an NPC holds (<see cref="G3NpcAnimator.LeftItem"/> / <see cref="G3NpcAnimator.RightItem"/>: smith hammer,
    /// pickaxe, broom, cup …) as static meshes on the body actor's hand slots, like G3's gCInventorySlot::GetBone:
    /// left hand → <c>Slot_LeftHand_Weapon</c>, right hand → <c>Slot_RightHand_Weapon</c>, modelled in the slot frame
    /// (Docs/formats-animation.md §8.7). Meshes come from the <see cref="ItemVisualCatalog"/>, keyed by the template of a hand
    /// prop, else by the mesh of the content item of that id (fix-153: the It_Booze an info has an orc drink).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NpcHandProps : MonoBehaviour
    {
        sealed class Hand
        {
            public Transform slot;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public string shown;
        }

        G3NpcAnimator _animator;
        ItemVisualCatalog _catalog;
        System.Func<string, string> _itemMesh;
        readonly Hand _left = new Hand(), _right = new Hand();

        /// <param name="itemMesh">Mesh key of a content item (its <c>Visual.Mesh</c>), null when unknown; may be null.</param>
        public void Init(G3NpcAnimator animator, ItemVisualCatalog catalog, System.Func<string, string> itemMesh = null)
        {
            _animator = animator;
            _catalog = catalog;
            _itemMesh = itemMesh;
            _left.slot = FindSlot(transform, "Slot_LeftHand_Weapon");
            _right.slot = FindSlot(transform, "Slot_RightHand_Weapon");
            if (isActiveAndEnabled) Subscribe();
        }

        void OnEnable() => Subscribe();

        void OnDisable()
        {
            if (_animator != null) _animator.HandsChanged -= Refresh;
        }

        void Subscribe()
        {
            if (_animator == null) return;
            _animator.HandsChanged -= Refresh;
            _animator.HandsChanged += Refresh;
            Refresh();
        }

        /// <summary>The last item put into a hand and whether it got a mesh ("It_Booze:shown"; dev scenarios, fix-153).</summary>
        public string LastAsked { get; private set; }

        void Refresh()
        {
            Show(_left, _animator.LeftItem, "Prop L");
            Show(_right, _animator.RightItem, "Prop R");
        }

        void Show(Hand hand, string template, string label)
        {
            if (hand.shown == template) return;
            hand.shown = template;
            ItemVisualCatalog.MeshEntry entry = null;
            bool visible = template != null && hand.slot != null && _catalog != null &&
                           (_catalog.TryGetMesh(template, out entry) ||
                            _itemMesh?.Invoke(template) is string mesh && _catalog.TryGetMesh(mesh, out entry));
            if (template != null) LastAsked = template + (visible ? ":shown" : ":nomesh");
            if (!visible)
            {
                if (hand.renderer != null)
                {
                    hand.renderer.enabled = false;
                    G3.Unity.G3ItemEffect.Sync(hand.renderer.gameObject, null);
                }
                return;
            }
            if (hand.renderer == null)
            {
                var go = new GameObject(label);
                go.transform.SetParent(hand.slot, false); // identity: the item mesh is modelled in the slot frame
                hand.filter = go.AddComponent<MeshFilter>();
                hand.renderer = go.AddComponent<MeshRenderer>();
            }
            hand.filter.sharedMesh = entry.mesh;
            hand.renderer.sharedMaterials = G3.Unity.G3CharacterRemaster.Map(entry.materials);
            hand.renderer.enabled = true;
            G3.Unity.G3ItemEffect.Sync(hand.renderer.gameObject, template); // magic weapons' effects (effects-2)
        }

        static Transform FindSlot(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var found = FindSlot(t.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
