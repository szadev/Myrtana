using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero's party on screen (Docs/party.md §6): the escorts' fights (leash, fights within the hero's side) and the
    /// summons' look-out. G3 runs AssessActivePerception in OnFollowPlayer / OnGuidePlayer about once a second, but for an
    /// NPC led by the player it skips the hostile step unless the NPC is Summoned, and an NPC in AIMode PlayerParty senses
    /// only the player (<see cref="PartyLeash.LooksOut"/>): companions and the guide never attack on sight (fix-41
    /// guide, fix-106 companions: Cyrus attacked the rebel Iomar). They fight what attacks them, and defend the hero when
    /// he is hit or knocked out through the fight-sound and defeat broadcasts (ReactionSelector). A summoned creature
    /// attacks the nearest NPC fighting the hero (<see cref="DefendLeader"/>). After a leash break-off that look-out pauses
    /// until the member is back with the hero (<see cref="PartyLeash.LookoutResumes"/>).
    /// </summary>
    public sealed class PartyDriver : MonoBehaviour, ISessionView
    {
        const float ScanSeconds = 1f;

        readonly Dictionary<ActorId, float> _nextScan = new Dictionary<ActorId, float>();
        readonly Dictionary<ActorId, float> _brokeOffAt = new Dictionary<ActorId, float>(); // leash break-offs (look-out pause)
        readonly Dictionary<ActorId, FightWatch> _fights = new Dictionary<ActorId, FightWatch>(); // a member's fight: its last hit (stalemate)

        /// <summary>A member's running fight: its target and when a hit last landed or was taken (real seconds).</summary>
        sealed class FightWatch
        {
            public ActorId Target;
            public double Landed, Taken;
            public float LastHit;
        }
        readonly List<ActorId> _scouts = new List<ActorId>();
        NpcViewManager _npcs;
        GameSession _session;

        public void Init(NpcViewManager npcs) => _npcs = npcs;

        public void Bind(GameSession session)
        {
            _session = session;
            _nextScan.Clear();
            _brokeOffAt.Clear();
            _fights.Clear();
        }

        void Update()
        {
            if (_session == null || _npcs == null || _session.Party == null || _session.Dialog.IsActive) return;
            var party = _session.Party.Members;
            _scouts.Clear();
            foreach (var m in party.Members)
                if (!m.Waiting) _scouts.Add(m.Id);
            if (party.Guide != null) _scouts.Add(party.Guide.Npc);
            float now = Time.time;
            bool heroKnown = _session.Party.Positions.TryGetPose(ActorId.Player.Name, out var heroPose);
            var hero = heroKnown ? new Vector3(heroPose.X, heroPose.Y, heroPose.Z) : Vector3.zero;
            foreach (var id in _scouts)
            {
                if (_nextScan.TryGetValue(id, out var due) && now < due) continue;
                _nextScan[id] = now + ScanSeconds;
                if (EndFightWithinSide(id)) continue;
                if (heroKnown && Leash(id, hero)) continue;
                // fix-41/fix-106: only summons look out (CP AssessActivePerception skips the hostile step for party
                // leader == player except Summoned); companions and the guide fight what attacks them or the hero.
                if (heroKnown && PartyLeash.LooksOut(party.TypeOf(id), party.IsGuiding(id))) LookOut(id, hero);
            }
        }

        /// <summary>
        /// A following member or the guide gives its fight up (<see cref="PartyLeash"/>): a chase that took it too far from the
        /// hero or ran out (WaitForFlee), or a fight without a hit either way for <see cref="PartyLeash.StalemateSeconds"/>.
        /// An escort whose enemy attacks it fights back, as G3's does (fix-37: breaking off a defence was undone by the next
        /// hit, ReactToDamage, every second). Its view then follows (or leads) again and catches up. True when it broke off.
        /// </summary>
        bool Leash(ActorId member, Vector3 hero)
        {
            if (!_npcs.TryGetView(member, out var view)) return false;
            var brain = _session.NpcCombat.Get(member);
            if (brain == null || !brain.IsFighting)
            {
                // fix-58: the escort's fights in the log (runner-31: Fraser's boar fight at the hunt spot left no trace)
                if (_fights.TryGetValue(member, out var over))
                    Debug.Log($"[party] {member} fight vs {over.Target} over at {view.position} toHero={Vector3.Distance(view.position, hero):F1}");
                _fights.Remove(member);
                return false;
            }
            float now = Time.time;
            var target = brain.State.Target;
            if (!_fights.TryGetValue(member, out var fight) || fight.Target != target)
            {
                _fights[member] = fight = new FightWatch { Target = target, Landed = brain.State.LastHitLanded, Taken = brain.State.LastHitTaken, LastHit = now };
                Debug.Log($"[party] {member} fights {target} ({brain.State.Phase}) at {view.position} toHero={Vector3.Distance(view.position, hero):F1}");
            }
            if (brain.State.LastHitLanded != fight.Landed || brain.State.LastHitTaken != fight.Taken)
            {
                fight.Landed = brain.State.LastHitLanded;
                fight.Taken = brain.State.LastHitTaken;
                fight.LastHit = now;
            }
            float toHero = Vector3.Distance(view.position, hero);
            var mode = _session.Party.Escorts.ModeOf(member);
            var enemy = _session.NpcCombat.Get(target);
            bool attacked = enemy != null && enemy.State.Phase == CombatPhase.Fight && enemy.State.Target == member;
            float sinceHit = now - fight.LastHit;
            string why = PartyLeash.BreaksOff(mode, true, toHero, attacked) ? "too far from the hero"
                : PartyLeash.GivesUpChase(mode, brain.State.Phase == CombatPhase.WaitForFlee) ? "the chase ran out"
                : PartyLeash.GivesUpStalemate(mode, sinceHit) ? $"no hit for {sinceHit:F0} s"
                : null;
            if (why == null) return false;
            Debug.Log($"[party] leash {member} breaks off {brain.State.Phase} vs {target} ({why}) toHero={toHero:F1} mode={mode} attacked={attacked}");
            brain.Disengage();
            _fights.Remove(member);
            _brokeOffAt[member] = now;
            return true;
        }

        /// <summary>
        /// A member or the guide still fighting another NPC of the hero's side (the fight began before the guide quest or
        /// the join) gives it up (<see cref="PlayerParty.IsFightWithinSide"/>). True when it broke off.
        /// </summary>
        bool EndFightWithinSide(ActorId member)
        {
            var brain = _session.NpcCombat.Get(member);
            if (brain == null || !brain.IsFighting) return false;
            var target = brain.State.Target;
            if (!_session.Party.Members.IsFightWithinSide(member, target)) return false;
            Debug.Log($"[party] {member} stops fighting {target}: both on the hero's side");
            brain.Disengage();
            return true;
        }

        void LookOut(ActorId member, Vector3 hero)
        {
            if (!_npcs.TryGetView(member, out var view)) return;
            if (_session.Combat.Vitals.StateOf(member) != VitalState.Alive) return;
            var brain = _session.NpcCombat.Get(member);
            if (brain != null && brain.IsFighting) return;
            if (_brokeOffAt.TryGetValue(member, out var brokeOff))
            {
                if (!PartyLeash.LookoutResumes(Time.time - brokeOff, Vector3.Distance(view.position, hero))) return;
                _brokeOffAt.Remove(member);
            }
            DefendLeader(member, view.position);
        }

        /// <summary>
        /// The hero's summoned creatures (G3 OnFollowPlayer → AssessActivePerception, leader = player, member type Summoned):
        /// they never pick a merely hostile NPC; they attack (reason Enemy) the nearest NPC within 40 m that is fighting
        /// <b>the hero himself</b> (perception slot 1: combat mode with the player as its target) or fights in the arena.
        /// Fights of the hero they hear make them join through AssessFightSound like every party member
        /// (<c>ReactionSelector.FightSound</c>); the hero's focus does not matter.
        /// </summary>
        void DefendLeader(ActorId member, Vector3 at)
        {
            const float Reach = 40f; // slot 1 range 4000 cm
            ActorId best = default;
            float bestDist = Reach;
            foreach (var kv in _npcs.Views)
            {
                if (kv.Key == member || kv.Value == null || _session.Combat.Vitals.StateOf(kv.Key) != VitalState.Alive) continue;
                if (_session.Party.Members.IsOnHeroSide(kv.Key)) continue; // never a companion or the guide (fix-30)
                var brain = _session.NpcCombat.Get(kv.Key);
                if (brain == null || !brain.IsAttacking) continue; // CombatState 1 (fighting, not fleeing)
                if (!brain.State.Target.IsPlayer && brain.State.Reason != AttackReason.Arena) continue;
                float d = Vector3.Distance(kv.Value.position, at);
                if (d < bestDist) { bestDist = d; best = kv.Key; }
            }
            if (!best.IsEmpty) _session.NpcCombat.Engage(member, best, AttackReason.Enemy);
        }
    }
}
