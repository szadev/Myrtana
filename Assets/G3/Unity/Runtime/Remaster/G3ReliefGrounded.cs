using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// A static thing that lies on G3's collision ground (a world item, a dropped weapon) shown on the drawn relief instead
    /// (Docs/remaster-world.md section 14): its transform is its collision pose plus <see cref="G3TerrainRelief.GroundDelta"/>
    /// at that spot, re-read whenever the ground under things changes (<see cref="G3TerrainRelief.GroundVersion"/>). Only a
    /// view moves: whatever the game keeps (item positions, saves) stays on the collision ground.
    /// </summary>
    public sealed class G3ReliefGrounded : MonoBehaviour
    {
        Vector3 _base, _wrote;
        int _version = -1;
        float _applied;

        /// <summary>Puts <paramref name="go"/> (now at its collision pose) on the drawn ground from now on.</summary>
        public static G3ReliefGrounded Place(GameObject go)
        {
            if (go == null) return null;
            if (!go.TryGetComponent<G3ReliefGrounded>(out var g)) g = go.AddComponent<G3ReliefGrounded>();
            g.Rebase();
            return g;
        }

        /// <summary>Takes the current position as the collision pose (call after moving the thing yourself).</summary>
        public void Rebase()
        {
            _base = transform.position; _wrote = _base; _applied = 0; _version = -1;
            Apply();
        }

        void LateUpdate()
        {
            if (_version != G3TerrainRelief.GroundVersion) Apply();
        }

        void Apply()
        {
            // someone else moved it since our last write: that is its new collision pose
            if ((transform.position - _wrote).sqrMagnitude > 1e-6f) _base = transform.position;
            _version = G3TerrainRelief.GroundVersion;
            _applied = G3TerrainRelief.GroundDelta(_base);
            _wrote = _base + Vector3.up * _applied;
            transform.position = _wrote;
        }
    }
}
