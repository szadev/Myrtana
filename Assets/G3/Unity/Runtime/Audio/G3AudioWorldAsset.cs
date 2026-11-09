using System.Collections.Generic;
using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Audio data baked from the G3 world (G3.Unity.Editor.G3AudioWorldBaker): music/weather zones, placed sound
    /// emitters (bucketed by world-manifest cell so they stream with the cells) and the ambient-environment grid of the
    /// SpeedTrees. Metres, Unity axes.
    /// </summary>
    public sealed class G3AudioWorldAsset : ScriptableObject
    {
        public List<MusicZoneData> zones = new List<MusicZoneData>();
        public List<SoundEmitterData> emitters = new List<SoundEmitterData>();
        public AmbientEnvironmentGrid ambientGrid = new AmbientEnvironmentGrid();

        Dictionary<int, List<SoundEmitterData>> _byCell;
        static readonly List<SoundEmitterData> None = new List<SoundEmitterData>();

        void OnEnable() => ResetCaches();

        public void ResetCaches() => _byCell = null;

        public IReadOnlyList<SoundEmitterData> EmittersInCell(int cell)
        {
            if (_byCell == null)
            {
                _byCell = new Dictionary<int, List<SoundEmitterData>>();
                foreach (var e in emitters)
                {
                    if (!_byCell.TryGetValue(e.cell, out var list)) _byCell[e.cell] = list = new List<SoundEmitterData>();
                    list.Add(e);
                }
            }
            return _byCell.TryGetValue(cell, out var l) ? l : None;
        }
    }
}
