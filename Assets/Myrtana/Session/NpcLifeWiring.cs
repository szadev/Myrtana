using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>A dead NPC came back to life in place (CP Respawn): the presentation stands its body up again.</summary>
    public readonly struct CorpseRevived : IGameEvent
    {
        public CorpseRevived(ActorId npc) => Npc = npc;
        public ActorId Npc { get; }
    }

    /// <summary>
    /// The NPC life between fights (par-npc): the creature and sleeper reaction tasks (<see cref="ReactionTasks"/>), the
    /// witnesses' flight and the fight watchers for the defeat comments, the processing-range entry (G3
    /// OnEnterProcessingRange: a living NPC's hit points are refreshed, a corpse is judged by <see cref="CorpseRules"/>:
    /// kept, removed or revived) and the corpse clock (<see cref="CorpseLedger"/>, saved).
    /// </summary>
    public sealed class NpcLifeServices : IDisposable
    {
        readonly NpcSimulation _npcs;
        readonly ActorRegistry _actors;
        readonly ActorVitals _vitals;
        readonly NpcCombatSystem _combat;
        readonly CrimeServices _crime;
        readonly ItemServices _items;
        readonly IContentDatabase _content;
        readonly IRandom _random;
        readonly IEventBus _events;
        readonly ActorRemoval _removal;
        readonly Func<double> _gameHours;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly List<ActorId> _entered = new List<ActorId>();
        readonly List<ActorId> _work = new List<ActorId>();
        /// <summary>Wounded NPCs that were in the processing range when the loaded game was saved (fix-175).</summary>
        readonly HashSet<ActorId> _woundedInRangeAtLoad = new HashSet<ActorId>();

        public NpcLifeServices(NpcSimulation npcs, ActorRegistry actors, ActorVitals vitals, NpcCombatSystem combat, CrimeServices crime,
                               ItemServices items, IContentDatabase content, IRandom random, IEventBus events, ActorRemoval removal,
                               Func<double> gameHours)
        {
            _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
            _actors = actors ?? throw new ArgumentNullException(nameof(actors));
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _crime = crime ?? throw new ArgumentNullException(nameof(crime));
            _items = items;
            _content = content;
            _random = random;
            _events = events;
            _removal = removal;
            _gameHours = gameHours ?? (() => 0);
            _npcs.EnteredRange += OnEnteredRange;
            // ZS_Flee for witnesses (fight sound, defeat, murder, theft by a non-fighter): the combat brain's flight.
            _crime.Witnesses.Flee = (npc, from) => _combat.Flee(npc, from);
            // par-npc2: NPC-led parties, ambient lines (smalltalk, sermons), who may listen to a sermon.
            _npcs.Parties = Parties;
            _npcs.Said += OnSaid;
            _npcs.MayListen = MayListen;
            if (events != null)
            {
                _subscriptions.Add(events.Subscribe<ActorDefeated>(e =>
                {
                    if (!e.Killed) return;
                    Corpses.Stamp(e.Victim, _gameHours());
                    _npcs.FullStop(e.Victim); // G3 Kill: a walk ends where the body fell (the routine stops: NpcSimulation.Lifeless, fix-80)
                    Parties.Forget(e.Victim); // G3 Kill: the dead leave their party (a dead leader's members are released)
                }));
                _subscriptions.Add(events.Subscribe<NpcReacted>(OnReacted));
                _subscriptions.Add(events.Subscribe<HitResolved>(OnHit));
                _subscriptions.Add(events.Subscribe<ActorGotUp>(OnGotUp));
                _subscriptions.Add(events.Subscribe<EnclaveLiberated>(e => Liberated(e.Enclave)));
            }
        }

        public ReactionTasks Tasks { get; } = new ReactionTasks();
        public CorpseLedger Corpses { get; } = new CorpseLedger();
        /// <summary>Burning or held in a magic barrier: a corpse in that state is never revived (set by the session).</summary>
        public Func<ActorId, bool> BurningOrBarrier { get; set; }
        /// <summary>The hit comments of fight watchers (CheerFight's global counter).</summary>
        public FightCheers Cheers { get; } = new FightCheers();
        /// <summary>Parties led by NPCs (CP JoinNPC; saved by <see cref="NpcPartiesSaveSection"/>).</summary>
        public NpcParties Parties { get; } = new NpcParties();
        /// <summary>
        /// Port option "Keep wounds after load" (fix-175, [owner] default on in the menu; false here = G3): an NPC that was
        /// wounded and in the processing range when the game was saved keeps its saved HP and SP when it enters the range
        /// again right after the load, instead of G3's RefreshHitPoints. Only that first entry: leaving the range and coming
        /// back during play refills as in G3, and NPCs out of range at the save are refilled on entry as in G3.
        /// </summary>
        public bool KeepWoundsAfterLoad { get; set; }

        /// <summary>
        /// Save section "life.wounded" (fix-175): the wounded, not dead NPCs in the processing range (not Abstract) at the save,
        /// for <see cref="KeepWoundsAfterLoad"/>. An older save has none: every NPC is refilled as in G3.
        /// </summary>
        public void WriteWoundedInRange(ISaveWriter w)
        {
            var ids = new List<string>();
            var agents = _npcs.Agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a.Detail == DetailLevel.Abstract || !_vitals.Holds(a.Id) || _vitals.IsDead(a.Id)) continue;
                ids.Add(a.Id.Name);
            }
            w.WriteSortedStrings(ids);
        }

        public void ReadWoundedInRange(ISaveReader r, int version)
        {
            _woundedInRangeAtLoad.Clear();
            foreach (var name in r.ReadStrings()) _woundedInRangeAtLoad.Add(new ActorId(name));
        }

        /// <summary>The hero's party, for the Mob members' meeting-point rule (set by the session; null = not checked).</summary>
        public PlayerParty PlayerParty { get; set; }
        /// <summary>Where the Fireclan enclave stands (<see cref="MobMeetingPoint"/>; null = unknown, not checked).</summary>
        public WorldPose? Fireclan { get; set; }

        /// <summary>The position of an enclave entity (content <see cref="EnclaveDef.Position"/>); null when unknown.</summary>
        public static WorldPose? EnclavePose(IContentDatabase content, string enclave)
        {
            if (content == null) return null;
            foreach (var e in content.All<EnclaveDef>())
                if (e.Id == enclave && e.Position != null && e.Position.Length >= 3) return new WorldPose(e.Position[0], e.Position[1], e.Position[2], 0f);
            return null;
        }

        void OnSaid(ActorId speaker, string line) => _events?.Publish(new NpcAmbientLine(speaker, line));

        /// <summary>SendPerceptionPreach's receivers: alive and up, not fighting, not a gate guard, not a slave.</summary>
        bool MayListen(ActorId npc)
        {
            if (_vitals.StateOf(npc) != VitalState.Alive || _combat.Get(npc)?.IsFighting == true) return false;
            if (_crime.Crime.GateGuards != null && _crime.Crime.GateGuards.IsGateGuard(npc)) return false;
            return !(_crime.Attitudes.Facts.TryGetProfile(npc, out var p) && p.Alignment == CrimeSystem.SlaveAlignment);
        }

        /// <summary>
        /// CP JoinNPC: <paramref name="member"/> becomes a Controlled member of <paramref name="leader"/>'s party (it leaves the
        /// hero's party first and takes the leader's alignment).
        /// </summary>
        public bool JoinNpc(ActorId member, ActorId leader)
        {
            if (!_npcs.TryGet(member, out _) || !_npcs.TryGet(leader, out _)) return false;
            if (_vitals.StateOf(member) == VitalState.Dead || _vitals.StateOf(leader) == VitalState.Dead) return false;
            if (PlayerParty != null && PlayerParty.TryGet(member, out _)) PlayerParty.Dismiss(member);
            if (!Parties.Join(member, leader)) return false;
            if (_crime.Attitudes.Facts.TryGetProfile(leader, out var lp)) ChangeAlignment(member, lp.Alignment);
            _npcs.FullStop(member);
            return true;
        }

        readonly List<ActorId> _mobs = new List<ActorId>();

        /// <summary>CP OnFollowPlayer: Mob members farther than 620 m from the Fireclan leave the hero (<see cref="MobMeetingPoint"/>).</summary>
        public int CheckMobMeetingPoint()
        {
            if (PlayerParty == null || !(Fireclan is WorldPose fireclan)) return 0;
            _mobs.Clear();
            foreach (var m in PlayerParty.Members)
                if (m.Type == PartyMemberType.Mob && !m.Waiting && _npcs.TryGet(m.Id, out var agent) &&
                    MobMeetingPoint.TooFar(_npcs.PoseOf(agent), fireclan))
                    _mobs.Add(m.Id);
            foreach (var id in _mobs)
            {
                PlayerParty.LeaveSilently(id);
                Say(id, MobMeetingPoint.Line, FollowKind, SpeechGate.ShortCooldownHours);
            }
            return _mobs.Count;
        }

        readonly Dictionary<ActorId, ActorId> _knockedOutBy = new Dictionary<ActorId, ActorId>();
        readonly List<ActorId> _watchers = new List<ActorId>();

        /// <summary>
        /// The say gate kinds of these AI lines (FUN_1000baf0's global last-say time per kind, <see cref="SpeechGate"/>):
        /// ZS_WatchFight's "ThereIsAFight" and CheerFight share kind 6 (0.0018: one line per game minute), the get-up line
        /// is kind 4 and the Mob's farewell (OnFollowPlayer) kind 1 (0.006).
        /// </summary>
        public const string SayKind = "WatchFight", GetUpKind = "GetUp", FollowKind = "Follow";

        void OnReacted(NpcReacted e)
        {
            if (e.Reaction.Kind != ReactionKind.Watch) return;
            // ZS_WatchFight: "ThereIsAFight" when the watch starts, both fighters humanoid and neither fleeing.
            bool already = _combat.Talk.Watches(e.Npc, e.Reaction.Target) && _combat.Talk.Watches(e.Npc, e.Reaction.Other);
            // Current target = one fighter, alternative target = the other (the defeat comment's and the cheers' condition).
            _combat.Talk.Watch(e.Npc, e.Reaction.Target, e.Reaction.Other);
            if (already || !Humanoid(e.Npc) || !Humanoid(e.Reaction.Target) || !Humanoid(e.Reaction.Other)) return;
            if (Fleeing(e.Reaction.Target) || Fleeing(e.Reaction.Other)) return;
            Say(e.Npc, KnockOutLines.ThereIsAFight);
        }

        bool Humanoid(ActorId actor) =>
            actor.IsPlayer || (_crime.Attitudes.Facts.TryGetProfile(actor, out var p) && p.Humanoid);

        bool Fleeing(ActorId actor) => !actor.IsPlayer && _crime.Attitudes.Facts.TryGetProfile(actor, out var p) && p.Fleeing;

        /// <summary>A humanoid NPC says a shared line through the global say gate (combat voice, see NpcReactionView).</summary>
        bool Say(ActorId npc, string line, string kind = SayKind, double cooldownHours = SpeechGate.BattleCooldownHours)
        {
            if (line == null || npc.IsPlayer || !Humanoid(npc) || !_combat.Talk.Gate.TrySay(kind, cooldownHours)) return false;
            _events?.Publish(new CombatShout(npc, line));
            return true;
        }

        /// <summary>
        /// Every landed hit: the knock-out's attacker is remembered for the get-up line; a plain hit is commented by the
        /// watchers of the victim (CheerFight per receiver of SendPerceptionFightSound: each one advances the global counter,
        /// the global say gate of kind 6 lets at most one line per game minute through, fix-156).
        /// </summary>
        void OnHit(HitResolved e)
        {
            if (e.Outcome == null || e.Outcome.Result == HitResult.Ignored || e.Attacker == e.Victim) return;
            if (e.Outcome.Killed) return;
            if (_combat.Accidents.IsForgiven(e)) return; // fix-162: no fight sound, so no cheers
            if (e.Outcome.KnockedOut)
            {
                _knockedOutBy[e.Victim] = e.Attacker;
                return;
            }
            _combat.Talk.WatchersOf(e.Victim, _watchers);
            foreach (var w in _watchers)
            {
                if (w == e.Attacker || _vitals.StateOf(w) != VitalState.Alive || !Humanoid(w)) continue;
                var attitudes = _crime.Attitudes;
                string line = Cheers.LineFor(e.Outcome.Reaction, attitudes.Of(w, e.Victim), attitudes.Of(w, e.Attacker));
                if (line != null) Say(w, line);
            }
        }

        /// <summary>ZS_Unconscious_End: "OhMyHead" or "NextTimeYoureInForIt" (<see cref="KnockOutLines.GetUpLine"/>).</summary>
        void OnGotUp(ActorGotUp e)
        {
            if (e.Actor.IsPlayer) return;
            _knockedOutBy.TryGetValue(e.Actor, out var attacker);
            _knockedOutBy.Remove(e.Actor);
            var attitude = attacker.IsEmpty ? Attitude.Neutral : _crime.Attitudes.Of(e.Actor, attacker);
            Say(e.Actor, KnockOutLines.GetUpLine(_random?.Next(100) ?? 0, attitude), GetUpKind, SpeechGate.ShortCooldownHours);
        }

        /// <summary>
        /// CP NotifyEnclave after a liberation (ChangeEnclaveStatus Flee): the enclave's loyal members out of the
        /// processing range (Abstract) are killed and vanish unless they carry a mission item (then they turn outlaws, "Out");
        /// loyal members in range go on with their routine (a mission-item carrier turns outlaw, a member of an outlaw
        /// enclave other than Nrd_BigMine turns Orc); its oppressed members in range that stand applaud the liberator
        /// (ZS_RevolutionSuccess → ZS_Applaud). Returns the removed members.
        /// </summary>
        public int Liberated(string enclave, ActorId liberator = default)
        {
            if (string.IsNullOrEmpty(enclave)) return 0;
            if (liberator.IsEmpty) liberator = ActorId.Player;
            _work.Clear();
            foreach (var agent in _npcs.Agents)
                if (_crime.Attitudes.Facts.TryGetProfile(agent.Id, out var p) && p.Enclave == enclave && _npcs.IsInWorld(agent)) _work.Add(agent.Id);
            int removed = 0;
            foreach (var id in _work)
            {
                if (!_npcs.TryGet(id, out var agent) || !_crime.Attitudes.Facts.TryGetProfile(id, out var p)) continue;
                var state = _vitals.StateOf(id);
                bool inRange = agent.Detail != DetailLevel.Abstract;
                bool loyal = _crime.Attitudes.LoyalToEnclave(p);
                if (loyal)
                {
                    if (state == VitalState.Dead) continue;
                    if (CarriesMissionItem(id)) { ChangeAlignment(id, OutlawAlignment); continue; }
                    if (inRange)
                    {
                        _npcs.FullStop(id);
                        if (TurnsOrcWhenLiberated(enclave, p.EnclaveAlignment)) ChangeAlignment(id, OrcAlignment);
                        continue;
                    }
                    _vitals.Kill(id, byPlayerParty: false);
                    _removal?.Remove(id);
                    Corpses.MarkGone(id);
                    removed++;
                }
                else if (inRange && state == VitalState.Alive)
                    _events?.Publish(new NpcApplauds(id, liberator));
            }
            _work.Clear();
            // CP NotifyEnclave's last step: the outlaw enclave itself turns Orc, after its members (fix-198 [owner, build 23c]:
            // only the members did, so the survivors of a Nordmar raider camp were hostile to their camp and the revolution
            // table's Flee row made them Friendly to the hero, a green focus name, instead of panicking as in G3).
            if (Enclaves != null && TurnsOrcWhenLiberated(enclave, EnclaveAlignment(enclave))) Enclaves.SetAlignment(enclave, OrcAlignment);
            return removed;
        }

        /// <summary>The run-time enclave states (story alignments); null: the enclave keeps its alignment (tests without one).</summary>
        public EnclaveStates Enclaves { get; set; }

        public const string OrcAlignment = "Orc", OutlawAlignment = "Out"; // G3 gEPoliticalAlignment 1 and 7

        /// <summary>CP NotifyEnclave: a liberated outlaw enclave other than Nrd_BigMine becomes Orc.</summary>
        public static bool TurnsOrcWhenLiberated(string enclave, string alignment) =>
            string.Equals(alignment, OutlawAlignment, StringComparison.Ordinal) &&
            !string.Equals(enclave, EnclaveSectorSwitches.BigMine, StringComparison.Ordinal);

        string EnclaveAlignment(string enclave) =>
            _crime.Attitudes.Facts is SessionAttitudeFacts facts ? facts.EnclaveAlignmentOf(enclave) : Enclaves?.AlignmentOverride(enclave);

        void ChangeAlignment(ActorId npc, string alignment)
        {
            if (_actors.TryGet(npc, out var state)) state.ChangeAlignment(alignment);
        }

        void OnEnteredRange(NpcAgent agent) => _entered.Add(agent.Id);

        /// <summary>Handles the NPCs that entered the processing range since the last frame (outside the simulation's loop).</summary>
        public void Tick()
        {
            CheckMobMeetingPoint();
            if (Corpses.NeedsPurge) Purge();
            if (_entered.Count == 0) return;
            _work.Clear();
            _work.AddRange(_entered);
            _entered.Clear();
            foreach (var npc in _work) EnterProcessingRange(npc);
        }

        /// <summary>
        /// G3 OnEnterProcessingRange: a dead NPC (AIMode Dead) goes to Respawn (<see cref="CorpseRules.Judge"/>); any other
        /// has its hit points and stamina refreshed (RefreshHitPoints: full, mana kept), a knocked-out one too: it keeps
        /// lying (ZS_StandUp follows its own timer). A loaded game does the same to the damaged NPCs around the hero
        /// [fix-175: eCEntity::ReadV83 never restores the "processing range entered" bit 0x80 and gCSession::Start runs
        /// 12 frames with the game running after a load, so every NPC near the hero enters the range again].
        /// [interp] An NPC still fighting is left alone (G3 cancels fights on leaving the range, so none enters fighting).
        /// With <see cref="KeepWoundsAfterLoad"/> the first entry after a load of an NPC wounded in range at the save is not
        /// refilled (port option, [owner]).
        /// </summary>
        public CorpseVerdict EnterProcessingRange(ActorId npc)
        {
            if (!_npcs.TryGet(npc, out var agent)) return CorpseVerdict.Keep;
            var state = _vitals.StateOf(npc);
            if (state != VitalState.Dead)
            {
                bool keepWounds = _woundedInRangeAtLoad.Remove(npc) && KeepWoundsAfterLoad; // fix-175 port option
                if (!keepWounds && _combat.Get(npc)?.IsFighting != true) _vitals.RefreshHitPoints(npc);
                return CorpseVerdict.Keep;
            }
            var facts = FactsOf(agent);
            double now = _gameHours();
            var verdict = CorpseRules.Judge(facts, now, _random?.Next(100) ?? 99);
            switch (verdict)
            {
                case CorpseVerdict.Stamp:
                    Corpses.Stamp(npc, now);
                    break;
                case CorpseVerdict.Remove:
                    _removal?.Remove(npc);
                    Corpses.MarkGone(npc);
                    break;
                case CorpseVerdict.Revive:
                    Revive(npc);
                    break;
            }
            return verdict;
        }

        /// <summary>A loaded game: the corpses removed before the save leave the simulation again.</summary>
        void Purge()
        {
            Corpses.Purged();
            _work.Clear();
            foreach (var id in Corpses.Gone) if (_npcs.TryGet(id, out _)) _work.Add(id);
            foreach (var id in _work) _removal?.Remove(id);
            _entered.RemoveAll(Corpses.IsGone);
        }

        CorpseFacts FactsOf(NpcAgent agent)
        {
            var def = CombatWiring.CombatDefOf(agent.Definition);
            var pose = _npcs.PoseOf(agent);
            var facts = new CorpseFacts
            {
                Template = CorpseRules.TemplateOf(agent.Id),
                Species = def.Species ?? agent.Definition.Species,
                AmbientCreature = def.AmbientCreature,
                DiedAt = Corpses.DiedAt(agent.Id),
                CarriesMissionItem = CarriesMissionItem(agent.Id),
                BurningOrBarrier = BurningOrBarrier?.Invoke(agent.Id) == true,
                PlayerDistance = _crime.World.TryGetPose(ActorId.Player, out var player) ? Senses.Distance(pose, player) : float.MaxValue,
            };
            if (!facts.AmbientCreature) facts.HumanoidNear = LivingHumanoidNear(agent, pose);
            return facts;
        }

        bool CarriesMissionItem(ActorId npc)
        {
            if (_items == null || _content == null) return false;
            foreach (var stack in _items.Inventories.Of(npc).Stacks)
                if (_content.TryGet(stack.Item, out ItemDef item) && item.IsMission) return true;
            return false;
        }

        bool LivingHumanoidNear(NpcAgent self, WorldPose at)
        {
            foreach (var other in _npcs.Agents)
            {
                if (other == self || _vitals.StateOf(other.Id) != VitalState.Alive) continue;
                if (!CombatWiring.CombatDefOf(other.Definition).Humanoid) continue;
                if (Senses.Distance(_npcs.PoseOf(other), at) <= CorpseRules.ReviveNoHumanoidRange) return true;
            }
            return false;
        }

        /// <summary>
        /// CP Respawn's revival: the generated loot goes, hit points and weaponry are refreshed, the fight and crime memory is
        /// reset, and the NPC goes back to its routine where it lies.
        /// </summary>
        void Revive(ActorId npc)
        {
            if (_items != null)
            {
                // G3 deletes the generated plunder: the inventory goes back to the NPC's static stacks.
                _items.NpcLoot.Forget(npc);
                _items.Inventories.Forget(npc);
                if (_content != null && _content.TryGet(npc.Name, out NpcInventoryDef start))
                    foreach (var stack in start.Stacks) _items.Inventories.Of(npc).Add(stack.Item, Math.Max(1, stack.Amount), stack.Quality);
            }
            _crime.Ledger.Forget(npc);
            _combat.Get(npc)?.Disengage();
            _combat.RestoreWeaponry(npc);
            _vitals.Forget(npc); // no entry = alive at full strength
            Corpses.Forget(npc);
            Tasks.Forget(npc);
            _events?.Publish(new CorpseRevived(npc));
        }

        public void Dispose()
        {
            _npcs.EnteredRange -= OnEnteredRange;
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
