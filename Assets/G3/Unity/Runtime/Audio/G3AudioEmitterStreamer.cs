using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Instantiates the baked world sound emitters (<see cref="G3AudioWorldAsset.emitters"/>) of every world cell when
    /// the <see cref="G3WorldStreamer"/> loads it, into the cell's scene, so they disappear with the cell. Without a
    /// streamer (single region scene) all emitters are spawned under this object.
    /// </summary>
    [RequireComponent(typeof(G3AudioContext))]
    [AddComponentMenu("G3/Audio/G3 Audio Emitter Streamer")]
    public sealed class G3AudioEmitterStreamer : MonoBehaviour
    {
        public G3WorldStreamer streamer;
        [Range(0, 1)] public float volume = 1f;

        [Header("Current (read only)")]
        [SerializeField] int cellsPopulated;
        [SerializeField] int emittersSpawned;

        G3AudioContext _context;
        readonly HashSet<int> _populated = new HashSet<int>();

        void Start()
        {
            _context = GetComponent<G3AudioContext>();
            if (_context.world == null || _context.library == null) { enabled = false; return; }
            if (streamer == null) streamer = FindAnyObjectByType<G3WorldStreamer>();
            if (streamer == null || streamer.manifest == null)
            {
                Spawn(-1, transform, all: true);
                return;
            }
            streamer.CellLoaded += OnCellLoaded;
            streamer.CellUnloaded += OnCellUnloaded;
            // Cells opened before Play (or loaded before we subscribed) get no event.
            foreach (var c in streamer.manifest.cells)
            {
                if (!c.HasScene) continue;
                var scene = SceneManager.GetSceneByPath(c.scenePath);
                if (scene.isLoaded) OnCellLoaded(c, scene);
            }
        }

        void OnDestroy()
        {
            if (streamer == null) return;
            streamer.CellLoaded -= OnCellLoaded;
            streamer.CellUnloaded -= OnCellUnloaded;
        }

        void OnCellLoaded(G3WorldManifest.Cell cell, Scene scene)
        {
            int index = streamer.manifest.Index(cell.ix, cell.iz);
            if (!_populated.Add(index)) return;
            var root = new GameObject($"G3 Sound Emitters {cell.ix}_{cell.iz}");
            if (scene.IsValid() && scene.isLoaded) SceneManager.MoveGameObjectToScene(root, scene);
            Spawn(index, root.transform, all: false);
            cellsPopulated = _populated.Count;
        }

        void OnCellUnloaded(G3WorldManifest.Cell cell)
        {
            _populated.Remove(streamer.manifest.Index(cell.ix, cell.iz));
            cellsPopulated = _populated.Count;
        }

        void Spawn(int cell, Transform parent, bool all)
        {
            var list = all ? _context.world.emitters : (IReadOnlyList<G3.Audio.SoundEmitterData>)_context.world.EmittersInCell(cell);
            foreach (var e in list)
            {
                var go = new GameObject(string.IsNullOrEmpty(e.name) ? e.clip : e.name);
                go.transform.SetParent(parent, false);
                go.AddComponent<G3SoundEmitter>().Init(e, _context.library, volume);
                emittersSpawned++;
            }
        }
    }
}
