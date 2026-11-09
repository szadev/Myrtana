using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>The maxima and identity of an NPC's vitals (from its definition and level, see <see cref="CombatStats"/>).</summary>
    public sealed class VitalsProfile
    {
        public VitalsProfile(int hitPointsMax, int staminaMax, int manaMax, int level, bool ambientCreature, string species,
                             bool playerParty = false)
        {
            HitPointsMax = hitPointsMax;
            StaminaMax = staminaMax;
            ManaMax = manaMax;
            Level = level;
            AmbientCreature = ambientCreature;
            Species = species;
            PlayerParty = playerParty;
        }

        public int HitPointsMax { get; }
        public int StaminaMax { get; }
        public int ManaMax { get; }
        /// <summary>The Level property (reported with <see cref="ActorDefeated"/> for kill XP).</summary>
        public int Level { get; }
        public bool AmbientCreature { get; }
        public string Species { get; }
        /// <summary>A companion of the player: stays down as briefly as the player.</summary>
        public bool PlayerParty { get; }
    }

    /// <summary>The player was knocked out or killed (killed = game over).</summary>
    public readonly struct PlayerDefeated : IGameEvent
    {
        public PlayerDefeated(bool killed) => Killed = killed;
        public bool Killed { get; }
    }

    /// <summary>A knocked-out actor (NPC or player) got up again.</summary>
    public readonly struct ActorGotUp : IGameEvent
    {
        public ActorGotUp(ActorId actor) => Actor = actor;
        public ActorId Actor { get; }
    }

    /// <summary>A hit was resolved and applied (for the presentation: reaction animation, sounds, HUD).</summary>
    public readonly struct HitResolved : IGameEvent
    {
        public HitResolved(ActorId attacker, ActorId victim, HitOutcome outcome)
        {
            Attacker = attacker;
            Victim = victim;
            Outcome = outcome;
        }

        public ActorId Attacker { get; }
        public ActorId Victim { get; }
        public HitOutcome Outcome { get; }
    }

    /// <summary>
    /// Hit points, stamina and mana of every actor, and whether it is alive, knocked out (down) or dead. The player's
    /// values are the hero's HP/SP/MP attributes; an NPC's values are kept here only after something changed them, so an
    /// untouched NPC is alive at full strength. Knock-outs and kills publish <see cref="ActorDefeated"/> (NPCs) or
    /// <see cref="PlayerDefeated"/>. Answers the story's <see cref="IActorVitals"/> questions.
    /// </summary>
    public sealed class ActorVitals : IActorVitals, IActorStore
    {
        public const string HitPointsId = "HP", StaminaId = "SP", ManaId = "MP";

        internal sealed class Entry
        {
            public int HitPoints, Stamina, Mana;
            public VitalState State;
            public float DownSeconds;
            public float RegenCarry;
            public bool ArenaLoser;
            /// <summary>G3 DefeatedByPlayer: knocked out or killed by the player or the party (info status "Defeated").</summary>
            public bool DefeatedByPlayer;
            /// <summary>
            /// The player's own hit (melee, missile or spell) took HP from this NPC at least once while the entry lived
            /// [owner, fix-93]: another NPC's kill of it then still pays the hero the kill XP (<see cref="KillXpAwarder"/>).
            /// </summary>
            public bool HurtByPlayer;
        }

        readonly StatBlock _hero;
        readonly Func<ActorId, VitalsProfile> _profiles;
        readonly CombatRules _rules;
        readonly IEventBus _events;
        readonly Dictionary<ActorId, Entry> _npcs = new Dictionary<ActorId, Entry>();
        readonly Entry _player = new Entry();

        /// <param name="hero">The hero's attributes (HP, SP, MP).</param>
        /// <param name="profiles">Maxima of an NPC; null for an actor the content does not know.</param>
        public ActorVitals(StatBlock hero, Func<ActorId, VitalsProfile> profiles, CombatRules rules, IEventBus events)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _events = events ?? throw new ArgumentNullException(nameof(events));
        }

        /// <summary>Keeps a knocked-out actor down while true (G3: while the player has its loot or dialog page open).</summary>
        public Func<ActorId, bool> HoldDown { get; set; } = _ => false;

        /// <summary>
        /// The player takes no harm while true (the dev god mode, G3's GodMode gate): hits, falls and other losses leave his
        /// HP alone and he is never knocked out or killed. A damage filter only: it never changes the HP attribute or its
        /// maximum [fix-132: a dev god that wrote 99999/99999 into the HP stat left the hero invulnerable for good].
        /// </summary>
        public Func<bool> PlayerInvulnerable { get; set; }

        /// <summary>The HP an invulnerable player's combat snapshot reports, so no hit counts as lethal (never stored).</summary>
        public const int InvulnerableHitPoints = 99999;

        /// <summary>Whether <paramref name="actor"/> is the player under <see cref="PlayerInvulnerable"/>.</summary>
        public bool IsInvulnerable(ActorId actor) => actor.IsPlayer && PlayerInvulnerable != null && PlayerInvulnerable();

        /// <summary>Set while <see cref="ApplyHit"/> or <see cref="Finish"/> defeats the victim: the defeat has a killer.</summary>
        bool _byKiller;
        /// <summary>Keeps a knocked-out actor down while an NPC's finishing move on it runs (set by the session wiring; null = none).</summary>
        public Func<ActorId, bool> FinishingHold { get; set; }

        internal IEnumerable<KeyValuePair<ActorId, Entry>> Npcs => _npcs;
        internal Entry Player => _player;

        // ----------------------------------------------------------------------------------------------- queries

        public int HitPoints(ActorId actor) => actor.IsPlayer ? _hero.Value(HitPointsId) : Npc(actor)?.HitPoints ?? 0;
        public int Stamina(ActorId actor) => actor.IsPlayer ? _hero.Value(StaminaId) : Npc(actor)?.Stamina ?? 0;
        public int Mana(ActorId actor) => actor.IsPlayer ? _hero.Value(ManaId) : Npc(actor)?.Mana ?? 0;

        public int HitPointsMax(ActorId actor) => actor.IsPlayer ? _hero.Maximum(HitPointsId) : _profiles(actor)?.HitPointsMax ?? 0;
        public int StaminaMax(ActorId actor) => actor.IsPlayer ? _hero.Maximum(StaminaId) : _profiles(actor)?.StaminaMax ?? 0;
        public int ManaMax(ActorId actor) => actor.IsPlayer ? _hero.Maximum(ManaId) : _profiles(actor)?.ManaMax ?? 0;

        public VitalState StateOf(ActorId actor) => actor.IsPlayer ? _player.State : _npcs.TryGetValue(actor, out var e) ? e.State : VitalState.Alive;

        public bool IsDown(ActorId actor) => StateOf(actor) == VitalState.Down;
        public bool IsDead(ActorId actor) => StateOf(actor) == VitalState.Dead;

        /// <summary>Real seconds an actor still stays down (0 when it is not down).</summary>
        public float DownSecondsLeft(ActorId actor) =>
            actor.IsPlayer ? _player.DownSeconds : _npcs.TryGetValue(actor, out var e) ? e.DownSeconds : 0f;

        /// <summary>G3 CondSecondaryNPCStatus: Alive = not dead, Unharmed = alive and never defeated by the player,
        /// Defeated = defeated by the player at some time, Dead = dead.</summary>
        public bool Is(ActorId actor, ActorCondition condition)
        {
            var e = actor.IsPlayer ? _player : _npcs.TryGetValue(actor, out var found) ? found : null;
            bool dead = e != null && e.State == VitalState.Dead;
            bool defeated = e != null && e.DefeatedByPlayer;
            switch (condition)
            {
                case ActorCondition.Alive: return !dead;
                case ActorCondition.Unharmed: return !dead && !defeated;
                case ActorCondition.Defeated: return defeated;
                case ActorCondition.Dead: return dead;
                default: return false;
            }
        }

        // ------------------------------------------------------------------------------------------------ changes

        /// <summary>
        /// Applies a resolved hit: block costs, damage, and the knock-out or death it causes.
        /// </summary>
        /// <param name="byPlayerParty">The attacker is the player or a companion the player leads.</param>
        public void ApplyHit(HitContext hit, HitOutcome outcome, bool byPlayerParty)
        {
            if (outcome.Result == HitResult.Ignored) return;
            var victim = hit.Victim.Id;
            if (IsInvulnerable(victim))
            {
                // no HP, stamina or mana lost, no knock-out; the reaction still plays (the views get the hit)
                _events.Publish(new HitResolved(hit.Attacker.Id, victim, outcome));
                return;
            }
            if (outcome.StaminaCost != 0) AddStamina(victim, -outcome.StaminaCost);
            if (outcome.ManaCost != 0) AddMana(victim, -outcome.ManaCost);
            if (outcome.Damage != 0) AddHitPoints(victim, -outcome.Damage);
            if (outcome.Damage > 0 && hit.Attacker != null && hit.Attacker.Id.IsPlayer && !victim.IsPlayer && EntryFor(victim) is Entry hurt)
                hurt.HurtByPlayer = true;
            _byKiller = true;
            try
            {
                if (outcome.Killed) Kill(victim, byPlayerParty);
                else if (outcome.KnockedOut) KnockOut(victim, byPlayerParty, hit.Arena);
            }
            finally
            {
                _byKiller = false;
            }
            _events.Publish(new HitResolved(hit.Attacker.Id, victim, outcome));
        }

        /// <summary>
        /// The finishing blow (OnAI_FinishingAttack): kills a downed actor and reports it as a killing hit of
        /// <paramref name="attacker"/>, so crime, perception and the views treat it like any kill (G3 SendPerceptionMurder).
        /// </summary>
        internal void Finish(ActorId attacker, ActorId victim, bool byPlayerParty)
        {
            int hp = HitPoints(victim);
            _byKiller = true;
            try
            {
                Kill(victim, byPlayerParty);
            }
            finally
            {
                _byKiller = false;
            }
            var outcome = new HitOutcome { Result = HitResult.Hit, Damage = hp, Lethal = true, Reaction = HitReaction.Dead };
            _events.Publish(new HitResolved(attacker, victim, outcome));
        }

        public void AddHitPoints(ActorId actor, int delta) => Change(actor, Vital.HitPoints, delta);
        public void AddStamina(ActorId actor, int delta) => Change(actor, Vital.Stamina, delta);
        public void AddMana(ActorId actor, int delta) => Change(actor, Vital.Mana, delta);

        /// <summary>Knocks an actor out (ZS_Unconscious family). The player's party loser gets up after 5 s, arena losers after 20 s, others 30 s.</summary>
        public void KnockOut(ActorId actor, bool byPlayerParty, bool arena = false)
        {
            if (IsInvulnerable(actor)) return;
            var e = EntryFor(actor);
            if (e == null || e.State == VitalState.Dead) return;
            var down = _rules.DownState;
            bool party = actor.IsPlayer || (_profiles(actor)?.PlayerParty ?? false);
            e.State = VitalState.Down;
            e.ArenaLoser = arena && !party;
            e.DownSeconds = party ? down.PlayerPartySeconds : arena ? down.ArenaLoserSeconds : down.OtherSeconds;
            e.RegenCarry = 0;
            _events.Publish(new CombatShout(actor, CombatLines.OfVictim(killed: false))); // ZS_PiercedKO … "DOWN"
            if (actor.IsPlayer)
            {
                _events.Publish(new PlayerDefeated(killed: false));
                return;
            }
            if (byPlayerParty) e.DefeatedByPlayer = true;
            Publish(actor, byPlayerParty, killed: false);
        }

        /// <summary>Kills an actor (ZS_RagDollDead). Killing the player is game over.</summary>
        public void Kill(ActorId actor, bool byPlayerParty)
        {
            if (IsInvulnerable(actor)) return;
            var e = EntryFor(actor);
            if (e == null || e.State == VitalState.Dead) return;
            e.State = VitalState.Dead;
            e.DownSeconds = 0;
            SetHitPoints(actor, e, 0);
            _events.Publish(new CombatShout(actor, CombatLines.OfVictim(killed: true))); // ZS_RagDollDead / ZS_PiercedDead: "DEAD"
            if (actor.IsPlayer)
            {
                _events.Publish(new PlayerDefeated(killed: true));
                return;
            }
            if (byPlayerParty) e.DefeatedByPlayer = true;
            Publish(actor, byPlayerParty, killed: true);
        }

        /// <summary>
        /// Makes an NPC a corpse without a fight (G3 ZS_Dead for an NPC placed with Rtn_Dead: already dead, lying pose):
        /// nobody killed it, so no cry, no defeat event (no XP, quest kill or crime). False when it is unknown or already dead.
        /// </summary>
        public bool LieDead(ActorId actor)
        {
            if (actor.IsPlayer) return false;
            var e = EntryFor(actor);
            if (e == null || e.State == VitalState.Dead) return false;
            e.State = VitalState.Dead;
            e.DownSeconds = 0;
            e.HitPoints = 0;
            return true;
        }

        /// <summary>
        /// Advances time: knocked-out actors regain HP and stamina every real second and get up when their time is over;
        /// NPC stamina regenerates (+10 per second). The player's own stamina tick is <see cref="StaminaRules.PlayerPerSecond"/>.
        /// </summary>
        public void Tick(float realSeconds)
        {
            if (realSeconds <= 0) return;
            TickDown(ActorId.Player, _player, realSeconds);
            List<ActorId> settled = null;
            foreach (var pair in _npcs)
            {
                var e = pair.Value;
                if (_profiles(pair.Key) == null)
                {
                    // The actor's definition is gone (removed without ActorRemoval): its entry is dropped, never ticked.
                    if (settled == null) settled = new List<ActorId>();
                    settled.Add(pair.Key);
                }
                else if (e.State == VitalState.Down) TickDown(pair.Key, e, realSeconds);
                else if (e.State == VitalState.Alive && TickRegen(pair.Key, e, realSeconds))
                {
                    if (settled == null) settled = new List<ActorId>();
                    settled.Add(pair.Key);
                }
            }
            // An NPC back at full strength and never defeated by the player needs no entry any more.
            if (settled != null) foreach (var id in settled) _npcs.Remove(id);
        }

        /// <summary>Restores an actor to full strength (sleeping in a bed, a full-heal effect).</summary>
        public void RestoreFully(ActorId actor)
        {
            if (actor.IsPlayer)
            {
                foreach (var id in new[] { HitPointsId, StaminaId, ManaId }) _hero.Apply(id, ModifierOp.SetValToMax, 0);
                return;
            }
            if (!_npcs.TryGetValue(actor, out var e) || e.State == VitalState.Dead) return;
            var p = _profiles(actor);
            if (e.DefeatedByPlayer && p != null) Fill(e, p);
            else _npcs.Remove(actor);
        }

        /// <summary>
        /// G3 RefreshHitPoints (OnEnterProcessingRange, Script_Game 0x100ab840): a non-player NPC that is not dead gets its
        /// hit points and stamina back to the maximum; mana is left as it is and a knocked-out NPC stays down (its own timer
        /// gets it up). An entry left at full strength, not down and never defeated by the player is dropped (the player's
        /// earlier hurt goes with it, as with <see cref="RestoreFully"/>).
        /// </summary>
        public void RefreshHitPoints(ActorId actor)
        {
            if (actor.IsPlayer || !_npcs.TryGetValue(actor, out var e) || e.State == VitalState.Dead) return;
            var p = _profiles(actor);
            if (p == null) return;
            e.HitPoints = p.HitPointsMax;
            e.Stamina = p.StaminaMax;
            if (e.State == VitalState.Alive && !e.DefeatedByPlayer && e.Mana >= p.ManaMax) _npcs.Remove(actor);
        }

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entry goes.</summary>
        public void Forget(ActorId actor)
        {
            if (!actor.IsPlayer) _npcs.Remove(actor);
        }

        public bool Holds(ActorId actor) => _npcs.ContainsKey(actor);

        // ------------------------------------------------------------------------------------------------ internals

        enum Vital { HitPoints, Stamina, Mana }

        void Change(ActorId actor, Vital vital, int delta)
        {
            if (delta == 0) return;
            if (actor.IsPlayer)
            {
                if (vital == Vital.HitPoints && delta < 0 && IsInvulnerable(actor)) return;
                _hero.Apply(vital == Vital.HitPoints ? HitPointsId : vital == Vital.Stamina ? StaminaId : ManaId, ModifierOp.AddToVal, delta);
                return;
            }
            var e = EntryFor(actor);
            var p = _profiles(actor);
            if (e == null || p == null) return; // unknown or removed actor
            switch (vital)
            {
                case Vital.HitPoints: e.HitPoints = Clamp(e.HitPoints + delta, p.HitPointsMax); break;
                case Vital.Stamina: e.Stamina = Clamp(e.Stamina + delta, p.StaminaMax); break;
                default: e.Mana = Clamp(e.Mana + delta, p.ManaMax); break;
            }
        }

        static int Clamp(int value, int max) => value < 0 ? 0 : value > max ? max : value;

        void SetHitPoints(ActorId actor, Entry e, int value)
        {
            if (actor.IsPlayer) _hero.Apply(HitPointsId, ModifierOp.SetVal, value);
            else e.HitPoints = value;
        }

        void TickDown(ActorId actor, Entry e, float realSeconds)
        {
            if (e.State != VitalState.Down) return;
            var down = _rules.DownState;
            int perSecond = e.ArenaLoser ? down.ArenaRegenPerSecond : down.RegenPerSecond;
            e.RegenCarry += realSeconds;
            int whole = (int)e.RegenCarry;
            if (whole > 0)
            {
                e.RegenCarry -= whole;
                AddHitPoints(actor, perSecond * whole);
                AddStamina(actor, perSecond * whole);
            }
            e.DownSeconds = Math.Max(0f, e.DownSeconds - realSeconds);
            if (e.DownSeconds > 0 || HoldDown(actor) || (FinishingHold != null && FinishingHold(actor))) return;
            e.DownSeconds = 0;
            e.RegenCarry = 0;
            e.ArenaLoser = false;
            e.State = VitalState.Alive;
            _events.Publish(new ActorGotUp(actor));
        }

        /// <summary>
        /// Ends a knock-out at once (dev console <c>getup</c>, fix-39): the actor is Alive again and <see cref="ActorGotUp"/>
        /// is published as when its down time runs out. False when it is not down.
        /// </summary>
        public bool WakeUp(ActorId actor)
        {
            var e = actor.IsPlayer ? _player : Npc(actor);
            if (e == null || e.State != VitalState.Down) return false;
            e.DownSeconds = 0;
            e.RegenCarry = 0;
            e.ArenaLoser = false;
            e.State = VitalState.Alive;
            _events.Publish(new ActorGotUp(actor));
            return true;
        }

        /// <summary>NPC stamina +10 per second; true when the NPC is back at full strength and can be forgotten.</summary>
        bool TickRegen(ActorId actor, Entry e, float realSeconds)
        {
            e.RegenCarry += realSeconds;
            int whole = (int)e.RegenCarry;
            if (whole > 0)
            {
                e.RegenCarry -= whole;
                AddStamina(actor, StaminaRules.NpcRegenPerSecond * whole);
            }
            var p = _profiles(actor);
            return p != null && !e.DefeatedByPlayer && !e.HurtByPlayer && e.HitPoints >= p.HitPointsMax && e.Stamina >= p.StaminaMax && e.Mana >= p.ManaMax;
        }

        Entry Npc(ActorId actor)
        {
            if (_npcs.TryGetValue(actor, out var e)) return e;
            var p = _profiles(actor);
            return p == null ? null : Fill(new Entry(), p);
        }

        /// <summary>The player has hurt this NPC (<see cref="Entry.HurtByPlayer"/>).</summary>
        public bool WasHurtByPlayer(ActorId actor) => !actor.IsPlayer && _npcs.TryGetValue(actor, out var e) && e.HurtByPlayer;

        /// <summary>The player's entry, or the NPC's entry (created at full strength on first change); null if unknown.</summary>
        Entry EntryFor(ActorId actor)
        {
            if (actor.IsPlayer) return _player;
            if (_npcs.TryGetValue(actor, out var e)) return e;
            var p = _profiles(actor);
            if (p == null) return null;
            e = Fill(new Entry(), p);
            _npcs.Add(actor, e);
            return e;
        }

        static Entry Fill(Entry e, VitalsProfile p)
        {
            e.HitPoints = p.HitPointsMax;
            e.Stamina = p.StaminaMax;
            e.Mana = p.ManaMax;
            return e;
        }

        void Publish(ActorId actor, bool byPlayerParty, bool killed)
        {
            var p = _profiles(actor);
            // [owner, fix-93] only a defeat with a killer (a hit or a finishing blow) passes the hero's earlier hurt on; a death
            // by burns or poison has none and pays nothing, as in G3 (ZS_RagDollDead without a killer).
            bool hurt = _byKiller && _npcs.TryGetValue(actor, out var e) && e.HurtByPlayer;
            _events.Publish(new ActorDefeated(actor, byPlayerParty, killed, p?.Level ?? 0, p?.AmbientCreature ?? false, p?.Species, hurt));
        }

        // ------------------------------------------------------------------------------------------------ save support

        internal void Clear()
        {
            _npcs.Clear();
            _player.State = VitalState.Alive;
            _player.DownSeconds = 0;
        }

        internal void Restore(ActorId actor, Entry entry)
        {
            if (actor.IsPlayer)
            {
                _player.State = entry.State;
                _player.DownSeconds = entry.DownSeconds;
                _player.ArenaLoser = entry.ArenaLoser;
                return;
            }
            _npcs[actor] = entry;
        }
    }
}
