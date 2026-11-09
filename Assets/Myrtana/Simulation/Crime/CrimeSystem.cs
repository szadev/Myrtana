using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>What the player is doing, as an NPC's active perception sees it this scan.</summary>
    public struct PlayerConduct
    {
        /// <summary>A dangerous weapon in the hand (G3 HasDangerousWeapon: melee other than fists, ranged, or a harmful spell).</summary>
        public bool WeaponDrawn;
        /// <summary>The dangerous thing in the hand is a spell (the warnings say "StopMagic" instead of "WeaponDown").</summary>
        public bool SpellDrawn;
        public bool Sneaking;
        /// <summary>The player falls (no NPC starts a talk then, CP AssessTalk).</summary>
        public bool Falling;
        /// <summary>The player swims or dives in (no NPC starts a talk then, CP AssessTalk).</summary>
        public bool Swimming;
        /// <summary>The owned zone the player stands in (default = none).</summary>
        public OwnedZone Zone;
    }

    /// <summary>
    /// The crime side of the player's deeds and of the active perception (Docs/perception-crime.md §3–§4): thefts and
    /// object use (counted against the zone's enclave, announced to witnesses), entering a zone, the warn – warn –
    /// attack sequences of NPCs that see a drawn weapon or an intruder, grudges, fights the player ran away from, and
    /// the enclave alarm.
    /// </summary>
    public sealed class CrimeSystem : IActorStore
    {
        readonly PerceptionRules _perception;
        readonly CrimeRules _rules;
        readonly AttitudeService _attitudes;
        readonly CrimeCatalogue _catalogue;
        readonly CrimeLedger _ledger;
        readonly IPerceptionWorld _world;
        readonly EngageFight _engage;
        readonly IEventBus _events;
        readonly RuleOptions _options;
        readonly Func<string, bool> _playerKnows;
        readonly Func<double> _gameHours;
        readonly Dictionary<ActorId, WarningSequence> _warnings = new Dictionary<ActorId, WarningSequence>();
        /// <summary>Why each running sequence warns (ReactToWeapon or Intruder).</summary>
        readonly Dictionary<ActorId, AttackReason> _warningReasons = new Dictionary<ActorId, AttackReason>();
        /// <summary>
        /// Until when (real seconds) an NPC observes the player (ZS_ObserveSuspect): after it ended a weapon warning, or
        /// after another NPC's warning reached it (AssessWarn). A reaction state has no active perception, so it starts no
        /// warning of its own meanwhile.
        /// </summary>
        readonly Dictionary<ActorId, double> _observingUntil = new Dictionary<ActorId, double>();
        /// <summary>
        /// The NPC that warns the intruder for each zone owner (enclave) right now: of the owners that notice him only this
        /// one runs ZS_ReactToZone, the others observe while it warns (see <see cref="ZoneWarner"/>).
        /// </summary>
        readonly Dictionary<string, ActorId> _zoneWarners = new Dictionary<string, ActorId>(StringComparer.Ordinal);
        /// <summary>The real time of the assessment in progress (the warn broadcasts it sends are delivered within it).</summary>
        double _now;

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            _warnings.Remove(actor);
            _warningReasons.Remove(actor);
            _observingUntil.Remove(actor);
            ReleaseZone(actor);
        }

        public bool Holds(ActorId actor) =>
            _warnings.ContainsKey(actor) || _warningReasons.ContainsKey(actor) || _observingUntil.ContainsKey(actor);

        /// <summary>OnReactToWeapon: a warner gives up ("YesGetLost") once the player is farther than this (cm) [data].</summary>
        public const float WeaponWarningReach = 1000;
        /// <summary>ZS_ObserveSuspect task time after a weapon warning ended (seconds) [data].</summary>
        public const double ObserveAfterWarning = 4;
        /// <summary>G3 political alignment of slaves (IsSlave: slaves ignore drawn weapons) [data].</summary>
        public const string SlaveAlignment = "Slave";

        /// <summary>Spells G3 does not count as a dangerous weapon (HasDangerousWeapon), besides those of no deity [data].</summary>
        static readonly HashSet<string> HarmlessSpells = new HashSet<string>(StringComparer.Ordinal)
        {
            "Spl_DuskToDawn", "Spl_Light", "Spl_Cure", "Spl_HealOther", "Spl_CurePoison", "Spl_CureDisease", "Spl_AnimalForm",
        };

        /// <summary>Whether a readied spell counts as a dangerous weapon (G3 HasDangerousWeapon).</summary>
        public static bool IsDangerousSpell(SpellDef spell) =>
            spell != null && spell.Deity != SpellDeity.None && !HarmlessSpells.Contains(spell.Id);

        /// <summary>G3 AssessActivePerception: an NPC this close (cm) that has an important info walks up and talks.</summary>
        public const float ImportantInfoRange = 500;
        /// <summary>CP AssessActivePerception, new first step: an entity without a collision shape that has an important
        /// info talks to the player within this (cm), no line of sight needed. No placed NPC lacks one (`G3Cli objects
        /// nocollision`: 0 of 7646), so this only matters for entities spawned without one.</summary>
        public const float NoCollisionTalkRange = 2000;

        /// <summary>Whether a story flag is set (zones freed by the story, <see cref="CrimeCatalogue.ZoneFreed"/>); null = none is.</summary>
        public Func<string, bool> StoryFlags { get; set; }

        /// <summary>The gate guards (null = none: no gate-guard step, every NPC may notice a weapon).</summary>
        public GateGuards GateGuards { get; set; }

        /// <summary>
        /// The enclaves' run-time states: an alarm puts a quiet enclave into Attack (G3 NotifyEnclave → ChangeEnclaveStatus);
        /// a fleeing (liberated) enclave no longer defends itself. Null = alarms only send defenders.
        /// </summary>
        public EnclaveStates Enclaves { get; set; }

        /// <summary>
        /// Whether killing an enclave's boss alarms it (G3 NotifyEnclave: not once the quest Beliar_Contact succeeded, i.e.
        /// the hero sides with Beliar). Null = always.
        /// </summary>
        public Func<bool> BossKillAlarms { get; set; }

        /// <summary>
        /// Whether the NPC has an important info for the player right now (G3 HasImportantInfo: an available Important
        /// entry and no available Refuse entry); null = NPCs never start a conversation. Wired by the session once the
        /// dialogs exist.
        /// </summary>
        public Func<ActorId, bool> HasImportantInfo { get; set; }

        /// <summary>Whether the NPC has a collision shape (G3 CollisionShape PS); null = every NPC has one.</summary>
        public Func<ActorId, bool> HasCollisionShape { get; set; }

        /// <summary>
        /// Ends the NPC's fight against the player when it is a warning or grudge fight (ReactToWeapon / Intruder / ReactToDamage); used for the guide
        /// leading the hero (null = nothing to end).
        /// </summary>
        public Action<ActorId> EndWarningFight { get; set; }

        /// <summary>The NPC fights, flees or fights in the arena (CP AssessTalk: no talk then); null = never.</summary>
        public Func<ActorId, bool> InFight { get; set; }

        /// <summary>
        /// CP 1.75 AssessTalk preconditions: no talk while the player falls, swims or dives, nor while the NPC is
        /// hostile to him, fights, flees, fights in the arena or is transformed.
        /// </summary>
        public static bool MayStartTalk(in PlayerConduct conduct, bool npcHostile, bool npcFighting, bool npcFleeing, bool npcTransformed) =>
            !conduct.Falling && !conduct.Swimming && !npcHostile && !npcFighting && !npcFleeing && !npcTransformed;

        /// <summary>CP: an NPC other than a gate guard walks up to talk only with a free line of sight.</summary>
        public ILineOfSight LineOfSight { get; set; } = OpenLineOfSight.Instance;

        /// <param name="playerKnows">Whether the player has learned a perk (the AB master thief).</param>
        public CrimeSystem(PerceptionRules perception, CrimeRules rules, AttitudeService attitudes, CrimeCatalogue catalogue, CrimeLedger ledger,
                           IPerceptionWorld world, EngageFight engage, IEventBus events, RuleOptions options, Func<string, bool> playerKnows,
                           Func<double> gameHours)
        {
            _perception = perception ?? throw new ArgumentNullException(nameof(perception));
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _attitudes = attitudes ?? throw new ArgumentNullException(nameof(attitudes));
            _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _engage = engage ?? throw new ArgumentNullException(nameof(engage));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _options = options ?? RuleOptions.Default;
            _playerKnows = playerKnows ?? (_ => false);
            _gameHours = gameHours ?? throw new ArgumentNullException(nameof(gameHours));
            _events.Subscribe<NpcReacted>(OnReacted);
        }

        /// <summary>
        /// G3 AssessWarn: an NPC in its routine that hears another's warning observes the warned player (ZS_ObserveSuspect,
        /// 4 s task time); one that is warning itself goes on with its own sequence. So of a group only the first NPC that
        /// noticed the intruder or the weapon speaks, the rest watch while its warnings last (each warning renews it).
        /// </summary>
        void OnReacted(NpcReacted e)
        {
            if (e.Cause != BroadcastKind.Warn || e.Reaction.Kind != ReactionKind.Observe || !e.Reaction.Target.IsPlayer) return;
            if (_warnings.ContainsKey(e.Npc)) return;
            _observingUntil[e.Npc] = _now + ObserveAfterWarning;
        }

        /// <summary>
        /// The NPC is in a reaction to the player (G3 AI mode Reaction: warning, or observing after a warning) as of the
        /// last assessment; such an NPC is not in its routine, so no special player hit applies (fix-28).
        /// </summary>
        public bool Reacting(ActorId npc) => _warnings.ContainsKey(npc) || Observing(npc, _now);

        bool Observing(ActorId npc, double realNow) => _observingUntil.TryGetValue(npc, out var until) && realNow < until;

        /// <summary>
        /// The player took <paramref name="stacks"/> stacks of <paramref name="item"/> at <paramref name="where"/> in
        /// <paramref name="zone"/> (from the ground, a shelf or a chest). Unless the item is free, the theft is counted
        /// against the zone's enclave (seen or not; AB master thieves excepted) and witnesses within 1000 may react.
        /// </summary>
        public void PlayerTookItem(string item, WorldPose where, OwnedZone zone, int stacks = 1, string useType = null,
                                   bool dropped = false, bool lockedChestWithOwnKey = false)
        {
            if (_catalogue.IsFree(item, useType, dropped, lockedChestWithOwnKey) || _catalogue.ZoneFreed(zone, StoryFlags)) return;
            CountTheft(zone, stacks);
            _events.Publish(new PerceptionBroadcast(BroadcastKind.TakeItem, ActorId.Player, default, where, AttackReason.Theft, item, zone,
                                                    _world.IndoorZoneOf(ActorId.Player)));
        }

        /// <summary>The player used an owned interactive object (opened a chest or door, lay in a bed …).</summary>
        public void PlayerUsedObject(string objectId, WorldPose where, OwnedZone zone, bool lockedChestWithOwnKey = false)
        {
            if (lockedChestWithOwnKey || _catalogue.ZoneFreed(zone, StoryFlags)) return;
            _events.Publish(new PerceptionBroadcast(BroadcastKind.UseInteractObject, ActorId.Player, default, where, AttackReason.UseInteractObject,
                                                    objectId, zone, _world.IndoorZoneOf(ActorId.Player)));
        }

        /// <summary>The player entered an owned zone: NPCs who sleep there come to see.</summary>
        public void PlayerEnteredZone(OwnedZone zone, WorldPose where)
        {
            if (!zone.IsOwned) return;
            _events.Publish(new PerceptionBroadcast(BroadcastKind.EnterZone, ActorId.Player, default, where, zone: zone,
                                                    sourceIndoorZone: zone.Indoor ? zone.Id : null));
        }

        void CountTheft(in OwnedZone zone, int stacks)
        {
            if (_catalogue.TheftHidden(_options, _playerKnows)) return;
            _ledger.Count(_catalogue.TheftCountedAgainst(zone), CrimeType.Theft, Math.Max(1, stacks));
        }

        /// <summary>
        /// One active-perception verdict of <paramref name="npc"/> about the player it noticed (perception steps 2, 5, 6,
        /// 8 and 11): a grudge or hostility attacks; trespassing and a drawn weapon run the warning sequence; sneaking
        /// makes it suspicious. A weapon warning, once started, goes on by itself (G3 ZS_ReactToWeapon with its 1 Hz
        /// callback OnReactToWeapon) until the weapon is put away ("WiseMove"), the player is beyond 1000 ("YesGetLost") or
        /// the attack at 8 s. <paramref name="realNow"/> is real seconds.
        /// </summary>
        public Reaction AssessPlayer(ActorId npc, Percept player, PlayerConduct conduct, double realNow)
        {
            _now = realNow;
            if (!_attitudes.Facts.TryGetProfile(npc, out var self) || self.InPlayerParty) return Reaction.None;
            // CP: an entity without a collision shape talks within 2000 when it has an important info.
            if (HasCollisionShape != null && !HasCollisionShape(npc) && player.Distance < NoCollisionTalkRange &&
                TalkAllowed(self, conduct) && HasImportantInfo != null && HasImportantInfo(npc))
                return Act(self, Reaction.Of(ReactionKind.Talk, ActorId.Player));
            // The guide leading the hero is his party leader (StartGuidePlayer): Friendly by the party bond, so no weapon or
            // zone warning, no grudge attack. A warning or warning fight it started before it began to guide (the hero still
            // held his weapon after a fight) ends here (editor-runner-24: Mort, Tufail fought the hero they led) [interp].
            if (LeadsPlayer(npc))
            {
                StopWarning(npc);
                EndWarningFight?.Invoke(npc);
                return GateOrTalk(self, player, conduct); // its talks (the arrival info) go on
            }
            if (_attitudes.IsUpset(npc, out var grudge)) return Act(self, Reaction.Attack(ActorId.Player, grudge));
            var attitude = _attitudes.Of(npc, ActorId.Player);
            if (attitude == Attitude.Hostile) return Act(self, Reaction.Attack(ActorId.Player, AttackReason.Enemy));
            if (_warningReasons.TryGetValue(npc, out var running) && running == AttackReason.ReactToWeapon)
                return ContinueWeaponWarning(self, player, conduct, realNow);
            if (!_warnings.ContainsKey(npc) && Observing(npc, realNow)) return Reaction.None; // ZS_ObserveSuspect: watching already

            var zone = ZoneVerdict(self, conduct.Zone);
            if (zone == ZoneReaction.Warn)
            {
                // One warner per zone owner: an owner that notices the intruder while another one warns him watches.
                if (ZoneWarner(conduct.Zone) is ActorId warner && warner != npc)
                {
                    StopWarning(npc);
                    return Act(self, Reaction.Of(ReactionKind.Observe, ActorId.Player, AttackReason.Intruder));
                }
                var warned = Warn(self, _perception.ZoneWarnings, AttackReason.Intruder, realNow, false);
                if (_warnings.ContainsKey(npc)) _zoneWarners[ZoneKey(conduct.Zone)] = npc;
                return warned;
            }
            if (NoticesWeapon(self, player, conduct, attitude, realNow))
                return Warn(self, _perception.WeaponWarnings, AttackReason.ReactToWeapon, realNow, conduct.SpellDrawn);
            StopWarning(npc);
            if (zone == ZoneReaction.Observe) return Act(self, Reaction.Of(ReactionKind.Observe, ActorId.Player, AttackReason.Intruder));
            var talk = GateOrTalk(self, player, conduct);
            if (talk.Kind != ReactionKind.None) return talk;
            // G3 AssessPlayerSneaking: not an NPC Friendly to the player (friend-type NPCs, fix-78)
            if (conduct.Sneaking && player.Distance <= _perception.SneakRange && attitude != Attitude.Friendly)
                return Act(self, Reaction.Of(ReactionKind.Observe, ActorId.Player));
            return Reaction.None;
        }

        /// <summary>
        /// G3 AssessActivePerception, the gate-guard and important-info steps (after the weapon, before sneaking):
        /// a gate guard attacks a player who came 80 closer to its guard point after the second warning, resets its
        /// warnings beyond 500 and otherwise talks if it has an important info (the warnings are such infos); any other
        /// NPC within 500 with a free line of sight (CP) and an important info talks to a player without a drawn weapon
        /// (AssessTalk: FullStop + ZS_Talk where both stand, the NPC turns to face him; there is no walk-up in G3) unless
        /// <see cref="MayStartTalk"/> forbids it (player falling/swimming, NPC hostile/fighting/fleeing/transformed).
        /// </summary>
        Reaction GateOrTalk(in AttitudeProfile self, Percept player, PlayerConduct conduct)
        {
            bool gateGuard = GateGuards != null && GateGuards.IsGateGuard(self.Id);
            if (gateGuard)
            {
                if (!_world.TryGetPose(self.Id, out var pose) || !_world.TryGetPose(ActorId.Player, out var playerPose)) return Reaction.None;
                var verdict = GateGuards.Assess(self.Id, pose, playerPose);
                if (verdict == GateGuardVerdict.Attack) return Act(self, Reaction.Attack(ActorId.Player, AttackReason.GateGuard));
                if (verdict == GateGuardVerdict.None) return Reaction.None;
            }
            else
            {
                if (player.Distance >= ImportantInfoRange || HasImportantInfo == null) return Reaction.None;
                if (conduct.WeaponDrawn) return Reaction.None; // AssessActivePerception: not while the player holds a weapon
                if (!_world.TryGetPose(self.Id, out var pose) || !_world.TryGetPose(ActorId.Player, out var playerPose)) return Reaction.None;
                if (!(LineOfSight ?? OpenLineOfSight.Instance).IsClear(pose, playerPose)) return Reaction.None;
            }
            if (!TalkAllowed(self, conduct)) return Reaction.None;
            if (HasImportantInfo == null || !HasImportantInfo(self.Id)) return Reaction.None;
            return Act(self, Reaction.Of(ReactionKind.Talk, ActorId.Player));
        }

        bool TalkAllowed(in AttitudeProfile self, in PlayerConduct conduct) =>
            MayStartTalk(conduct, _attitudes.Of(self.Id, ActorId.Player) == Attitude.Hostile, InFight != null && InFight(self.Id),
                         self.Fleeing, self.Transformed);

        /// <summary>
        /// G3 AssessPlayerWeapon: a humanoid, not friend-type, not a slave, not a gate guard, not Friendly to the player,
        /// within 700, that is not still observing the player after an earlier weapon warning.
        /// </summary>
        bool NoticesWeapon(in AttitudeProfile self, Percept player, in PlayerConduct conduct, Attitude attitude, double realNow)
        {
            if (!conduct.WeaponDrawn || player.Distance > _perception.WeaponRange || attitude == Attitude.Friendly) return false;
            if (!self.Humanoid || self.FriendType || self.Alignment == SlaveAlignment) return false;
            if (GateGuards != null && GateGuards.IsGateGuard(self.Id)) return false;
            return !(_observingUntil.TryGetValue(self.Id, out var until) && realNow < until);
        }

        Reaction ContinueWeaponWarning(in AttitudeProfile self, Percept player, in PlayerConduct conduct, double realNow)
        {
            if (!conduct.WeaponDrawn) return EndWeaponWarning(self, "WiseMove", realNow);
            if (player.Distance > WeaponWarningReach) return EndWeaponWarning(self, "YesGetLost", realNow);
            return Warn(self, _perception.WeaponWarnings, AttackReason.ReactToWeapon, realNow, conduct.SpellDrawn);
        }

        /// <summary>ZS_ReactToWeapon_End: the warner says its line, holsters and observes the player for a while.</summary>
        Reaction EndWeaponWarning(in AttitudeProfile self, string line, double realNow)
        {
            StopWarning(self.Id);
            _observingUntil[self.Id] = realNow + ObserveAfterWarning;
            return Act(self, Reaction.Of(ReactionKind.Observe, ActorId.Player, AttackReason.ReactToWeapon, line));
        }

        enum ZoneReaction { None, Observe, Warn }

        /// <summary>
        /// G3 AssessPlayerZone: in an owned zone that does not welcome the player, NPCs politically friendly to the owner
        /// warn (only within 330 height, else nothing), everyone else observes.
        /// </summary>
        ZoneReaction ZoneVerdict(in AttitudeProfile self, in OwnedZone zone)
        {
            if (!zone.IsOwned || zone.Security == ZoneSecurity.None) return ZoneReaction.None;
            if (ZoneWelcomes(zone)) return ZoneReaction.None;
            if (zone.Security == ZoneSecurity.Public) return ZoneReaction.Observe;
            // G3 GetPoliticalAttitude(self, owner) == Friendly: an NPC's own alignment counts, not its enclave's, so the slaves
            // of an orc town (Slave is Neutral to everyone) only observe; a member of the owning enclave counts when loyal to
            // it (covers members without an alignment).
            bool owner = (self.Enclave == zone.Owner && _attitudes.LoyalToEnclave(self)) ||
                         _attitudes.PoliticallyFriendly(self.Alignment, zone.OwnerAlignment);
            if (!owner) return ZoneReaction.Observe;
            if (!_world.TryGetPose(self.Id, out var pose) || !_world.TryGetPose(ActorId.Player, out var playerPose)) return ZoneReaction.None;
            return Senses.HeightDifference(pose, playerPose) > _perception.ZoneMaxHeight ? ZoneReaction.None : ZoneReaction.Warn;
        }

        bool LeadsPlayer(ActorId npc) =>
            _attitudes.Facts.TryGetProfile(ActorId.Player, out var player) && !player.PartyLeader.IsEmpty && player.PartyLeader == npc;

        static string ZoneKey(in OwnedZone zone) => zone.Owner ?? zone.Id ?? "";

        /// <summary>
        /// The NPC warning the intruder for the zone's owner, if one still runs its intruder sequence (null = none). G3 has
        /// no such lock: the first NPC's warning (AssessWarn, 1000) turns the owners near it into observers, but owners
        /// farther apart that notice the hero in the same second each start ZS_ReactToZone. One voice per zone is what the
        /// player hears in the original's towns [interp]; the perception driver scans the nearest NPCs first, so the
        /// nearest owner that notices him is the one that warns.
        /// </summary>
        public ActorId? ZoneWarner(in OwnedZone zone)
        {
            var key = ZoneKey(zone);
            if (!_zoneWarners.TryGetValue(key, out var warner)) return null;
            if (_warningReasons.TryGetValue(warner, out var reason) && reason == AttackReason.Intruder) return warner;
            _zoneWarners.Remove(key);
            return null;
        }

        void ReleaseZone(ActorId npc)
        {
            string key = null;
            foreach (var kv in _zoneWarners)
                if (kv.Value == npc) { key = kv.Key; break; }
            if (key != null) _zoneWarners.Remove(key);
        }

        /// <summary>The zone's attitude to the player (G3 GetZoneAttitudeToPlayer): Friendly once fame reaches 75.</summary>
        public bool ZoneWelcomes(in OwnedZone zone)
        {
            switch (zone.Security)
            {
                case ZoneSecurity.Political: return _attitudes.MeetsFame(FameKind.Political, zone.OwnerAlignment ?? zone.Owner);
                case ZoneSecurity.Enclave: return _attitudes.MeetsFame(FameKind.Enclave, zone.Owner);
                case ZoneSecurity.Public: return false;
                default: return true;
            }
        }

        Reaction Warn(in AttitudeProfile self, WarningTimings timings, AttackReason reason, double realNow, bool spell)
        {
            // A sequence for another reason (an intruder who now only holds a weapon) starts over.
            if (_warningReasons.TryGetValue(self.Id, out var was) && was != reason) StopWarning(self.Id);
            if (!_warnings.TryGetValue(self.Id, out var sequence))
            {
                _warnings[self.Id] = sequence = new WarningSequence(timings);
                _warningReasons[self.Id] = reason;
            }
            switch (sequence.Update(true, realNow))
            {
                case WarningStep.FirstWarning:
                case WarningStep.SecondWarning:
                    AnnounceWarning(self.Id);
                    return Act(self, Reaction.Of(ReactionKind.Warn, ActorId.Player, reason, WarningLine(self.Id, reason, sequence.Step, spell)));
                case WarningStep.Attack:
                    StopWarning(self.Id);
                    return Act(self, Reaction.Attack(ActorId.Player, reason));
                default:
                    return Reaction.None;
            }
        }

        /// <summary>
        /// The shared line of a warning (ZS_ReactToZone / ZS_ReactToWeapon): "GetLost" / "ISaidGetOutOfHere" to an intruder;
        /// "WeaponDown" / "ISaidWeaponDown" to a weapon ("StopMagic" / "ISaidStopMagic" to a spell), and
        /// "LookingForTroubleAgain" first from an NPC that has beaten the player before.
        /// </summary>
        string WarningLine(ActorId npc, AttackReason reason, WarningStep step, bool spell)
        {
            bool second = step == WarningStep.SecondWarning;
            if (reason == AttackReason.Intruder) return second ? "ISaidGetOutOfHere" : "GetLost";
            if (second) return spell ? "ISaidStopMagic" : "ISaidWeaponDown";
            if (_ledger.FightOf(npc).Result == FightResult.PlayerLost) return "LookingForTroubleAgain";
            return spell ? "StopMagic" : "WeaponDown";
        }

        /// <summary>G3 ZS_ReactToSneak's shared line.</summary>
        public const string SneakLine = "WhatsThisSupposedToBe";

        /// <summary>
        /// What an NPC that starts to observe the player says (null = nothing): a line the rules chose ("WiseMove",
        /// "YesGetLost", "YouDisturbedMySlumber" …), else only ZS_ReactToSneak calls out ("WhatsThisSupposedToBe").
        /// ZS_ObserveSuspect itself is silent: the zone observers of AssessPlayerZone and the listeners of a warning (AssessWarn)
        /// only watch. "WhyAreYouInHere" belongs to ZS_ObserveZonePlayer, the resident that walked up to the intruder
        /// (ZS_GotoPlayerZone, not modelled) — fix-78: the hero's friends shouted it at him in every house.
        /// </summary>
        public static string ObserveLine(in Reaction r, BroadcastKind? cause)
        {
            if (r.Kind != ReactionKind.Observe) return null;
            if (r.Line != null) return r.Line;
            return cause == null && r.Reason == AttackReason.None ? SneakLine : null;
        }

        /// <summary>Each warning is announced to the NPCs around, who start watching the player.</summary>
        void AnnounceWarning(ActorId warner)
        {
            if (_world.TryGetPose(warner, out var pose))
                _events.Publish(new PerceptionBroadcast(BroadcastKind.Warn, warner, ActorId.Player, pose, sourceIndoorZone: _world.IndoorZoneOf(warner)));
        }

        public void StopWarning(ActorId npc)
        {
            _warnings.Remove(npc);
            _warningReasons.Remove(npc);
            ReleaseZone(npc);
        }

        /// <summary>The warning step an NPC has reached (None = not warning).</summary>
        public WarningStep WarningOf(ActorId npc) => _warnings.TryGetValue(npc, out var s) ? s.Step : WarningStep.None;

        /// <summary>
        /// G3 AssessTarget called by a script (PickPocket's failure, reason Theft): the NPC attacks <paramref name="target"/>
        /// (or flees, by the combat system's panic rule) and, against the player, memorizes the crime of the reason
        /// (enclave or personal, <see cref="CrimeCatalogue.Memorize"/>). False when the NPC cannot fight.
        /// </summary>
        public bool AssessTarget(ActorId npc, ActorId target, AttackReason reason)
        {
            if (!_attitudes.Facts.TryGetProfile(npc, out var self)) return _engage(npc, target, reason);
            return Act(self, Reaction.Attack(target, reason)).Kind == ReactionKind.Attack;
        }

        Reaction Act(in AttitudeProfile self, Reaction reaction)
        {
            if (reaction.Kind == ReactionKind.Attack)
            {
                if (!_engage(self.Id, reaction.Target, reaction.Reason)) return Reaction.None;
                if (reaction.Target.IsPlayer) _catalogue.Memorize(_ledger, self, CrimeCatalogue.CrimeOf(reaction.Reason));
            }
            _events.Publish(new NpcReacted(self.Id, reaction, null));
            return reaction;
        }

        /// <summary>
        /// A fight against the player ended without a result (the player got away): the NPC stays upset for a while,
        /// depending on the reason (G3 LastFightAgainstPlayer = Cancelled).
        /// </summary>
        public void FightCancelled(ActorId npc, AttackReason reason) =>
            _ledger.RecordFight(npc, FightResult.Cancelled, reason, _gameHours());

        /// <summary>
        /// The enclave is attacked (G3 NotifyEnclave): up to 10 of its nearest active members run at the attacker, and a
        /// quiet enclave goes into Attack (<see cref="Enclaves"/>; whether it then gives up, which is liberation, is the
        /// world state's once-a-second test). A fleeing enclave raises no alarm. Returns the defenders sent.
        /// </summary>
        /// <param name="murder">
        /// The alarm of a kill (G3 NotifyEnclave mode 0): the enclave goes into Attack only once it knows of a murder by the
        /// player (KnownPlayerCrime Murder) or when its boss was killed (<paramref name="bossKilled"/>, until Beliar_Contact
        /// succeeded: <see cref="BossKillAlarms"/>); the defenders run either way.
        /// </param>
        /// <param name="victim">The member that was killed (CP: Faring's three story orcs raise no alarm).</param>
        public IReadOnlyList<ActorId> RaiseEnclaveAlarm(string enclave, ActorId attacker, bool murder = false, bool bossKilled = false,
                                                        ActorId victim = default)
        {
            if (enclave == null || !_world.TryGetPose(attacker, out var at)) return Array.Empty<ActorId>();
            if (Enclaves != null)
            {
                var state = Enclaves.Of(enclave);
                if (state.Status == EnclaveStatus.Flee || EnclaveAlarm.IgnoresVictim(enclave, victim)) return Array.Empty<ActorId>();
                bool attack = !murder || _ledger.Enclave(enclave).Known == CrimeType.Murder || (bossKilled && (BossKillAlarms?.Invoke() ?? true));
                if (attack && state.Status != EnclaveStatus.Attack) Enclaves.SetStatus(enclave, EnclaveStatus.Attack);
            }
            var members = new List<Defender>();
            foreach (var r in _world.Receivers())
            {
                if (!_attitudes.Facts.TryGetProfile(r.Id, out var p) || p.Enclave != enclave || !_attitudes.LoyalToEnclave(p)) continue;
                members.Add(new Defender(r.Id, Senses.Distance(r.Pose, at), BroadcastReception.Awake(r.Mode) && !p.CannotFight));
            }
            var sent = EnclaveAlarm.Defenders(members, _rules);
            foreach (var d in sent) _engage(d, attacker, AttackReason.Enemy);
            return sent.ToList();
        }

        /// <summary>Forgets the warnings in progress (after loading; they are not saved).</summary>
        public void ClearTransient()
        {
            _warnings.Clear();
            _warningReasons.Clear();
            _observingUntil.Clear();
            _zoneWarners.Clear();
        }
    }
}
