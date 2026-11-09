using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The character remaster as runtime data (Docs/remaster-characters.md §5.6, §6): for every renderer of the original
    /// characters it covers (the hero with his start body, the armour bodies he and the NPCs wear), what the remastered
    /// character shows instead: mesh, materials and bone names (skinned parts are rebound to the character's own bones by
    /// name), renderers the remaster adds (v2 hair cards, extra meshes of a part), the remastered talking heads and the
    /// material table for everything else. Written by the editor's <c>CharacterRemasterBuilder</c> (menu G3/Remaster/Build
    /// Runtime Character Set) to <c>Assets/G3Imported/Remaster/Characters/Resources/G3CharacterRemasterSet.asset</c>; read
    /// by <see cref="G3CharacterRemaster"/>.
    /// Everything is by name and Resources path (§6.6): the set references no mesh, material, texture or kit, so loading
    /// it costs nothing; a swap loads its remaster mesh and materials the first time a character on screen needs them, and
    /// switching off releases them. Being in Resources is what ships them in a player.
    /// </summary>
    public sealed class G3CharacterRemasterSet : ScriptableObject
    {
        public const string ResourceName = "G3CharacterRemasterSet";

        /// <summary>Resources folder of the swaps' mesh refs (<see cref="G3CharacterMeshRef"/>), material copies and face kits.</summary>
        public const string SwapFolder = "G3CharacterSwaps";

        /// <summary>A renderer the remaster adds next to a swapped part (hair cards on Slot_Hair, a part's second mesh).</summary>
        [Serializable]
        public sealed class Extra
        {
            public string name;
            [Tooltip("Parent transform by name (a bone or slot); empty = the swapped renderer's parent")] public string parent;
            public bool skinned;
            [Tooltip("Resources path of the mesh's G3CharacterMeshRef")] public string mesh;
            [Tooltip("Resources paths of the materials")] public string[] materials = Array.Empty<string>();
            public string[] bones = Array.Empty<string>();
            public string rootBone;
            public Vector3 position;
            public Quaternion rotation = Quaternion.identity;
            public Vector3 scale = Vector3.one;
            [Tooltip("Part of the head (first person: shadow only, like G3HeroView.headRenderers)")] public bool head;
        }

        /// <summary>One original renderer state and its remastered replacement.</summary>
        [Serializable]
        public sealed class Swap
        {
            [Tooltip("Part name for logs (the renderer's object name in the source rig)")] public string label;
            public bool skinned;
            [Tooltip("Name of the original mesh")] public string original;
            [Tooltip("Vertex count of the original mesh (mesh names are not unique over all imported files)")] public int originalVertices;
            [Tooltip("Names of the original materials this entry was built for (exact match preferred; another set with the same mesh still swaps)")]
            public string[] originalMaterials = Array.Empty<string>();
            [Tooltip("The remaster has no counterpart: the renderer is switched off while on")] public bool hide;
            [Tooltip("Resources path of the remaster mesh's G3CharacterMeshRef; empty = the original mesh stays")] public string mesh;
            [Tooltip("Resources paths of the remaster materials")] public string[] materials = Array.Empty<string>();
            public string[] bones = Array.Empty<string>();
            public string rootBone;
            public List<Extra> extras = new List<Extra>();
        }

        public List<Swap> swaps = new List<Swap>();

        /// <summary>Resources folder of the table's remaster materials (each loaded on first use, with its textures).</summary>
        public const string MaterialFolder = "G3CharacterMaterials";

        /// <summary>One original character material (by name) and its remaster (R1 / AI maps on the G3/Remaster shaders,
        /// a Resources path under <see cref="MaterialFolder"/>), for renderers without a mesh swap: NPCs, orcs, creatures,
        /// items in hand (§6.3). Names, not references: loading the set must not load every texture set.</summary>
        [Serializable]
        public struct MaterialPair
        {
            public string original;
            public string remaster;
        }

        /// <summary>Material table over every NPC prefab and item visual (CharacterRemasterBuilder.BuildMaterials).</summary>
        public List<MaterialPair> materials = new List<MaterialPair>();

        /// <summary>A remastered talking head: the static head's mesh name and the kit's Resources path.</summary>
        [Serializable]
        public struct FaceKitRef
        {
            public string head;
            public string kit;
        }

        /// <summary>Talking heads built from the remastered facial parts (loaded when a remastered head starts to talk).</summary>
        public List<FaceKitRef> faceKits = new List<FaceKitRef>();

        [Tooltip("When and from what the set was built")] public string built;
    }
}
