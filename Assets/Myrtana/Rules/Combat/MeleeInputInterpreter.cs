using System;

namespace Myrtana.Rules
{
    /// <summary>The G3 melee input timings (CP 1.75, combat_moves.json primaryButton/secondaryButton/comboWindows), in real seconds.</summary>
    public sealed class MeleeInputTiming
    {
        /// <summary>Primary held longer than this → a hold move (PowerAttack, Pierce, Hack, Finishing); shorter → a tap.</summary>
        public float PrimaryHold { get; set; } = 0.1f;
        /// <summary>Secondary held longer than this → Parade (JumpBack when the actor cannot block).</summary>
        public float SecondaryHold { get; set; } = 0.05f;
        /// <summary>Secondary released within this time of the press → QuickAttack (a riposte when parrying).</summary>
        public float SecondaryRelease { get; set; } = 0.2f;
        /// <summary>A power attack released within this time becomes an Attack…</summary>
        public float PowerToAttack { get; set; } = 0.2f;
        /// <summary>…and within this time a SimpleWhirl (1H or torch in the left hand), later nothing.</summary>
        public float PowerToSimpleWhirl { get; set; } = 0.5f;
        /// <summary>A primary press released within this time is a tap (G3 DurationPressedMSecs &lt; 201).</summary>
        public float TapMax { get; set; } = 0.2f;
        /// <summary>Releasing in a Pierce or Finishing attack within this time → QuickAttack.</summary>
        public float PierceToQuick { get; set; } = 0.7f;
        /// <summary>Releasing in a Hack attack within this time → WhirlAttack.</summary>
        public float HackToWhirl { get; set; } = 0.2f;
        /// <summary>
        /// The raised parade drops once the secondary button has been held longer than this; null = never. CP 1.75 with
        /// the extended AI does that (OnPlayerSecondaryAction: DurationPressedMSecs &gt; 2500 with the block up, engine
        /// setup flag 0x1eb); by the owner's decision (fix-68) the block stays up for as long as the button is held,
        /// so the game never sets it (<see cref="CpAaiParadeDrop"/> keeps the CP value for reference and tests).
        /// </summary>
        public float? ParadeAutoDrop { get; set; }

        /// <summary>CP 1.75 AAI's drop of a held parade, seconds (not used by the game, see <see cref="ParadeAutoDrop"/>).</summary>
        public const float CpAaiParadeDrop = 2.5f;

        /// <summary>The game's timings: the block is held without a time limit under every rule option (fix-68 [owner]).</summary>
        public static MeleeInputTiming For(RuleOptions options) => new MeleeInputTiming();
    }

    /// <summary>What the player holds and where it stands, read at every input event.</summary>
    public struct MeleeStance
    {
        public WeaponClass RightHand;
        /// <summary>A one-handed weapon or a torch in the left hand (needed for SimpleWhirl).</summary>
        public bool LeftOneHandedOrTorch;
        /// <summary>The actor can block in its stance; otherwise the held secondary button jumps back.</summary>
        public bool CanParade;
        /// <summary>The focus is a downed, killable enemy within reach of a finishing move (300).</summary>
        public bool FinisherInReach;
        /// <summary>The focus is down or dead (keeps a raised parade from dropping).</summary>
        public bool FocusDownOrDead;
        /// <summary>Knocked into a sitting position: primary → GetUpAttack, secondary → GetUpParade.</summary>
        public bool SittingKnockedDown;
        /// <summary>
        /// A hit reaction's Hit plays (G3: the task is ZS_Stumble / ZS_QuickStumble / ZS_ParadeStumble …, not PS_Melee):
        /// presses only start their timers, releases only clear the flags (and lower the parade), nothing is queued or started.
        /// </summary>
        public bool Reacting;
        /// <summary>
        /// The weapon is being drawn (G3 PS_Melee's entry state runs _AI_HoldInventoryItems before PS_Melee_Loop): requests
        /// wait in the queue as behind a running move.
        /// </summary>
        public bool Drawing;
        /// <summary>
        /// The body shows no parade (not raising, holding or keeping it through a parried stumble). The interpreter then
        /// forgets a parade it believes up, so a held button raises it again (fix-203: a hit during the raise was resolved as
        /// parried, the body ended standing, the input still thought the block up and never raised it again).
        /// </summary>
        public bool BodyOutOfParade;

        /// <summary>The block-held tap gives a whirl with these right-hand weapons (G3 heavyRightHandUseTypes).</summary>
        public bool HeavyRightHand =>
            RightHand == WeaponClass.TwoHanded || RightHand == WeaponClass.Staff || RightHand == WeaponClass.Halberd ||
            RightHand == WeaponClass.Axe || RightHand == WeaponClass.Tool;
        /// <summary>Quick attacks need a weapon (moveAvailability: humanoids unless fists).</summary>
        public bool CanQuickAttack => RightHand != WeaponClass.Fist;
    }

    public enum MeleeCommandKind { None, StartMove, RaiseParade, LowerParade }

    /// <summary>One result of the interpreter: start a move, raise or lower the parade, or nothing.</summary>
    public readonly struct MeleeCommand : IEquatable<MeleeCommand>
    {
        public static readonly MeleeCommand None = default;
        public static readonly MeleeCommand RaiseParade = new MeleeCommand(MeleeCommandKind.RaiseParade, CombatAction.Parade);
        public static readonly MeleeCommand LowerParade = new MeleeCommand(MeleeCommandKind.LowerParade, CombatAction.None);

        MeleeCommand(MeleeCommandKind kind, CombatAction move)
        {
            Kind = kind;
            Move = move;
        }

        public MeleeCommandKind Kind { get; }
        public CombatAction Move { get; }

        public static MeleeCommand Start(CombatAction move) => new MeleeCommand(MeleeCommandKind.StartMove, move);

        public bool Equals(MeleeCommand other) => Kind == other.Kind && Move == other.Move;
        public override bool Equals(object obj) => obj is MeleeCommand c && Equals(c);
        public override int GetHashCode() => ((int)Kind * 397) ^ (int)Move;
        public override string ToString() => Kind == MeleeCommandKind.StartMove ? $"Start({Move})" : Kind.ToString();
    }

    /// <summary>
    /// The player's melee input as in G3 (CP 1.75 Script_Game OnPlayerAction / OnPlayerSecondaryAction, PS_Melee_*):
    /// button presses and releases with their times become moves. Primary tap (≤ 200 ms) → Attack; hold (> 100 ms) →
    /// PowerAttack. With the block flag set (secondary held > 50 ms, or the parade up): tap → QuickAttack (WhirlAttack with
    /// a heavy right hand), hold → PierceAttack (1H) or HackAttack, or FinishingAttack near a downed enemy. A primary
    /// release while a PowerAttack, Pierce, Finishing or Hack runs is read against that move's combo window instead
    /// (Attack/SimpleWhirl, QuickAttack, WhirlAttack, at once). Secondary held → Parade (queued behind a running move,
    /// once per press; a block raised during the press comes back after every move while it stays held: G3
    /// FUN_10002130); released within 200 ms → QuickAttack (a riposte at
    /// once while parrying; otherwise queued like a tap: repeated right clicks chain quick attacks), JumpBack without a
    /// quick attack. Knocked into a sitting position: primary press → GetUpAttack, secondary press → GetUpParade.
    /// One-slot queue, the newest wins; a move the actor cannot do is never queued (G3 FUN_10004f80).
    /// Pure: the Unity adapter feeds button events and times, starts the returned moves (paying stamina through
    /// <see cref="CombatService.StartMove"/>) and reports when a move has ended (Docs/combat.md §1.1, §9.3, §9.5).
    /// </summary>
    public sealed class MeleeInputInterpreter
    {
        readonly MeleeInputTiming _timing;
        double? _primaryDown, _secondaryDown, _paradeRaisedAt;
        bool _holdFired, _holdStarted, _primaryGetUp, _secondaryGetUp, _blockLatched, _reacting;
        /// <summary>
        /// G3's player parade flag (DAT_10118ab0, set by <c>_AI_Parade</c>): the block was raised during this press. Moves
        /// do not clear it; the release, ResetAll and a control loss do. While it is set and the button held, the melee loop
        /// raises the block again whenever nothing runs (FUN_10002130, fix-75).
        /// </summary>
        bool _paradeWanted;
        /// <summary>The CP AAI drop lowered the parade during this press: it stays down until the button is pressed again.</summary>
        bool _paradeDropped;
        CombatAction _current, _queued;
        double _currentStart;

        public MeleeInputInterpreter(MeleeInputTiming timing = null) => _timing = timing ?? new MeleeInputTiming();

        /// <summary>Optional: every decision as one line (the dev combat log); null = silent.</summary>
        public Action<string> Trace { get; set; }
        /// <summary>The move waiting for the running one to end (None = nothing queued; Parade = raise the block).</summary>
        public CombatAction Queued => _queued;

        /// <summary>The move running now (None = free; the parade is not a move).</summary>
        public CombatAction Current => _current;
        public bool InParade => _paradeRaisedAt != null;
        public bool Busy => _current != CombatAction.None;
        /// <summary>
        /// G3's "block held" flag (DAT_10118b41): set once the secondary button has been held &gt; 50 ms, cleared by its
        /// release; or the parade is up.
        /// </summary>
        public bool BlockHeld => _blockLatched || InParade;
        /// <summary>A hit reaction took the hero out of the melee loop (<see cref="HitTaken"/>) and its Hit still plays.</summary>
        public bool Reacting => _reacting;
        /// <summary>The block was raised during this press and comes back after every move while it is held (G3 FUN_10002130).</summary>
        public bool ParadeWanted => _paradeWanted;
        /// <summary>
        /// The block is a held state (fix-203, owner): the secondary button is down past the 50 ms hold, so whenever nothing
        /// runs and the parade is down it is raised, after a hit reaction (ResetAll), a move or a queued tap alike, without a
        /// new press. Stricter G3 re-raised only a parade already raised during this press (DAT_10118ab0).
        /// </summary>
        public bool BlockIntent => _blockLatched && _secondaryDown != null && !_paradeDropped;

        bool Suspended(in MeleeStance stance) => _reacting || stance.Reacting;
        bool BusyFor(in MeleeStance stance) => Busy || stance.Drawing;

        /// <summary>
        /// The hero was hit and a reaction starts (G3 AssessHit → SetTask ZS_…): the running move is cut and input waits
        /// until the reaction's Hit is over (<see cref="MeleeStance.Reacting"/> false, then <see cref="Update"/>).
        /// <paramref name="resetAll"/> (Stumble, QuickStumble, PierceStumble: G3 ResetAll) also drops the queued move and
        /// lowers the parade; the parried stumbles keep both (<see cref="HitReactionRules.ResetsInput"/>). The block flag
        /// and the hold flag stay (G3 keeps DAT_10118b41 / DAT_10118b40). Call it before the cut move is reported over.
        /// </summary>
        public void HitTaken(double time, bool resetAll)
        {
            _reacting = true;
            string cut = _current != CombatAction.None ? $"{_current} cut" : "no move ran";
            // fix-203: a hold move (power attack, pierce, hack, finishing) cut by the hit, or a queued one ResetAll drops,
            // while its button is still held fires again when the reaction is over (the held charge resumes, no new press).
            bool holdCut = _primaryDown != null && _holdStarted && IsComboState(_current);
            bool holdDropped = resetAll && _primaryDown != null && _holdFired && !_holdStarted && IsComboState(_queued);
            if (holdCut || holdDropped)
            {
                _holdFired = _holdStarted = false;
                Say($"hit: LMB still held, the cut {(holdCut ? _current : _queued)} fires again after the reaction");
            }
            _current = CombatAction.None;
            if (!resetAll)
            {
                Say($"hit (parried): {cut}; input waits for the stumble's Hit{(_queued != CombatAction.None ? $", queued {_queued} kept" : "")}");
                return;
            }
            Say($"hit: {cut}; ResetAll{(_queued != CombatAction.None ? $": queued {_queued} dropped" : "")}{(InParade ? ", parade lowered" : "")}; input waits for the reaction's Hit");
            _queued = CombatAction.None;
            _paradeRaisedAt = null;
            _paradeWanted = false; // ResetAll → FUN_10001d90: the parade flag goes too
        }

        /// <summary>
        /// The buttons can no longer be read (a control lock, a free cursor): their presses are forgotten (no tap, no hold),
        /// the block flag cleared and a raised parade lowered (as by a release, G3 FUN_10001d90); the running and queued moves
        /// stay (a queued Parade is dropped: its button is gone).
        /// </summary>
        public MeleeCommand ButtonsLost(double time)
        {
            bool up = InParade;
            if (_primaryDown != null || _secondaryDown != null || up) Say($"buttons lost (controls locked or cursor free){(up ? ": parade lowered" : "")}");
            _primaryDown = _secondaryDown = _paradeRaisedAt = null;
            _holdFired = _holdStarted = _primaryGetUp = _secondaryGetUp = _blockLatched = _paradeWanted = _paradeDropped = false;
            if (_queued == CombatAction.Parade) _queued = CombatAction.None;
            return up ? MeleeCommand.LowerParade : MeleeCommand.None;
        }

        /// <summary>A command could not be shown (no motion, no fight possible now): the interpreter forgets it.</summary>
        public void Refused(MeleeCommand command)
        {
            if (command.Kind == MeleeCommandKind.StartMove && _current == command.Move) _current = CombatAction.None;
            else if (command.Kind == MeleeCommandKind.RaiseParade) _paradeRaisedAt = null; // the flag stays: a later frame may raise it
        }

        public MeleeCommand PrimaryPressed(double time, in MeleeStance stance)
        {
            _primaryDown = time;
            _holdFired = _holdStarted = _primaryGetUp = false;
            if (!stance.SittingKnockedDown) return MeleeCommand.None;
            // G3 OnPlayerAction in ZS_SitKnockDown: the press edge queues GetUpAttack (no hold, no tap timing).
            _holdFired = _primaryGetUp = true;
            Say("sitting: press -> GetUpAttack");
            return Start(CombatAction.GetUpAttack, time);
        }

        public MeleeCommand PrimaryReleased(double time, in MeleeStance stance)
        {
            if (_primaryDown == null) return MeleeCommand.None;
            double held = time - _primaryDown.Value;
            _primaryDown = null;
            if (_primaryGetUp)
            {
                _primaryGetUp = false;
                return MeleeCommand.None;
            }
            if (Suspended(stance))
            {
                // G3 OnPlayerAction outside PS_Melee: the release only clears the hold flag (DAT_10118b40).
                _holdFired = _holdStarted = false;
                Say($"LMB release ({held * 1000:0} ms) during a hit reaction: ignored");
                return MeleeCommand.None;
            }
            if (IsComboState(_current))
            {
                // G3: a release while PS_Melee_PowerAttack/Pierce/Finishing/Hack runs is read against that state's time (FullStop, at once).
                double stateTime = time - _currentStart;
                var combo = ComboOnRelease(stateTime, stance);
                if (combo == CombatAction.None)
                {
                    Say($"release in {_current} at {stateTime * 1000:0} ms: no combo (window over or none)");
                    return MeleeCommand.None;
                }
                if (_holdFired && !_holdStarted && _queued != CombatAction.None)
                {
                    Say($"release: queued {_queued} dropped [interp]");
                    _queued = CombatAction.None; // this press's own hold move must not follow the combo
                }
                Say($"release in {_current} at {stateTime * 1000:0} ms: combo");
                return Start(combo, time);
            }
            if (!_holdFired) return Request(TapMove(stance), time, $"tap ({held * 1000:0} ms)", stance);
            if (!_holdStarted)
            {
                // G3 OnPlayerAction: the hold move is still queued, so the release is read as a tap (≤ 200 ms): it replaces the queue.
                if (held <= _timing.TapMax) return Request(TapMove(stance), time, $"tap ({held * 1000:0} ms) after a queued hold", stance);
                Say($"release ({held * 1000:0} ms): queued {_queued} kept");
                return MeleeCommand.None;
            }
            Say($"release ({held * 1000:0} ms) after {_current}: no combo");
            return MeleeCommand.None;
        }

        public MeleeCommand SecondaryPressed(double time, in MeleeStance stance)
        {
            _secondaryDown = time;
            _secondaryGetUp = _paradeDropped = false;
            if (!stance.SittingKnockedDown) return MeleeCommand.None;
            // G3 OnPlayerSecondaryAction in ZS_SitKnockDown: the press edge queues GetUpParade.
            _secondaryGetUp = true;
            Say("sitting: press -> GetUpParade");
            return Start(CombatAction.GetUpParade, time);
        }

        public MeleeCommand SecondaryReleased(double time, in MeleeStance stance)
        {
            if (_secondaryDown == null) return MeleeCommand.None;
            double held = time - _secondaryDown.Value;
            bool quick = held <= _timing.SecondaryRelease;
            _secondaryDown = null;
            _blockLatched = _paradeDropped = false;
            _paradeWanted = false; // every release: FUN_10001d90 clears the parade flag
            if (_secondaryGetUp || Suspended(stance))
            {
                // G3 OnPlayerSecondaryAction: a release always clears the block flag and lowers the parade (FUN_10001d90),
                // whatever the task; nothing else outside PS_Melee (the get-up press, a hit reaction). A parade raised
                // after a GetUpParade press used to outlive this release.
                bool reacting = Suspended(stance), up = InParade;
                _secondaryGetUp = false;
                _paradeRaisedAt = null;
                if (reacting) Say($"RMB release ({held * 1000:0} ms) during a hit reaction: block flag cleared, parade lowered");
                return up || reacting ? MeleeCommand.LowerParade : MeleeCommand.None;
            }
            if (InParade)
            {
                // PS_Melee_Parade: a quick release ripostes at once (FullStop → PS_Melee_QuickAttack).
                _paradeRaisedAt = null;
                if (quick && stance.CanQuickAttack)
                {
                    Say($"RMB release ({held * 1000:0} ms) in the parade: riposte");
                    return Start(CombatAction.QuickAttack, time);
                }
                return MeleeCommand.LowerParade;
            }
            if (!quick)
            {
                if (_queued == CombatAction.Parade)
                {
                    Say("RMB up: queued Parade dropped [interp]");
                    _queued = CombatAction.None;
                }
                return MeleeCommand.None;
            }
            // G3: a quick release queues QuickAttack (JumpBack without one) like a tap: right clicks chain quick attacks.
            return Request(stance.CanQuickAttack ? CombatAction.QuickAttack : CombatAction.JumpBack, time, $"RMB tap ({held * 1000:0} ms)", stance);
        }

        /// <summary>Call every frame: fires hold moves once a button has been held long enough and drops a stale parade.</summary>
        public MeleeCommand Update(double time, in MeleeStance stance)
        {
            if (stance.SittingKnockedDown) return MeleeCommand.None; // ZS_SitKnockDown: only the press edges count
            if (stance.Reacting) return MeleeCommand.None; // ZS_Stumble …: OnPlayerAction/SecondaryAction act in PS_Melee only
            if (_reacting)
            {
                // The reaction's Hit is over: G3 AssessAgressor → PS_Melee → PS_Melee_Loop; held buttons count from now on.
                _reacting = false;
                Say("hit reaction over: back in the melee loop");
            }
            if (_primaryDown != null && !_holdFired && time - _primaryDown.Value > _timing.PrimaryHold)
            {
                _holdFired = true;
                var hold = Request(HoldMove(stance), time, $"hold > {_timing.PrimaryHold * 1000:0} ms", stance);
                _holdStarted = hold.Kind == MeleeCommandKind.StartMove;
                return hold;
            }
            if (_secondaryDown != null && !_blockLatched && time - _secondaryDown.Value > _timing.SecondaryHold)
            {
                // G3: held > 50 ms → the block flag is set and Parade (JumpBack when blocking is impossible) is queued once.
                _blockLatched = true;
                var move = stance.CanParade ? CombatAction.Parade : CombatAction.JumpBack;
                if (BusyFor(stance))
                {
                    string busy = Busy ? _current.ToString() : "drawing";
                    Say(_queued != CombatAction.None
                        ? $"RMB held -> {move} queued, replaces {_queued} (busy: {busy})"
                        : $"RMB held -> {move} queued (busy: {busy})");
                    _queued = move;
                    return MeleeCommand.None;
                }
                if (move == CombatAction.JumpBack) return Start(CombatAction.JumpBack, time);
                return Raise(time);
            }
            if (_paradeRaisedAt != null && _secondaryDown is double pressed && _timing.ParadeAutoDrop is float drop &&
                !(_primaryDown != null && _holdFired) && !stance.FocusDownOrDead && time - pressed > drop)
            {
                // CP AAI (OnPlayerSecondaryAction): parade up, no primary hold fired (DAT_10118b40), focus none or not down/dead.
                _paradeRaisedAt = null;
                _paradeWanted = false; // FUN_10001d90 clears the parade flag: it stays down while held
                _paradeDropped = true;
                Say($"RMB held > {drop * 1000:0} ms: parade dropped (AAI)");
                return MeleeCommand.LowerParade;
            }
            if (InParade && stance.BodyOutOfParade && !BusyFor(stance))
            {
                _paradeRaisedAt = null;
                Say("the body shows no parade: input parade down");
            }
            // G3 PS_Melee_Loop takes the queue on every tick while no move runs (after a hit reaction or the draw too).
            if (_queued != CombatAction.None && !BusyFor(stance)) return TakeQueued(time, stance, "loop");
            return ReRaise(time, stance, "loop");
        }

        /// <summary>
        /// The presentation finished the current move: starts the queued move (a queued Parade raises the block while the
        /// button is still held); with nothing queued, a block raised during this press comes back while it is held.
        /// </summary>
        public MeleeCommand MoveEnded(double time, in MeleeStance stance)
        {
            _current = CombatAction.None;
            // Cut by a hit (HitTaken came first) or the draw still runs: the queue waits for the loop (Update).
            if (Suspended(stance) || stance.Drawing) return MeleeCommand.None;
            if (_queued == CombatAction.None) return ReRaise(time, stance, "move ended");
            return TakeQueued(time, stance, "move ended");
        }

        /// <summary>
        /// G3 FUN_10002130 (every PS_Melee_Loop tick, before the queue is read): block flag set, nothing queued and the ani
        /// state not Parade → Parade is queued and taken in the same tick. So the held block comes back at once after a move
        /// (a block-held quick attack, whirl, pierce …) or a hit reaction while the button stays down. G3 also needs its
        /// parade flag (raised during this press, cleared by ResetAll); by the owner's rule the held button is enough
        /// (<see cref="BlockIntent"/>, fix-203).
        /// </summary>
        MeleeCommand ReRaise(double time, in MeleeStance stance, string why)
        {
            if (!BlockIntent || InParade || _queued != CombatAction.None || BusyFor(stance) || Suspended(stance) ||
                !stance.CanParade || stance.SittingKnockedDown) return MeleeCommand.None;
            Say($"{why}: block still held -> parade raised{(_paradeWanted ? " again" : "")}");
            return Raise(time);
        }

        MeleeCommand Raise(double time)
        {
            _paradeRaisedAt = time;
            _paradeWanted = true; // _AI_Parade sets DAT_10118ab0
            return MeleeCommand.RaiseParade;
        }

        MeleeCommand TakeQueued(double time, in MeleeStance stance, string why)
        {
            var next = _queued;
            _queued = CombatAction.None;
            if (next == CombatAction.Parade)
            {
                if (_secondaryDown == null || !stance.CanParade || stance.SittingKnockedDown)
                {
                    Say($"{why}: queued Parade dropped (button up)");
                    return MeleeCommand.None;
                }
                Say($"{why}: queued Parade raised");
                return Raise(time);
            }
            Say($"{why}: queued {next} starts");
            // A queued hold move whose button is still held: its release combos from now on (G3 reads the state).
            if (_primaryDown != null && _holdFired) _holdStarted = true;
            return Start(next, time);
        }

        /// <summary>Forgets everything (weapon holstered, knocked out, a menu opened).</summary>
        public void Reset()
        {
            _primaryDown = _secondaryDown = _paradeRaisedAt = null;
            _holdFired = _holdStarted = _primaryGetUp = _secondaryGetUp = _blockLatched = _reacting = _paradeWanted = _paradeDropped = false;
            _current = _queued = CombatAction.None;
        }

        /// <summary>The tap's move, None when the actor cannot do it (fists have no quick attack; tools no whirl).</summary>
        CombatAction TapMove(in MeleeStance stance)
        {
            if (!BlockHeld) return CombatAction.Attack;
            if (stance.HeavyRightHand) return stance.RightHand == WeaponClass.Tool ? CombatAction.None : CombatAction.WhirlAttack;
            return stance.CanQuickAttack ? CombatAction.QuickAttack : CombatAction.None;
        }

        /// <summary>The hold's move, None when the actor cannot do it (fists: no pierce, hack or finishing move).</summary>
        CombatAction HoldMove(in MeleeStance stance)
        {
            if (!BlockHeld) return CombatAction.PowerAttack;
            if (stance.RightHand == WeaponClass.Fist) return CombatAction.None;
            if (stance.FinisherInReach) return CombatAction.FinishingAttack;
            return stance.RightHand == WeaponClass.OneHanded ? CombatAction.PierceAttack : CombatAction.HackAttack;
        }

        static bool IsComboState(CombatAction a) =>
            a == CombatAction.PowerAttack || a == CombatAction.PierceAttack || a == CombatAction.FinishingAttack || a == CombatAction.HackAttack;

        CombatAction ComboOnRelease(double stateTime, in MeleeStance stance)
        {
            switch (_current)
            {
                case CombatAction.PowerAttack:
                    if (stateTime <= _timing.PowerToAttack) return CombatAction.Attack;
                    if (stateTime <= _timing.PowerToSimpleWhirl) return stance.LeftOneHandedOrTorch ? CombatAction.SimpleWhirl : CombatAction.Attack;
                    return CombatAction.None;
                case CombatAction.PierceAttack:
                case CombatAction.FinishingAttack:
                    return stateTime <= _timing.PierceToQuick && stance.CanQuickAttack ? CombatAction.QuickAttack : CombatAction.None;
                case CombatAction.HackAttack:
                    return stateTime <= _timing.HackToWhirl ? CombatAction.WhirlAttack : CombatAction.None;
                default:
                    return CombatAction.None;
            }
        }

        /// <summary>Starts the move now, or queues it (one slot, the newest wins: G3's input queue) while another move runs.</summary>
        MeleeCommand Request(CombatAction move, double time, string why, in MeleeStance stance)
        {
            if (move == CombatAction.None)
            {
                Say($"{why} -> no move (not available with this weapon)");
                return MeleeCommand.None;
            }
            if (!BusyFor(stance))
            {
                Say($"{why} -> {move} starts");
                return Start(move, time);
            }
            string busy = Busy ? $"{_current} {(time - _currentStart) * 1000:0} ms in" : "drawing";
            Say(_queued != CombatAction.None
                ? $"{why} -> {move} queued, replaces {_queued} (busy: {busy})"
                : $"{why} -> {move} queued (busy: {busy})");
            _queued = move;
            return MeleeCommand.None;
        }

        void Say(string line) => Trace?.Invoke(line);

        MeleeCommand Start(CombatAction move, double time)
        {
            _current = move;
            _currentStart = time;
            _paradeRaisedAt = null;
            return MeleeCommand.Start(move);
        }
    }
}
