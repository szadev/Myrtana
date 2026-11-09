using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The combat AI of all NPCs: one <see cref="NpcCombatBrain"/> per NPC that has fought (created on demand), the
    /// shared <see cref="CombatCoordinator"/>, and the reactions to resolved hits: a victim fights back (ReactToDamage),
    /// a landed hit starts the attacker's hit cooldown, a defeated NPC leaves its fight. The presentation ticks the brains
    /// of the NPCs it shows, with their <see cref="CombatSituation"/>. The perception broadcasts (fight sound, defeat,
    /// murder, call for help; Docs/combat.md §7) come with the perception slice.
    /// </summary>
    public sealed class NpcCombatSystem : IDisposable, IActorStore
    {
        readonly CombatAiRules _ai;
        readonly CombatRules _combat;
        readonly RuleOptions _options;
        readonly IRandom _random;
        readonly Func<ActorId, NpcCombatDef> _definitions;
        readonly Func<ActorId, CombatLoadout> _loadouts;
        readonly Func<double> _now, _gameNow;
        readonly Dictionary<ActorId, NpcCombatBrain> _brains = new Dictionary<ActorId, NpcCombatBrain>();
        readonly Dictionary<ActorId, HashSet<string>> _lost = new Dictionary<ActorId, HashSet<string>>();
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly IEventBus _events;

        /// <param name="definitions">Combat facts of an NPC; null for an actor that cannot fight (unknown, the player).</param>
        /// <param name="now">Real seconds of play (the brains' timers).</param>
        /// <param name="gameNow">Game seconds (the G3 hit cooldowns).</param>
        public NpcCombatSystem(CombatAiRules ai, CombatRules combat, RuleOptions options, IRandom random,
                               Func<ActorId, NpcCombatDef> definitions, Func<ActorId, CombatLoadout> loadouts,
                               Func<double> now, Func<double> gameNow, IEventBus events)
        {
            _ai = ai ?? throw new ArgumentNullException(nameof(ai));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
            _loadouts = loadouts ?? (_ => CombatLoadout.Fists);
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _gameNow = gameNow ?? throw new ArgumentNullException(nameof(gameNow));
            Coordinator = new CombatCoordinator(ai.Token, options);
            Talk = new BattleTalk(now, () => gameNow() / 3600.0);
            Accidents = new AccidentalHits(this, now);
            _events = events;
            if (events != null)
            {
                _subscriptions.Add(events.Subscribe<HitResolved>(OnHitResolved));
                _subscriptions.Add(events.Subscribe<ActorDefeated>(e => Get(e.Victim)?.Disengage()));
            }
        }

        public CombatAiRules Rules => _ai;
        /// <summary>The port's live combat options handed to every brain (the session's <c>CombatService.Port</c>).</summary>
        public CombatPortOptions Port { get; set; } = new CombatPortOptions();
        public CombatCoordinator Coordinator { get; }
        /// <summary>The battle lines' say gate and the fight watchers (defeat comments).</summary>
        public BattleTalk Talk { get; }
        /// <summary>G3's Current/LastAttacker per NPC and the port's "Forgive accidental hits" verdicts (fix-162).</summary>
        public AccidentalHits Accidents { get; }
        /// <remarks>The concrete collection: a foreach over it needs no boxed enumerator (perception runs it every frame, perf-9).</remarks>
        public Dictionary<ActorId, NpcCombatBrain>.ValueCollection Brains => _brains.Values;

        /// <summary>Longest a finishing move keeps its victim down (s of play): a finisher that never lands lets it up again.</summary>
        public const double FinishingHoldSeconds = 10;

        /// <summary>
        /// An NPC's finishing move on <paramref name="victim"/> runs (ZS_Attack_FinishingAttack): the victim stays down
        /// until the blow (OnAI_FinishingAttack kills "if still down"; the player's 5 s knock-out must not run out while the
        /// finisher draws, walks up and raises) [interp]. Capped at <see cref="FinishingHoldSeconds"/>.
        /// </summary>
        public bool IsBeingFinished(ActorId victim)
        {
            double now = _now();
            foreach (var b in _brains.Values)
                if (b.State.Phase == CombatPhase.Finishing && b.State.MoveRunning && b.State.Target == victim &&
                    now - b.State.MoveStart <= FinishingHoldSeconds) return true;
            return false;
        }

        /// <summary>The spell an item casts, for the brains' magic (null = no magic; set by the session wiring).</summary>
        public Func<string, SpellDef> Spells
        {
            get => _spells;
            set
            {
                _spells = value;
                foreach (var b in _brains.Values) b.Spells = value;
            }
        }

        Func<string, SpellDef> _spells;

        /// <summary>
        /// The player is "in battle" (CP 1.75 helper used by PS_Interact, quick loot and saving: HUD_NotDuringBattle): an NPC
        /// in AI mode Combat whose current target is the player and whose combat state is Active, i.e. it holds the attack
        /// token (G3 FUN_10001880 over Entity::GetNPCs: CombatState 1, AIMode 3, GetCurrentTarget == player). There is
        /// no distance test: an attacker that runs after the player keeps him in battle until its chase runs out
        /// (GetFollowTime, then ZS_Attack_WaitForFlee gives up beyond 2000, <see cref="NpcCombatBrain"/>). The waiting
        /// fighters of a group (no token, combat state Passive) and an NPC that waits for the player to flee do not count.
        /// fix-65 (r32 object_anims R32-3): before, any fighter against the player counted. A fight engaged this frame
        /// (not ticked yet, so not at the coordinator) counts: G3 AssessTarget hands out the token at once.
        /// </summary>
        public bool IsPlayerInCombat
        {
            get
            {
                foreach (var b in _brains.Values)
                    if (InBattleWithPlayer(b.State.Phase, b.State.Target, b.HoldsToken, Coordinator.IsFighting(b.Self))) return true;
                return false;
            }
        }

        /// <summary>One NPC's part of <see cref="IsPlayerInCombat"/> (pure; see there).</summary>
        public static bool InBattleWithPlayer(CombatPhase phase, ActorId target, bool holdsToken, bool atCoordinator) =>
            target.IsPlayer && (phase == CombatPhase.Fight || phase == CombatPhase.Finishing) && (holdsToken || !atCoordinator);

        /// <summary>The brain of an NPC (created on first use); null for an actor that cannot fight.</summary>
        public NpcCombatBrain BrainOf(ActorId npc)
        {
            if (npc.IsEmpty || npc.IsPlayer) return null;
            if (_brains.TryGetValue(npc, out var brain)) return brain;
            var definition = _definitions(npc);
            if (definition == null) return null;
            brain = new NpcCombatBrain(npc, definition, LoadoutOf(npc), _ai, _combat, _options, _random, Coordinator) { Spells = _spells, Port = Port };
            Attach(brain);
            return brain;
        }

        /// <summary>The NPC's weapons: its equipment without the weapons taken from it.</summary>
        public CombatLoadout LoadoutOf(ActorId npc)
        {
            var loadout = _loadouts(npc) ?? CombatLoadout.Fists;
            return _lost.TryGetValue(npc, out var lost) ? loadout.Without(lost) : loadout;
        }

        /// <summary>Weapons taken from NPCs (ransacking), per NPC.</summary>
        public IEnumerable<KeyValuePair<ActorId, IReadOnlyCollection<string>>> LostItems =>
            _lost.Select(kv => new KeyValuePair<ActorId, IReadOnlyCollection<string>>(kv.Key, kv.Value));

        /// <summary>The NPC lost <paramref name="item"/> of its weaponry (taken while it was down, or dead).</summary>
        public bool HasLost(ActorId npc, string item) => _lost.TryGetValue(npc, out var lost) && lost.Contains(item ?? "");

        /// <summary>
        /// G3 RefreshWeaponry/AssureItems when a living NPC enters the processing range: the weapons taken from it are
        /// there again.
        /// </summary>
        public void RestoreWeaponry(ActorId npc)
        {
            if (!_lost.Remove(npc)) return;
            if (_brains.TryGetValue(npc, out var brain)) brain.Loadout = LoadoutOf(npc);
            WeaponryChanged?.Invoke(npc);
        }

        /// <summary>
        /// The NPC lost a weapon of its weaponry (<see cref="TakeItem"/>: looted, ransacked, abandoned, a loaded game's lost
        /// items) or got its weapons back (<see cref="RestoreWeaponry"/>): the presentation re-shows the weapons on its body
        /// (fix-149: a looted sword and shield stayed on the corpse).
        /// </summary>
        public event Action<ActorId> WeaponryChanged;

        /// <summary>
        /// Someone took <paramref name="item"/> from the NPC (it dropped the weapon when knocked out): the NPC fights
        /// without it from now on.
        /// </summary>
        public void TakeItem(ActorId npc, string item)
        {
            if (npc.IsEmpty || npc.IsPlayer || string.IsNullOrEmpty(item)) return;
            if (!_lost.TryGetValue(npc, out var lost)) _lost[npc] = lost = new HashSet<string>();
            if (!lost.Add(item)) return;
            if (_brains.TryGetValue(npc, out var brain)) brain.Loadout = LoadoutOf(npc);
            WeaponryChanged?.Invoke(npc);
        }

        /// <summary>The NPC's equipment changed (G3 Wear): a brain that exists fights with the new loadout.</summary>
        public void RefreshLoadout(ActorId npc)
        {
            if (_brains.TryGetValue(npc, out var brain)) brain.Loadout = LoadoutOf(npc);
        }

        /// <summary>The brain if the NPC has one already.</summary>
        public NpcCombatBrain Get(ActorId npc) => _brains.TryGetValue(npc, out var brain) ? brain : null;

        /// <summary>The NPC starts (or escalates) a fight against <paramref name="target"/>.</summary>
        public bool Engage(ActorId npc, ActorId target, AttackReason reason)
        {
            if (Paralysed(npc)) return false;
            var brain = BrainOf(npc);
            if (brain == null || target == npc) return false;
            // fix-142: a target the NPC just gave up as unreachable is left alone (orders still start a fight)
            // (a hit from it makes the NPC back off again: brain.Engage)
            if (reason < AttackReason.PlayerCommand && brain.IgnoresUnreachable(target, _now()))
            {
                if (reason == AttackReason.ReactToDamage) brain.Engage(target, reason, _now());
                return false;
            }
            // G3 AssessTarget: Panic toward the target or !CanAttack (deer, meatbugs …) -> ZS_Flee, never ZS_Attack (fix-46).
            if (FleesFrom(brain, target)) brain.Flee(target, _now());
            else brain.Engage(target, reason, _now());
            return true;
        }

        /// <summary>
        /// G3 AssessTarget: an NPC in Panic toward <paramref name="other"/> or one that cannot attack at all (CannotAttack
        /// species, ambient creatures) gets ZS_Flee instead of ZS_Attack, also when it was hit: prey never fights back
        /// (fix-46: a deer hit by a guide stood one brain tick in Fight, longer while frozen; r28 "Deer fights Xardas").
        /// </summary>
        bool FleesFrom(NpcCombatBrain brain, ActorId other) =>
            _ai.NeverFights(brain.Definition) || AttitudeOf?.Invoke(brain.Self, other) == Attitude.Panic;

        /// <summary>The NPC flees from <paramref name="from"/> (the Fear spell's ZS_Flee).</summary>
        public bool Flee(ActorId npc, ActorId from)
        {
            var brain = BrainOf(npc);
            if (brain == null || from == npc) return false;
            brain.Flee(from, _now());
            return true;
        }

        void Attach(NpcCombatBrain brain)
        {
            _brains[brain.Self] = brain;
            brain.Speech = Talk.Gate;
            brain.GaveUp = (target, reason) => _events?.Publish(new FightGivenUp(brain.Self, target, reason));
        }

        /// <summary>
        /// Held by a spell (magic sleep, mist; G3 AIMode 7): the NPC neither starts a fight nor fights back when hit (CP removed
        /// the sleep's wake-on-hit callback). Set by the session's spell effects (Docs/combat.md §12.5).
        /// </summary>
        public Func<ActorId, bool> Paralysed { get; set; } = _ => false;

        /// <summary>Ends every fight (e.g. on a teleport or a cutscene).</summary>
        public void DisengageAll()
        {
            foreach (var b in _brains.Values) b.Disengage();
        }

        /// <summary>
        /// The actor left the game (<see cref="ActorRemoval"/>): its fight ends, its brain, attack token and lost weapons go,
        /// and every NPC fighting it stops (the target is gone).
        /// </summary>
        public void Forget(ActorId actor)
        {
            if (_brains.TryGetValue(actor, out var brain))
            {
                brain.Disengage();
                _brains.Remove(actor);
            }
            Coordinator.Leave(actor);
            Talk.Forget(actor);
            Accidents.Forget(actor);
            _lost.Remove(actor);
            _complaints.Remove(actor);
            foreach (var b in _brains.Values.Where(b => b.IsFighting && b.State.Target == actor).ToList()) b.Disengage();
        }

        public bool Holds(ActorId actor) =>
            _brains.ContainsKey(actor) || _lost.ContainsKey(actor) || Coordinator.IsFighting(actor) || Accidents.Holds(actor) ||
            _brains.Values.Any(b => b.IsFighting && b.State.Target == actor);

        /// <summary>Restores a brain after loading.</summary>
        internal void Restore(ActorId npc, NpcCombatState state)
        {
            var definition = _definitions(npc);
            if (definition == null) return;
            var brain = new NpcCombatBrain(npc, definition, LoadoutOf(npc), _ai, _combat, _options, _random, Coordinator, state) { Spells = _spells, Port = Port };
            Attach(brain);
            if (brain.IsAttacking) Coordinator.Restore(npc, state.Target, active: false);
        }

        internal void Clear()
        {
            foreach (var b in _brains.Values) Coordinator.Leave(b.Self);
            _brains.Clear();
            var had = WeaponryChanged != null && _lost.Count > 0 ? _lost.Keys.ToList() : null;
            _lost.Clear();
            _complaints.Clear();
            Accidents.Clear();
            if (had != null) foreach (var npc in had) WeaponryChanged?.Invoke(npc);
        }

        void OnHitResolved(HitResolved e)
        {
            if (e.Outcome.Result == HitResult.Ignored) return;
            double gameNow = _gameNow();
            Get(e.Attacker)?.OnHitLanded(gameNow);
            var victim = BrainOf(e.Victim);
            if (victim == null) return;
            victim.OnHitTaken(gameNow);
            // A reaction task ends the victim's running move, a raised parade included (audit #3).
            if (NpcCombatBrain.Interrupts(e.Outcome.Reaction)) victim.OnInterrupted(_now());
            // The defeat (published before this event) ended the victim's fight; a knocked-out body does not fight back.
            if (e.Outcome.Killed || e.Outcome.KnockedOut || Paralysed(e.Victim)) return;
            if (e.Attacker.IsEmpty || e.Attacker == e.Victim) return;
            double now = _now();
            // fix-162 [owner]: the hero's stray hit on an NPC fighting his enemy (or on his party) is forgiven: a complaint
            // line, no fight back.
            if (Accidents.IsForgiven(e))
            {
                Complain(victim);
                return;
            }
            if (victim.IsFighting)
            {
                // G3 AssessAgressor in AI mode Combat: the hitter takes the target's place only on its second hit in a row or
                // when the victim waits without the attack token (fix-162; before, a fighting victim never turned).
                if (victim.IsAttacking && AccidentalHitRules.SwitchesToHitter(victim.State.Target == e.Attacker, e.Attacker.IsPlayer,
                        victim.Definition.FriendType, PartyBond?.Invoke(e.Victim, e.Attacker) == true,
                        Accidents.LandedTheHitBefore(e), victim.HoldsToken))
                    victim.Engage(e.Attacker, AttackReason.ReactToDamage, now);
                return;
            }
            bool complaining = _complaints.TryGetValue(e.Victim, out var since) && now - since < ComplainSeconds;
            var attitude = AttitudeOf?.Invoke(e.Victim, e.Attacker) ?? Attitude.Neutral;
            bool bond = PartyBond?.Invoke(e.Victim, e.Attacker) == true;
            if (ReactionToHit(attitude, complaining, bond) == HitResponse.Complain)
            {
                if (complaining) return; // a party bond forgives the further hits of the same complaint
                _complaints[e.Victim] = now;
                _events?.Publish(new NpcReacted(e.Victim, Reaction.Of(ReactionKind.Observe, e.Attacker, AttackReason.ReactToDamage, ComplainLine), null));
                return;
            }
            _complaints.Remove(e.Victim);
            if (FleesFrom(victim, e.Attacker)) victim.Flee(e.Attacker, now);
            else victim.Engage(e.Attacker, AttackReason.ReactToDamage, now);
        }

        /// <summary>The say gate's kind of ZS_ReactToDamage's line (kind 10, one per game minute).</summary>
        public const string ComplainKind = "ReactToDamage";

        /// <summary>A forgiven hit: "WhatAreYouDoing" through the global say gate, the fight goes on (fix-162).</summary>
        void Complain(NpcCombatBrain victim)
        {
            if (!victim.Definition.Humanoid || !Talk.Gate.TrySay(ComplainKind)) return;
            _events?.Publish(new CombatShout(victim.Self, ComplainLine));
        }

        /// <summary>How a victim that is not fighting answers a hit (G3 AssessAgressor).</summary>
        public enum HitResponse { FightBack, Complain }

        /// <summary>ZS_ReactToDamage ends after 10 s.</summary>
        public const double ComplainSeconds = 10;

        /// <summary>The SVM line of ZS_ReactToDamage.</summary>
        public const string ComplainLine = "WhatAreYouDoing";

        /// <summary>
        /// The victim's attitude toward its attacker (G3 GetAttitude in AssessAgressor); set by the session wiring.
        /// Null = Neutral (every victim fights back).
        /// </summary>
        public Func<ActorId, ActorId, Attitude> AttitudeOf { get; set; }

        /// <summary>
        /// The victim and the attacker are in one party: one leads the other (the hero's companion, or the guide leading the
        /// hero); set by the session wiring.
        /// </summary>
        public Func<ActorId, ActorId, bool> PartyBond { get; set; }

        /// <summary>
        /// G3 AssessAgressor for a victim not in combat: a victim Friendly to its attacker complains instead of fighting
        /// back (task ZS_ReactToDamage: "WhatAreYouDoing", watches the offender for 10 s); a second hit while it still
        /// complains (AIMode Reaction) makes it fight (AssessTarget, ReactToDamage). A party bond never turns into a
        /// fight: the guide leading the hero or his companion forgives an accidental hit (in G3 such a fight ends at once
        /// when the hero is knocked down, and an escort quest must not stall on it) [interp].
        /// </summary>
        public static HitResponse ReactionToHit(Attitude victimToAttacker, bool complaining, bool partyBond)
        {
            if (victimToAttacker != Attitude.Friendly) return HitResponse.FightBack;
            return partyBond || !complaining ? HitResponse.Complain : HitResponse.FightBack;
        }

        readonly Dictionary<ActorId, double> _complaints = new Dictionary<ActorId, double>();

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
