using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>How a spell holds an NPC (G3 AIMode 7 magic sleep; ZS_Misted).</summary>
    public enum SpellHold { Asleep, Misted }

    /// <summary>An NPC (or the player) fell under a spell's hold, or woke from it (<see cref="On"/> false).</summary>
    public readonly struct ActorSpellbound : IGameEvent
    {
        public ActorSpellbound(ActorId actor, SpellHold hold, bool on)
        {
            Actor = actor;
            Hold = hold;
            On = on;
        }

        public ActorId Actor { get; }
        public SpellHold Hold { get; }
        public bool On { get; }
    }

    /// <summary>The kinds of spell objects that stay in the world for a while.</summary>
    public enum SpellObjectKind { Mist, TimeBubble }

    /// <summary>A mist or time bubble appeared (<see cref="On"/>) or ended; the presentation shows its effect there.</summary>
    public readonly struct SpellObjectChanged : IGameEvent
    {
        public SpellObjectChanged(SpellObjectKind kind, ActorId owner, WorldPose pose, float seconds, bool on)
        {
            Kind = kind;
            Owner = owner;
            Pose = pose;
            Seconds = seconds;
            On = on;
        }

        public SpellObjectKind Kind { get; }
        public ActorId Owner { get; }
        public WorldPose Pose { get; }
        public float Seconds { get; }
        public bool On { get; }
    }

    /// <summary>A spell object in the world: where, whose, and how long it lasts.</summary>
    public sealed class SpellArea
    {
        internal SpellArea(SpellObjectKind kind, ActorId owner, WorldPose pose, float seconds)
        {
            Kind = kind;
            Owner = owner;
            Pose = pose;
            Remaining = seconds;
        }

        public SpellObjectKind Kind { get; }
        public ActorId Owner { get; }
        public WorldPose Pose { get; }
        public float Remaining { get; internal set; }
        internal float TickIn;
    }

    /// <summary>
    /// The spells that act on NPCs and the world (Docs/combat.md §12.5; G3 CP 1.75 MagicSleep, MagicHypnosis,
    /// MagicWordOfControl, MagicTameAnimal, MagicCharm, MagicBloodlust, MagicCallAnimals, ZS_Mist, ZS_Timebubble,
    /// PS_Magic_FlameSword, MagicTelekinesis, MagicOpenLocks). Pure; the presentation shows the holds and objects.
    /// <list type="bullet">
    /// <item>Sleep: a target that can sleep (<see cref="SpellEffectRules.CanSleep"/>) stops, lies down and is held for 30 s
    /// (the player 5 s); a hit does not wake it (CP removed the wake callback). An NPC says "WhatWasThat" when it wakes.
    /// Hypnosis puts every calm NPC within 20 m of the caster to sleep (not fighting, talking, down, dead or in a party).</item>
    /// <item>Word of Control / Tame Animal: <see cref="ControlSpells"/>; the target joins the hero's party as Controlled
    /// (follows, defends him) until it is dismissed, another control or summon spell is cast, or the hero teleports. Only
    /// one group at a time (the previous Controlled members and summons are dismissed).</item>
    /// <item>Charm: an NPC not fighting forgets the hero's crimes and fights against it and says "WhatWasThat"; a boss attacks.</item>
    /// <item>Bloodlust: the victim attacks the nearest NPC within 10 m of it (the caster too); bosses and mages attack the caster.</item>
    /// <item>Call Animals: the 1/2/3 nearest world animals within 250 m that can fight attack the target.</item>
    /// <item>Mist: 20 s (+5), radius 10 m, one tick per second: NPCs inside stop fighting; those friendly to the caster stand
    /// (Misted) until they leave it, the others attack the caster. Misted NPCs neither perceive nor are perceived.</item>
    /// <item>Time bubble: 80–110 s (+50 %); everyone but the caster and his party within 10 m moves at half speed [interp].</item>
    /// <item>A teleport of the hero ends his mist and time bubble (CP MagicTeleport).</item>
    /// </list>
    /// </summary>
    public sealed class WorldSpells : IDisposable, IActorStore
    {
        const float Cm = 100f;

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            _asleep.Remove(actor);
            _misted.Remove(actor);
        }

        public bool Holds(ActorId actor) => _asleep.ContainsKey(actor) || _misted.Contains(actor);

        readonly ActorRegistry _actors;
        readonly NpcSimulation _npcs;
        readonly NpcCombatSystem _combat;
        readonly ActorVitals _vitals;
        readonly PlayerParty _party;
        readonly IRandom _random;
        readonly IEventBus _events;
        readonly IGameLog _log;
        readonly Dictionary<ActorId, float> _asleep = new Dictionary<ActorId, float>();
        readonly HashSet<ActorId> _misted = new HashSet<ActorId>();
        readonly List<ActorId> _scratch = new List<ActorId>();
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        public WorldSpells(ActorRegistry actors, NpcSimulation npcs, NpcCombatSystem combat, ActorVitals vitals, PlayerParty party,
                           IRandom random, IEventBus events, IGameLog log)
        {
            _actors = actors ?? throw new ArgumentNullException(nameof(actors));
            _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            _party = party;
            _random = random ?? new SeededRandom(1);
            _events = events;
            _log = log;
            if (events != null)
            {
                _subscriptions.Add(events.Subscribe<TeleportRequested>(_ => EndHeroObjects()));
                _subscriptions.Add(events.Subscribe<ActorDefeated>(e => Release(e.Victim)));
                _subscriptions.Add(events.Subscribe<CreatureSummoned>(e => { if (e.Summoner.IsPlayer) DismissControlled(); }));
            }
        }

        /// <summary>Where an actor stands (the player included); null = unknown.</summary>
        public Func<ActorId, WorldPose?> Positions { get; set; }
        /// <summary>The attitude of an NPC toward another actor (mist: friendly to the caster stands still).</summary>
        public Func<ActorId, ActorId, Attitude> Attitudes { get; set; }
        /// <summary>The crime ledger (Charm forgets the hero's crimes and fights).</summary>
        public CrimeLedger Ledger { get; set; }
        /// <summary>G3 IsGateGuard (Word of Control needs INT 200 for them).</summary>
        public Func<ActorId, bool> IsGateGuard { get; set; } = _ => false;
        /// <summary>Dismisses the player's summoned creatures (one group at a time).</summary>
        public Action<ActorId> DismissSummons { get; set; }

        public SpellArea Mist { get; private set; }
        public SpellArea TimeBubble { get; private set; }

        public bool IsAsleep(ActorId actor) => _asleep.ContainsKey(actor);
        public bool InMist(ActorId actor) => _misted.Contains(actor);
        /// <summary>Held by a spell (asleep or misted): no perception, no fighting back (G3 AIMode 7 / InMist).</summary>
        public bool Paralysed(ActorId actor) => _asleep.ContainsKey(actor) || _misted.Contains(actor);
        public IEnumerable<ActorId> Sleepers => _asleep.Keys;

        /// <summary>
        /// The time bubble's speed for an actor at <paramref name="pose"/>: half inside the bubble, except its caster and the
        /// caster's party; 1 elsewhere [interp].
        /// </summary>
        public float TimeScaleOf(ActorId actor, WorldPose pose)
        {
            var bubble = TimeBubble;
            if (bubble == null || actor == bubble.Owner || bubble.Owner.IsPlayer && _party != null && _party.IsLedByPlayer(actor)) return 1f;
            return pose.DistanceTo(bubble.Pose) <= SpellEffectRules.TimeBubbleRadius / Cm ? SpellEffectRules.TimeBubbleScale : 1f;
        }

        // ------------------------------------------------------------------------------------------- sleep

        /// <summary>MagicSleep: true when the target fell asleep (CanSleep); nothing is said otherwise.</summary>
        public bool Sleep(ActorId target)
        {
            if (target.IsEmpty || _vitals.StateOf(target) != VitalState.Alive || !CanSleep(target)) return false;
            if (!_asleep.ContainsKey(target)) Hold(target, SpellHold.Asleep);
            _asleep[target] = target.IsPlayer ? SpellEffectRules.PlayerSleepSeconds : SpellEffectRules.NpcSleepSeconds;
            return true;
        }

        /// <summary>MagicHypnosis: every calm NPC within 20 m of the caster that can sleep; returns them.</summary>
        public IReadOnlyList<ActorId> Hypnosis(ActorId caster)
        {
            var result = new List<ActorId>();
            var centre = Positions?.Invoke(caster);
            if (centre == null) return result;
            foreach (var (npc, _) in NpcsNear(centre.Value, SpellEffectRules.HypnosisRadius / Cm))
            {
                if (npc == caster || IsBusy(npc) || LedBy(npc, caster) || LedBy(caster, npc)) continue;
                if (Sleep(npc)) result.Add(npc);
            }
            return result;
        }

        bool CanSleep(ActorId actor) =>
            actor.IsPlayer ? SpellEffectRules.CanSleep("Human") : SpellEffectRules.CanSleep(Def(actor)?.Species);

        /// <summary>Hypnosis skips fighting, talking, down, dead and party NPCs (G3 AIModes 2, 3, 6, 8, 9, 11).</summary>
        bool IsBusy(ActorId npc)
        {
            if (_vitals.StateOf(npc) != VitalState.Alive) return true;
            if (_combat.Get(npc) is NpcCombatBrain brain && brain.IsFighting) return true;
            if (_party != null && _party.IsLedByPlayer(npc)) return true;
            return _npcs.TryGet(npc, out var agent) && agent.Activity == NpcActivity.Talking;
        }

        // ------------------------------------------------------------------------------------------- control

        /// <summary>The control facts of a target (ControlSpells' input).</summary>
        public ControlTarget FactsOf(ActorId target)
        {
            var def = Def(target);
            var brain = _combat.Get(target);
            bool fighting = brain != null && brain.IsFighting;
            return new ControlTarget
            {
                IsPlayer = target.IsPlayer,
                Species = target.IsPlayer ? "Human" : def?.Species,
                Evil = def != null && def.Evil,
                Boss = def != null && def.Boss,
                Mage = def != null && (def.Mage || def.Class == NpcClass.Mage),
                GateGuard = IsGateGuard != null && IsGateGuard(target),
                Animal = def != null && def.Animal,
                InCombat = fighting,
                CalmOrParalysed = !fighting && _vitals.StateOf(target) == VitalState.Alive,
            };
        }

        /// <summary>MagicWordOfControl: a human, orc or goblin joins the hero as Controlled; the refusal lines are said.</summary>
        public ControlOutcome WordOfControl(ActorId caster, ActorId target, int intelligence)
        {
            var outcome = ControlSpells.WordOfControl(FactsOf(target), caster.IsPlayer, intelligence);
            return Control(caster, target, outcome);
        }

        /// <summary>MagicTameAnimal: an animal joins the hero as Controlled (the strong ones need INT 100).</summary>
        public ControlOutcome TameAnimal(ActorId caster, ActorId target, int intelligence)
        {
            var outcome = caster.IsPlayer ? ControlSpells.TameAnimal(FactsOf(target), intelligence) : ControlOutcome.DoesntWork;
            return Control(caster, target, outcome);
        }

        ControlOutcome Control(ActorId caster, ActorId target, ControlOutcome outcome)
        {
            if (outcome != ControlOutcome.Works)
            {
                Say(caster, LineOf(outcome));
                return outcome;
            }
            DismissControlled();
            DismissSummons?.Invoke(caster);
            _combat.Get(target)?.Disengage();
            _npcs.FullStop(target);
            _party?.Join(target, PartyMemberType.Controlled);
            Say(target, "Follow");
            return outcome;
        }

        /// <summary>Releases the hero's Controlled members (G3 GO_ControlDismiss; they resume their routine).</summary>
        public void DismissControlled()
        {
            if (_party == null) return;
            foreach (var m in _party.Members.Where(m => m.Type == PartyMemberType.Controlled).Select(m => m.Id).ToList())
                _party.Dismiss(m);
        }

        /// <summary>MagicCharm (CP): an NPC not fighting forgets the hero's crimes and fights against it; a boss attacks.</summary>
        public ControlOutcome Charm(ActorId caster, ActorId target)
        {
            var outcome = ControlSpells.Charm(FactsOf(target));
            switch (outcome)
            {
                case ControlOutcome.Works:
                    if (Ledger != null)
                    {
                        Ledger.ForgivePersonally(target);
                        Ledger.RecordFight(target, FightResult.None, AttackReason.None, 0);
                    }
                    Say(target, SpellEffectRules.WakeVoice);
                    break;
                case ControlOutcome.AttacksCaster:
                    _combat.Engage(target, caster, AttackReason.ReactToDamage);
                    break;
                default:
                    Say(caster, LineOf(outcome));
                    break;
            }
            return outcome;
        }

        // ------------------------------------------------------------------------------------------- bloodlust, call animals

        /// <summary>
        /// MagicBloodlust: the victim attacks the nearest NPC within 10 m (it may be the caster; ambient creatures are
        /// skipped) with reason 0x11; bosses and mages attack the caster. Returns whom the victim attacks (empty: nobody).
        /// </summary>
        public ActorId Bloodlust(ActorId caster, ActorId target)
        {
            var def = Def(target);
            if (target.IsEmpty || target.IsPlayer || def == null) return default;
            if (def.Boss || def.Mage || def.Class == NpcClass.Mage)
            {
                _combat.Engage(target, caster, AttackReason.ReactToDamage);
                return caster;
            }
            var at = Positions?.Invoke(target);
            if (at == null) return default;
            var candidates = NpcsNear(at.Value, SpellEffectRules.BloodlustRadius / Cm)
                .Where(n => n.Id != target && Def(n.Id)?.AmbientCreature != true && _vitals.StateOf(n.Id) == VitalState.Alive)
                .Select(n => (n.Id, n.Distance)).ToList();
            var casterPose = caster.IsPlayer ? Positions?.Invoke(caster) : null;
            if (casterPose != null && casterPose.Value.DistanceTo(at.Value) <= SpellEffectRules.BloodlustRadius / Cm)
                candidates.Add((caster, casterPose.Value.DistanceTo(at.Value)));
            if (candidates.Count == 0) return default;
            var victim = candidates.OrderBy(c => c.Item2).First().Item1;
            _combat.Get(target)?.Disengage();
            _combat.Engage(target, victim, AttackReason.Duel); // gEAttackReason 0x11
            return victim;
        }

        /// <summary>
        /// MagicCallAnimals: the nearest 1/2/3 (INT ≤200/&gt;200/&gt;300) world animals within 250 m of the caster that can
        /// fight (not ambient, deer, pigs or cows; not the caster or target; not called already) attack the target with
        /// reason 0x10 (PlayerCommand). They do not join the party.
        /// </summary>
        public IReadOnlyList<ActorId> CallAnimals(ActorId caster, ActorId target, int intelligence)
        {
            var result = new List<ActorId>();
            var centre = Positions?.Invoke(caster);
            if (centre == null || target.IsEmpty) return result;
            int wanted = SpellEffectRules.CallAnimalsCount(intelligence);
            foreach (var (npc, _) in NpcsNear(centre.Value, SpellEffectRules.CallAnimalsRadius / Cm))
            {
                if (result.Count >= wanted) break;
                if (npc == caster || npc == target || _vitals.StateOf(npc) != VitalState.Alive) continue;
                var def = Def(npc);
                if (def == null || !def.Animal || def.AmbientCreature || SpellEffectRules.PeacefulAnimals.Contains(def.Species ?? "")) continue;
                var brain = _combat.Get(npc);
                if (brain != null && brain.IsFighting && brain.State.Reason == AttackReason.PlayerCommand) continue;
                if (_combat.Engage(npc, target, AttackReason.PlayerCommand)) result.Add(npc);
            }
            return result;
        }

        // ------------------------------------------------------------------------------------------- mist, time bubble

        /// <summary>ZS_Mist: one mist at a time (an older one ends); 20 s, +5 with the player's deity perk.</summary>
        public SpellArea SummonMist(ActorId caster, WorldPose at, bool deityPerk)
        {
            EndMist();
            Mist = new SpellArea(SpellObjectKind.Mist, caster, at, SpellEffectRules.MistSeconds(caster.IsPlayer && deityPerk));
            _events?.Publish(new SpellObjectChanged(SpellObjectKind.Mist, caster, at, Mist.Remaining, true));
            MistTick();
            return Mist;
        }

        /// <summary>ZS_Timebubble: 80–110 s (+50 % with the player's deity perk); one bubble at a time [interp].</summary>
        public SpellArea StartTimeBubble(ActorId caster, WorldPose at, bool deityPerk)
        {
            EndTimeBubble();
            float seconds = SpellEffectRules.TimeBubbleSeconds(_random.Next(4), caster.IsPlayer && deityPerk);
            TimeBubble = new SpellArea(SpellObjectKind.TimeBubble, caster, at, seconds);
            _events?.Publish(new SpellObjectChanged(SpellObjectKind.TimeBubble, caster, at, seconds, true));
            return TimeBubble;
        }

        /// <summary>MagicMistTick: NPCs within 10 m get InMist and assess it; those outside lose it.</summary>
        void MistTick()
        {
            var mist = Mist;
            if (mist == null) return;
            var inside = new HashSet<ActorId>(NpcsNear(mist.Pose, SpellEffectRules.MistRadius / Cm).Select(n => n.Id));
            _scratch.Clear();
            _scratch.AddRange(_misted.Where(a => !inside.Contains(a)));
            foreach (var a in _scratch) Unhold(a, SpellHold.Misted);
            foreach (var npc in inside)
            {
                if (_misted.Contains(npc) || npc == mist.Owner || _vitals.StateOf(npc) != VitalState.Alive || _asleep.ContainsKey(npc)) continue;
                var brain = _combat.Get(npc);
                if (brain != null && brain.State.Phase == CombatPhase.Flee) continue;
                if (brain != null && brain.IsFighting) brain.Disengage();
                var attitude = Attitudes?.Invoke(npc, mist.Owner) ?? Attitude.Neutral;
                if (attitude == Attitude.Friendly)
                {
                    _npcs.FullStop(npc);
                    Hold(npc, SpellHold.Misted);
                }
                else _combat.Engage(npc, mist.Owner, AttackReason.ReactToDamage);
            }
        }

        public void EndMist()
        {
            if (Mist == null) return;
            var mist = Mist;
            Mist = null;
            _scratch.Clear();
            _scratch.AddRange(_misted);
            foreach (var a in _scratch) Unhold(a, SpellHold.Misted);
            _events?.Publish(new SpellObjectChanged(SpellObjectKind.Mist, mist.Owner, mist.Pose, 0, false));
        }

        public void EndTimeBubble()
        {
            if (TimeBubble == null) return;
            var bubble = TimeBubble;
            TimeBubble = null;
            _events?.Publish(new SpellObjectChanged(SpellObjectKind.TimeBubble, bubble.Owner, bubble.Pose, 0, false));
        }

        void EndHeroObjects()
        {
            if (Mist != null && Mist.Owner.IsPlayer) EndMist();
            if (TimeBubble != null && TimeBubble.Owner.IsPlayer) EndTimeBubble();
            DismissControlled();
        }

        // ------------------------------------------------------------------------------------------- tick

        public void Tick(float realSeconds)
        {
            if (realSeconds <= 0) return;
            if (_asleep.Count > 0)
            {
                _scratch.Clear();
                foreach (var kv in _asleep) if (kv.Value - realSeconds <= 0) _scratch.Add(kv.Key);
                foreach (var key in _asleep.Keys.ToList()) _asleep[key] -= realSeconds;
                foreach (var a in _scratch) Wake(a);
            }
            if (Mist != null)
            {
                Mist.Remaining -= realSeconds;
                Mist.TickIn -= realSeconds;
                if (Mist.Remaining <= 0) EndMist();
                else if (Mist.TickIn <= 0)
                {
                    Mist.TickIn = SpellEffectRules.MistTickSeconds;
                    MistTick();
                }
            }
            if (TimeBubble != null)
            {
                TimeBubble.Remaining -= realSeconds;
                if (TimeBubble.Remaining <= 0) EndTimeBubble();
            }
        }

        /// <summary>ZS_MagicSleep_Loop: the player continues; an NPC says "WhatWasThat" (then fetches its weapon).</summary>
        void Wake(ActorId actor)
        {
            if (!_asleep.Remove(actor)) return;
            _events?.Publish(new ActorSpellbound(actor, SpellHold.Asleep, false));
            if (!actor.IsPlayer && _vitals.StateOf(actor) == VitalState.Alive) Say(actor, SpellEffectRules.WakeVoice);
        }

        /// <summary>A defeated actor is no longer held.</summary>
        void Release(ActorId actor)
        {
            if (_asleep.Remove(actor)) _events?.Publish(new ActorSpellbound(actor, SpellHold.Asleep, false));
            if (_misted.Remove(actor)) _events?.Publish(new ActorSpellbound(actor, SpellHold.Misted, false));
        }

        void Hold(ActorId actor, SpellHold hold)
        {
            if (hold == SpellHold.Misted && !_misted.Add(actor)) return;
            if (!actor.IsPlayer)
            {
                _combat.Get(actor)?.Disengage();
                _npcs.FullStop(actor);
            }
            _events?.Publish(new ActorSpellbound(actor, hold, true));
        }

        void Unhold(ActorId actor, SpellHold hold)
        {
            if (hold == SpellHold.Misted && !_misted.Remove(actor)) return;
            _events?.Publish(new ActorSpellbound(actor, hold, false));
        }

        // ------------------------------------------------------------------------------------------- helpers

        NpcCombatDef Def(ActorId actor) =>
            !actor.IsPlayer && _actors.TryGet(actor, out var state) ? CombatWiring.CombatDefOf(state) : null;

        bool LedBy(ActorId member, ActorId leader) =>
            leader.IsPlayer ? _party != null && _party.IsLedByPlayer(member) : false;

        /// <summary>NPC agents within <paramref name="radiusMetres"/> of a point, nearest first.</summary>
        IEnumerable<(ActorId Id, float Distance)> NpcsNear(WorldPose at, float radiusMetres) =>
            _npcs.Agents.Select(a => (a.Id, Distance: _npcs.PoseOf(a).DistanceTo(at)))
                .Where(a => a.Distance <= radiusMetres)
                .OrderBy(a => a.Distance)
                .ToList();

        void Say(ActorId speaker, string line)
        {
            if (!string.IsNullOrEmpty(line) && !speaker.IsEmpty) _events?.Publish(new CombatShout(speaker, line));
        }

        static string LineOf(ControlOutcome outcome)
        {
            switch (outcome)
            {
                case ControlOutcome.DoesntWork: return "DoesntWork";
                case ControlOutcome.Impossible: return "Impossible";
                case ControlOutcome.TooHard: return "TooHard";
                default: return null;
            }
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
