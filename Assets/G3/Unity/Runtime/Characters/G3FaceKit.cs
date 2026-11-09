using System;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// The talking version of one G3 head (Docs/dialog-presentation.md §4): the head mesh of its
    /// <c>FacialAnimFilePath</c> actor (<c>…_Animated_01.FXA</c>), skinned to the skeleton's head bones plus the face bones
    /// (jaw, lips, brows), and the face pose of every viseme sampled from <c>&lt;stem&gt;_LipSync.xmot</c> (frame i = viseme i).
    /// Built by the editor importer into <c>Resources/G3Faces/&lt;static head stem&gt;</c>; worn at runtime by
    /// <see cref="G3TalkingFace"/> in place of the static head while a character speaks.
    /// </summary>
    public sealed class G3FaceKit : ScriptableObject
    {
        public const string ResourceFolder = "G3Faces";

        /// <summary>A face bone the skeleton lacks, created under its parent bone in the bind pose.</summary>
        [Serializable]
        public struct Node
        {
            public string name, parent;
            public Vector3 position;
            public Quaternion rotation;
        }

        /// <summary>Local pose of one face bone for every viseme (index = viseme).</summary>
        [Serializable]
        public sealed class Track
        {
            public string bone;
            public Vector3[] positions;
            public Quaternion[] rotations;
        }

        [Tooltip("Static head the kit replaces (renderer object name in the character prefab)")] public string head;
        [Tooltip("The facial actor the mesh comes from")] public string facialActor;
        public Mesh mesh;
        [Tooltip("Materials of the facial actor; the worn head's own materials are preferred when the submeshes match")]
        public Material[] materials;
        [Tooltip("Bone names in the order of the mesh's bone weights / bind poses")] public string[] bones;
        public string rootBone;
        public Node[] nodes = Array.Empty<Node>();
        public Track[] tracks = Array.Empty<Track>();
        public int visemeCount;
        [Tooltip("Viseme with the widest open jaw (shown for loud sound when a line has no viseme track)")] public int openViseme = 1;

        [Tooltip("Blend shape face (MetaHuman hero head, Docs/remaster-metahuman.md §5.2): visemes as ARKit shape weights on " +
                 "the worn head itself; used instead of mesh/nodes/tracks when it has shapes")]
        public VisemeMorphTable morphs = new VisemeMorphTable();

        /// <summary>The kit drives blend shapes of the worn head instead of swapping in a bone-driven facial mesh.</summary>
        public bool IsMorph => morphs != null && morphs.Shapes != null && morphs.Shapes.Length > 0 && morphs.VisemeCount > 0;

        public static G3FaceKit Load(string headStem) =>
            string.IsNullOrEmpty(headStem) ? null : Resources.Load<G3FaceKit>(ResourceFolder + "/" + headStem);
    }
}
