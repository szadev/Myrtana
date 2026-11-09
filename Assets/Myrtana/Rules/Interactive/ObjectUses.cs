using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The CP 1.75 values of the hero's simple object uses (Script_Game PS_Interact and its states; Docs/interactive-objects.md
    /// §8): the tools, how long the work motion loops, what a water barrel heals, the shrine refusal.
    /// </summary>
    public sealed class ObjectUseRules
    {
        public ObjectUseRules(string pickaxe, string shovel, string miningPerk, int workSeconds, int barrelHitPoints, string shrineRefusalLine,
                              string nothingLine, string saw = "It_Saw", string drumStick = "It_DrumStick", int drumSticks = 2)
        {
            Saw = saw;
            DrumStick = drumStick;
            DrumSticks = drumSticks;
            Pickaxe = pickaxe;
            Shovel = shovel;
            MiningPerk = miningPerk;
            WorkSeconds = workSeconds;
            BarrelHitPoints = barrelHitPoints;
            ShrineRefusalLine = shrineRefusalLine;
            NothingLine = nothingLine;
        }

        /// <summary>The tool PS_Interact asks for at an ore vein (UseType PickOre).</summary>
        public string Pickaxe { get; }
        /// <summary>The tool PS_Interact asks for at a treasure spot (UseType PirateTreasure).</summary>
        public string Shovel { get; }
        /// <summary>Doubles every mined stack (GeneratePickOreInventory).</summary>
        public string MiningPerk { get; }
        /// <summary>
        /// The pickaxe and shovel works loop for this many seconds of the use state (<c>_AI_Action</c> command time 10,
        /// <see cref="TimedAction"/>): PickOre strikes 5 times, DigGround digs about 4 times. fix-139: it is not a loop count.
        /// </summary>
        public int WorkSeconds { get; }
        /// <summary>PS_Interact_DrinkWaterBarrel: AddHitPoints 10.</summary>
        public int BarrelHitPoints { get; }
        /// <summary>The hero's SVM line when a shrine of an opposed god refuses him ("No").</summary>
        public string ShrineRefusalLine { get; }
        /// <summary>A mined vein or an emptied treasure spot: StartSayNothingToGet.</summary>
        public string NothingLine { get; }
        /// <summary>The tool PS_Interact asks for at a log (UseType SawLog, case 0x48: HasItems It_Saw 1).</summary>
        public string Saw { get; }
        /// <summary>The sticks PS_Interact asks for at an orc drum (UseType OrcDrum, case 100: HasItems It_DrumStick 2).</summary>
        public string DrumStick { get; }
        public int DrumSticks { get; }

        /// <summary>The tool and count an activity (<see cref="InteractiveKind.Activity"/>) of <paramref name="useType"/> needs; null = none.</summary>
        public (string item, int amount)? ActivityTool(string useType)
        {
            if (string.Equals(useType, "SawLog", StringComparison.OrdinalIgnoreCase)) return (Saw, 1);
            if (string.Equals(useType, "OrcDrum", StringComparison.OrdinalIgnoreCase)) return (DrumStick, DrumSticks);
            return null;
        }

        public static ObjectUseRules Default { get; } =
            new ObjectUseRules("It_Pickaxe", "It_Shovel", "Perk_Mining", 10, 10, "No", "NothingToGet");
    }

    /// <summary>What happens when the hero tries a tool use now (nothing changes).</summary>
    public enum ObjectUseVerdict
    {
        Ok,
        /// <summary>The tool is missing: HUD_MissingItem_Interact + the tool's name, no motion.</summary>
        MissingTool,
        /// <summary>Mined before, or a dug-up spot that is empty: the hero says "NothingToGet".</summary>
        NothingToGet,
        /// <summary>No object of that kind.</summary>
        NotUsable,
    }

    /// <summary>A stack the hero mined (GeneratePickOreInventory prints GO_Mining for each).</summary>
    public readonly struct OreMined : IGameEvent
    {
        public OreMined(string objectId, string item, int amount)
        {
            ObjectId = objectId;
            Item = item;
            Amount = amount;
        }

        public string ObjectId { get; }
        public string Item { get; }
        public int Amount { get; }
    }

    /// <summary>
    /// The hero's object uses that need no page of their own (G3 CP 1.75 PS_Interact and its states):
    /// <list type="bullet">
    /// <item>ore veins (PS_Interact_PickOre, UseType 67): need <see cref="ObjectUseRules.Pickaxe"/>; once per vein (UsedByPlayer)
    /// every stack of each Mining treasure set of the vein goes to the hero in full, doubled with Perk_Mining
    /// (GeneratePickOreInventory); a used vein says "NothingToGet";</item>
    /// <item>treasure spots (PS_Interact_PirateTreasure, 87): need <see cref="ObjectUseRules.Shovel"/>; a spot whose plunder
    /// was generated and is empty says "NothingToGet"; digging generates the plunder once and opens the loot page (the
    /// container of <see cref="InteractiveObjects"/>);</item>
    /// <item>water barrels (PS_Interact_DrinkWaterBarrel, 86): +10 HP, any number of times;</item>
    /// <item>shrines (PS_Interact case 107): a hero who chose Innos is refused by a shrine whose name contains "Beliar", one who
    /// chose Beliar by "Innos", one who chose Adanos by both; the hero says "No".</item>
    /// </list>
    /// A use function of the object would set a game event (G3 does it after mining and drinking); no ore vein, treasure spot
    /// or barrel of the CP 1.75 world has one. State: the mined veins (section "world.veins").
    /// </summary>
    public sealed class ObjectUses
    {
        /// <summary>The game events of the god the hero chose (dialogs set them) and the name parts of the gods they oppose.</summary>
        static readonly (string chosen, string[] opposed)[] Gods =
        {
            ("Innos_chosen", new[] { "Beliar" }),
            ("Beliar_chosen", new[] { "Innos" }),
            ("Adanos_chosen", new[] { "Innos", "Beliar" }),
        };

        readonly ObjectUseRules _rules;
        readonly IContentDatabase _content;
        readonly InteractiveObjects _objects;
        readonly Inventories _inventories;
        readonly Func<string, bool> _heroKnows;
        readonly IGameEventFlags _flags;
        readonly Action<int> _heal;
        readonly IEventBus _events;
        readonly HashSet<string> _mined = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="heal">Adds hit points to the hero.</param>
        public ObjectUses(ObjectUseRules rules, IContentDatabase content, InteractiveObjects objects, Inventories inventories,
                          Func<string, bool> heroKnows, IGameEventFlags flags, Action<int> heal, IEventBus events)
        {
            _rules = rules ?? ObjectUseRules.Default;
            _content = content;
            _objects = objects;
            _inventories = inventories;
            _heroKnows = heroKnows;
            _flags = flags;
            _heal = heal;
            _events = events;
        }

        public ObjectUseRules Rules => _rules;

        public IEnumerable<string> Mined => _mined;

        public bool WasMined(string id) => _mined.Contains(id);

        InteractiveObjectDef Def(string id, InteractiveKind kind) =>
            id != null && _objects.TryGet(id, out var def) && def.Kind == kind ? def : null;

        bool HeroHas(string item, int amount = 1) => !string.IsNullOrEmpty(item) && _inventories.Of(ActorId.Player).Has(item, amount);

        // ------------------------------------------------------------------------------------------------ activities

        /// <summary>
        /// PS_Interact cases 0x48 / 0x55 / 100: a log needs the saw, an orc drum two drum sticks, a water pipe nothing; the
        /// use then loops until the hero leaves it and changes nothing.
        /// </summary>
        public ObjectUseVerdict CheckActivity(string id)
        {
            var def = Def(id, InteractiveKind.Activity);
            if (def == null) return ObjectUseVerdict.NotUsable;
            var tool = _rules.ActivityTool(def.UseType);
            return tool == null || HeroHas(tool.Value.item, tool.Value.amount) ? ObjectUseVerdict.Ok : ObjectUseVerdict.MissingTool;
        }

        /// <summary>The tool a refused activity names (HUD_MissingItem_Interact + its name).</summary>
        public string ActivityToolOf(string id) =>
            id != null && _objects.TryGet(id, out var def) ? _rules.ActivityTool(def.UseType)?.item : null;

        // ------------------------------------------------------------------------------------------------ ore veins

        /// <summary>PS_Interact case 67: a used vein first ("NothingToGet"), then the pickaxe.</summary>
        public ObjectUseVerdict CheckMining(string id)
        {
            if (Def(id, InteractiveKind.OreVein) == null) return ObjectUseVerdict.NotUsable;
            if (_mined.Contains(id)) return ObjectUseVerdict.NothingToGet;
            return HeroHas(_rules.Pickaxe) ? ObjectUseVerdict.Ok : ObjectUseVerdict.MissingTool;
        }

        /// <summary>
        /// The end of the pickaxe work: every stack of the vein's Mining sets, doubled with the mining perk, goes to the hero
        /// (one <see cref="OreMined"/> each) and the vein is used up. Empty when the use is refused.
        /// </summary>
        public IReadOnlyList<ItemStack> Mine(string id)
        {
            if (CheckMining(id) != ObjectUseVerdict.Ok) return Array.Empty<ItemStack>();
            var def = Def(id, InteractiveKind.OreVein);
            var gained = new List<ItemStack>();
            int factor = _heroKnows(_rules.MiningPerk) ? 2 : 1;
            foreach (var setId in def.Container?.TreasureSets ?? Array.Empty<string>())
            {
                if (!_content.TryGet(setId, out TreasureSetDef set) || set.Use != TreasureUse.Mining) continue;
                foreach (var stack in set.Stacks)
                {
                    int amount = Math.Max(1, stack.Amount) * factor;
                    _inventories.Give(ActorId.Player, stack.Item, amount, stack.Quality);
                    gained.Add(new ItemStack(stack.Item, amount, stack.Quality));
                    _events?.Publish(new OreMined(id, stack.Item, amount));
                }
            }
            _mined.Add(id);
            SetUseEvent(def);
            return gained;
        }

        // ------------------------------------------------------------------------------------------------ treasure spots

        /// <summary>PS_Interact case 87: an emptied spot first ("NothingToGet"), then the shovel.</summary>
        public ObjectUseVerdict CheckDig(string id)
        {
            if (Def(id, InteractiveKind.Treasure) == null) return ObjectUseVerdict.NotUsable;
            if (_objects.IsFilled(id) && _inventories.Of(InteractiveObjects.ContainerOf(id)).Stacks.Count == 0) return ObjectUseVerdict.NothingToGet;
            return HeroHas(_rules.Shovel) ? ObjectUseVerdict.Ok : ObjectUseVerdict.MissingTool;
        }

        /// <summary>
        /// The end of the digging (PS_Interact_PirateTreasure after the 10 shovel actions): the spot's plunder is generated
        /// the first time only (GeneratePlunderInvFromTreasureSets; a spot dug again shows what was left in it), the object's
        /// use function is set as a game event (every dig, as G3 does for the player), and its loot can be taken. G3 has no
        /// other side effect here: no fame, no spawn, no crime of its own, no visual change of the spot. False when refused.
        /// </summary>
        public bool Dig(string id)
        {
            if (CheckDig(id) != ObjectUseVerdict.Ok || !_objects.OpenContainer(id)) return false;
            SetUseEvent(Def(id, InteractiveKind.Treasure));
            return true;
        }

        /// <summary>The spot was dug up at least once (its plunder was generated; saved with the chests' state).</summary>
        public bool WasDug(string id) => Def(id, InteractiveKind.Treasure) != null && _objects.IsFilled(id);

        // ------------------------------------------------------------------------------------------------ water barrels

        /// <summary>The hero drank from a water barrel: +10 HP. False for any other object.</summary>
        public bool DrinkFromBarrel(string id)
        {
            var def = Def(id, InteractiveKind.WaterBarrel);
            if (def == null) return false;
            _heal?.Invoke(_rules.BarrelHitPoints);
            SetUseEvent(def);
            return true;
        }

        // ------------------------------------------------------------------------------------------------ shrines

        /// <summary>A shrine object (a talking object of UseType Shrine): the hero prays before he talks to it.</summary>
        public bool IsShrine(string id) =>
            id != null && _objects.TryGet(id, out var def) && string.Equals(def.UseType, "Shrine", StringComparison.OrdinalIgnoreCase);

        /// <summary>PS_Interact case 107: the shrine belongs to a god opposed to the one the hero chose.</summary>
        public bool ShrineRefuses(string id) =>
            id != null && _objects.TryGet(id, out var def) && ShrineRefuses(def.Name, _flags);

        public static bool ShrineRefuses(string shrineName, IGameEventFlags flags)
        {
            if (string.IsNullOrEmpty(shrineName) || flags == null) return false;
            foreach (var (chosen, opposed) in Gods)
                if (flags.IsSet(chosen))
                    return opposed.Any(god => shrineName.IndexOf(god, StringComparison.Ordinal) >= 0);
            return false;
        }

        void SetUseEvent(InteractiveObjectDef def)
        {
            if (!string.IsNullOrEmpty(def.UseFunc)) _flags?.Set(def.UseFunc);
        }

        internal void Restore(IEnumerable<string> mined)
        {
            _mined.Clear();
            foreach (var id in mined ?? Enumerable.Empty<string>()) _mined.Add(id);
        }
    }

    /// <summary>Saves the ore veins the hero mined (section "world.veins").</summary>
    public sealed class ObjectUsesSaveSection : ISaveParticipant
    {
        readonly ObjectUses _uses;

        public ObjectUsesSaveSection(ObjectUses uses) => _uses = uses;

        public string Section => "world.veins";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteSortedStrings(_uses.Mined);

        public void Read(ISaveReader r, int version) => _uses.Restore(r.ReadStrings());
    }
}
