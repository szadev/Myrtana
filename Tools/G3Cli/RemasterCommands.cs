using System;
using System.IO;
using G3.Formats.Remaster;

static partial class Program
{
    /// <summary>
    /// Remaster tooling without Unity (Docs/remaster-pipeline.md):
    ///   remaster check &lt;original.glb&gt; &lt;remaster.glb&gt; [report.txt]  contract check, exit 0 = pass, 2 = fail
    ///   remaster info &lt;file.glb&gt;                                     nodes, meshes, materials
    ///   remaster testrig &lt;out.glb&gt;                                    generated G3-style test character (no game data)
    /// </summary>
    static int RemasterCommand(string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "";
        switch (sub)
        {
            case "check" when args.Length >= 4:
            {
                var report = RigCheck.Compare(Glb.Read(args[2]), Glb.Read(args[3]));
                string text = report.ToText();
                Console.WriteLine(text);
                if (args.Length > 4) File.WriteAllText(args[4], text);
                return report.Passed ? 0 : 2;
            }
            case "info" when args.Length >= 3:
                Console.WriteLine(RigCheck.Describe(Glb.Read(args[2])));
                return 0;
            case "testrig" when args.Length >= 3:
                Glb.Write(TestRig.Build(), args[2]);
                Console.WriteLine(args[2]);
                return 0;
        }
        Console.WriteLine("remaster check <original.glb> <remaster.glb> [report.txt] | info <file.glb> | testrig <out.glb>");
        return 1;
    }
}
