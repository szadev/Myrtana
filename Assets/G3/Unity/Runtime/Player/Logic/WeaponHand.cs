namespace G3.Player
{
    /// <summary>A draw or sheathe motion in progress.</summary>
    public enum WeaponTransition { None, Drawing, Sheathing }

    /// <summary>What a fighter has to do to get a wanted weapon into the hand (<see cref="WeaponHand.Next"/>).</summary>
    public enum WeaponStep
    {
        /// <summary>The wanted weapon is in the hand and no draw or sheathe runs.</summary>
        Ready,
        /// <summary>A draw or sheathe is running: wait for it.</summary>
        Wait,
        /// <summary>Another weapon (or the spell hand) is in the hand: put it away first.</summary>
        Sheathe,
        /// <summary>The hands are empty: draw the wanted weapon.</summary>
        Draw,
    }

    /// <summary>
    /// The one source of truth for the weapon in a fighter's hand (pure, no engine types; tested in Tools/G3Tests).
    /// G3 switches weapons with one blocking command (_AI_HoldInventoryItems_Base): the put-away motion of the held
    /// stacks, then <c>HoldStacks</c> (items change hands at once: the old weapon to its carry slot, the new one to the
    /// hand), then the draw motion of the new stacks. Here a draw or sheathe is a transition whose hand change happens
    /// once (between the two hold motions, G3 HoldStacks, or at the end); a transition cut short by another motion (a hit reaction, a
    /// fall, a parade) keeps the hand as it is at that moment, so "drawn" and "in the hand" can never disagree
    /// [interp: G3's command runs to its end unless the AI state is left].
    /// Hand fields are G3 motion fields ("1H", "2H", "Staff", "Fist", "Bow", "CrossBow", "Cast"); the template is the
    /// item's visual key (null for fists and spells).
    /// </summary>
    public sealed class WeaponHand
    {
        public const string Fist = "Fist";

        /// <summary>Hand field of the weapon in the hand; null = empty hands.</summary>
        public string Field { get; private set; }
        /// <summary>Item template (visual key) of the weapon in the hand.</summary>
        public string Template { get; private set; }
        public WeaponTransition Transition { get; private set; }
        /// <summary>The weapon being drawn (valid while <see cref="Transition"/> is Drawing).</summary>
        public string PendingField { get; private set; }
        public string PendingTemplate { get; private set; }

        public bool InHand => Field != null;
        /// <summary>In the combat stance: drawing, or holding a weapon that is not being put away.</summary>
        public bool Drawn => Transition == WeaponTransition.Drawing || (Transition == WeaponTransition.None && InHand);
        /// <summary>No draw or sheathe is running.</summary>
        public bool Settled => Transition == WeaponTransition.None;
        /// <summary>The field the motions use: the weapon being drawn, else the one in the hand, else fists.</summary>
        public string MotionField => Transition == WeaponTransition.Drawing ? PendingField : Field ?? Fist;
        public string MotionTemplate => Transition == WeaponTransition.Drawing ? PendingTemplate : Template;

        /// <summary>Starts drawing; refused while a transition runs or anything is in the hand (put it away first).</summary>
        public bool BeginDraw(string field, string template)
        {
            if (!Settled || InHand) return false;
            PendingField = string.IsNullOrEmpty(field) ? Fist : field;
            PendingTemplate = template;
            Transition = WeaponTransition.Drawing;
            return true;
        }

        /// <summary>Starts putting the held weapon away; refused while a transition runs or with empty hands.</summary>
        public bool BeginSheathe()
        {
            if (!Settled || !InHand) return false;
            Transition = WeaponTransition.Sheathing;
            return true;
        }

        /// <summary>The motion reached its hand change: the drawn weapon enters, the sheathed one leaves the hand. True when the hand changed.</summary>
        public bool SwitchHands()
        {
            switch (Transition)
            {
                case WeaponTransition.Drawing:
                    if (Field == PendingField && Template == PendingTemplate) return false;
                    Field = PendingField;
                    Template = PendingTemplate;
                    return true;
                case WeaponTransition.Sheathing:
                    if (Field == null) return false;
                    Field = null;
                    Template = null;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The draw or sheathe motion ended (or there was none): the hand change happens if it has not. True when the hand changed.</summary>
        public bool Finish()
        {
            bool changed = SwitchHands();
            EndTransition();
            return changed;
        }

        /// <summary>Another motion cut the draw or sheathe short: the hand keeps what it holds now (never changes).</summary>
        public void Interrupt() => EndTransition();

        /// <summary>Empty hands at once (the weapon dropped on a knock-out, reset). True when the hand changed.</summary>
        public bool Clear()
        {
            bool changed = Field != null;
            Field = null;
            Template = null;
            EndTransition();
            return changed;
        }

        /// <summary>
        /// Puts <paramref name="field"/>/<paramref name="template"/> into the empty hands at once, without a motion (G3
        /// HoldStacks inside _AI_HoldInventoryItems after a used item, fix-68). Refused while a transition runs or anything
        /// is in the hand. True when the hand changed.
        /// </summary>
        public bool Hold(string field, string template)
        {
            if (!Settled || InHand) return false;
            Field = string.IsNullOrEmpty(field) ? Fist : field;
            Template = template;
            return true;
        }

        /// <summary>What to do to hold <paramref name="field"/>/<paramref name="template"/> (an NPC's weapon switch, step by step).</summary>
        public WeaponStep Next(string field, string template)
        {
            if (string.IsNullOrEmpty(field)) field = Fist;
            if (!Settled) return WeaponStep.Wait;
            if (!InHand) return WeaponStep.Draw;
            return Field == field && Template == template ? WeaponStep.Ready : WeaponStep.Sheathe;
        }

        void EndTransition()
        {
            Transition = WeaponTransition.None;
            PendingField = null;
            PendingTemplate = null;
        }
    }
}
