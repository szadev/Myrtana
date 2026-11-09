using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using G3.Formats.Animation;
using G3.Formats.Image;
using G3.Formats.Material;
using G3.Formats.Mesh;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The offline Source exporter (Docs/remaster-pipeline.md §12) on generated actors, no game data.</summary>
    public class SourceExportTests
    {
        /// <summary>In-memory game files.</summary>
        sealed class FakeData : ISourceData
        {
            public readonly Dictionary<string, EmfxActor> Actors = new Dictionary<string, EmfxActor>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, MeshResource> Meshes = new Dictionary<string, MeshResource>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, MaterialResource> Shaders = new Dictionary<string, MaterialResource>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public EmfxActor Actor(string name) =>
                Actors.TryGetValue(Path.GetFileNameWithoutExtension(name), out var a) ? a : throw new FileNotFoundException(name);
            public MeshResource Mesh(string file) => Meshes.TryGetValue(Path.GetFileNameWithoutExtension(file), out var m) ? m : null;
            public MaterialResource Shader(string stem) => Shaders.TryGetValue(stem, out var s) ? s : null;
            public string ImageName(string stem) => Images.FirstOrDefault(i => string.Equals(i, stem, StringComparison.OrdinalIgnoreCase));
            public ImageResource Image(string stem) => null;
            public bool HasMotion(string stem) => false;
        }

        static ActorNode Node(string name, int parent, V3 pos, Q4 rot) => new ActorNode { Name = name, Parent = parent, Position = pos, Rotation = rot };

        static readonly Q4 Turn = new Q4(0, 0.7071068f, 0, 0.7071068f); // 90° about Y

        /// <summary>Skeleton: Hero_ROOT → Spine → Head, plus Slot_Weapon; body mesh skinned to Spine/Head.</summary>
        static EmfxActor Skeleton()
        {
            var a = new EmfxActor();
            a.Nodes.Add(Node("Hero_ROOT", -1, new V3(0, 0, 0), Turn));
            a.Nodes.Add(Node("Spine", 0, new V3(0, 100, 0), Q4.Identity));
            a.Nodes.Add(Node("Head", 1, new V3(0, 60, 5), new Q4(0.1f, 0, 0, 0.995f)));
            a.Nodes.Add(Node("Slot_Weapon", 1, new V3(20, 0, 0), Q4.Identity));
            return a;
        }

        static ActorMesh Tri(int node, V3[] pos, SkinInfluence[][] skin, int materialId)
        {
            var m = new ActorMesh
            {
                Node = node, Positions = pos, Normals = pos.Select(_ => new V3(0, 0, 2)).ToArray(),
                Uv0 = new float[] { 0, 0, 1, 0, 0, 1 }, Indices = new[] { 0, 1, 2 }, OrgVertex = new[] { 0, 1, 2 }, Skin = skin,
            };
            m.SubMeshes.Add(new ActorSubMesh { MaterialId = materialId, IndexStart = 0, IndexCount = 3, VertexCount = 3 });
            return m;
        }

        static ActorMaterial Material(string name, string diffuse)
        {
            var mc = new ActorMaterial { Name = name };
            mc.Layers.Add(new MaterialLayer { MapType = 1, Texture = diffuse });
            return mc;
        }

        static FakeData Data()
        {
            var d = new FakeData();
            var body = Skeleton();
            body.Meshes.Add(Tri(1, new[] { new V3(0, 100, 0), new V3(10, 100, 0), new V3(0, 150, 0) }, new[]
            {
                new[] { new SkinInfluence(1, 0.5f), new SkinInfluence(2, 0.25f) },               // normalised to 2/3, 1/3
                new[] { new SkinInfluence(1, 1f) },
                new[] { new SkinInfluence(1, 0.1f), new SkinInfluence(2, 0.2f), new SkinInfluence(3, 0.3f), new SkinInfluence(0, 0.15f), new SkinInfluence(2, 0.05f) },
            }, 0));
            body.Materials.Add(Material("Body_Mat", "Body_Diffuse_S1"));
            body.MaterialRefs.Add(new MaterialRef { MaterialIndex = 0, Name = "G3_Body_01.xshmat" });
            d.Actors["G3_Hero_Body_Test"] = body;
            d.Shaders["G3_Body_01"] = new MaterialResource { Name = "G3_Body_01", Diffuse = new TextureSlot { Image = "Body_Diffuse_S1" } };

            // Head: own copy of the skeleton path + Slot_Hair and a face bone (Jaw) under Head.
            var head = new EmfxActor();
            head.Nodes.Add(Node("Hero_ROOT", -1, new V3(0, 0, 0), new Q4(0, 0, 1, 0)));   // rotated root: bind poses come from the body
            head.Nodes.Add(Node("Spine", 0, new V3(0, 100, 0), Q4.Identity));
            head.Nodes.Add(Node("Head", 1, new V3(0, 60, 5), new Q4(0.1f, 0, 0, 0.995f)));
            head.Nodes.Add(Node("Slot_Hair", 2, new V3(0, 15, 0), Q4.Identity));
            head.Nodes.Add(Node("Jaw", 2, new V3(0, -5, 4), Q4.Identity));
            head.Meshes.Add(Tri(2, new[] { new V3(0, 160, 0), new V3(5, 160, 0), new V3(0, 170, 0) }, new[]
            {
                new[] { new SkinInfluence(2, 1f) }, new[] { new SkinInfluence(4, 1f) }, new[] { new SkinInfluence(4, 0.5f), new SkinInfluence(2, 0.5f) },
            }, 0));
            head.Materials.Add(Material("Head_Main", "Head_Main_01_S1"));
            head.MaterialRefs.Add(new MaterialRef { MaterialIndex = 0, Name = "G3_Face.xshmat" });
            d.Actors["G3_Head_Hero_Test_01"] = head;
            d.Shaders["G3_Face"] = new MaterialResource { Name = "G3_Face", Diffuse = new TextureSlot { Image = "Generic_Face" } };

            var hair = new MeshResource { Name = "G3_Animation_Head_Hair_Test_01" };
            hair.Elements.Add(new MeshElement
            {
                Material = "G3_Hair_01.xshmat", Positions = new float[] { 0, 0, 0, 10, 0, 0, 0, 10, 0 }, Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                Uv0 = new float[] { 0, 0, 1, 0, 0, 1 }, Indices = new uint[] { 0, 1, 2 },
            });
            d.Meshes["G3_Animation_Head_Hair_Test_01"] = hair;
            d.Shaders["G3_Hair_01"] = new MaterialResource { Name = "G3_Hair_01", Diffuse = new TextureSlot { Image = "Hair_Diffuse_S1" } };
            d.Images.UnionWith(new[] { "Body_Diffuse_S1", "Head_Main_01_S1", "Head_Main_01_S29", "Generic_Face", "Hair_Diffuse_S1", "Hair_Diffuse_S5" });
            return d;
        }

        static NpcSpawn Spawn(string name, int headSwitch = 0, int hairSwitch = 0)
        {
            var s = new NpcSpawn { Name = name, Actor = "G3_Hero_Skeleton.FXA", Enabled = true, World = SourcePlan.Identity() };
            s.Parts.Add(new NpcPart { Resource = "G3_Head_Hero_Test_01.FXA", Switch = headSwitch, FacialResource = "G3_Head_Hero_Test_Animated_01.FXA" });
            s.Parts.Add(new NpcPart { Resource = "G3_Hero_Body_Test.FXA" });
            s.Parts.Add(new NpcPart { Resource = "G3_Animation_Head_Hair_Test_01.xcmsh", IsMesh = true, Switch = hairSwitch });
            return s;
        }

        [Test]
        public void RigHasUnityHierarchyOrderAddedNodesAndRendererObjects()
        {
            var rig = new SourceRigBuilder(Data()).Build(Spawn("Npc"), "Npc");
            Assert.That(rig.Skeleton, Is.EqualTo("G3_Hero_Body_Test"));
            Assert.That(rig.Nodes.Take(5).Select(n => n.Name), Is.EqualTo(new[] { "Npc", "Hero_ROOT", "Spine", "Head", "Slot_Weapon" }));
            var head = rig.Nodes.FindIndex(n => n.Name == "Head");
            // Children in creation order: skeleton nodes first, then the head's added nodes, then the hair object on Slot_Hair.
            Assert.That(rig.Nodes[head].Children.Select(c => rig.Nodes[c].Name), Is.EqualTo(new[] { "Slot_Hair", "Jaw" }));
            int slot = rig.Nodes.FindIndex(n => n.Name == "Slot_Hair");
            Assert.That(rig.Nodes[slot].Children.Select(c => rig.Nodes[c].Name), Is.EqualTo(new[] { "G3_Animation_Head_Hair_Test_01" }));
            // Renderers in hierarchy order: the hair (deep under the skeleton) before the skinned parts at the root.
            Assert.That(rig.Renderers().Select(r => r.Name), Is.EqualTo(new[] { "G3_Animation_Head_Hair_Test_01", "G3_Head_Hero_Test_01", "G3_Hero_Body_Test" }));
            Assert.That(rig.Parts, Is.EqualTo(new[] { "G3_Head_Hero_Test_01", "G3_Hero_Body_Test", "G3_Animation_Head_Hair_Test_01" }));
            // cm → m, quaternion copied.
            Assert.That(rig.Nodes[head].T, Is.EqualTo(new[] { 0f, 0.6f, 0.05f }).Within(1e-7));
        }

        [Test]
        public void SkinWeightsAreTop4NormalisedAndBindPosesComeFromTheBodySkeleton()
        {
            var rig = new SourceRigBuilder(Data()).Build(Spawn("Npc"), "Npc");
            var body = rig.Renderers().First(r => r.Name == "G3_Hero_Body_Test");
            Assert.That(body.BoneWeight[0], Is.EqualTo(2f / 3).Within(1e-6));
            Assert.That(body.BoneWeight[1], Is.EqualTo(1f / 3).Within(1e-6));
            Assert.That(body.BoneWeight.Skip(8).Take(4).Sum(), Is.EqualTo(1f).Within(1e-6), "5 influences → 4 largest, renormalised");
            Assert.That(body.BoneWeight[8], Is.EqualTo(0.3f / 0.75f).Within(1e-6));
            for (int b = 0; b < body.Bones.Length; b++)
            {
                var id = M4.Mul(rig.World(body.Bones[b]), body.Bindposes[b]);
                for (int i = 0; i < 16; i++) Assert.That(id[i], Is.EqualTo(M4.Identity()[i]).Within(1e-6), rig.Nodes[body.Bones[b]].Name);
            }
            // The head's own (rotated) Hero_ROOT is ignored; its Jaw (not in the skeleton) is a bone of its own.
            var head = rig.Renderers().First(r => r.Name == "G3_Head_Hero_Test_01");
            Assert.That(head.Bones.Select(b => rig.Nodes[b].Name), Is.EqualTo(new[] { "Head", "Jaw" }));
            var off = new SourceRigBuilder(Data()) { BindAddedNodes = false }.Build(Spawn("Npc"), "Npc").Renderers().First(r => r.Name == "G3_Head_Hero_Test_01");
            Assert.That(off.Bones.Length, Is.EqualTo(2), "without BindAddedNodes the Jaw binds to its skeleton ancestor (same node, two slots)");
        }

        [Test]
        public void MaterialsFollowTheImporterNamingAndSwitches()
        {
            var d = Data();
            var mats = new SourceMaterials(d);
            // Body: FXA layer = .xshmat diffuse → the shared material; head: generic .xshmat → the FXA material.
            Assert.That(mats.ForActor(d.Actors["G3_Hero_Body_Test"], 0, "G3_Hero_Body_Test").Name, Is.EqualTo("G3_Body_01"));
            var face = mats.ForActor(d.Actors["G3_Head_Hero_Test_01"], 0, "G3_Head_Hero_Test_01");
            Assert.That(face.Name, Is.EqualTo("Head_Main"));
            Assert.That(face.BaseImage, Is.EqualTo("Head_Main_01_S1"));
            var s29 = mats.Variant(face, 29);
            Assert.That(s29.Name, Is.EqualTo("Head_Main_S29"));
            Assert.That(s29.BaseImage, Is.EqualTo("Head_Main_01_S29"));
            Assert.That(mats.Variant(face, 7), Is.SameAs(face), "no _S7 image: the material stays as authored");
            Assert.That(mats.ResolveImage("Head_Main_01_S7", 0), Is.EqualTo("Head_Main_01_S1"));
            Assert.That(mats.ResolveImage("Missing_01", 3), Is.Null);
            Assert.That(mats.World("Nope.xshmat").Name, Is.EqualTo(SourceMaterials.FallbackName));
        }

        [Test]
        public void ExportWritesMirroredRigWithUniqueJointsStaticSlotAndPassesTheContract()
        {
            var rig = new SourceRigBuilder(Data()).Build(Spawn("Npc", 29, 5), "Npc");
            var head = SourceExport.FromRig(rig, SourceExport.PartRenderers(rig, "G3_Head_Hero_Test_01", false), null);
            Assert.That(head.Nodes.Count, Is.EqualTo(rig.Nodes.Count - 3), "every transform but the 3 renderer objects");
            Assert.That(head.Materials.Select(m => m.Name), Is.EqualTo(new[] { "Head_Main_S29" }));
            var src = rig.Renderers().First(r => r.Name == "G3_Head_Hero_Test_01");
            Assert.That(head.Meshes[0].Positions[3], Is.EqualTo(-src.Positions[3]), "Unity → glTF mirrors x");
            Assert.That(head.Meshes[0].SubMeshes[0].Indices, Is.EqualTo(new[] { 0, 2, 1 }), "winding reversed");

            var off = new SourceRigBuilder(Data()) { BindAddedNodes = false }.Build(Spawn("Npc"), "Npc");
            var merged = SourceExport.FromRig(off, SourceExport.PartRenderers(off, "G3_Head_Hero_Test_01", false), null);
            Assert.That(merged.Meshes[0].Joints.Length, Is.EqualTo(1), "two bone slots on one node → one joint");
            Assert.That(merged.Meshes[0].Weights[8], Is.EqualTo(1f).Within(1e-6), "merged influences add up");

            var hair = SourceExport.FromRig(rig, SourceExport.PartRenderers(rig, "G3_Animation_Head_Hair_Test_01", true), null);
            Assert.That(hair.Nodes[hair.Meshes[0].Node].Name, Is.EqualTo("Slot_Hair"));
            Assert.That(hair.Materials[0].Name, Is.EqualTo("G3_Hair_01_S5"));
            Assert.That(hair.Meshes[0].SubMeshes[0].Indices, Is.EqualTo(new[] { 0, 1, 2 }), ".xcmsh (i0,i2,i1) then mirrored back");

            var all = SourceExport.FromRig(rig, rig.Renderers(), null);
            var back = Glb.Read(Glb.Write(all));
            Assert.That(RigCheck.Compare(all, back).Passed, RigCheck.Compare(all, back).ToText());
            Assert.That(SourceCompare.Compare(all, back).Passed, SourceCompare.Compare(all, back).ToText());
        }

        [Test]
        public void CompareFindsMovedVerticesAndOtherMaterials()
        {
            var rig = new SourceRigBuilder(Data()).Build(Spawn("Npc"), "Npc");
            var a = Glb.Read(Glb.Write(SourceExport.FromRig(rig, rig.Renderers(), null)));
            var b = Glb.Read(Glb.Write(SourceExport.FromRig(rig, rig.Renderers(), null)));
            b.Meshes[1].Positions[0] += 0.001f;
            b.Materials[0].Name = "Other";
            var r = SourceCompare.Compare(a, b);
            Assert.That(r.Passed, Is.False);
            Assert.That(r.Errors.Any(e => e.Contains("positions")), r.ToText());
            Assert.That(r.Errors.Any(e => e.Contains("materials")), r.ToText());
        }

        [Test]
        public void PlanTakesFirstWearerAddsFacialTalkRigAndCollectsWearers()
        {
            var d = Data();
            d.Actors["G3_Head_Hero_Test_Animated_01"] = d.Actors["G3_Head_Hero_Test_01"];
            var orc = new NpcSpawn { Name = "Orc", Actor = "G3_Orc_Skeleton.FXA", World = SourcePlan.Identity() };
            orc.Parts.Add(new NpcPart { Resource = "G3_Orc_Body_Warrior.FXA" });
            var spawns = new List<NpcSpawn> { orc, Spawn("First", 29), Spawn("Second", 5) };
            bool Exists(string s) { try { d.Actor(s); return true; } catch { return false; } }
            var jobs = SourcePlan.Make(spawns, (SourceKind[])Enum.GetValues(typeof(SourceKind)), Exists);
            Assert.That(jobs.Select(j => j.Kind), Is.EqualTo(new[] { SourceKind.Head, SourceKind.Facial, SourceKind.Body, SourceKind.Hair }));
            Assert.That(jobs.All(j => j.Wearers.Select(w => w.Name).SequenceEqual(new[] { "First", "Second" })));
            var facial = jobs[1];
            Assert.That(facial.Stem, Is.EqualTo("G3_Head_Hero_Test_Animated_01"));
            Assert.That(facial.Spawn.Parts.Select(p => p.Stem), Is.EqualTo(new[] { "G3_Hero_Body_Test", "G3_Head_Hero_Test_Animated_01" }));
            Assert.That(facial.Spawn.Parts.All(p => p.Switch == 0));
            Assert.That(jobs[0].Spawn.Name, Is.EqualTo("First"));
            Assert.That(SourcePlan.IsHuman(orc), Is.False);

            var named = new NpcPart { Resource = "G3_Head_Hero_Test_01.FXA" };
            Assert.That(SourcePlan.FacialOf(named, Exists), Is.EqualTo("G3_Head_Hero_Test_Animated_01"), "named after the head when no FacialAnimFilePath");
            var heads = SourcePlan.Make(spawns, new[] { SourceKind.Head }, Exists);
            Assert.That(heads.Select(j => j.Kind), Is.EqualTo(new[] { SourceKind.Head }));
        }

        [Test]
        public void UnwornPartsGoOnTheHeroRig()
        {
            var hero = Spawn("PC_Hero");
            var jobs = SourcePlan.Make(new[] { hero }, (SourceKind[])Enum.GetValues(typeof(SourceKind)), _ => true);
            int worn = jobs.Count;
            SourcePlan.AddUnworn(jobs, new[]
            {
                ("G3_Hero_Body_Spare", false), ("G3_Hero_Body_Spare_LOD1", false), ("G3_Hero_Body_Skeleton", false), ("G3_Fat_Body_Smith", false),
                ("G3_Animation_Head_Beard_Spare_01", true), ("G3_Hero_Body_Test", false), ("G3_Some_Rock", true),
            }, hero, (SourceKind[])Enum.GetValues(typeof(SourceKind)));
            var added = jobs.Skip(worn).ToList();
            Assert.That(added.Select(j => j.Stem), Is.EqualTo(new[] { "G3_Animation_Head_Beard_Spare_01", "G3_Hero_Body_Spare" }));
            var body = added[1];
            Assert.That(body.Spawn.Parts.Select(p => p.Stem), Is.EqualTo(new[] { "G3_Head_Hero_Test_01", "G3_Hero_Body_Spare" }), "body replaced, hair dropped");
            Assert.That(added[0].Spawn.Parts.Select(p => p.Stem), Is.EqualTo(new[] { "G3_Head_Hero_Test_01", "G3_Hero_Body_Test", "G3_Animation_Head_Beard_Spare_01" }));
            Assert.That(added.All(j => j.Wearers.Count == 0));
        }

        [Test]
        public void ManifestIsJsonWithCounts()
        {
            var e = new SourceManifestEntry { Actor = "A \"q\"", Kind = "head", File = "A.glb", Spawn = "S", Triangles = 3, Bytes = 10 };
            e.Materials.Add(("M", "Img"));
            e.Npcs.Add("Diego");
            e.Switches.Add(29);
            var doc = JsonDocument.Parse(SourceManifest.ToJson(new[] { e, new SourceManifestEntry { Actor = "B", Kind = "body", File = "B.glb", Bytes = 5 } }, "test"));
            Assert.That(doc.RootElement.GetProperty("counts").GetProperty("head").GetInt32(), Is.EqualTo(1));
            Assert.That(doc.RootElement.GetProperty("bytes").GetInt64(), Is.EqualTo(15));
            var p = doc.RootElement.GetProperty("parts")[0];
            Assert.That(p.GetProperty("actor").GetString(), Is.EqualTo("A \"q\""));
            Assert.That(p.GetProperty("npcs")[0].GetString(), Is.EqualTo("Diego"));
            Assert.That(p.GetProperty("materials")[0].GetProperty("image").GetString(), Is.EqualTo("Img"));
        }

        [Test]
        public void PngRoundTripAndRowFlip()
        {
            var rgba = new byte[3 * 2 * 4];
            for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 11);
            var back = Png.Decode(Png.Encode(rgba, 3, 2), out int w, out int h);
            Assert.That((w, h), Is.EqualTo((3, 2)));
            Assert.That(back, Is.EqualTo(rgba));
            var flipped = Png.FlipRows(rgba, 3, 2);
            Assert.That(flipped.Take(12), Is.EqualTo(rgba.Skip(12)));
        }

        [Test]
        public void Dxt1DecodesFourAndThreeColourBlocks()
        {
            // c0 = red > c1 = blue: 4 colours; index 2 = 2/3 red + 1/3 blue.
            var four = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0b10_01_00_10, 0, 0, 0 };
            var px = Dxt.DecodeRgba(four, 4, 4, PixelFormat.DXT1);
            Assert.That(px.Take(4), Is.EqualTo(new byte[] { 170, 0, 85, 255 }));
            Assert.That(px.Skip(4).Take(4), Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
            // c0 <= c1: 3 colours + transparent black at index 3.
            var three = new byte[] { 0x1F, 0x00, 0x00, 0xF8, 0b11, 0, 0, 0 };
            Assert.That(Dxt.DecodeRgba(three, 4, 4, PixelFormat.DXT1).Take(4), Is.EqualTo(new byte[] { 0, 0, 0, 0 }));
        }
    }
}
