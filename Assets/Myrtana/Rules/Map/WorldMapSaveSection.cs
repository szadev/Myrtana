using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Save section "world.map": the map the map page showed last. G3 saves nothing here (Docs/map-teleport.md §1.6);
    /// keeping it makes the page open on the same map after a load. Owned maps and teleport stones are inventory stacks.
    /// </summary>
    public sealed class WorldMapSaveSection : ISaveParticipant
    {
        readonly WorldMaps _maps;

        public WorldMapSaveSection(WorldMaps maps) => _maps = maps;

        public string Section => "world.map";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteString(_maps.Shown);

        public void Read(ISaveReader r, int version) => _maps.Restore(r.ReadString());
    }
}
