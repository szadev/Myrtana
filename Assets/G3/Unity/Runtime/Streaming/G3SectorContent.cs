using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The static objects (meshes, usable furniture, lit torches and camp fires) of one story-switchable sector inside a
    /// cell scene (built by G3WorldBatch): <see cref="content"/> is active only while <see cref="G3StorySectors"/> says
    /// the sector is on. This holder itself stays active so it keeps listening. A story switch (liberation: several sectors
    /// of every loaded cell at once) goes through <see cref="G3RendererChanges"/> (paced, GPU Resident Drawer safe,
    /// Docs/performance.md); the state at load is set at once.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Sector Content")]
    public sealed class G3SectorContent : MonoBehaviour
    {
        [Tooltip("G3 world sector name (e.g. G3_Myrtana_01_Faring_NPC_02)")] public string sector;
        [Tooltip("The sector is on at the start of a new game (.wrldatasc)")] public bool enabledAtStart;
        [Tooltip("Shown while the sector is on")] public GameObject content;

        static readonly System.Collections.Generic.List<MeshRenderer> Buffer = new System.Collections.Generic.List<MeshRenderer>();

        void Awake()
        {
            G3StorySectors.Changed += OnChanged;
            Sync();
        }

        void OnDestroy() => G3StorySectors.Changed -= OnChanged;

        void OnChanged()
        {
            if (content == null || content.activeSelf == G3StorySectors.IsOn(sector, enabledAtStart)) return;
            G3RendererChanges.Enqueue(content, Sync, "story sectors");
        }

        /// <summary>Brings the content to the sector's current state; returns the mesh renderers switched.</summary>
        int Sync()
        {
            if (this == null || content == null) return 0;
            bool on = G3StorySectors.IsOn(sector, enabledAtStart);
            if (content.activeSelf == on) return 0;
            content.GetComponentsInChildren(true, Buffer);
            int n = Buffer.Count;
            Buffer.Clear();
            content.SetActive(on);
            return n;
        }
    }
}
