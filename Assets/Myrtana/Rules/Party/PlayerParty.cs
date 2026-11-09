using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>How an NPC belongs to the hero's party (G3 gEPartyMemberType; Docs/party.md §1).</summary>
    public enum PartyMemberType { None, Party, Mob, Slave, Controlled, Summoned }

    /// <summary>
    /// The party topics of a conversation partner (G3 info condition types Join, MobJoin, SlaveJoin, Dismiss, MobDismiss,
    /// Wait and the CP type Companion; dialog.json "condTypes").
    /// </summary>
    public enum PartyOffer { Join, MobJoin, SlaveJoin, Dismiss, MobDismiss, Wait, Companion }

    public enum PartyChange { Joined, Waits, Dismissed }

    /// <summary>Someone joined, waits or left the hero's party; <see cref="Message"/> is G3's screen message (empty = none).</summary>
    public readonly struct PartyChanged : IGameEvent
    {
        public PartyChanged(ActorId member, PartyMemberType type, PartyChange change, TextId message)
        {
            Member = member;
            Type = type;
            Change = change;
            Message = message;
        }

        public ActorId Member { get; }
        public PartyMemberType Type { get; }
        public PartyChange Change { get; }
        /// <summary>GO_PartyJoin, GO_MobJoin … with <c>$(name)</c> = the member's name.</summary>
        public TextId Message { get; }
    }

    /// <summary>
    /// The hero's guide turned away from him because the hero attacked its enclave (G3 NotifyEnclave: the player's party
    /// leader is set to None; the guide's FollowNpc quest is then Lost, <c>FollowQuestGuides</c>).
    /// </summary>
    public readonly struct GuideTurnedAway : IGameEvent
    {
        public GuideTurnedAway(ActorId guide, string quest)
        {
            Guide = guide;
            Quest = quest;
        }

        public ActorId Guide { get; }
        /// <summary>The FollowNpc quest it guided for (null when unknown).</summary>
        public string Quest { get; }
    }

    /// <summary>One member of the hero's party.</summary>
    public sealed class PartyMember
    {
        internal PartyMember(ActorId id, PartyMemberType type, long order)
        {
            Id = id;
            Type = type;
            Order = order;
        }

        public ActorId Id { get; }
        public PartyMemberType Type { get; internal set; }
        /// <summary>Told to wait (G3 PSParty.Waiting): stands where it is until it is asked to come along again.</summary>
        public bool Waiting { get; internal set; }
        /// <summary>Join order (G3 keeps the members in the leader's member list in this order).</summary>
        internal long Order { get; set; }

        public override string ToString() => $"{Id} {Type}{(Waiting ? " waiting" : "")}";
    }

    /// <summary>An NPC leads the hero to a place (G3 StartGuidePlayer for a FollowNpc quest; Docs/party.md §4).</summary>
    public sealed class PartyGuide
    {
        public PartyGuide(ActorId npc, string destination, string quest)
        {
            Npc = npc;
            Destination = destination;
            Quest = quest;
        }

        public ActorId Npc { get; }
        /// <summary>The quest's destination entity: a named place or an NPC.</summary>
        public string Destination { get; }
        public string Quest { get; }
    }

    /// <summary>
    /// The hero's party in Gothic 3 Community Patch 1.75 (Docs/party.md): companions (member type Party), mob members
    /// and freed slaves, each led by the player, possibly told to wait; and the NPC that guides the hero on a FollowNpc
    /// quest. Pure state and the G3 rules of the party topics (Join/Dismiss/Wait …), the join limit (one active Party
    /// companion, CanJoinPlayerParty) and what teleports, deaths, deliveries and enclave alarms do to members. Movement
    /// is the simulation's and the presentation's business.
    /// </summary>
    public sealed class PlayerParty : IActorStore
    {
        public static readonly TextId PartyJoinMessage = new TextId("GO_PartyJoin"), MobJoinMessage = new TextId("GO_MobJoin"),
                                      SlaveJoinMessage = new TextId("GO_SlaveJoin"), PartyDismissMessage = new TextId("GO_PartyDismiss"),
                                      MobDismissMessage = new TextId("GO_MobDismiss"), WaitMessage = new TextId("GO_SlaveDismiss"),
                                      NoPartyMessage = new TextId("GO_NoParty"),
                                      ControlJoinMessage = new TextId("GO_ControlJoin"), ControlDismissMessage = new TextId("GO_ControlDismiss");

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): it leaves the party like a dead member (a guide stops guiding).</summary>
        public void Forget(ActorId actor)
        {
            MemberDied(actor);
        }

        public bool Holds(ActorId actor) => TryGet(actor, out _) || IsGuiding(actor);

        /// <summary>G3 follow distance: (slot + 3) × 150 cm, slot = index among the members that do not wait (FUN_10012d40).</summary>
        public const float FollowStepMetres = 1.5f;
        /// <summary>Follow distance of an NPC that is not in the list of following members (500 cm).</summary>
        public const float DefaultFollowMetres = 5f;

        readonly ActorRoles _roles;
        readonly IEventBus _events;
        readonly List<PartyMember> _members = new List<PartyMember>();
        long _nextOrder;

        public PlayerParty(ActorRoles roles, IEventBus events = null)
        {
            _roles = roles ?? throw new ArgumentNullException(nameof(roles));
            _events = events;
        }

        /// <summary>
        /// Whether a member is within the processing range around the hero (G3 PSNavigation::IsInProcessingRange; set by
        /// the session from the NPCs' detail level). Defaults to "yes".
        /// </summary>
        public Func<ActorId, bool> InProcessingRange { get; set; } = _ => true;

        /// <summary>The members in join order.</summary>
        public IReadOnlyList<PartyMember> Members => _members;

        /// <summary>The NPC guiding the hero (FollowNpc quest), or null.</summary>
        public PartyGuide Guide { get; private set; }

        public bool TryGet(ActorId npc, out PartyMember member)
        {
            // a loop, not FirstOrDefault: called per NPC per frame (attitudes, the simulation's escorts), no closure (perf-9)
            for (int i = 0; i < _members.Count; i++)
                if (_members[i] != null && _members[i].Id == npc)
                {
                    member = _members[i];
                    return true;
                }
            member = null;
            return false;
        }

        /// <summary>The NPC's party leader is the player (G3 "led by the player").</summary>
        public bool IsLedByPlayer(ActorId npc) => TryGet(npc, out _);

        /// <summary>
        /// A defeat or kill by <paramref name="actor"/> counts as the player's (XP once, DefeatedByPlayer): the hero himself or
        /// a member he leads that is not summoned (CP Kill 0x1001bc30 / Defeat 0x10027510, GiveXP 0x1004e160). The guide
        /// does not count: it leads the hero, not the other way round.
        /// </summary>
        public bool DefeatsCountForPlayer(ActorId actor) =>
            actor.IsPlayer || (IsLedByPlayer(actor) && TypeOf(actor) != PartyMemberType.Summoned);

        /// <summary>
        /// The hero, a member he leads or the NPC guiding him: all of them have the hero as party leader, so they never
        /// pick each other as enemies (G3 party members share the leader's side) <b>[interp: the guide counted in]</b>.
        /// </summary>
        public bool IsOnHeroSide(ActorId actor) => actor.IsPlayer || IsLedByPlayer(actor) || IsGuiding(actor);

        /// <summary>
        /// A fight between two NPCs of the hero's side (members, guide): it began before one of them joined or started
        /// guiding (G3 JoinPlayerParty / StartGuidePlayer set the party leader to the player, and NPCs of one party are
        /// Friendly to each other), so it has no reason any more and ends. Fights with the hero himself are not affected.
        /// </summary>
        public bool IsFightWithinSide(ActorId attacker, ActorId target) =>
            !attacker.IsEmpty && !target.IsEmpty && !attacker.IsPlayer && !target.IsPlayer && attacker != target
            && IsOnHeroSide(attacker) && IsOnHeroSide(target);

        public bool IsWaiting(ActorId npc) => TryGet(npc, out var m) && m.Waiting;

        public PartyMemberType TypeOf(ActorId npc) => TryGet(npc, out var m) ? m.Type : PartyMemberType.None;

        /// <summary>Follows the hero now: a member that does not wait.</summary>
        public bool IsFollowing(ActorId npc) => TryGet(npc, out var m) && !m.Waiting;

        /// <summary>Companion in the CP sense: Party, Mob or Slave member led by the player (condition type Companion).</summary>
        public bool IsCompanion(ActorId npc) =>
            TryGet(npc, out var m) && m.Type != PartyMemberType.None && m.Type != PartyMemberType.Controlled && m.Type != PartyMemberType.Summoned;

        /// <summary>Index of a following member among the members that do not wait (join order); −1 otherwise.</summary>
        public int FollowSlot(ActorId npc)
        {
            int slot = 0;
            foreach (var m in _members)
            {
                if (m.Waiting) continue;
                if (m.Id == npc) return slot;
                slot++;
            }
            return -1;
        }

        /// <summary>How close a member keeps to the hero, metres: (slot + 3) × 1.5, or 5 when it is not following.</summary>
        public float FollowDistance(ActorId npc)
        {
            int slot = FollowSlot(npc);
            return slot < 0 ? DefaultFollowMetres : (slot + 3) * FollowStepMetres;
        }

        /// <summary>
        /// Whether a party topic is available with <paramref name="npc"/> (gCInfo_PS::IsAvailable, CP 1.75): Join needs
        /// PartyEnabled and the NPC not following (a waiting member may be asked again); MobJoin and SlaveJoin need their
        /// flag and the NPC not led by the player; Dismiss needs it led by the player with PartyEnabled (or controlled);
        /// MobDismiss needs MobEnabled and led; Wait a led, not controlled member that does not wait yet.
        /// </summary>
        public bool Offers(ActorId npc, PartyOffer offer)
        {
            bool led = TryGet(npc, out var m);
            switch (offer)
            {
                case PartyOffer.Join: return _roles.Has(npc, ActorRole.Party) && (!led || m.Waiting);
                // CP AreConditionsFulfilled 14/28/29 share one test: the flag and not (led by the player and not waiting) —
                // a waiting slave or mob member is asked to follow again with the same topic. fix-100: ours took
                // "not led", so Kliff told to wait had no topic left and stayed behind for good.
                case PartyOffer.MobJoin: return _roles.Has(npc, ActorRole.Mob) && (!led || m.Waiting);
                case PartyOffer.SlaveJoin: return _roles.Has(npc, ActorRole.Slavery) && (!led || m.Waiting);
                case PartyOffer.Dismiss: return led && (_roles.Has(npc, ActorRole.Party) || m.Type == PartyMemberType.Controlled);
                case PartyOffer.MobDismiss: return led && _roles.Has(npc, ActorRole.Mob);
                case PartyOffer.Wait: return led && m.Type != PartyMemberType.Controlled && !m.Waiting;
                case PartyOffer.Companion: return IsCompanion(npc);
                default: return false;
            }
        }

        /// <summary>
        /// G3 CanJoinPlayerParty (the Join pre-check): a new companion is refused while another Party member follows the
        /// hero within the processing range; waiting or distant companions are dismissed when the new one joins.
        /// </summary>
        public bool HasRoomFor(ActorId npc) => ActiveCompanionOtherThan(npc).IsEmpty;

        /// <summary>The Party-type member that blocks <paramref name="npc"/> from joining (empty = none).</summary>
        public ActorId ActiveCompanionOtherThan(ActorId npc)
        {
            foreach (var m in _members)
                if (m.Type == PartyMemberType.Party && m.Id != npc && !m.Waiting && InProcessingRange(m.Id)) return m.Id;
            return default;
        }

        /// <summary>
        /// The NPC joins the hero (G3 JoinPlayerParty / JoinPlayerMob / JoinPlayerSlavery): leader = player, not waiting,
        /// the member type, the screen message. A new Party companion first dismisses the waiting or distant one.
        /// </summary>
        public void Join(ActorId npc, PartyMemberType type)
        {
            if (npc.IsEmpty || npc.IsPlayer || type == PartyMemberType.None) return;
            if (type == PartyMemberType.Party)
                foreach (var other in _members.Where(m => m.Type == PartyMemberType.Party && m.Id != npc).ToList())
                    if (other.Waiting || !InProcessingRange(other.Id)) Dismiss(other.Id);
            if (!TryGet(npc, out var member))
            {
                member = new PartyMember(npc, type, _nextOrder++);
                _members.Add(member);
            }
            member.Type = type;
            member.Waiting = false;
            Publish(member, PartyChange.Joined, JoinMessageOf(type));
        }

        /// <summary>The member waits where it is (G3 WaitPlayerParty, message GO_SlaveDismiss "… is waiting").</summary>
        public void Wait(ActorId npc)
        {
            if (!TryGet(npc, out var member) || member.Waiting) return;
            member.Waiting = true;
            Publish(member, PartyChange.Waits, WaitMessage);
        }

        /// <summary>The member leaves the party (G3 DismissPlayerParty / DismissPlayerMob) and returns to its routine.</summary>
        public void Dismiss(ActorId npc) => Remove(npc, announce: true);

        /// <summary>G3 StopBringNPC: PartyEnabled off, then dismissed.</summary>
        public void StopBringNpc(ActorId npc)
        {
            _roles.Set(npc, ActorRole.Party, false);
            Dismiss(npc);
        }

        /// <summary>
        /// A BringNpc delivery is done (gCInfo_PS OnEndInfo for a Delivery info): the NPC is released (StopBringNPC) and its
        /// Party, Slavery and Mob flags are cleared.
        /// </summary>
        public void Delivered(ActorId npc)
        {
            StopBringNpc(npc);
            _roles.Set(npc, ActorRole.Slavery, false);
            _roles.Set(npc, ActorRole.Mob, false);
        }

        /// <summary>
        /// The port's option (player feedback item 5; off = G3): members that follow the hero keep following through a
        /// teleport and catch up at the arrival point (the follow rules put a far follower behind the hero on the
        /// NavMesh, Docs/party.md section 3). Members told to wait keep waiting.
        /// </summary>
        public bool FollowsHeroOnTeleport { get; set; }

        /// <summary>
        /// The hero teleported (G3 MagicTeleport): Party, Mob and Slave members stay behind and wait; controlled and
        /// summoned members are released. With <see cref="FollowsHeroOnTeleport"/> the following members stay in the
        /// party and come along instead of waiting.
        /// </summary>
        public void PlayerTeleported()
        {
            foreach (var m in _members.ToList())
            {
                if (m.Type == PartyMemberType.Controlled || m.Type == PartyMemberType.Summoned) Remove(m.Id, announce: false);
                else if (!FollowsHeroOnTeleport) Wait(m.Id);
            }
        }

        /// <summary>The member leaves without a dismiss message (CP: a Mob member too far from the Fireclan's meeting point).</summary>
        public void LeaveSilently(ActorId npc) => Remove(npc, announce: false);

        /// <summary>A member died (G3 Kill: the dead leave their party). A dead guide stops guiding.</summary>
        public void MemberDied(ActorId npc)
        {
            Remove(npc, announce: false);
            if (Guide != null && Guide.Npc == npc) Guide = null;
        }

        /// <summary>
        /// CP NotifyEnclave: the hero's companions that belong to the alarmed enclave leave him, and a guide of that enclave
        /// stops guiding (<see cref="GuideTurnedAway"/>: G3 then sets the Running FollowNpc quest Lost, fix-190).
        /// </summary>
        public void EnclaveTurnedAgainstPlayer(string enclave, Func<ActorId, string> enclaveOf)
        {
            if (string.IsNullOrEmpty(enclave) || enclaveOf == null) return;
            foreach (var m in _members.ToList())
                if (string.Equals(enclaveOf(m.Id), enclave, StringComparison.Ordinal)) Remove(m.Id, announce: false);
            if (Guide != null && string.Equals(enclaveOf(Guide.Npc), enclave, StringComparison.Ordinal))
            {
                var guide = Guide;
                Guide = null;
                _events?.Publish(new GuideTurnedAway(guide.Npc, guide.Quest));
            }
        }

        /// <summary>G3 StartGuidePlayer: the NPC leads the hero to <paramref name="destination"/>.</summary>
        public void StartGuide(ActorId npc, string destination, string quest)
        {
            if (npc.IsEmpty) return;
            Guide = new PartyGuide(npc, destination, quest);
        }

        /// <summary>G3 StopGuidePlayer (for a given guide, or any when <paramref name="npc"/> is empty).</summary>
        public void StopGuide(ActorId npc = default)
        {
            if (Guide != null && (npc.IsEmpty || Guide.Npc == npc)) Guide = null;
        }

        public bool IsGuiding(ActorId npc) => Guide != null && Guide.Npc == npc;

        void Remove(ActorId npc, bool announce)
        {
            if (!TryGet(npc, out var member)) return;
            _members.Remove(member);
            if (announce) Publish(member, PartyChange.Dismissed, member.Type == PartyMemberType.Mob ? MobDismissMessage
                                 : member.Type == PartyMemberType.Controlled ? ControlDismissMessage : PartyDismissMessage);
            else Publish(member, PartyChange.Dismissed, default);
        }

        static TextId JoinMessageOf(PartyMemberType type)
        {
            switch (type)
            {
                case PartyMemberType.Mob: return MobJoinMessage;
                case PartyMemberType.Slave: return SlaveJoinMessage;
                case PartyMemberType.Party: return PartyJoinMessage;
                case PartyMemberType.Controlled: return ControlJoinMessage;
                default: return default;
            }
        }

        void Publish(PartyMember member, PartyChange change, TextId message) =>
            _events?.Publish(new PartyChanged(member.Id, member.Type, change, message));

        // ------------------------------------------------------------------------------------------- save

        internal void Restore(IEnumerable<(ActorId Id, PartyMemberType Type, bool Waiting)> members, PartyGuide guide)
        {
            _members.Clear();
            _nextOrder = 0;
            foreach (var (id, type, waiting) in members)
                _members.Add(new PartyMember(id, type, _nextOrder++) { Waiting = waiting });
            Guide = guide;
        }
    }

    /// <summary>Save section "party": the members in join order with type and waiting flag, and the guide.</summary>
    public sealed class PartySaveSection : ISaveParticipant
    {
        readonly PlayerParty _party;

        public PartySaveSection(PlayerParty party) => _party = party;

        public string Section => "party";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            w.WriteInt32(_party.Members.Count);
            foreach (var m in _party.Members)
            {
                w.WriteString(m.Id.Name);
                w.WriteInt32((int)m.Type);
                w.WriteBool(m.Waiting);
            }
            var g = _party.Guide;
            w.WriteBool(g != null);
            if (g == null) return;
            w.WriteString(g.Npc.Name);
            w.WriteString(g.Destination);
            w.WriteString(g.Quest);
        }

        public void Read(ISaveReader r, int version)
        {
            int count = r.ReadInt32();
            var members = new List<(ActorId, PartyMemberType, bool)>();
            for (int i = 0; i < count; i++) members.Add((new ActorId(r.ReadString()), (PartyMemberType)r.ReadInt32(), r.ReadBool()));
            PartyGuide guide = null;
            if (r.ReadBool()) guide = new PartyGuide(new ActorId(r.ReadString()), r.ReadString(), r.ReadString());
            _party.Restore(members, guide);
        }
    }
}
