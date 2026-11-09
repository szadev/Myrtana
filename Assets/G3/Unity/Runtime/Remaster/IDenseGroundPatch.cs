using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// A near-camera ground mesh that can be made dense round a point (remaster-relief's terrain focus patch,
    /// Docs/remaster-world.md 12.6 / 13): the snow asks for one round the hero and displaces its vertices by the footprints
    /// (G3RemasterSnowPrints.hlsl, G3SnowDisplaceWS). The mesh marks its dense vertices with TEXCOORD3.z = local edge length
    /// (m); 0 / absent elsewhere. Registered by its owner in <see cref="G3SnowV2.DenseGround"/>.
    /// </summary>
    public interface IDenseGroundPatch
    {
        /// <summary>Keeps the ground dense round <paramref name="point"/>: edge length <paramref name="spacing"/> m there, growing by
        /// <paramref name="slope"/> m per m of distance; re-centred on a <paramref name="grid"/> m grid. Called every frame.</summary>
        void SetFocus(int id, Vector3 point, float spacing, float slope, float grid);

        /// <summary>Drops the focus.</summary>
        void ClearFocus(int id);
    }
}
