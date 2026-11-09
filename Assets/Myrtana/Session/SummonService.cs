using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>A creature was summoned at <see cref="Pose"/> (the presentation plays the spell's target effect there).</summary>
    public readonly struct CreatureSummoned : IGameEvent
    {
        public CreatureSummoned(ActorId summoner, ActorId creature, string spell, WorldPose pose, bool castEffect)
        {
            Summoner = summoner;
            Creature = creature;
            Spell = spell;
            Pose = pose;
            CastEffect = castEffect;
        }

        public ActorId Summoner { get; }
        public ActorId Creature { get; }
        public string Spell { get; }
        public WorldPose Pose { get; }
        /// <summary>False for Army of Cuteness (G3 spawns its rabbits without the cast effect).</summary>
        public bool CastEffect { get; }
    }

    /// <summary>A summoned creature's body left the world.</summary>
    public readonly struct SummonVanished : IGameEvent
    {
        public SummonVanished(ActorId creature) => Creature = creature;
        public ActorId Creature { get; }
    }

    /// <summary>
    /// Summoning in the session (Docs/combat.md §12.2; G3 SummonPartyMonster and the MagicSummon* effects, CP 1.75):
    /// <list type="bullet">
    /// <item>A summon spell calls the creatures of its <see cref="SummonSpec"/> (<see cref="SummonGroups.Plan"/>); the
    /// caster's previous group dies first (one group per caster). Each creature is a new actor made from the world's
    /// template NPC of that name (the first NPC "Skeleton_Sword#…", else one of that species), placed around the caster.</item>
    /// <item>The player's creatures join his party as <see cref="PartyMemberType.Summoned"/> and follow him (ZS_FollowPlayer);
    /// they are not companions (no join limit), earn no XP for the player and cannot be talked to.</item>
    /// <item>An NPC's creatures take the caster's political alignment and are Summoned members of its party; if the player is
    /// within 20 m they attack him with the caster's attack reason, otherwise they die at once (CP).</item>
    /// <item>No timer: a creature ends when it dies, when a new group replaces it, when it is more than 40 m from the player
    /// (CP leash, every Summoned creature), or when the hero teleports (DismissPlayerParty). Ending = ZS_RagDollDead
    /// (killed, no XP). An NPC caster's creatures also die when their fight ends (CP ZS_Attack_End). The body is removed
    /// <see cref="CorpseSeconds"/> later <b>[interp]</b>: Script_Game has no removal for summon corpses (ZS_RagDollDead /
    /// ZS_Dead_Loop treat them as any corpse, CleanUpPlunderInv gives them no loot); any engine-side removal is unread.</item>
    /// <item>Summons are saved like every other G3 entity (section "session.summons", <see cref="SummonSaveSection"/>): the
    /// living creatures come back with their summoner before the NPC, party, vitals and combat sections fill in their pose,
    /// party slot, hit points and fight. Bodies waiting for removal are not saved.</item>
    /// </list>
    /// Pure; the presentation shows the bodies (they are ordinary agents of the NPC simulation).
    /// </summary>
    public sealed class SummonService : IDisposable, IActorStore
    {
        /// <summary>A dead summon's body stays this long (real s) before it is removed [interp].</summary>
        public const float CorpseSeconds = 30f;
        /// <summary>Distance of the creatures from the caster (m; G3 FindSpawnPose) [interp].</summary>
        public const float SpawnRadiusMetres = 2f;
        const string NoCastEffect = "MagicSummonArmyOfCuteness";
        const string SummonMark = "#smn";

        readonly ActorRegistry _actors;
        readonly NpcSimulation _npcs;
        readonly PlayerParty _party;
        readonly ActorVitals _vitals;
        readonly NpcCombatSystem _combat;
        readonly IEventBus _events;
        readonly IGameLog _log;
        readonly ActorRemoval _removal;
        readonly Func<IEnumerable<NpcDef>> _definitions;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly Dictionary<ActorId, float> _corpses = new Dictionary<ActorId, float>();
        readonly HashSet<ActorId> _spawned = new HashSet<ActorId>();
        readonly HashSet<ActorId> _fought = new HashSet<ActorId>();
        readonly List<ActorId> _scratch = new List<ActorId>();
        Dictionary<string, NpcDef> _byName, _bySpecies;
        int _counter;

        public SummonService(SummonGroups groups, ActorRegistry actors, NpcSimulation npcs, PlayerParty party, ActorVitals vitals,
                             NpcCombatSystem combat, Func<IEnumerable<NpcDef>> definitions, IEventBus events, IGameLog log,
                             ActorRemoval removal = null)
        {
            Groups = groups ?? throw new ArgumentNullException(nameof(groups));
            _actors = actors ?? throw new ArgumentNullException(nameof(actors));
            _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
            _party = party;
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            _combat = combat;
            _definitions = definitions ?? (() => Array.Empty<NpcDef>());
            _events = events;
            _log = log;
            // The session passes its removal path (every actor store registered); alone, the service builds one over what it knows.
            _removal = removal ?? new ActorRemoval(events).Register(this).Register(combat).Register(party).Register(vitals)
                                                       .Register(npcs).Register(actors);
            if (events != null)
            {
                _subscriptions.Add(events.Subscribe<ActorDefeated>(OnDefeated));
                _subscriptions.Add(events.Subscribe<PartyChanged>(OnPartyChanged));
            }
        }

        public SummonGroups Groups { get; }

        /// <summary>Where an actor is now (the player included; null = unknown). Set by the session.</summary>
        public Func<ActorId, WorldPose?> Positions { get; set; }

        /// <summary>The summoner of a creature (empty = not summoned).</summary>
        public ActorId SummonerOf(ActorId creature) => Groups.LeaderOf(creature);

        public bool IsSummoned(ActorId actor) => Groups.IsSummoned(actor);

        /// <summary>The spell calls creatures (its target effect has a summon spec).</summary>
        public bool IsSummonSpell(SpellDef spell) => spell != null && Groups.Rules.For(spell.Effect) != null;

        /// <summary>
        /// The spell's creatures appear around <paramref name="at"/> (the caster's pose); returns them (empty: the spell
        /// summons nothing for this caster, e.g. Summon Monster for anyone but Runak, or no template NPC exists).
        /// </summary>
        public IReadOnlyList<ActorId> Summon(ActorId caster, SpellDef spell, int casterIntelligence, WorldPose at)
        {
            if (caster.IsEmpty || spell == null) return Array.Empty<ActorId>();
            var plan = Groups.Plan(spell, casterIntelligence, caster.IsPlayer ? null : caster.Name);
            if (plan.Count == 0) return Array.Empty<ActorId>();
            Dismiss(caster);

            string alignment = null;
            if (!caster.IsPlayer && _actors.TryGet(caster, out var casterState))
                alignment = casterState.AlignmentOr(casterState.Definition.Combat?.Faction);
            var playerPose = Positions?.Invoke(ActorId.Player);
            var created = new List<ActorId>();
            for (int i = 0; i < plan.Count; i++)
            {
                var template = TemplateFor(plan[i]);
                if (template == null)
                {
                    _log?.Warning($"Summon {spell.Id}: no NPC of template '{plan[i]}' in the content; nothing appears.");
                    continue;
                }
                var pose = SpawnPose(at, i, plan.Count);
                var def = Creature(template, plan[i], pose);
                if (!_actors.Add(def)) continue;
                if (_npcs.Add(def, pose) == null)
                {
                    _actors.Remove(def.Id);
                    continue;
                }
                _spawned.Add(def.Id);
                Groups.Join(caster, def.Id);
                created.Add(def.Id);
                _events?.Publish(new CreatureSummoned(caster, def.Id, spell.Id, pose, spell.Effect != NoCastEffect));

                if (caster.IsPlayer)
                {
                    _party?.Join(def.Id, PartyMemberType.Summoned);
                    continue;
                }
                if (alignment != null && _actors.TryGet(def.Id, out var state)) state.ChangeAlignment(alignment);
                float distance = playerPose.HasValue ? pose.DistanceTo(playerPose.Value) * 100f : float.MaxValue;
                if (Groups.NpcSummonAttacks(distance))
                {
                    var reason = _combat?.Get(caster)?.State.Reason ?? AttackReason.None;
                    _combat?.Engage(def.Id, ActorId.Player, reason == AttackReason.None ? AttackReason.Enemy : reason);
                }
                else End(def.Id);
            }
            return created;
        }

        /// <summary>A caster's group dies (a new summon replaces it; the dismiss helper's ZS_RagDollDead).</summary>
        public void Dismiss(ActorId leader)
        {
            foreach (var m in Groups.Dismiss(leader).ToList()) End(m);
        }

        /// <summary>Per frame: the 40 m leash and the removal of old summon corpses.</summary>
        public void Tick(float realSeconds)
        {
            int leash = Groups.Rules.LeashDistance;
            var player = Positions?.Invoke(ActorId.Player);
            if (leash > 0 && player.HasValue && !Groups.IsEmpty)
            {
                _scratch.Clear();
                foreach (var g in Groups.Groups)
                    foreach (var m in g.Value)
                    {
                        var pose = PoseOf(m);
                        if (pose.HasValue && pose.Value.DistanceTo(player.Value) * 100f > leash) _scratch.Add(m);
                    }
                foreach (var m in _scratch)
                {
                    Groups.Remove(m);
                    End(m);
                }
            }

            EndFinishedNpcSummons();
            if (_corpses.Count == 0) return;
            _scratch.Clear();
            foreach (var id in _corpses.Keys.ToList())
            {
                _corpses[id] -= realSeconds;
                if (_corpses[id] <= 0) _scratch.Add(id);
            }
            foreach (var id in _scratch) Vanish(id);
        }

        /// <summary>
        /// CP ZS_Attack_End: a Summoned creature whose leader is not the player kills itself when its fight is over (an NPC
        /// caster's creatures live for one fight).
        /// </summary>
        void EndFinishedNpcSummons()
        {
            if (_combat == null || Groups.IsEmpty) return;
            _scratch.Clear();
            foreach (var g in Groups.Groups)
            {
                if (g.Key.IsPlayer) continue;
                foreach (var m in g.Value)
                {
                    var brain = _combat.Get(m);
                    if (brain == null || _vitals.StateOf(m) != VitalState.Alive) continue;
                    if (brain.IsFighting) _fought.Add(m);
                    else if (_fought.Contains(m)) _scratch.Add(m);
                }
            }
            foreach (var m in _scratch)
            {
                _fought.Remove(m);
                End(m);
            }
        }

        /// <summary>
        /// After loading a save: Summoned party entries without a creature (a save made before summons were saved) go; the
        /// player's restored creatures that the party lost join it again.
        /// </summary>
        public void AfterLoad()
        {
            if (_party == null) return;
            foreach (var m in _party.Members.Where(m => m.Type == PartyMemberType.Summoned).ToList())
                if (!_actors.Exists(m.Id)) _party.MemberDied(m.Id);
            foreach (var m in Groups.MembersOf(ActorId.Player).ToList())
                if (_party.TypeOf(m) == PartyMemberType.None) _party.Join(m, PartyMemberType.Summoned);
        }

        /// <summary>The living summoned creatures (sorted by summoner, then creature) and the id counter.</summary>
        internal void Write(ISaveWriter w)
        {
            w.WriteInt32(_counter);
            var living = new List<(ActorId Leader, ActorId Creature)>();
            foreach (var g in Groups.Groups)
                foreach (var m in g.Value)
                    if (_spawned.Contains(m) && _actors.Exists(m) && _vitals.StateOf(m) != VitalState.Dead) living.Add((g.Key, m));
            living.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Leader.Name, b.Leader.Name);
                return c != 0 ? c : string.CompareOrdinal(a.Creature.Name, b.Creature.Name);
            });
            w.WriteInt32(living.Count);
            foreach (var (leader, creature) in living)
            {
                w.WriteString(leader.Name);
                w.WriteString(creature.Name);
                w.WriteString(_actors.TryGet(creature, out var state) && state.AlignmentChanged ? state.Alignment : null);
            }
        }

        /// <summary>Re-creates the saved creatures from their templates (a fresh session, before the NPC section).</summary>
        internal void Read(ISaveReader r, int version)
        {
            _counter = Math.Max(_counter, r.ReadInt32());
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var leader = new ActorId(r.ReadString());
                var id = new ActorId(r.ReadString());
                string alignment = r.ReadString();
                string templateName = TemplateNameOf(id);
                var template = templateName != null ? TemplateFor(templateName) : null;
                if (template == null || _actors.Exists(id))
                {
                    _log?.Warning($"Saved summon {id.Name} cannot be restored (no template NPC '{templateName}').");
                    continue;
                }
                var pose = PoseOf(leader) ?? template.Spawn ?? default;
                var def = Creature(template, id, pose);
                if (!_actors.Add(def)) continue;
                if (_npcs.Add(def, pose) == null)
                {
                    _actors.Remove(id);
                    continue;
                }
                _spawned.Add(id);
                Groups.Join(leader, id);
                if (alignment != null && _actors.TryGet(id, out var state)) state.ChangeAlignment(alignment);
            }
        }

        static string TemplateNameOf(ActorId creature)
        {
            string name = creature.Name ?? "";
            int mark = name.LastIndexOf(SummonMark, StringComparison.Ordinal);
            return mark > 0 ? name.Substring(0, mark) : null;
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }

        // ------------------------------------------------------------------------------------------- internals

        /// <summary>The creature around the caster: i-th of n on a half ring in front of it [interp: G3 FindSpawnPose].</summary>
        public static WorldPose SpawnPose(WorldPose caster, int index, int count)
        {
            double yaw = caster.Yaw * Math.PI / 180.0;
            double spread = count <= 1 ? 0 : Math.PI * 0.75 * (index / (double)(count - 1) - 0.5) * 2;
            double a = yaw + spread;
            float radius = SpawnRadiusMetres + (count > 5 ? (index % 2) * 1.2f : 0);
            return new WorldPose(caster.X + (float)(Math.Sin(a) * radius), caster.Y, caster.Z + (float)(Math.Cos(a) * radius), caster.Yaw);
        }

        void End(ActorId creature)
        {
            Groups.Remove(creature);
            if (_vitals.StateOf(creature) != VitalState.Dead) _vitals.Kill(creature, byPlayerParty: false);
            if (_party != null && _party.TypeOf(creature) == PartyMemberType.Summoned) _party.MemberDied(creature);
            if (_spawned.Contains(creature) && !_corpses.ContainsKey(creature)) _corpses[creature] = CorpseSeconds;
        }

        void Vanish(ActorId creature)
        {
            _removal.Remove(creature);
            _events?.Publish(new SummonVanished(creature));
        }

        /// <summary>
        /// The actor left the game (<see cref="ActorRemoval"/>): a creature leaves its group and the corpse timer; a removed
        /// caster's own creatures die (its group ends as on a new summon).
        /// </summary>
        public void Forget(ActorId actor)
        {
            _corpses.Remove(actor);
            _spawned.Remove(actor);
            _fought.Remove(actor);
            Groups.Remove(actor);
            if (!actor.IsPlayer) Dismiss(actor);
        }

        public bool Holds(ActorId actor) =>
            _corpses.ContainsKey(actor) || _spawned.Contains(actor) || _fought.Contains(actor) || Groups.IsSummoned(actor) ||
            Groups.MembersOf(actor).Count > 0;

        void OnDefeated(ActorDefeated e)
        {
            if (!_spawned.Contains(e.Victim)) return;
            if (!e.Killed)
            {
                // A summoned creature does not lie knocked out: it dies [interp: G3 creatures die anyway].
                End(e.Victim);
                return;
            }
            Groups.Remove(e.Victim);
            if (_party != null && _party.TypeOf(e.Victim) == PartyMemberType.Summoned) _party.MemberDied(e.Victim);
            if (!_corpses.ContainsKey(e.Victim)) _corpses[e.Victim] = CorpseSeconds;
        }

        /// <summary>The hero teleported (or the creature was dismissed otherwise): a Summoned member leaving the party dies.</summary>
        void OnPartyChanged(PartyChanged e)
        {
            if (e.Type != PartyMemberType.Summoned || e.Change != PartyChange.Dismissed || !_spawned.Contains(e.Member)) return;
            if (_vitals.StateOf(e.Member) != VitalState.Dead) End(e.Member);
        }

        WorldPose? PoseOf(ActorId actor) =>
            _npcs.TryGet(actor, out var agent) ? _npcs.PoseOf(agent) : Positions?.Invoke(actor);

        NpcDef TemplateFor(string template)
        {
            if (_byName == null)
            {
                _byName = new Dictionary<string, NpcDef>(StringComparer.OrdinalIgnoreCase);
                _bySpecies = new Dictionary<string, NpcDef>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in _definitions().Where(d => !d.Id.IsPlayer && !_spawned.Contains(d.Id)).OrderBy(d => d.Id.Name, StringComparer.Ordinal))
                {
                    string name = d.Id.Name;
                    int hash = name.IndexOf('#');
                    if (hash > 0 && !_byName.ContainsKey(name.Substring(0, hash))) _byName[name.Substring(0, hash)] = d;
                    string species = d.Combat?.Species ?? d.Species;
                    if (species != null && !_bySpecies.ContainsKey(species)) _bySpecies[species] = d;
                }
            }
            return _byName.TryGetValue(template, out var def) || _bySpecies.TryGetValue(template, out def) ? def : null;
        }

        /// <summary>
        /// A new actor from the template NPC: same looks, species, level and weaponry; no routine, enclave, boss/guard role or
        /// stored attitude (a summoned creature belongs to nobody but its summoner) [interp].
        /// </summary>
        NpcDef Creature(NpcDef template, string templateName, WorldPose pose)
        {
            var id = new ActorId($"{templateName}{SummonMark}{++_counter:x4}");
            while (_actors.Exists(id)) id = new ActorId($"{templateName}{SummonMark}{++_counter:x4}");
            return Creature(template, id, pose);
        }

        static NpcDef Creature(NpcDef template, ActorId id, WorldPose pose)
        {
            var c = template.Combat;
            var combat = c == null ? null : new NpcCombatDef(c.Level, c.LevelMax, c.Species, c.Humanoid, boss: false, immortal: false,
                friendType: false, mage: c.Mage, ambientCreature: c.AmbientCreature, evil: c.Evil, animal: c.Animal, gameAnimal: c.GameAnimal,
                role: NpcRole.Personal, @class: c.Class, hasEnclave: false, faction: c.Faction, attitudeToPlayer: null,
                equipment: c.Equipment, enclave: null);
            return new NpcDef(id, template.DisplayName, template.VoiceSet, template.Species, template.Visual, pose, routine: null, combat: combat);
        }
    }

    /// <summary>Save section "session.summons": the living summoned creatures (see <see cref="SummonService"/>).</summary>
    public sealed class SummonSaveSection : ISaveParticipant
    {
        readonly SummonService _summons;

        public SummonSaveSection(SummonService summons) => _summons = summons ?? throw new ArgumentNullException(nameof(summons));

        public string Section => "session.summons";
        public int Version => 1;
        public void Write(ISaveWriter w) => _summons.Write(w);
        public void Read(ISaveReader r, int version) => _summons.Read(r, version);
    }
}
