using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;
using G3.Formats.Templates;

namespace G3.Formats.Items
{
    /// <summary>gEItemCategory (gothic3sdk GameEnum.h).</summary>
    public enum ItemCategory { None = 0, Weapon = 1, Armor = 2, Artefact = 3, Alchemy = 4, Empty_A = 5, Misc = 6, Written = 7, Empty_B = 8, Spellbook = 9, Skill = 10 }

    /// <summary>gESlot (gCInventorySlot.Slot / gCItem_PS post-data slot).</summary>
    public enum EquipSlot
    {
        None = 0, RightHand = 1, LeftHand = 2, EquipLeftHand = 3, Beard = 4, BackLeft = 5, BackRight = 6, Bow = 7, Crossbow = 8,
        Armor = 9, Belt = 10, Amulet = 11, Ring1 = 12, Ring2 = 13, Ammo = 14, Hair = 15, Head = 16, Body = 17, Helmet = 18
    }

    /// <summary>gEDamageType.</summary>
    public enum DamageType { None = 0, Impact = 1, Blade = 2, Missile = 3, Fire = 4, Ice = 5, Lightning = 6, Physics = 7 }

    /// <summary>gEStackType (gCInventoryStack.Type in world data).</summary>
    public enum StackType { Normal = 0, Treasure = 1, Merchandise = 2 }

    /// <summary>EAttribModOperation.</summary>
    public enum AttribModOp { AddToVal = 0, AddToMax = 1, AddPercentageToVal = 2, AddPercentageToMax = 3, SetVal = 4, SetMax = 5, SetValToPercentage = 6, SetMaxToPercentage = 7, SetValToMax = 8 }

    /// <summary>gEUseType — the subset items/interactables use (full list in gothic3sdk GameEnum.h).</summary>
    public enum UseType
    {
        None = 0, Action = 1, OneHanded = 2, TwoHanded = 3, Arrow = 4, Bow = 5, CrossBow = 6, Bolt = 7, Fist = 8, Shield = 9,
        Armor = 10, Helmet = 11, Staff = 12, Amulet = 13, Ring = 14, Cast = 15, Potion = 16, Plant = 17, Meat = 18, Fruit = 19,
        Bread = 20, Bottle = 21, Cup = 22, Bowl = 23, Torch = 24, Alarmhorn = 25, Broom = 26, Brush = 27, Lute = 28, Rake = 29,
        TrophyTeeth = 30, Valuable = 31, Smoke = 32, OrcPipe = 33, Scoop = 34, Stick = 35, Shovel = 36, Hammer = 37, Fan = 38,
        Pan = 39, Saw = 40, TrophySkin = 41, Map = 42, Book = 43, Letter = 44, Key = 45, Lockpick = 46, CarryFront = 47,
        CarryShoulder = 48, Pickaxe = 49, TrophyFur = 50, Halberd = 51, Axe = 52, ITEM_E = 53, Modify = 54, PhysicalFist = 55,
        ITEM_H = 56, Anvil = 57, Forge = 58, GrindStone = 59, Cauldron = 60, Barbecue = 61, Alchemy = 62, Bookshelf = 63,
        Bookstand = 64, TakeStone = 65, DropStone = 66, PickOre = 67, PickGround = 68, DigGround = 69, Field = 70, Repair = 71,
        SawLog = 72, Lumberjack = 73, Bed = 74, SleepGround = 75, CleanFloor = 76, Dance = 77, FanBoss = 78, Boss = 79,
        Throne = 80, Pace = 81, Bard = 82, Stool = 83, Bench = 84, Waterpipe = 85, WaterBarrel = 86, PirateTreasure = 87,
        Campfire = 88, SitCampfire = 89, SitGround = 90, Smalltalk = 91, Preach = 92, Spectator = 93, Stand = 94, Guard = 95,
        Trader = 96, Listener = 97, OrcDance = 98, Stoneplate = 99, OrcDrum = 100, Door = 101, OrcBoulder = 102, EatGround = 103,
        DrinkWater = 104, Pee = 105, Chest = 106, Shrine = 107, AttackPoint = 108, Roam = 109, BODY_A = 110, Beard = 111,
        Hair = 112, Head = 113, Body = 114, Flee = 115, Talk = 116
    }

    /// <summary>Typed property access over a decoded PropertyObject (strings need the owning GenomeReader).</summary>
    public static class Props
    {
        public static string Str(GenomeReader g, PropertyObject ps, string name)
        {
            var p = ps?.Get(name);
            return p == null ? null : g.StrFrom(p.Value);
        }
        public static int Int(PropertyObject ps, string name, int def = 0)
        {
            var p = ps?.Get(name);
            if (p == null || p.Value.Length == 0) return def;
            return p.Value.Length >= 4 ? BitConverter.ToInt32(p.Value, 0) : p.Value.Length == 2 ? BitConverter.ToInt16(p.Value, 0) : p.Value[0];
        }
        public static float Float(PropertyObject ps, string name, float def = 0) { var p = ps?.Get(name); return p == null ? def : p.AsFloat(); }
        public static bool Bool(PropertyObject ps, string name, bool def = false) { var p = ps?.Get(name); return p == null ? def : p.AsBool(); }
        public static int Enum(PropertyObject ps, string name, int def = 0) { var p = ps?.Get(name); return p == null ? def : p.AsEnum(); }
        /// <summary>eCTemplateEntityProxy / eCEntityProxy: u16 version, bool valid, [bCGuid]. Guid.Empty when unset.</summary>
        public static Guid Proxy(PropertyObject ps, string name)
        {
            var p = ps?.Get(name);
            return p == null ? Guid.Empty : ProxyValue(p.Value, 0);
        }
        public static Guid ProxyValue(byte[] v, int offset)
        {
            if (v == null || v.Length < offset + 3 + 16 || v[offset + 2] == 0) return Guid.Empty;
            var b = new byte[16];
            Buffer.BlockCopy(v, offset + 3, b, 0, 16);
            return new Guid(b);
        }
        public static float[] Vec3(PropertyObject ps, string name)
        {
            var p = ps?.Get(name);
            if (p == null || p.Value.Length < 12) return null;
            return new[] { BitConverter.ToSingle(p.Value, 0), BitConverter.ToSingle(p.Value, 4), BitConverter.ToSingle(p.Value, 8) };
        }
    }

    /// <summary>One attribute requirement or modifier of gCItem_PS (ReqAttribN* / ModAttribN*).</summary>
    public struct AttribEntry
    {
        public string Tag;      // "STR", "DEX", "INT", "HP", "MP", "SP", "PROT_BLADE", ... (Strings key "ATTRIB_" + Tag)
        public int Op;          // Req: EAttribReqOperation (0 val >=, 1 max >=); Mod: AttribModOp
        public int Value;
        public override string ToString() => $"{Tag}{(Value >= 0 ? "+" : "")}{Value}" + (Op != 0 ? $"(op{Op})" : "");
    }

    /// <summary>
    /// Item data of one template (or a world entity) carrying gCItem_PS. Docs/formats-items.md §2.
    /// </summary>
    public sealed class ItemInfo
    {
        public Guid Guid;                 // template item-header GUID (= creator GUID of world entities)
        public string Name;               // template / entity name, e.g. "It_1H_AncestorSword_01"
        public string TemplatePath;       // .tple pak path (templates only)
        public ItemCategory Category;
        public UseType UseType;           // gCInteraction_PS.UseType (weapon class, potion, food, body part, ...)
        public int Amount = 1, Quality, GoldValue;
        public bool MissionItem, Permanent, Robe, FullBody, Dropped;
        /// <summary>gCItem_PS.Texture, e.g. "IT_1H_AncestorSword_01.tga" → 64×64 tile in a gui.pak g3_hud_it_* atlas, rect from Library.pak/g3.igl (Docs/formats-gui.md §4).</summary>
        public string Icon;
        public string Mesh;               // eCVisualMeshDynamic_PS/Static_PS ResourceFileName (.xcmsh) …
        public string Actor;              // … or eCVisualAnimation_PS ResourceFilePath (.xact/.FXA) for body/head items
        public int MaterialSwitch;
        public float Mass;                // eCRigidBody_PS.TotalMass (kg?) — there is no explicit weight property
        public readonly List<AttribEntry> Requirements = new List<AttribEntry>();
        public readonly List<AttribEntry> Modifiers = new List<AttribEntry>();
        public Guid RequiredSkill1, RequiredSkill2, ActivateSkill1, ActivateSkill2, Spell, Skill, ArmorSet, ItemWorld, ItemInventory;
        public DamageType DamageType;     // gCDamage_PS
        public int DamageAmount;
        public float DamageManaMultiplier = 1, DamageHitMultiplier = 1;
        public string ScriptUseFunc;      // gCInteraction_PS.ScriptUseFunc
        public string Effect;             // gCEffect_PS.Effect (particle effect name)
        /// <summary>gCProjectile_PS.TouchBehavior (0 KillSelf, 1 Reflect, 2 Stop, 3 MaterialDependant); -1 = no projectile set (fix-176).</summary>
        public int ProjectileTouch = -1;
        public bool ProjectileLinkToBones; // gCProjectile_PS.LinkToBones
        // Recipe (gCRecipe_PS)
        public RecipeInfo Recipe;
        // gCItem_PS post data (world entities): equip slot of NPC equipment, visibility
        public EquipSlot Slot;
        public bool Visible;
        public int ScriptLines;
        public bool PostDataOk;      // post data located and decoded

        /// <summary>Strings key of the display name (stringtable.ini [LocAdmin_Strings], 5 languages).</summary>
        public string NameKey => "FO_" + Name;
        /// <summary>Strings key of the inventory description.</summary>
        public string DescriptionKey => "ITEMDESC_" + Name;
        public string IconImage => string.IsNullOrEmpty(Icon) ? null : System.IO.Path.GetFileNameWithoutExtension(Icon) + ".ximg";

        public bool IsWeapon => Category == ItemCategory.Weapon;
        public int Protection(string tag)
        {
            int v = 0;
            foreach (var m in Modifiers) if (m.Tag == tag && m.Op == 0) v += m.Value;
            return v;
        }

        /// <summary>Decode from the property sets of a template header or world entity.</summary>
        public static ItemInfo From(GenomeReader g, IEnumerable<PropertyObject> sets, string name, Guid guid)
        {
            PropertyObject item = null;
            var info = new ItemInfo { Name = name, Guid = guid };
            foreach (var ps in sets)
            {
                switch (ps.ClassName)
                {
                    case "gCItem_PS": item = ps; break;
                    case "eCVisualMeshDynamic_PS":
                    case "eCVisualMeshStatic_PS":
                        info.Mesh = Props.Str(g, ps, "ResourceFileName");
                        if (string.IsNullOrEmpty(info.Mesh)) info.Mesh = Props.Str(g, ps, "ResourceFilePath");
                        info.MaterialSwitch = Props.Int(ps, "MaterialSwitch");
                        break;
                    case "eCVisualAnimation_PS":
                        info.Actor = Props.Str(g, ps, "ResourceFilePath");
                        info.MaterialSwitch = Props.Int(ps, "MaterialSwitch");
                        break;
                    case "eCRigidBody_PS": info.Mass = Props.Float(ps, "TotalMass"); break;
                    case "gCInteraction_PS":
                        info.UseType = (UseType)Props.Enum(ps, "UseType");
                        info.ScriptUseFunc = Props.Str(g, ps, "ScriptUseFunc");
                        break;
                    case "gCDamage_PS":
                        info.DamageType = (DamageType)Props.Enum(ps, "DamageType");
                        info.DamageAmount = Props.Int(ps, "DamageAmount");
                        info.DamageManaMultiplier = Props.Float(ps, "DamageManaMultiplier", 1);
                        info.DamageHitMultiplier = Props.Float(ps, "DamageHitMultiplier", 1);
                        break;
                    case "gCEffect_PS": info.Effect = Props.Str(g, ps, "Effect"); break;
                    case "gCProjectile_PS":
                        info.ProjectileTouch = Props.Enum(ps, "TouchBehavior");
                        info.ProjectileLinkToBones = Props.Bool(ps, "LinkToBones");
                        break;
                    case "gCRecipe_PS": info.Recipe = RecipeInfo.From(ps); break;
                }
            }
            if (item == null) return null;
            info.Amount = Props.Int(item, "Amount", 1);
            info.Quality = Props.Int(item, "Quality");
            info.GoldValue = Props.Int(item, "GoldValue");
            info.MissionItem = Props.Bool(item, "MissionItem");
            info.Permanent = Props.Bool(item, "Permanent");
            info.Category = (ItemCategory)Props.Enum(item, "Category");
            info.Icon = Props.Str(g, item, "Texture");
            info.Dropped = Props.Bool(item, "Dropped");
            info.Robe = Props.Bool(item, "Robe");
            info.FullBody = Props.Bool(item, "FullBody");
            info.ItemWorld = Props.Proxy(item, "ItemWorld");
            info.ItemInventory = Props.Proxy(item, "ItemInventory");
            info.ArmorSet = Props.Proxy(item, "ArmorSet");
            info.Spell = Props.Proxy(item, "Spell");
            info.Skill = Props.Proxy(item, "Skill");
            info.RequiredSkill1 = Props.Proxy(item, "RequiredSkill1");
            info.RequiredSkill2 = Props.Proxy(item, "RequiredSkill2");
            info.ActivateSkill1 = Props.Proxy(item, "ActivateSkill1");
            info.ActivateSkill2 = Props.Proxy(item, "ActivateSkill2");
            for (int i = 1; i <= 4; i++)
            {
                var tag = Props.Str(g, item, $"ReqAttrib{i}Tag");
                if (string.IsNullOrEmpty(tag)) continue;
                // The engine names the Op property of entries 2..4 "ReqAttrib1Op" too (gothic3sdk note), so only #1 is reliable.
                info.Requirements.Add(new AttribEntry { Tag = tag, Op = i == 1 ? Props.Enum(item, "ReqAttrib1Op") : 0, Value = Props.Int(item, $"ReqAttrib{i}Value") });
            }
            for (int i = 1; i <= 6; i++)
            {
                var tag = Props.Str(g, item, $"ModAttrib{i}Tag");
                if (string.IsNullOrEmpty(tag)) continue;
                info.Modifiers.Add(new AttribEntry { Tag = tag, Op = Props.Enum(item, $"ModAttrib{i}Op"), Value = Props.Int(item, $"ModAttrib{i}Value") });
            }
            info.PostDataOk = ItemPostData.Read(g, item, out info.Slot, out info.Visible, out info.ScriptLines);
            return info;
        }

        public static ItemInfo From(TemplateFile t)
        {
            var main = t.Main;
            if (main == null) return null;
            var info = From(main.Source, main.PropertySets, t.Name, t.Guid);
            if (info != null) info.TemplatePath = t.Path;
            return info;
        }

    }

    /// <summary>
    /// gCItem_PS post-class-version data (g3dit gCItem_PS.readPostClassVersion):
    /// 24 constant bytes (01 00 00 00 01 00 00 00 00 01 00 00 00 00 01 00 00 00 00 01 00 00 00 00), i32 slot (gESlot),
    /// u8 visible (only bit 0 meaningful), bTObjArray {u8 1, u32 n, n × {u16 1, 5 × entry (Command, Entity1, Entity2, ID1, ID2)}}, u8 scriptExecuted.
    /// PropertyObject does not record where the post data starts, so it is located backwards from ps.End: in GENOMFLE
    /// files an entry is a u16 index, so the block is 35 + 12·n bytes; n is found by matching the constant prefix and count.
    /// </summary>
    public static class ItemPostData
    {
        static readonly byte[] Stats = { 1, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0 };

        public static bool Read(GenomeReader g, PropertyObject ps, out EquipSlot slot, out bool visible, out int scriptLines)
        {
            slot = EquipSlot.None; visible = false; scriptLines = 0;
            if (g == null || ps == null || !g.IsGenomFile) return false;
            var d = g.Data;
            for (int n = 0; n < 64; n++)
            {
                long start = ps.End - 35 - 12L * n;
                if (start < 0) break;
                bool ok = true;
                for (int i = 0; i < Stats.Length && ok; i++) ok = d[start + i] == Stats[i];
                if (!ok || d[start + 29] != 1 || BitConverter.ToInt32(d, (int)start + 30) != n) continue;
                slot = (EquipSlot)BitConverter.ToInt32(d, (int)start + 24);
                visible = (d[start + 28] & 1) != 0;
                scriptLines = n;
                return true;
            }
            return false;
        }
    }

    /// <summary>gCRecipe_PS: up to 4 ingredients → result (Craft = gESkillCategory: 3 smithing, 5 alchemy, 7 cooking).</summary>
    public sealed class RecipeInfo
    {
        public int Craft;
        public readonly List<KeyValuePair<Guid, int>> Ingredients = new List<KeyValuePair<Guid, int>>();
        public Guid Result, ReqSkill;
        public int ResultAmount, ResultQuality;

        public static RecipeInfo From(PropertyObject ps)
        {
            var r = new RecipeInfo { Craft = Props.Enum(ps, "Craft"), Result = Props.Proxy(ps, "ResultItem"), ResultAmount = Props.Int(ps, "ResultAmount"), ResultQuality = Props.Int(ps, "ResultQuality"), ReqSkill = Props.Proxy(ps, "ReqSkill1") };
            for (int i = 1; i <= 4; i++)
            {
                var gd = Props.Proxy(ps, $"Ingredient{i}Item");
                if (gd != Guid.Empty) r.Ingredients.Add(new KeyValuePair<Guid, int>(gd, Props.Int(ps, $"Ingredient{i}Amount")));
            }
            return r;
        }
    }
}

namespace G3.Formats.Items
{
    /// <summary>All item templates (live templates carrying gCItem_PS), by GUID and name.</summary>
    public sealed class ItemCatalog
    {
        public readonly List<ItemInfo> Items = new List<ItemInfo>();
        readonly Dictionary<Guid, ItemInfo> _byGuid = new Dictionary<Guid, ItemInfo>();
        readonly Dictionary<string, ItemInfo> _byName = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);

        public ItemInfo Get(Guid g) => _byGuid.TryGetValue(g, out var i) ? i : null;
        public ItemInfo Get(string name) => name != null && _byName.TryGetValue(name, out var i) ? i : null;

        public static ItemCatalog Build(TemplateArchive templates, bool includeDeleted = false)
        {
            var c = new ItemCatalog();
            foreach (var t in templates.Files)
            {
                if (t.IsDeleted && !includeDeleted) continue;
                var info = ItemInfo.From(t);
                if (info == null) continue;
                c.Items.Add(info);
                foreach (var h in t.Headers) c._byGuid[h.Guid] = info;
                if (info.Name != null && !c._byName.ContainsKey(info.Name)) c._byName[info.Name] = info;
            }
            return c;
        }
    }
}
