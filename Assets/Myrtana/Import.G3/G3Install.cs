using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Story;

namespace Myrtana.Import.G3
{
    /// <summary>Read access to the story data of a Gothic 3 installation (the "Data" folder and the UpdatePack overlay).</summary>
    public sealed class G3Install
    {
        public G3Install(string dataDirectory) => DataDirectory = Path.GetFullPath(dataDirectory);

        public string DataDirectory { get; }
        string GameRoot => Path.GetDirectoryName(DataDirectory.TrimEnd('\\', '/'));

        public G3FileSystem Open(string volume) => new G3FileSystem(DataDirectory, volume);

        /// <summary>stringtable.ini of the Strings volume, overlaid with the UpdatePack's stringtableMod.ini if present.</summary>
        public StringTable LoadStrings()
        {
            StringTable table;
            using (var fs = Open("Strings")) table = StringTable.ReadIni(fs.Get("Strings/stringtable.ini").Read());
            var mod = Path.Combine(GameRoot, "UpdatePack", "Z_UpdatePack_SupportMod", "stringtableMod.ini");
            if (File.Exists(mod)) table.Merge(StringTable.ReadIni(File.ReadAllBytes(mod)));
            return table;
        }

        public List<Info> LoadInfos(ImportReport report) => LoadAll("Infos", ".info", InfoFile.ReadIni, report);

        public List<Quest> LoadQuests(ImportReport report) => LoadAll("Quests", ".quest", QuestFile.ReadIni, report);

        List<T> LoadAll<T>(string volume, string extension, Func<byte[], T> read, ImportReport report)
        {
            var list = new List<T>();
            using (var fs = Open(volume))
            {
                foreach (var e in fs.Files.Where(f => f.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    try { list.Add(read(e.Read())); }
                    catch (Exception ex) when (ex is InvalidDataException || ex is FormatException)
                    {
                        report.Count($"unreadable {extension} file", e.Path + ": " + ex.Message);
                    }
                }
            }
            return list;
        }
    }
}
