namespace Myrtana.Rules
{
    /// <summary>Where the hero's eat/drink motion is (<see cref="ItemUseFlow"/>).</summary>
    public enum ItemUseStage
    {
        Idle,
        /// <summary>The Use_Begin motion plays: nothing consumed yet.</summary>
        Begin,
        /// <summary>The consume point passed (ConsumeItems after the use ani): the item is gone, its effect applied.</summary>
        Consumed,
    }

    /// <summary>What cut a running item use short (fix-201).</summary>
    public enum ItemUseCut
    {
        None,
        Death,
        KnockDown,
        Fall,
        HitReaction,
        Swim,
        Transform,
        Dialog,
        /// <summary>Another owner (a combat motion, a scripted gesture) took the body's motion.</summary>
        MotionLost,
    }

    /// <summary>The hero's state one frame into an item use (filled by <c>HeroConsumption</c>).</summary>
    public struct ItemUseWatch
    {
        public bool Dead, Down, Falling, Reacting, Swimming, Transformed, DialogStarted, MotionOwned;
    }

    /// <summary>How an item use ended.</summary>
    public enum ItemUseEnd
    {
        /// <summary>Played through: consumed at the consume point.</summary>
        Finished,
        /// <summary>Cut before the consume point: the item stays in the inventory, no effect (G3: ConsumeItems never ran).</summary>
        CutKept,
        /// <summary>Cut after the consume point: consumed and applied already; only the end motion is skipped.</summary>
        CutApplied,
    }

    /// <summary>
    /// fix-201: the hero's eat/drink flow as a state machine (G3 PS_HUD_Use / PS_QuickUse → <c>_AI_UseInventoryItem</c>:
    /// hold the item, the use ani, then <c>ConsumeItems</c>, then the stacks held before go back into the hands). Any
    /// interruption — a hit reaction, a knock-down, a fall, death, swimming, a transformation, a dialog, another owner of
    /// the motion — ends the use at once and cleanly: the item is kept when the consume point was not reached (the latent AI
    /// function was replaced by the ZS_* task before ConsumeItems) [_AI_UseInventoryItem consumes after the use ani;
    /// the cut-before case is interp], applied when it was; the owner then releases every lock and restores the hands.
    /// Before fix-201 a stumble took the body's motion and the use's end never came on time, leaving the item-use lock set.
    /// </summary>
    public sealed class ItemUseFlow
    {
        /// <summary>No eat or drink motion pair lasts longer than this, seconds (both ~1–2 s at double speed) [interp].</summary>
        public const float MaxSeconds = 8f;

        public ItemUseStage Stage { get; private set; }
        public bool Running => Stage != ItemUseStage.Idle;
        /// <summary>The item is consumed at the consume point (false: a dialog's shown use, the core applied it already).</summary>
        public bool Consumes { get; private set; }
        public float Seconds { get; private set; }
        public ItemUseCut LastCut { get; private set; }
        public ItemUseEnd LastEnd { get; private set; }

        public void Start(bool consumes)
        {
            Stage = ItemUseStage.Begin;
            Consumes = consumes;
            Seconds = 0f;
            LastCut = ItemUseCut.None;
        }

        /// <summary>The begin motion is over: true when the item must be consumed now (once).</summary>
        public bool ReachConsumePoint()
        {
            if (Stage != ItemUseStage.Begin) return false;
            Stage = ItemUseStage.Consumed;
            return Consumes;
        }

        /// <summary>
        /// One frame: the cut that ends the use now (None: it goes on). <paramref name="dt"/> counts towards
        /// <see cref="MaxSeconds"/> (a lost motion that never reports its end).
        /// </summary>
        public ItemUseCut Check(in ItemUseWatch w, float dt)
        {
            if (!Running) return ItemUseCut.None;
            Seconds += dt;
            var cut = CutOf(w);
            if (cut == ItemUseCut.None && Seconds > MaxSeconds) cut = ItemUseCut.MotionLost;
            return cut;
        }

        /// <summary>The highest-ranking interruption in <paramref name="w"/> (death before a fall before a stumble …).</summary>
        public static ItemUseCut CutOf(in ItemUseWatch w)
        {
            if (w.Dead) return ItemUseCut.Death;
            if (w.Down) return ItemUseCut.KnockDown;
            if (w.Falling) return ItemUseCut.Fall;
            if (w.Reacting) return ItemUseCut.HitReaction;
            if (w.Swimming) return ItemUseCut.Swim;
            if (w.Transformed) return ItemUseCut.Transform;
            if (w.DialogStarted) return ItemUseCut.Dialog;
            if (!w.MotionOwned) return ItemUseCut.MotionLost;
            return ItemUseCut.None;
        }

        /// <summary>Ends the use by <paramref name="cut"/> (None: played through). Idle afterwards.</summary>
        public ItemUseEnd End(ItemUseCut cut = ItemUseCut.None)
        {
            var end = cut == ItemUseCut.None ? ItemUseEnd.Finished
                    : Stage == ItemUseStage.Consumed || !Consumes ? ItemUseEnd.CutApplied : ItemUseEnd.CutKept;
            if (!Running) end = ItemUseEnd.Finished;
            LastCut = cut;
            LastEnd = end;
            Stage = ItemUseStage.Idle;
            Seconds = 0f;
            return end;
        }

        /// <summary>The weapon held before the use goes back into the hands after this cut (not when he lies or died).</summary>
        public static bool RestoresHands(ItemUseCut cut) => cut != ItemUseCut.Death && cut != ItemUseCut.KnockDown;
    }
}
