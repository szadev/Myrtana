using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Gothic 3's distant low-poly world (sector G3_World_Lowpoly_01_Levelmesh_01), cut into the streaming grid.
    /// A piece is drawn only while the full-detail cells covering its square are not loaded, so the horizon is never
    /// empty and the near world never fights with the low-poly copy. Visibility uses Renderer.forceRenderingOff
    /// (not serialized), so toggling never dirties the master scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class G3LowpolyBackdrop : MonoBehaviour
    {
        [Serializable]
        public sealed class Piece
        {
            public int cell;              // manifest cell index
            public Renderer[] renderers;
            [NonSerialized] public bool hidden;
        }

        public List<Piece> pieces = new List<Piece>();

        /// <summary>Show/hide all pieces: hide(cellIndex) == true hides the piece of that cell.</summary>
        public void Refresh(Func<int, bool> hide)
        {
            foreach (var p in pieces)
            {
                bool h = hide(p.cell);
                p.hidden = h;
                foreach (var r in p.renderers)
                    if (r != null) r.forceRenderingOff = h;
            }
        }

        public void ShowAll() => Refresh(_ => false);
    }
}
