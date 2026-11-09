using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// A remaster mesh of the runtime character set, reachable by a Resources path (Docs/remaster-characters.md §6.6):
    /// the set stores the path, not the mesh, so loading the set loads no mesh and no texture; a swap loads its mesh the
    /// first time a character on screen needs it. Written by <c>CharacterRemasterBuilder.Build()</c> into
    /// <c>Resources/G3CharacterSwaps/</c> (git-ignored); the mesh itself stays the importer's asset.
    /// </summary>
    public sealed class G3CharacterMeshRef : ScriptableObject
    {
        public Mesh mesh;
    }
}
