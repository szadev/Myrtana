using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// One weight class of orc boulder, named by its use function (gCInteraction_PS.UseFunc: OB_Light, OB_Medium, OB_Heavy,
    /// OB_Faring_Boulder): the strength needed to tilt it and to lift it, and the game event lifting it sets (Faring's).
    /// </summary>
    public sealed class BoulderWeight
    {
        [JsonConstructor]
        public BoulderWeight(string useFunc, int tiltStrength, int liftStrength, string liftFlag = null)
        {
            UseFunc = useFunc;
            TiltStrength = tiltStrength;
            LiftStrength = liftStrength;
            LiftFlag = string.IsNullOrEmpty(liftFlag) ? null : liftFlag;
        }

        public string UseFunc { get; }
        public int TiltStrength { get; }
        public int LiftStrength { get; }
        public string LiftFlag { get; }
    }

    /// <summary>
    /// Orc boulders (Docs/interactive-objects.md, UseType OrcBoulder): the hero tilts the boulder, takes it up and lifts it
    /// over his head. Each stage checks his strength against the boulder's weight class; a boulder that is too heavy makes
    /// him say <see cref="RefusalLine"/> and let go. The first lift of each boulder raises <see cref="GainAttribute"/>.
    /// </summary>
    public sealed class BoulderRules : IDefinition
    {
        public const string DefinitionId = "orcBoulders";

        [JsonConstructor]
        public BoulderRules(string id, string defaultUseFunc, IReadOnlyList<BoulderWeight> weights, string gainAttribute, int gainPoints,
                            string refusalLine)
        {
            Id = id;
            DefaultUseFunc = defaultUseFunc;
            Weights = weights ?? Array.Empty<BoulderWeight>();
            GainAttribute = gainAttribute;
            GainPoints = gainPoints;
            RefusalLine = refusalLine;
        }

        public string Id { get; }
        /// <summary>The weight class of a boulder without a use function.</summary>
        public string DefaultUseFunc { get; }
        public IReadOnlyList<BoulderWeight> Weights { get; }
        public string GainAttribute { get; }
        public int GainPoints { get; }
        /// <summary>The hero's SVM line when the boulder is too heavy.</summary>
        public string RefusalLine { get; }

        string IDefinition.Key => Id;

        public BoulderWeight WeightOf(string useFunc)
        {
            string func = string.IsNullOrWhiteSpace(useFunc) ? DefaultUseFunc : useFunc.Trim();
            return Weights.FirstOrDefault(w => w.UseFunc == func) ?? Weights.FirstOrDefault(w => w.UseFunc == DefaultUseFunc);
        }

        /// <summary>The CP 1.75 values (Script_Game OB_* and PS_Interact_LiftOrcBoulder_Loop).</summary>
        public static BoulderRules Default { get; } = new BoulderRules(DefinitionId, "OB_Light",
            new[]
            {
                new BoulderWeight("OB_Light", 100, 150),
                new BoulderWeight("OB_Medium", 150, 200),
                new BoulderWeight("OB_Heavy", 200, 250),
                new BoulderWeight("OB_Faring_Boulder", 200, 250, "Faring_Boulder_Success"),
            },
            "STR", 5, "TooHeavy");
    }

    /// <summary>The stages of a boulder use, each with its own strength check.</summary>
    public enum BoulderStage { Tilt, Lift }

    public enum BoulderVerdict
    {
        /// <summary>Strong enough for the stage.</summary>
        Ok,
        /// <summary>Too heavy: the hero says the refusal line and lets go.</summary>
        TooHeavy,
        /// <summary>No boulder of that id.</summary>
        NotABoulder,
    }

    /// <summary>Published when the hero lifted a boulder over his head.</summary>
    public readonly struct BoulderLifted : IGameEvent
    {
        public BoulderLifted(string objectId, bool firstTime)
        {
            ObjectId = objectId;
            FirstTime = firstTime;
        }

        public string ObjectId { get; }
        public bool FirstTime { get; }
    }

    /// <summary>
    /// The hero's boulder uses: <see cref="Check"/> per stage (the lift check of a story boulder sets its game event, as G3's
    /// use function does), <see cref="FinishLift"/> once the boulder is up (the attribute raise of the first lift).
    /// State: the boulders the hero has lifted (saved as "world.boulders").
    /// </summary>
    public sealed class OrcBoulders
    {
        readonly BoulderRules _rules;
        readonly Func<string, InteractiveObjectDef> _lookup;
        readonly Func<string, int> _heroAttribute;
        readonly IGameEventFlags _flags;
        readonly Action<AttributeGain> _gain;
        readonly IEventBus _events;
        readonly HashSet<string> _lifted = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="lookup">The interactive object of an id (null when unknown).</param>
        /// <param name="heroAttribute">The hero's current attribute value ("STR").</param>
        /// <param name="gain">Applies an attribute raise to the hero.</param>
        public OrcBoulders(BoulderRules rules, Func<string, InteractiveObjectDef> lookup, Func<string, int> heroAttribute,
                           IGameEventFlags flags, Action<AttributeGain> gain, IEventBus events)
        {
            _rules = rules ?? BoulderRules.Default;
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _heroAttribute = heroAttribute ?? throw new ArgumentNullException(nameof(heroAttribute));
            _flags = flags;
            _gain = gain;
            _events = events;
        }

        public BoulderRules Rules => _rules;

        public IEnumerable<string> Lifted => _lifted;

        public bool IsBoulder(string id) => BoulderOf(id) != null;

        public bool WasLifted(string id) => _lifted.Contains(id);

        InteractiveObjectDef BoulderOf(string id)
        {
            var def = id != null ? _lookup(id) : null;
            return def != null && def.Kind == InteractiveKind.Boulder ? def : null;
        }

        /// <summary>The weight class of the boulder (null when it is none).</summary>
        public BoulderWeight WeightOf(string id)
        {
            var def = BoulderOf(id);
            return def != null ? _rules.WeightOf(def.UseFunc) : null;
        }

        /// <summary>
        /// The strength check of one stage. A passed lift check sets the boulder's game event (OB_Faring_Boulder:
        /// Faring_Boulder_Success).
        /// </summary>
        public BoulderVerdict Check(string id, BoulderStage stage)
        {
            var weight = WeightOf(id);
            if (weight == null) return BoulderVerdict.NotABoulder;
            int need = stage == BoulderStage.Tilt ? weight.TiltStrength : weight.LiftStrength;
            if (_heroAttribute(_rules.GainAttribute ?? "STR") < need) return BoulderVerdict.TooHeavy;
            if (stage == BoulderStage.Lift && weight.LiftFlag != null) _flags?.Set(weight.LiftFlag);
            return BoulderVerdict.Ok;
        }

        /// <summary>The boulder is up: the first lift of each boulder raises the attribute. Returns the raise (null: none).</summary>
        public AttributeGain FinishLift(string id)
        {
            if (!IsBoulder(id)) return null;
            bool first = _lifted.Add(id);
            AttributeGain gain = null;
            if (first && !string.IsNullOrEmpty(_rules.GainAttribute) && _rules.GainPoints != 0)
            {
                gain = new AttributeGain(_rules.GainAttribute, ModifierOp.AddToVal, _rules.GainPoints);
                _gain?.Invoke(gain);
            }
            _events?.Publish(new BoulderLifted(id, first));
            return gain;
        }

        /// <summary>The whole use without motions (tilt, lift, finish): what a headless run does.</summary>
        public BoulderVerdict Use(string id)
        {
            var tilt = Check(id, BoulderStage.Tilt);
            if (tilt != BoulderVerdict.Ok) return tilt;
            var lift = Check(id, BoulderStage.Lift);
            if (lift != BoulderVerdict.Ok) return lift;
            FinishLift(id);
            return BoulderVerdict.Ok;
        }

        internal void Restore(IEnumerable<string> lifted)
        {
            _lifted.Clear();
            foreach (var id in lifted ?? Enumerable.Empty<string>()) _lifted.Add(id);
        }
    }

    /// <summary>Saves the boulders the hero lifted (section "world.boulders").</summary>
    public sealed class OrcBouldersSaveSection : ISaveParticipant
    {
        readonly OrcBoulders _boulders;

        public OrcBouldersSaveSection(OrcBoulders boulders) => _boulders = boulders;

        public string Section => "world.boulders";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteSortedStrings(_boulders.Lifted);

        public void Read(ISaveReader r, int version) => _boulders.Restore(r.ReadStrings());
    }
}
