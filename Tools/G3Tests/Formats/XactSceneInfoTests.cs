using System.IO;
using System.Text;
using G3.Formats.Animation;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// The FXA scene-info chunk (16) ends where its fields end, not where its size field says: G3_Head_Hero_Diego_01.xact
    /// understates it (128 for 152 bytes) and failed to load, so Diego was built without a head (fix-15).
    /// </summary>
    [TestFixture]
    public class XactSceneInfoTests
    {
        static void S32(BinaryWriter w, string s) { w.Write(s.Length); w.Write(Encoding.ASCII.GetBytes(s)); }

        static byte[] Actor(int infoSizeDelta)
        {
            var fxa = new MemoryStream();
            var c = new BinaryWriter(fxa);
            c.Write(Encoding.ASCII.GetBytes("FXA ")); c.Write((byte)1); c.Write((byte)1);
            var info = new MemoryStream();
            var i = new BinaryWriter(info);
            i.Write(new byte[] { 2, 0x10, 0xb2, 0x0c, 1, 0, 0, 0 });
            S32(i, "3D Studio Max 7"); S32(i, @"W:\GothicIII_Figuren\Humans\_Heads\Named_Heros\Hero_Head_Special_Diego_11.max"); S32(i, "Mar  1 2005");
            c.Write(16); c.Write((int)info.Length + infoSizeDelta); c.Write(1); c.Write(info.ToArray());
            var node = new MemoryStream();
            var n = new BinaryWriter(node);
            for (int k = 0; k < 3; k++) n.Write(0f);                 // position
            n.Write(0f); n.Write(0f); n.Write(0f); n.Write(1f);      // rotation
            n.Write(0f); n.Write(0f); n.Write(0f); n.Write(1f);      // scale orientation
            n.Write(1f); n.Write(1f); n.Write(1f);                   // scale
            for (int k = 0; k < 3; k++) n.Write(0f);                 // shear
            S32(n, "Hero_ROOT"); S32(n, "");
            c.Write(0); c.Write((int)node.Length); c.Write(3); c.Write(node.ToArray());

            var file = new MemoryStream();
            var w = new BinaryWriter(file);
            w.Write(Encoding.ASCII.GetBytes("GENOMFLE")); w.Write((ushort)1); w.Write(0); // table offset patched below
            w.Write((ushort)54); w.Write(0u); w.Write(0f); w.Write(0L); w.Write(0u);
            for (int k = 0; k < 6; k++) w.Write(0f);
            w.Write(0); w.Write(0);                                  // look-at constraints, LoD actors
            w.Write(0x616E6567u); w.Write((ushort)4); w.Write((int)fxa.Length); w.Write(fxa.ToArray());
            w.Write(0); w.Write((byte)1); w.Write(0);             // material refs, vertex colour LoDs
            int table = (int)file.Position;
            w.Write(0xDEADBEEFu); w.Write((byte)0);
            var bytes = file.ToArray();
            System.BitConverter.GetBytes(table).CopyTo(bytes, 10);
            return bytes;
        }

        [TestCase(0)]
        [TestCase(-24)]
        public void SceneInfoEndsAfterItsFields(int sizeDelta)
        {
            var a = Xact.Read(Actor(sizeDelta), "test.xact");
            Assert.AreEqual(1, a.Actor.Nodes.Count);
            Assert.AreEqual("Hero_ROOT", a.Actor.Nodes[0].Name);
        }
    }
}
