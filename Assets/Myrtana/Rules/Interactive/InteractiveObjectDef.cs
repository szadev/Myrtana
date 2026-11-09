using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>What the hero does with an interactive world object (G3 gEUseType, grouped by the player state it starts).</summary>
    public enum InteractiveKind
    {
        /// <summary>A container with a loot page (UseType Chest).</summary>
        Chest,
        /// <summary>A mover that opens and closes (gCDoor_PS, UseType Door).</summary>
        Door,
        /// <summary>Sleep until morning (UseType Bed).</summary>
        Bed,
        /// <summary>A crafting or reading station (Anvil, Cauldron, Alchemy, GrindStone, Campfire, Bookstand …; Rules/Crafting).</summary>
        Station,
        /// <summary>A lever or switch that toggles linked movers (no G3 world data uses it; kept for other content packs).</summary>
        Switch,
        /// <summary>
        /// An object the hero talks to (gCDialog_PS on a statue, altar, furnace, portal or temple door: Innos/Beliar shrines,
        /// Innos_Tester, Erzofen, Adanos_Gate …): its <see cref="InteractiveObjectDef.Name"/> owns the dialog entries.
        /// </summary>
        Speaker,
        /// <summary>An orc boulder the hero tilts and lifts (UseType OrcBoulder, <see cref="OrcBoulders"/>).</summary>
        Boulder,
        /// <summary>A bench, stool or throne the hero sits on until he gets up (UseType Bench/Stool/Throne, <see cref="ObjectUses"/>).</summary>
        Seat,
        /// <summary>An ore, gold, iron or sulfur vein mined once with a pickaxe (UseType PickOre, <see cref="ObjectUses.Mine"/>).</summary>
        OreVein,
        /// <summary>A water barrel or fountain the hero drinks from (UseType WaterBarrel, <see cref="ObjectUses.DrinkFromBarrel"/>).</summary>
        WaterBarrel,
        /// <summary>A treasure spot dug up with a shovel, then looted like a chest (UseType PirateTreasure, <see cref="ObjectUses.CheckDig"/>).</summary>
        Treasure,
        /// <summary>
        /// A use the hero keeps up until he leaves it (G3 PS_Interact_Loop, exit action 0x66): a water pipe (UseType
        /// Waterpipe, sits down and smokes), a log to saw (SawLog, needs It_Saw) or an orc drum (OrcDrum, needs two
        /// It_DrumStick); <see cref="ObjectUses.CheckActivity"/>.
        /// </summary>
        Activity,
        /// <summary>
        /// A signpost arm (G3 entities <c>Sign_Faring</c>, <c>Sign_Montera</c> …: a static board with gCInteraction_PS,
        /// UseType None, FocusNameType not Disable): focusing it shows its focus name <c>FO_Sign_&lt;Place&gt;</c> (the
        /// destination); there is nothing to use (fix-80).
        /// </summary>
        Sign,
    }

    /// <summary>
    /// An object in the world the hero can use: a chest, door, bed, station or switch (G3 entity with gCInteraction_PS and
    /// optionally gCLock_PS, gCDoor_PS, gCInventory_PS). Position in metres, orientation as forward and up vectors, in the
    /// presentation's axes. Only the start state is here; what changed in a game lives in <see cref="InteractiveObjects"/>.
    /// </summary>
    public sealed class InteractiveObjectDef : IDefinition
    {
        [JsonConstructor]
        public InteractiveObjectDef(string id, string name, InteractiveKind kind, string useType, string visual, float[] position,
                                    float[] forward, float[] up, float scale, ActorId owner, LockDef @lock, ContainerDef container,
                                    DoorDef door, IReadOnlyList<string> targets, string useFunc = null, string sector = null,
                                    float focusHeight = 0f, CookFireDef cookFire = null)
        {
            CookFire = cookFire;
            FocusHeight = focusHeight > 0f ? focusHeight : 0f;
            Id = id;
            Name = name;
            Kind = kind;
            UseType = useType;
            Visual = visual;
            Position = position ?? new float[3];
            Forward = forward ?? new float[] { 0, 0, 1 };
            Up = up ?? new float[] { 0, 1, 0 };
            Scale = scale <= 0 ? 1 : scale;
            Owner = owner;
            Lock = @lock;
            Container = container;
            Door = door;
            Targets = targets ?? Array.Empty<string>();
            UseFunc = useFunc;
            Sector = string.IsNullOrEmpty(sector) ? null : sector;
        }

        /// <summary>
        /// The story-switchable world sector the object belongs to (G3 SetSectorStatus; Simulation WorldSectors); null =
        /// always there. While the sector is off the object is not in the world.
        /// </summary>
        public string Sector { get; }

        /// <summary>The G3 entity GUID.</summary>
        public string Id { get; }
        /// <summary>Entity name; the focus name is the string <c>FO_&lt;Name&gt;</c>.</summary>
        public string Name { get; }
        public InteractiveKind Kind { get; }
        /// <summary>G3 gEUseType name ("Chest", "Bed", "Anvil" …): selects the hero's use motion.</summary>
        public string UseType { get; }
        /// <summary>Mesh (<c>.xcmsh</c>) or actor (<c>.FXA</c>) resource of the object's look.</summary>
        public string Visual { get; }
        public float[] Position { get; }
        public float[] Forward { get; }
        public float[] Up { get; }
        public float Scale { get; }
        /// <summary>Using or looting it is a crime against this NPC (empty = nobody's).</summary>
        public ActorId Owner { get; }
        /// <summary>Null = no lock.</summary>
        public LockDef Lock { get; }
        /// <summary>Contents of a chest; null for other kinds.</summary>
        public ContainerDef Container { get; }
        /// <summary>Mover data of a door; null for other kinds.</summary>
        public DoorDef Door { get; }
        /// <summary>Movers a switch toggles (object ids).</summary>
        public IReadOnlyList<string> Targets { get; }
        /// <summary>The object's script use function (G3 <c>gCInteraction_PS.ScriptUseFunc</c>): what a bookstand teaches ("BS_Alchemy" …); null = none.</summary>
        public string UseFunc { get; }

        /// <summary>
        /// Height of the object's mesh bounds, m, unscaled (0 = unknown): G3 hangs the focus name at the local point
        /// (0, bounding-box height, 0) above the entity origin (gCGUIEntityRenderer). Kept for signposts (fix-80), whose
        /// board has its origin in its middle.
        /// </summary>
        public float FocusHeight { get; }

        /// <summary>
        /// A decorative G3 fire the port lets the hero cook at ([owner] option, fix-88, <see cref="CookFires"/>; use type
        /// <see cref="CookFires.UseType"/>); null for every other object.
        /// </summary>
        public CookFireDef CookFire { get; }

        /// <summary>The focus name string (G3 FocusNameType Entity).</summary>
        [JsonIgnore] public TextId FocusName => new TextId("FO_" + Name);

        string IDefinition.Key => Id;
    }

    /// <summary>G3 gCLock_PS: the start state, the key that opens it and how hard it is to pick.</summary>
    public sealed class LockDef
    {
        [JsonConstructor]
        public LockDef(bool locked, int difficulty, string key, int keyAmount)
        {
            Locked = locked;
            Difficulty = difficulty;
            Key = key;
            KeyAmount = Math.Max(1, keyAmount);
        }

        public bool Locked { get; }
        /// <summary>-1/0 = no picking level (key only, when there is a key), 1..3 = lockpick level, above 3 = cannot be picked.</summary>
        public int Difficulty { get; }
        /// <summary>The key item (null = none).</summary>
        public string Key { get; }
        /// <summary>How many keys the hero must carry (CP 1.75 KeyAmount).</summary>
        public int KeyAmount { get; }
    }

    /// <summary>A chest's contents: treasure sets rolled when it is first opened, plus fixed stacks.</summary>
    public sealed class ContainerDef
    {
        [JsonConstructor]
        public ContainerDef(IReadOnlyList<string> treasureSets, IReadOnlyList<TreasureStack> stacks)
        {
            TreasureSets = treasureSets ?? Array.Empty<string>();
            Stacks = stacks ?? Array.Empty<TreasureStack>();
        }

        public IReadOnlyList<string> TreasureSets { get; }
        public IReadOnlyList<TreasureStack> Stacks { get; }
    }

    /// <summary>G3 gCDoor_PS: whether the door starts closed and the actor motions that open and close it.</summary>
    public sealed class DoorDef
    {
        [JsonConstructor]
        public DoorDef(bool closed, string openMotion, string closeMotion)
        {
            Closed = closed;
            OpenMotion = openMotion;
            CloseMotion = closeMotion;
        }

        public bool Closed { get; }
        public string OpenMotion { get; }
        public string CloseMotion { get; }
    }
}
