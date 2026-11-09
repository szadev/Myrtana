using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Status conditions a hit or a spell gives (the G3 status bits used by combat and magic).</summary>
    [Flags]
    public enum ActorStatus { None = 0, Burning = 1, Frozen = 2, Poisoned = 4, Diseased = 8 }

    /// <summary>A status condition started or ended on an actor.</summary>
    public readonly struct StatusChanged : IGameEvent
    {
        public StatusChanged(ActorId actor, ActorStatus status, bool active)
        {
            Actor = actor;
            Status = status;
            Active = active;
        }

        public ActorId Actor { get; }
        public ActorStatus Status { get; }
        public bool Active { get; }
    }

    /// <summary>
    /// Burning and freezing (status_tick.json, Docs/combat.md §12): a fire hit sets the victim on fire for 10 real seconds,
    /// which takes 5 HP per second; an ice hit freezes it for max(5, damage/20) seconds (the resolver's
    /// <see cref="HitOutcome.FreezeSeconds"/>), during which it stands still and further ice does no damage. Damage over
    /// time that drops a creature below 1 HP kills it; a humanoid NPC dies when killable and Hostile/Predator to the player,
    /// else it is knocked out (CP OnTick, fix-209). The player dies of it.
    /// <para>
    /// Poison and disease (combat-6, CP 1.75 OnTick / CanBePoisoned / CanBeDiseased): poison takes 5 HP a second
    /// (stacking with burning) for 30 s on the player, max(10, 60 - (LevelMax + Level)/2) s on an NPC
    /// (<see cref="PoisonSeconds"/>), and kills like burning. A disease has no tick and no end until cured: it cuts the
    /// player's stamina regeneration to 1 a second (StaminaRules) and slows an NPC's combat motions x0.75
    /// (MotionSpeedRules). Healing spells and the cure spells clear them; death and a knock-out clear poison.
    /// </para>
    /// </summary>
    public sealed class StatusEffects : IActorStore
    {
        sealed class Entry
        {
            public float BurnLeft, FrozenLeft, BurnCarry, PoisonLeft, PoisonCarry;
            public ActorStatus Other;
        }

        readonly Dictionary<ActorId, Entry> _actors = new Dictionary<ActorId, Entry>();
        readonly ActorVitals _vitals;
        readonly BurnRules _burn;
        readonly Func<ActorId, bool> _humanoid;
        readonly IEventBus _events;
        readonly Func<ActorId, float> _poisonSeconds;
        readonly Func<ActorId, bool> _immune;

        /// <summary>OnTick: HP a poisoned actor loses each second.</summary>
        public const int PoisonHitPointsPerSecond = 5;
        /// <summary>How long the player stays poisoned (real seconds).</summary>
        public const float PlayerPoisonSeconds = 30f;

        /// <summary>How long an NPC stays poisoned: max(10, 60 - (LevelMax + Level) / 2) real seconds.</summary>
        public static float NpcPoisonSeconds(int level, int levelMax) => Math.Max(10, 60 - (levelMax + level) / 2);

        /// <param name="poisonSeconds">Poison duration of an actor (null: the player's 30 s for everyone).</param>
        /// <param name="immune">Actors poison and disease cannot touch (evil creatures); null: nobody.</param>
        public StatusEffects(ActorVitals vitals, BurnRules burn, Func<ActorId, bool> humanoid, IEventBus events,
                             Func<ActorId, float> poisonSeconds = null, Func<ActorId, bool> immune = null)
        {
            _poisonSeconds = poisonSeconds ?? (_ => PlayerPoisonSeconds);
            _immune = immune ?? (_ => false);
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            _burn = burn ?? new BurnRules(0, 0);
            _humanoid = humanoid ?? (_ => true);
            _events = events;
        }

        public bool Has(ActorId actor, ActorStatus status) => (Of(actor) & status) != 0;
        public bool IsBurning(ActorId actor) => Has(actor, ActorStatus.Burning);
        public bool IsFrozen(ActorId actor) => Has(actor, ActorStatus.Frozen);
        public bool IsPoisoned(ActorId actor) => Has(actor, ActorStatus.Poisoned);
        public float PoisonSecondsLeft(ActorId actor) => _actors.TryGetValue(actor, out var e) ? e.PoisonLeft : 0f;
        /// <summary>The actor's poison duration (<see cref="NpcPoisonSeconds"/> / <see cref="PlayerPoisonSeconds"/>).</summary>
        public float PoisonSeconds(ActorId actor) => _poisonSeconds(actor);
        public float FrozenSecondsLeft(ActorId actor) => _actors.TryGetValue(actor, out var e) ? e.FrozenLeft : 0f;

        public ActorStatus Of(ActorId actor)
        {
            if (!_actors.TryGetValue(actor, out var e)) return ActorStatus.None;
            var s = e.Other;
            if (e.BurnLeft > 0) s |= ActorStatus.Burning;
            if (e.FrozenLeft > 0) s |= ActorStatus.Frozen;
            if (e.PoisonLeft > 0) s |= ActorStatus.Poisoned;
            return s;
        }

        /// <summary>A resolved hit: sets the victim on fire or freezes it, as the outcome says.</summary>
        public void OnHit(ActorId victim, HitOutcome outcome)
        {
            if (outcome == null || outcome.Result != HitResult.Hit || _vitals.StateOf(victim) != VitalState.Alive) return;
            if (outcome.Burns && _burn.Seconds > 0) Burn(victim, _burn.Seconds);
            if (outcome.FreezeSeconds > 0) Freeze(victim, outcome.FreezeSeconds);
            if (outcome.Poisons) Poison(victim);
            if (outcome.Diseases) Set(victim, ActorStatus.Diseased);
        }

        /// <summary>Poisons the actor for its duration (a new dose restarts the timer); refused for immune actors.</summary>
        public bool Poison(ActorId actor) => Poison(actor, _poisonSeconds(actor));

        public bool Poison(ActorId actor, float seconds)
        {
            if (seconds <= 0 || _immune(actor) || _vitals.StateOf(actor) != VitalState.Alive) return false;
            var e = EntryFor(actor);
            bool was = e.PoisonLeft > 0;
            e.PoisonLeft = Math.Max(e.PoisonLeft, seconds);
            if (!was) Publish(actor, ActorStatus.Poisoned, true);
            return true;
        }

        /// <summary>MagicDisease: the target catches the disease and loses all its stamina at once; refused for immune actors.</summary>
        public bool Infect(ActorId actor)
        {
            if (_immune(actor) || _vitals.StateOf(actor) != VitalState.Alive) return false;
            Set(actor, ActorStatus.Diseased);
            _vitals.AddStamina(actor, -_vitals.Stamina(actor));
            return true;
        }

        public void Burn(ActorId actor, float seconds)
        {
            var e = EntryFor(actor);
            bool was = e.BurnLeft > 0;
            e.BurnLeft = Math.Max(e.BurnLeft, seconds);
            if (!was) Publish(actor, ActorStatus.Burning, true);
        }

        public void Freeze(ActorId actor, float seconds)
        {
            var e = EntryFor(actor);
            bool was = e.FrozenLeft > 0;
            e.FrozenLeft = Math.Max(e.FrozenLeft, seconds);
            if (!was) Publish(actor, ActorStatus.Frozen, true);
        }

        /// <summary>Sets a lasting condition (diseased; poisoned starts the poison timer); burning and freezing go through <see cref="Burn"/> and <see cref="Freeze"/>.</summary>
        public void Set(ActorId actor, ActorStatus status)
        {
            if ((status & ActorStatus.Poisoned) != 0) Poison(actor);
            if (_immune(actor)) return;
            var e = EntryFor(actor);
            var added = status & ~(ActorStatus.Burning | ActorStatus.Frozen | ActorStatus.Poisoned) & ~e.Other;
            e.Other |= added;
            if (added != ActorStatus.None) Publish(actor, added, true);
        }

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its conditions go without events.</summary>
        public void Forget(ActorId actor) => _actors.Remove(actor);

        public bool Holds(ActorId actor) => _actors.ContainsKey(actor);

        /// <summary>Clears conditions (healing and cure spells; death).</summary>
        public void Clear(ActorId actor, ActorStatus statuses)
        {
            if (!_actors.TryGetValue(actor, out var e)) return;
            if ((statuses & ActorStatus.Burning) != 0 && e.BurnLeft > 0) { e.BurnLeft = 0; Publish(actor, ActorStatus.Burning, false); }
            if ((statuses & ActorStatus.Frozen) != 0 && e.FrozenLeft > 0) { e.FrozenLeft = 0; Publish(actor, ActorStatus.Frozen, false); }
            if ((statuses & ActorStatus.Poisoned) != 0 && e.PoisonLeft > 0) { e.PoisonLeft = 0; e.PoisonCarry = 0; Publish(actor, ActorStatus.Poisoned, false); }
            var removed = e.Other & statuses;
            e.Other &= ~statuses;
            if (removed != ActorStatus.None) Publish(actor, removed, false);
            Prune(actor, e);
        }

        /// <summary>Real time passes: burning takes its HP every second, the timers run down.</summary>
        public void Tick(float realSeconds)
        {
            if (realSeconds <= 0 || _actors.Count == 0) return;
            foreach (var actor in new List<ActorId>(_actors.Keys))
            {
                var e = _actors[actor];
                if (_vitals.StateOf(actor) != VitalState.Alive)
                {
                    Clear(actor, ActorStatus.Burning | ActorStatus.Frozen | ActorStatus.Poisoned);
                    continue;
                }
                if (e.FrozenLeft > 0 && (e.FrozenLeft -= realSeconds) <= 0)
                {
                    e.FrozenLeft = 0;
                    Publish(actor, ActorStatus.Frozen, false);
                }
                if (e.BurnLeft > 0) TickBurn(actor, e, realSeconds);
                if (e.PoisonLeft > 0 && _vitals.StateOf(actor) == VitalState.Alive) TickPoison(actor, e, realSeconds);
                if (_actors.ContainsKey(actor)) Prune(actor, e);
            }
        }

        void TickBurn(ActorId actor, Entry e, float dt)
        {
            float step = Math.Min(dt, e.BurnLeft);
            e.BurnLeft -= dt;
            e.BurnCarry += step;
            int seconds = (int)e.BurnCarry;
            e.BurnCarry -= seconds;
            if (seconds > 0)
            {
                _vitals.AddHitPoints(actor, -seconds * _burn.HitPointsPerSecond);
                if (_vitals.HitPoints(actor) < 1) DiesOfBurns(actor);
            }
            if (e.BurnLeft <= 0)
            {
                e.BurnLeft = 0;
                e.BurnCarry = 0;
                Publish(actor, ActorStatus.Burning, false);
            }
        }

        void TickPoison(ActorId actor, Entry e, float dt)
        {
            float step = Math.Min(dt, e.PoisonLeft);
            e.PoisonLeft -= dt;
            e.PoisonCarry += step;
            int seconds = (int)e.PoisonCarry;
            e.PoisonCarry -= seconds;
            if (seconds > 0)
            {
                _vitals.AddHitPoints(actor, -seconds * PoisonHitPointsPerSecond);
                if (_vitals.HitPoints(actor) < 1)
                {
                    DiesOfBurns(actor);
                    return;
                }
            }
            if (e.PoisonLeft <= 0)
            {
                e.PoisonLeft = 0;
                e.PoisonCarry = 0;
                Publish(actor, ActorStatus.Poisoned, false);
            }
        }

        /// <summary>
        /// fix-209, CP 1.75 OnTick (the GetHitPoints &lt; 1 branch of burning/poison): a humanoid NPC brought below 1 HP by
        /// damage over time dies (ZS_RagDollDead) when it can be killed (CanBeKilled) and its attitude to the player is
        /// Hostile or Predator (GetAttitude 4..5); otherwise it is knocked out (ZS_Unconscious). Set by the session wiring;
        /// null = humanoid NPCs are always knocked out. A hostile orc set on fire by an arrow burns to death.
        /// </summary>
        public Func<ActorId, bool> HumanoidDiesOfDamageOverTime { get; set; }

        /// <summary>The OnTick rule of <see cref="HumanoidDiesOfDamageOverTime"/>: killable and Hostile or Predator to the player.</summary>
        public static bool DiesOfDamageOverTime(bool canBeKilled, Attitude toPlayer) =>
            canBeKilled && (toPlayer == Attitude.Hostile || toPlayer == Attitude.Predator);

        /// <summary>Damage over time below 1 HP: creatures and the player die, humanoid NPCs die or are knocked out (<see cref="HumanoidDiesOfDamageOverTime"/>; burning and poison cleared).</summary>
        void DiesOfBurns(ActorId actor)
        {
            Clear(actor, ActorStatus.Burning | ActorStatus.Poisoned);
            if (actor.IsPlayer || !_humanoid(actor) || HumanoidDiesOfDamageOverTime?.Invoke(actor) == true) _vitals.Kill(actor, byPlayerParty: false);
            else _vitals.KnockOut(actor, byPlayerParty: false);
        }

        Entry EntryFor(ActorId actor)
        {
            if (!_actors.TryGetValue(actor, out var e)) _actors[actor] = e = new Entry();
            return e;
        }

        void Prune(ActorId actor, Entry e)
        {
            if (e.BurnLeft <= 0 && e.FrozenLeft <= 0 && e.PoisonLeft <= 0 && e.Other == ActorStatus.None) _actors.Remove(actor);
        }

        void Publish(ActorId actor, ActorStatus status, bool active) => _events?.Publish(new StatusChanged(actor, status, active));

        /// <summary>Writes every running status (sorted by actor, so equal states give equal bytes).</summary>
        internal void Write(ISaveWriter w)
        {
            var actors = new List<ActorId>(_actors.Keys);
            actors.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            w.WriteInt32(actors.Count);
            foreach (var id in actors)
            {
                var e = _actors[id];
                w.WriteString(id.Name);
                w.WriteDouble(e.BurnLeft);
                w.WriteDouble(e.BurnCarry);
                w.WriteDouble(e.FrozenLeft);
                w.WriteDouble(e.PoisonLeft);
                w.WriteDouble(e.PoisonCarry);
                w.WriteInt32((int)e.Other);
            }
        }

        /// <summary>Restores the statuses of the actors that exist (a loaded game; no events: the views read the state).</summary>
        internal void Read(ISaveReader r, Func<ActorId, bool> exists)
        {
            _actors.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var id = new ActorId(r.ReadString());
                var e = new Entry
                {
                    BurnLeft = (float)r.ReadDouble(), BurnCarry = (float)r.ReadDouble(), FrozenLeft = (float)r.ReadDouble(),
                    PoisonLeft = (float)r.ReadDouble(), PoisonCarry = (float)r.ReadDouble(), Other = (ActorStatus)r.ReadInt32(),
                };
                if (exists == null || exists(id)) _actors[id] = e;
            }
        }
    }

    /// <summary>
    /// Save section "magic.status": burning, frost, poison and disease of every actor (G3 keeps them as entity properties,
    /// so a poisoned or diseased hero is still poisoned or diseased after loading). Read after the vitals.
    /// </summary>
    public sealed class StatusEffectsSaveSection : ISaveParticipant
    {
        readonly StatusEffects _status;
        readonly Func<ActorId, bool> _exists;

        public StatusEffectsSaveSection(StatusEffects status, Func<ActorId, bool> exists)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _exists = exists;
        }

        public string Section => "magic.status";
        public int Version => 1;
        public void Write(ISaveWriter w) => _status.Write(w);
        public void Read(ISaveReader r, int version) => _status.Read(r, _exists);
    }
}
