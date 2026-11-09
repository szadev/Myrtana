namespace G3.Formats.Templates
{
    /// <summary>
    /// The collision material of a template: the eEShapeMaterial of the first eCCollisionShape in its
    /// eCCollisionShape_PS (post data: u32 n; n × eCCollisionShape object + shape data). G3 uses it for impact sounds
    /// (AssessHit's parade clash, OnItemTouchesLevel). `G3Cli audio itemmat` lists it per item.
    /// </summary>
    public static class TemplateShapes
    {
        static readonly string[] Names =
        {
            "None", "Wood", "Metal", "Water", "Stone", "Earth", "Ice", "Leather", "Clay", "Glass", "Flesh", "Snow", "Debris",
            "Foliage", "Magic", "Grass", "SpringAndDamper1", "SpringAndDamper2", "SpringAndDamper3", "Damage", "Sand", "Movement",
        };

        /// <summary>eEShapeMaterial value of the first collision shape of any header, or -1 when the template has none.</summary>
        public static int FirstMaterial(TemplateFile file)
        {
            if (file == null) return -1;
            foreach (var h in file.Headers)
            {
                int i = h.PropertySets.FindIndex(p => p.ClassName == "eCCollisionShape_PS");
                if (i < 0 || i >= h.PostDataStart.Count || h.Source == null) continue;
                try
                {
                    var g = h.Source;
                    g.Position = h.PostDataStart[i];
                    int n = g.R.ReadInt32();
                    var o = n > 0 ? g.ReadObject() : null;
                    if (o != null) return o.Get("Material")?.AsEnum() ?? 0;
                }
                catch (System.IO.IOException) { }
                catch (System.ArgumentException) { }
            }
            return -1;
        }

        /// <summary>The engine's name of an eEShapeMaterial value ("Metal", "Wood" …), null when unknown.</summary>
        public static string MaterialName(int material) => material >= 0 && material < Names.Length ? Names[material] : null;
    }
}
