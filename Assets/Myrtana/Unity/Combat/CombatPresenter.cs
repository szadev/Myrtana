using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Connects the fighters' bodies to the combat rules (Docs/combat.md §8–§9): a swing that touches a body becomes a
    /// <see cref="HitContext"/> snapshot for <see cref="CombatService.Strike"/>; the resolved hits, defeats and get-ups
    /// come back as reactions, knock-downs and deaths on the bodies. On a knock-out the victim's weapon falls to the
    /// ground (a prop); the winner may take it when ransacking, else the owner picks it up when it gets up. Arrows and
    /// bolts are <see cref="Projectile"/>s whose touches become missile hits. Owns the combat parts of the NPC views
    /// (<see cref="NpcCombatView"/>) while they are shown. Decides no damage.
    /// </summary>
    public sealed class CombatPresenter : MonoBehaviour, ISessionView
    {

        /// <summary>The weapon an actor dropped (a world item, <see cref="WorldItems.Drop"/>) during its current knock-out.</summary>
        sealed class Dropped
        {
            public string Item;
            public string WorldItem;
        }

        readonly Dictionary<ActorId, CombatFighter> _fighters = new Dictionary<ActorId, CombatFighter>();
        // Knocked out without a hit (a script, a status effect): shown down after this frame's hits were shown.
        readonly List<ActorId> _fellWithoutHit = new List<ActorId>();
        /// <summary>Killed this frame without (yet) a HitResolved: burning, poison, falls, scripts (ragdoll without a push).</summary>
        readonly List<ActorId> _diedWithoutHit = new List<ActorId>();
        // fix-149: G3 DropHandItems drops both hands (the weapon, and the shield or second blade in the left hand).
        readonly Dictionary<ActorId, List<Dropped>> _dropped = new Dictionary<ActorId, List<Dropped>>();
        Myrtana.Simulation.NpcCombatSystem _weaponrySource;
        /// <summary>Who robs which knocked-out body: once per knock-out (G3 victim.Ransacked).</summary>
        readonly RansackClaims _ransacks = new RansackClaims();
        readonly Dictionary<(ActorId, ActorStatus), GameObject> _auras = new Dictionary<(ActorId, ActorStatus), GameObject>();
        // G3 status effects (eff_magic_firespell_target_01, eff_magic_icespell_target_0N; effects-2) by actor and status.
        readonly Dictionary<(ActorId, ActorStatus), int> _auraFx = new Dictionary<(ActorId, ActorStatus), int>();
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        ItemVisualCatalog _catalog;
        NpcViewManager _npcs;

        [Tooltip("Log every reported melee hit and its outcome (diagnostics)")] public bool logHits;

        public GameSession Session { get; private set; }
        /// <summary>The player was knocked out (false) or killed (true).</summary>
        public event Action<bool> PlayerFell;
        /// <summary>A hit on the player was resolved (camera shake).</summary>
        public event Action<HitOutcome> PlayerHit;
        /// <summary>
        /// Dev diagnostics (runner <c>touchwatch</c>, fix-59): every weapon, missile or spell-missile touch that reached the
        /// rules, with its outcome (a dropped one is <see cref="HitResult.Ignored"/> with its gate): attacker, victim.
        /// </summary>
        public static event Action<ActorId, ActorId, HitDelivery, HitOutcome> TouchResolved;

        public void Init(NpcViewManager npcs, ItemVisualCatalog catalog)
        {
            _npcs = npcs;
            _catalog = catalog;
            npcs.ViewSpawned += OnViewSpawned;
            npcs.ViewDespawned += OnViewDespawned;
        }

        public void Bind(GameSession session)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            Session = session;
            _dropped.Clear(); // the dropped weapons stay world items (WorldItemViews shows them)
            if (_weaponrySource != null) _weaponrySource.WeaponryChanged -= RefreshCarried;
            _weaponrySource = session.NpcCombat;
            if (_weaponrySource != null) _weaponrySource.WeaponryChanged += RefreshCarried; // looted gear leaves the body (fix-149)
            var events = session.Events;
            _subscriptions.Add(events.Subscribe<HitResolved>(OnHitResolved));
            _subscriptions.Add(events.Subscribe<ActorDefeated>(OnDefeated));
            _subscriptions.Add(events.Subscribe<ActorGotUp>(OnGotUp));
            _subscriptions.Add(events.Subscribe<PlayerDefeated>(e =>
            {
                if (!e.Killed) _fellWithoutHit.Add(ActorId.Player);
                // fix-101: a death without a hit (a fall, burning, a script) had no motion: the hero stood under the game-over
                // text (GO_GameOver). Shown in LateUpdate unless its HitResolved comes (that plays the hit's death).
                else if (!_diedWithoutHit.Contains(ActorId.Player)) _diedWithoutHit.Add(ActorId.Player);
                PlayerFell?.Invoke(e.Killed);
            }));
            _subscriptions.Add(events.Subscribe<StatusChanged>(OnStatusChanged));
            _subscriptions.Add(events.Subscribe<CreatureSummoned>(OnCreatureSummoned));
            // CP _AI_UseInventoryItem (Stollengrollen without the stamina): DropHandItems, then the knock-out pose (fix-13)
            _subscriptions.Add(events.Subscribe<Myrtana.Story.HeroCollapsed>(_ => { if (TryGet(ActorId.Player, out var hero)) Drop(hero); }));
            // G3 ZS_Talk_Loop ends with ContinueRoutine: below 61 % HP an NPC that can heal itself does (ZS_HealSelf) - a
            // companion drinks the healing potion the hero just gave it (CP info BPANKRATZ31841, Give only; fix-164).
            _subscriptions.Add(events.Subscribe<Myrtana.Story.DialogEnded>(e =>
            {
                if (!e.Partner.IsPlayer && TryGet(e.Partner, out var partner) && partner.TryGetComponent<NpcCombatView>(out var v)) v.ContinueRoutine();
            }));
            foreach (var a in _auras.Values) if (a != null) Destroy(a);
            _auras.Clear();
            foreach (var h in _auraFx.Values) G3.Unity.G3EffectPlayer.Stop(h);
            _auraFx.Clear();
            foreach (var f in _fighters.Values) Restore(f);
            WorldSpellView.Attach(this, _npcs); // sleep, mist, time bubble, telekinesis looks (magic-3)
        }

        void OnDestroy()
        {
            foreach (var s in _subscriptions) s.Dispose();
            if (_weaponrySource != null) _weaponrySource.WeaponryChanged -= RefreshCarried;
            if (_npcs != null)
            {
                _npcs.ViewSpawned -= OnViewSpawned;
                _npcs.ViewDespawned -= OnViewDespawned;
            }
        }

        // ------------------------------------------------------------------------------------------- fighters

        public void Register(CombatFighter fighter)
        {
            _fighters[fighter.Actor] = fighter;
            fighter.Touched -= OnTouched;
            fighter.Touched += OnTouched;
            fighter.FinishingBlow -= OnFinishingBlow;
            fighter.FinishingBlow += OnFinishingBlow;
            fighter.SpeedFactor = (motion, windUp) => SpeedFactor(fighter, motion, windUp);
            if (Session != null) Restore(fighter);
        }

        /// <summary>The rules' playback speed of a fighter's combat motion (combat_moves.json animationSpeedFactor).</summary>
        float SpeedFactor(CombatFighter f, CombatMotion motion, bool windUp)
        {
            if (Session == null) return 1f;
            bool player = f.Actor.IsPlayer;
            var brain = player ? null : Session.NpcCombat.Get(f.Actor);
            var query = new MotionSpeedQuery
            {
                Motion = motion,
                WindUp = windUp,
                IsPlayer = player,
                Species = player ? "Human" : CombatDefOf(f.Actor)?.Species,
                RightHand = f.WeaponClass,
                DualOneHanded = f.DualWielding,
                Diseased = Session.Magic.Status.Has(f.Actor, ActorStatus.Diseased), // NPCs ×0.75 (combat-6)
                Stamina = player ? Session.Combat.Vitals.Stamina(ActorId.Player) : 0,
                Arena = brain != null && brain.IsFighting && brain.State.Reason == AttackReason.Arena,
                HasPerkCrossbow2 = player && Session.Hero.Knows("Perk_Crossbow_2"),
                HasPerkCrossbow3 = player && Session.Hero.Knows("Perk_Crossbow_3"),
            };
            return MotionSpeedRules.Factor(query, Session.Settings.Rules);
        }

        public void Unregister(CombatFighter fighter)
        {
            fighter.Touched -= OnTouched;
            fighter.FinishingBlow -= OnFinishingBlow;
            if (!_fighters.TryGetValue(fighter.Actor, out var f) || f != fighter) return;
            _fighters.Remove(fighter.Actor);
            // its flames / ice go with the body; RestoreAuras starts them again when it is shown again
            foreach (var s in new[] { ActorStatus.Burning, ActorStatus.Frozen })
            {
                var key = (fighter.Actor, s);
                if (_auras.TryGetValue(key, out var aura) && aura != null) Destroy(aura);
                _auras.Remove(key);
                if (_auraFx.TryGetValue(key, out int h)) G3.Unity.G3EffectPlayer.Stop(h);
                _auraFx.Remove(key);
            }
        }

        public bool TryGet(ActorId actor, out CombatFighter fighter) => _fighters.TryGetValue(actor, out fighter) && fighter != null;

        /// <summary>The item meshes (hand and carried weapons).</summary>
        public ItemVisualCatalog ItemVisuals => _catalog;

        /// <summary>The actor's weapon lies on the ground (dropped when knocked out, not picked up or taken yet).</summary>
        public bool WeaponDropped(ActorId actor) => TryGetLying(actor, out _);

        /// <summary>
        /// fix-149: <paramref name="item"/> of the actor's weaponry lies on the ground, so its body does not show it - dropped
        /// during this knock-out (until picked up or taken), or, for a body that is down or dead, any item it dropped that
        /// still lies in the world (a loaded game forgets this view's drops; the loot leaves them out the same way,
        /// <see cref="NpcLoot"/>).
        /// </summary>
        public bool Lies(ActorId actor, string item)
        {
            if (Session == null || string.IsNullOrEmpty(item)) return false;
            if (_dropped.TryGetValue(actor, out var list))
                foreach (var d in list)
                    if (d.Item == item && Session.Items.World.TryGetDropped(d.WorldItem, out _)) return true;
            if (Session.Combat.Vitals.StateOf(actor) == VitalState.Alive) return false;
            foreach (var d in Session.Items.World.DroppedBy(actor))
                if (d.Item == item) return true;
            return false;
        }

        /// <summary>Re-shows an NPC's carried weapons after its weapon was dropped, picked up again or taken.</summary>
        void RefreshCarried(ActorId actor)
        {
            if (TryGet(actor, out var f) && f.TryGetComponent<NpcCombatView>(out var v)) v.RefreshCarried();
        }

        public IEnumerable<CombatFighter> Fighters => _fighters.Values;

        /// <summary>
        /// A shown fighter's pose follows its vitals: dead and downed NPCs lie when they come into view again or a game is
        /// loaded — at once, in their final pose (<paramref name="atOnce"/>; G3 shows a body met again in its ground pose,
        /// fix-80); a fall without a hit (<see cref="LateUpdate"/>) still plays.
        /// </summary>
        void Restore(CombatFighter f, bool atOnce = true)
        {
            var state = Session.Combat.Vitals.StateOf(f.Actor);
            if (state == VitalState.Dead && !f.Animator.IsDead) f.Die(HitReaction.Dead, f.transform.position + f.transform.forward, atOnce: atOnce);
            else if (state == VitalState.Down && !f.Animator.IsDown) f.KnockOut(HitReaction.LieKnockDown, f.transform.position + f.transform.forward, atOnce);
            else if (state == VitalState.Alive && (f.Animator.IsDead || f.Animator.IsDown)) f.ResetState();
            RestoreAuras(f);
            if (!f.Actor.IsPlayer) RefreshCarried(f.Actor); // a loaded game: its lost and lying weapons (fix-149)
        }

        /// <summary>
        /// A body that is burning / frozen when it is shown (a loaded game, an NPC streaming in) gets its flames / ice
        /// at once: the status rules only publish <see cref="StatusChanged"/> when a status starts or ends.
        /// </summary>
        void RestoreAuras(CombatFighter f)
        {
            var status = Session.Magic.Status;
            foreach (var s in new[] { ActorStatus.Burning, ActorStatus.Frozen })
            {
                var key = (f.Actor, s);
                if (!status.Has(f.Actor, s) || _auras.ContainsKey(key) || _auraFx.ContainsKey(key)) continue;
                OnStatusChanged(new StatusChanged(f.Actor, s, true));
            }
        }

        void OnViewSpawned(NpcView view)
        {
            if (Session == null || view.GetComponent<G3.Unity.Characters.G3MotionPlayer>() == null) return;
            // G3 OnEnterProcessingRange -> RefreshWeaponry/AssureItems: a living NPC has the weapons taken from it again.
            if (Session.Combat.Vitals.StateOf(view.Actor) == VitalState.Alive && !WeaponDropped(view.Actor))
                Session.NpcCombat.RestoreWeaponry(view.Actor);
            var combat = view.TryGetComponent<NpcCombatView>(out var c) ? c : view.gameObject.AddComponent<NpcCombatView>();
            combat.Bind(this, view);
        }

        void OnViewDespawned(NpcView view)
        {
            var combat = view.GetComponent<NpcCombatView>();
            if (combat != null) combat.Unbind();
        }

        // ------------------------------------------------------------------------------------------- hits

        void OnTouched(CombatFighter attacker, CombatBody body, MeleeBlades blade)
        {
            var victim = body.Fighter;
            if (victim == null || Session == null || victim == attacker) return;
            var vitals = Session.Combat.Vitals;
            // A finishing move kills by time (OnFinishingBlow), not by touching the lying body.
            if (attacker.CurrentAction == CombatAction.FinishingAttack && vitals.IsDown(victim.Actor)) return;
            if (vitals.StateOf(victim.Actor) != VitalState.Alive || vitals.StateOf(attacker.Actor) != VitalState.Alive) return;
            var hit = BuildHit(attacker, victim, blade);
            var outcome = Session.Combat.Strike(hit, byPlayerParty: ByPlayerParty(attacker.Actor));
            TouchResolved?.Invoke(attacker.Actor, victim.Actor, hit.Delivery, outcome);
            if (logHits) Debug.Log($"[Myrtana] hit {attacker.Actor} ({hit.Attacker.Action}{(attacker.DualWielding ? ", " + blade + " blade" : "")}) → {victim.Actor}: {outcome}");
        }

        /// <summary>
        /// OnAI_FinishingAttack, 1.2 s into the move: the target still lying is killed (if it can be), with the blood
        /// effect's sound; the kill is published as the finisher's killing hit (death motion, "Dead" cry, murder).
        /// </summary>
        void OnFinishingBlow(CombatFighter attacker, ActorId target)
        {
            if (Session == null || !TryGet(target, out var victim) || !Session.Combat.Vitals.IsDown(target)) return;
            if (Session.Combat.Vitals.StateOf(attacker.Actor) != VitalState.Alive) return;
            // G3 Kill: XP and DefeatedByPlayer for the hero and the members he leads, not only his own blow (fix-81).
            bool killed = Session.Combat.Finish(attacker.Actor, Snapshot(victim), Session.Hero.Level, ByPlayerParty(attacker.Actor));
            if (killed) G3.Unity.G3EffectSounds.Play(FinishingEffect, CombatFighter.BlowPoint(attacker, victim)); // on the weapon (fix-55)
            if (logHits) Debug.Log($"[Myrtana] finishing blow {attacker.Actor} → {target}: {(killed ? "killed" : "not killable")}");
        }

        /// <summary>OnAI_FinishingAttack's blood effect, started on the attacker's weapon.</summary>
        const string FinishingEffect = "eff_col_wh_03_me_fl";

        /// <summary>
        /// The rule snapshot of a melee hit of <paramref name="attacker"/> on <paramref name="victim"/> with one of its blades:
        /// the damage is that weapon's own (G3 AssessHit reads the touching damager; a dual wielder's left blade is the
        /// off-hand weapon).
        /// </summary>
        public HitContext BuildHit(CombatFighter attacker, CombatFighter victim, MeleeBlades blade = MeleeBlades.Right)
        {
            var a = Snapshot(attacker);
            var v = Snapshot(victim);
            if (victim.Actor.IsPlayer && CombatInputLog.Enabled)
                CombatInputLog.Line($"HIT on the hero by {attacker.Actor} ({a.Action}): parade {(v.InParade ? "UP" : "down")} " +
                                    $"[{victim.Animator.StateText} {victim.Animator.StepStage}], sees the attacker {Sees(victim, attacker)}, hero action {v.Action}");
            var weapon = attacker.BladeItem(blade) ?? attacker.HeldWeaponItem;
            var damage = weapon?.Damage;
            var npcCombat = Session.NpcCombat;
            var victimBrain = npcCombat.Get(victim.Actor);
            var attackerBrain = npcCombat.Get(attacker.Actor);
            var hit = new HitContext
            {
                Attacker = a,
                Victim = v,
                Delivery = HitDelivery.Melee,
                DamageType = damage != null && Enum.TryParse(damage.Type, out DamageType type) ? type : DamageType.Impact,
                DamageAmount = damage?.Amount ?? 0,
                Quality = WeaponQualityOf(attacker, weapon, a.Species), // sharp, blessed, poisoned …; a creature's fist (fix-127)
                AttackerSeesVictim = true,
                VictimSeesAttacker = Sees(victim, attacker),
                // the player's: no focus = whatever he swings at; an NPC's: its fight target (HitSides.Fill below)
                VictimIsTarget = attacker.Opponent == null || attacker.Opponent == victim,
                VictimFightsSomeoneElse = victimBrain != null && victimBrain.IsAttacking && victimBrain.State.Target != attacker.Actor &&
                                          !LedBy(victimBrain.State.Target, attacker.Actor),
                AttackerTargetsVictim = attacker.Opponent == victim,
                Arena = IsArenaFight(attackerBrain, victim.Actor) || IsArenaFight(victimBrain, attacker.Actor),
                PlayerLevel = Session.Hero.Level,
            };
            // attitudes, party, targets: G3 OnTouchDamage's party and bystander filters (fix-59)
            HitSides.Fill(Session, hit, attacker.Actor, victim.Actor, TargetOf(attacker), TargetOf(victim));
            return hit;
        }

        /// <summary>
        /// The fighter's current target (G3 PSNpc.GetCurrentTarget): an NPC's fight target, shown or not, none once its
        /// fight ended (<see cref="HitSides.NpcTargetOf"/>); the hero's focus opponent.
        /// </summary>
        ActorId TargetOf(CombatFighter f) =>
            f.Actor.IsPlayer ? (f.Opponent != null ? f.Opponent.Actor : default) : HitSides.NpcTargetOf(Session, f.Actor);

        /// <summary>
        /// The NPC belongs to the party <paramref name="leader"/> leads (G3 DoLogicalDamage: an NPC fighting a member of
        /// the player's party is not a bystander; only the player leads a party here).
        /// </summary>
        bool LedBy(ActorId npc, ActorId leader)
        {
            var party = Session.Party?.Members;
            return leader.IsPlayer && party != null && party.IsLedByPlayer(npc);
        }

        /// <summary>
        /// The quality bits of the weapon in the attacker's hand: the item's own, plus the hero's stack quality (forged,
        /// sharpened, poisoned); with no weapon item, the species' fist (the bloodfly's poison sting, the zombie's disease,
        /// the fire varan's flames …, <see cref="SpeciesWeaponry"/>; fix-127).
        /// </summary>
        WeaponQuality WeaponQualityOf(CombatFighter attacker, ItemDef weapon, string species)
        {
            var item = weapon ?? attacker.HeldWeaponItem;
            if (item == null) return SpeciesWeaponry.FistQuality(species);
            int q = item.Quality;
            if (attacker.Actor.IsPlayer) q |= Session.Items.Inventories.Of(ActorId.Player).QualityOf(item.Id);
            else
            {
                // fix-237: the worn species weapon (Stalker spiked club, Ogre morning star, Goblin club) of a creature
                var loadout = Session.NpcCombat.LoadoutOf(attacker.Actor);
                if (loadout.MeleeItem == item) q |= (int)loadout.MeleeQuality;
            }
            return (WeaponQuality)q;
        }

        // ------------------------------------------------------------------------------------------- projectiles

        /// <summary>
        /// Shoots <paramref name="shot"/> from <paramref name="from"/> (G3 Shoot: the ammunition template becomes the
        /// projectile; its mesh flies along <paramref name="velocity"/>).
        /// </summary>
        public Projectile Shoot(Projectile.Shot shot, Vector3 from, Vector3 velocity)
        {
            var go = new GameObject("Projectile " + (shot.Ammo?.Id ?? "?"));
            var tip = Vector3.forward;
            string mesh = shot.Ammo?.Visual?.Mesh;
            if (_catalog != null && mesh != null && _catalog.TryGetMesh(mesh, out var entry))
            {
                go.AddComponent<MeshFilter>().sharedMesh = entry.mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = entry.materials;
                tip = TipAxis(entry.mesh.bounds);
            }
            G3.Unity.G3ItemEffect.Sync(go, shot.Ammo?.Id); // the fire arrow burns in flight (effects-2); its flame goes out on the touch (Projectile.Rest, fix-176)
            var p = go.AddComponent<Projectile>();
            p.Launch(this, shot, from, velocity, tip);
            return p;
        }

        /// <summary>The mesh's local axis towards its tip: the longest axis, towards its far end [interp].</summary>
        static Vector3 TipAxis(Bounds b)
        {
            int axis = b.size.x >= b.size.y && b.size.x >= b.size.z ? 0 : b.size.y >= b.size.z ? 1 : 2;
            var v = Vector3.zero;
            v[axis] = Mathf.Abs(b.max[axis]) >= Mathf.Abs(b.min[axis]) ? 1 : -1;
            return v;
        }

        /// <summary>
        /// A projectile touched <paramref name="victim"/>: the missile hit is resolved (headshot when the nearest bone is a
        /// head bone); returns the bone the arrow sticks to.
        /// </summary>
        public Transform OnProjectileHit(Projectile projectile, CombatFighter victim, Vector3 point)
        {
            var bone = NearestBone(victim, point);
            var shot = projectile.Info;
            var attacker = shot.Shooter;
            if (Session == null || attacker == null) return bone;
            var vitals = Session.Combat.Vitals;
            var state = vitals.StateOf(victim.Actor);
            if (state == VitalState.Down)
            {
                // fix-241 [owner] "Arrows finish downed foes": the arrow on a knocked-out body kills it as a finishing move would
                var down = BuildMissileHit(shot, victim, bone != null && bone.name.Contains("_Head"));
                bool killed = Session.Combat.FinishByRanged(down, ByPlayerParty(attacker.Actor));
                if (logHits) Debug.Log($"[Myrtana] shot {attacker.Actor} ({shot.Ammo?.Id}) → downed {victim.Actor}: {(killed ? "finished" : "no effect")}");
                return bone;
            }
            if (state != VitalState.Alive) return bone;
            var hit = BuildMissileHit(shot, victim, bone != null && bone.name.Contains("_Head"));
            var outcome = Session.Combat.Strike(hit, byPlayerParty: ByPlayerParty(attacker.Actor));
            TouchResolved?.Invoke(attacker.Actor, victim.Actor, hit.Delivery, outcome);
            if (logHits) Debug.Log($"[Myrtana] shot {attacker.Actor} ({shot.Ammo?.Id} x{shot.HitMultiplier:0.00}{(hit.Headshot ? " head" : "")}) → {victim.Actor}: {outcome}");
            return bone;
        }

        /// <summary>The rule snapshot of a missile hit (projectile damage = ammunition + weapon; the ammunition's type and quality).</summary>
        public HitContext BuildMissileHit(Projectile.Shot shot, CombatFighter victim, bool headshot)
        {
            var hit = BuildHit(shot.Shooter, victim);
            hit.Attacker.Weapon = shot.WeaponClass;
            hit.Attacker.Action = CombatAction.None;
            hit.Delivery = HitDelivery.Missile;
            hit.DamageType = RangedRules.ProjectileDamageType(shot.Ammo);
            hit.DamageAmount = RangedRules.ProjectileDamage(shot.Ammo, shot.Weapon);
            hit.HitMultiplier = shot.HitMultiplier;
            hit.Quality = (WeaponQuality)shot.Quality;
            hit.Headshot = headshot;
            hit.FireOrExplosiveProjectile = RangedRules.IsFireOrExplosive(shot.Ammo?.Id);
            if (shot.Shooter.Actor.IsPlayer || shot.Homing == victim)
                hit.VictimIsTarget = shot.Shooter.Opponent == null || shot.Shooter.Opponent == victim || shot.Homing == victim;
            // G3 PS_Ranged never sets the player's current target (only PS_Normal/Melee/Magic do; the ranged focus is for
            // the HUD), so OnTouchDamage's "member of the owner's party, not its target" drop always spares the hero's
            // party from his arrows [+ interp: a target left over from before the draw is not modelled].
            hit.AttackerTargetsVictim = !shot.Shooter.Actor.IsPlayer && shot.Shooter.Opponent == victim;
            return hit;
        }

        // ------------------------------------------------------------------------------------------- spells

        /// <summary>
        /// Launches a spell missile (G3 MagicProjectile: the Spawn template flies from the hand at the focus; it homes only when power-cast, fix-159): a
        /// placeholder glow until the G3 effects are imported, with the missile's effect sound.
        /// </summary>
        public Projectile CastMissile(Projectile.Shot shot, Vector3 from, Vector3 velocity)
        {
            var spell = shot.Spell.Spell;
            var go = new GameObject("Spell " + (spell.Missile?.Template ?? spell.Id));
            if (!G3.Unity.G3EffectPlayer.Has(spell.Missile?.Effect)) // the G3 missile visuals come with its effect below
                SpellFx.Orb(go.transform, SpellFx.ColorOf(spell), shot.Spell.Powered ? 0.25f : 0.15f, 3f);
            var p = go.AddComponent<Projectile>();
            p.Launch(this, shot, from, velocity, Vector3.forward);
            G3.Unity.G3EffectSounds.Play(spell.Missile?.Effect, from, go.transform);
            return p;
        }

        /// <summary>A spell missile touched <paramref name="victim"/>: the spell hit (Docs/combat.md §2) is resolved.</summary>
        public void OnSpellHit(Projectile projectile, CombatFighter victim, Vector3 point)
        {
            var shot = projectile.Info;
            var caster = shot.Shooter;
            if (Session == null || caster == null) return;
            var state = Session.Combat.Vitals.StateOf(victim.Actor);
            if (state != VitalState.Alive && state != VitalState.Down) return;
            var hit = MagicService.ToSpellHit(BuildHit(caster, victim), shot.Spell, flewAtTarget: projectile.Style == ProjectilePathStyle.Missile);
            if (state == VitalState.Down)
            {
                // fix-241 [owner] "Arrows finish downed foes": a damaging spell missile on a knocked-out body finishes it
                bool killed = Session.Combat.FinishByRanged(hit, ByPlayerParty(caster.Actor));
                if (logHits) Debug.Log($"[Myrtana] spell {caster.Actor} ({shot.Spell.Spell.Id}) → downed {victim.Actor}: {(killed ? "finished" : "no effect")}");
                return;
            }
            if (caster.Actor.IsPlayer || shot.Homing == victim)
                hit.VictimIsTarget = caster.Opponent == null || caster.Opponent == victim || shot.Homing == victim;
            hit.AttackerTargetsVictim = caster.Opponent == victim || shot.Homing == victim; // PS_Magic_Loop: current target = focus
            var outcome = Session.Combat.Strike(hit, byPlayerParty: ByPlayerParty(caster.Actor));
            TouchResolved?.Invoke(caster.Actor, victim.Actor, hit.Delivery, outcome);
            if (logHits) Debug.Log($"[Myrtana] spell {caster.Actor} ({shot.Spell.Spell.Id}{(hit.PowerCast ? " power" : "")}, mana {hit.ManaUsed}) → {victim.Actor}: {outcome}");
        }

        /// <summary>An area spell's hit (<see cref="AreaSpellObject"/>): resolved and applied as the caster's.</summary>
        public HitOutcome StrikeArea(CombatFighter caster, HitContext hit) =>
            Session.Combat.Strike(hit, byPlayerParty: ByPlayerParty(caster.Actor));

        /// <summary>A summoned creature appears: the spell's target effect where it stands (G3 SummonPartyMonster's cast effect).</summary>
        void OnCreatureSummoned(CreatureSummoned e)
        {
            if (!e.CastEffect || !Session.Magic.TryGetSpell(e.Spell, out var spell)) return;
            var at = new Vector3(e.Pose.X, e.Pose.Y, e.Pose.Z);
            string effect = spell.Visuals.TargetCast ?? spell.Visuals.CasterCast;
            if (G3.Unity.G3EffectPlayer.Has(effect)) G3.Unity.G3EffectPlayer.Play(effect, at);
            else SpellFx.Burst(at + Vector3.up, SpellFx.ColorOf(spell), 1f);
        }

        /// <summary>
        /// Burning and frozen bodies (G3 AssessHit / OnTick / ZS_Freeze, Docs/formats-effects.md §8): burning shows
        /// <c>eff_magic_firespell_target_01</c> (flames on legs, arms, spine and head) until the status ends (time, deep
        /// water, knock-out, death); frozen shows <c>eff_magic_icespell_target_01/02/03</c> by body size and ends with the
        /// matching <c>eff_magic_icespell_burst_0N</c>; a frozen body stands still (time scale 0). Poison shows nothing in G3.
        /// Without imported effects the old glow stands in.
        /// </summary>
        void OnStatusChanged(StatusChanged e)
        {
            if ((e.Status & (ActorStatus.Burning | ActorStatus.Frozen)) == 0) return;
            foreach (var status in new[] { ActorStatus.Burning, ActorStatus.Frozen })
            {
                if ((e.Status & status) == 0) continue;
                var key = (e.Actor, status);
                if (_auras.TryGetValue(key, out var old))
                {
                    if (old != null) Destroy(old);
                    _auras.Remove(key);
                }
                bool hadFx = _auraFx.TryGetValue(key, out int handle);
                if (hadFx)
                {
                    G3.Unity.G3EffectPlayer.Stop(handle);
                    _auraFx.Remove(key);
                }
                if (!TryGet(e.Actor, out var f)) continue;
                if (status == ActorStatus.Frozen) f.SetFrozen(e.Active);
                var body = f.Visual != null ? f.Visual : f.transform;
                if (!e.Active)
                {
                    if (status == ActorStatus.Frozen && hadFx && Session.Combat.Vitals.StateOf(e.Actor) == VitalState.Alive)
                        G3.Unity.G3EffectPlayer.Play(AuraEffect(f, thaw: true), body.position, body);
                    continue;
                }
                string effect = status == ActorStatus.Burning ? G3.Unity.G3StatusEffects.Burning : AuraEffect(f, thaw: false);
                if (G3.Unity.G3EffectPlayer.Has(effect))
                {
                    int h = G3.Unity.G3EffectPlayer.Start(effect, body);
                    if (h != 0)
                    {
                        _auraFx[key] = h;
                        continue;
                    }
                }
                var center = f.Body.Center - f.transform.position;
                _auras[key] = SpellFx.Aura(f.transform, center, status == ActorStatus.Burning ? SpellFx.Fire : SpellFx.Ice, center.y * 1.6f);
            }
        }

        /// <summary>The frozen / thaw effect by G3 IsHumanoid / IsAmbientCreature / IsLarge of a fighter.</summary>
        string AuraEffect(CombatFighter f, bool thaw)
        {
            var def = f.Actor.IsPlayer ? null : CombatDefOf(f.Actor);
            bool ambient = def != null && def.AmbientCreature;
            bool large = def != null && def.Species != null && TransformRules.LargeForms.Contains(def.Species);
            return thaw ? G3.Unity.G3StatusEffects.Thaw(f.Humanoid, ambient, large) : G3.Unity.G3StatusEffects.Frozen(f.Humanoid, ambient, large);
        }

        /// <summary>The skeleton bone nearest to <paramref name="point"/> (stands in for G3's touched bone collider) [interp].</summary>
        static Transform NearestBone(CombatFighter f, Vector3 point)
        {
            if (f.Visual == null) return null;
            Transform best = null;
            float bestDist = float.MaxValue;
            foreach (var t in f.Visual.GetComponentsInChildren<Transform>())
            {
                if (t == f.Visual || t.name.StartsWith("Slot_", StringComparison.Ordinal) || t.GetComponent<Renderer>() != null ||
                    t.GetComponent<Projectile>() != null || t.GetComponent<CombatBody>() != null) continue;
                float d = (t.position - point).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = t; }
            }
            return best;
        }

        static bool IsArenaFight(NpcCombatBrain brain, ActorId other) =>
            brain != null && brain.IsFighting && brain.State.Target == other && brain.State.Reason == AttackReason.Arena;

        /// <summary>
        /// G3 eCEntity::IsInFOV: <paramref name="whom"/> stands in the front half-space of <paramref name="who"/> (|azimuth|
        /// ≤ 90°; Docs/perception-crime.md §2). Decides parries and the "unaware victim" of the special player hits (fix-28:
        /// was ±100° [interp]).
        /// </summary>
        public static bool Sees(CombatFighter who, CombatFighter whom)
        {
            var to = whom.transform.position - who.transform.position;
            var fwd = who.transform.forward;
            return SneakCombatRules.InFieldOfView(fwd.x, fwd.z, to.x, to.z);
        }

        /// <summary>
        /// G3 AI mode Routine of an NPC (AssessHit's special player hits need it): not fighting, not reacting to the hero
        /// (warning, observing: <see cref="CrimeSystem.Reacting"/>, the view watching someone), not held by a spell (sleep,
        /// mist, freeze), not talking, alive (fix-28; was: only "not fighting").
        /// </summary>
        public bool InRoutine(CombatFighter f)
        {
            if (f == null || f.Actor.IsPlayer || Session == null) return false;
            var actor = f.Actor;
            var brain = Session.NpcCombat.Get(actor);
            bool reacting = Session.Crime.Crime.Reacting(actor) || f.GetComponentInParent<NpcView>() is NpcView view && view.Watching;
            bool spellbound = Session.SpellEffects.World.Paralysed(actor) || Session.Magic.Status.IsFrozen(actor);
            bool talking = Session.Npcs.TryGet(actor, out var agent) && agent.Activity == NpcActivity.Talking;
            return SneakCombatRules.InRoutine(brain != null && brain.IsFighting, reacting, spellbound, talking,
                                              Session.Combat.Vitals.StateOf(actor) == VitalState.Alive);
        }

        /// <summary>The victim would take a special player hit from <paramref name="attacker"/> now (in Routine, not seeing him).</summary>
        public bool Unaware(CombatFighter victim, CombatFighter attacker) =>
            victim != null && attacker != null && SneakCombatRules.Unaware(InRoutine(victim), Sees(victim, attacker));

        /// <summary>
        /// Attitude of <paramref name="self"/> towards <paramref name="other"/> from the attitude service
        /// (Docs/perception-crime.md §5; a fight does not change it, so arena and brawl knock-outs stay knock-outs). The
        /// player's attitude mirrors the NPC's [interp: G3 keeps no attitude of the player].
        /// </summary>
        public Attitude AttitudeOf(ActorId self, ActorId other) => Session.Crime.Attitudes.Of(self, other);

        /// <summary>
        /// The hero or a member of his party that is not summoned (G3: defeats and kills by the party count as the
        /// player's, for XP and the DefeatedByPlayer flag; Docs/party.md).
        /// </summary>
        bool ByPlayerParty(ActorId attacker) =>
            attacker.IsPlayer || (Session.Party?.Members?.DefeatsCountForPlayer(attacker) ?? false);

        public NpcCombatDef CombatDefOf(ActorId actor) =>
            !actor.IsPlayer && Session.Actors.TryGet(actor, out var state) ? CombatWiring.CombatDefOf(state) : null;

        /// <summary>The rule snapshot of a fighter with its equipment and stance.</summary>
        public Combatant Snapshot(CombatFighter f)
        {
            Combatant c;
            bool sneakParade = false;
            if (f.Actor.IsPlayer)
            {
                // An animal form fights with its NPC values (G3 IsTransformed: NPC attributes, protection; magic-3).
                c = Session.SpellEffects.TransformedPlayer() ?? Session.Combat.PlayerCombatant(Session.Hero);
                // The shield blocks only in the left hand, i.e. beside a drawn one-handed weapon (combat-6).
                c.Shield = Session.Items.Equipment.In(WornSlot.Shield) != null && f.HeldWeaponClass == WeaponClass.OneHanded;
                c.Swimming = f.TryGetComponent<G3.Unity.G3Player>(out var hero) && hero.Swimming;
                // The sneak stance with a melee weapon is G3's Parade ani-state: it parries in vanilla only (CP #54, fix-28).
                sneakParade = SneakCombatRules.SneakStanceBlocks(Session.Settings.Rules) && f.TryGetComponent<PlayerCombat>(out var pc) && pc.SneakingInParade;
            }
            else
            {
                c = Session.Combat.NpcCombatant(f.Actor, CombatDefOf(f.Actor), Session.Hero.Level);
                c.Summoned = Session.Summons.IsSummoned(f.Actor);
                var brain = Session.NpcCombat.Get(f.Actor);
                c.InRoutine = InRoutine(f);
                c.Shield = brain != null && brain.Loadout.Shield && f.HeldWeaponClass == WeaponClass.OneHanded;
            }
            c.Weapon = f.HeldWeaponClass; // the weapon in the hand, not the one selected (combat-6)
            // G3 dual wielding (the 1H_1H pair in the hands): the 1H1H perks, the power attack's level and doubling by the state
            // position (DualWieldRules.PowerAttackStatePosition), the AAI parry exception.
            c.DualWield = f.DualWielding;
            c.SecondPose = c.DualWield && DualWieldRules.SecondPose(DualWieldRules.PowerAttackStatePosition);
            c.Frozen = Session.Magic.Status.IsFrozen(f.Actor);
            c.CastStance = f.SpellDrawn;
            // G3 weapon config "melee": a melee weapon or the fists drawn (only then a heavy hit sits the victim down).
            c.MeleeConfig = f.Animator != null && f.Animator.Drawn && !f.RangedDrawn && !f.SpellDrawn;
            c.Action = f.CurrentAction;
            c.InParade = f.IsParrying || sneakParade;
            c.KnockedDown = f.SittingKnockedDown;
            return c;
        }

        /// <summary>gamepad-1: vibration on hits the hero lands or takes (option, only while the pad is in use).</summary>
        static void Rumble(bool heroIsVictim, HitOutcome o)
        {
            bool heavy = o.KnockedOut || o.Reaction == HitReaction.SitKnockDown || o.Reaction == HitReaction.LieKnockDown;
            var p = G3.Player.HitRumble.For(heroIsVictim, o.Result == HitResult.Blocked, heavy, o.Killed);
            if (!p.IsNone) global::G3.Unity.G3Pad.Rumble(p.Low, p.High, p.Seconds);
        }

        void OnHitResolved(HitResolved e)
        {
            if (e.Outcome.Result == HitResult.Ignored || !TryGet(e.Victim, out var victim)) return;
            var from = TryGet(e.Attacker, out var attacker) ? attacker.transform.position : victim.transform.position + victim.transform.forward;
            var o = e.Outcome;
            if (e.Victim.IsPlayer || e.Attacker.IsPlayer) Rumble(e.Victim.IsPlayer, o);
            if (e.Victim.IsPlayer)
            {
                if (CombatInputLog.Enabled) CombatInputLog.Line($"  -> {o.Result} {o.Reaction} damage {o.Damage}");
                PlayerHit?.Invoke(o);
            }
            if (o.Killed)
            {
                _diedWithoutHit.Remove(e.Victim);
                if (!e.Victim.IsPlayer) Drop(victim); // G3 Kill -> DropHandItems
                victim.Die(o.Reaction, from, RagdollPush(victim, attacker), HeroCollider());
            }
            else if (o.KnockedOut)
            {
                Drop(victim);
                victim.KnockOut(o.Reaction, from);
            }
            else victim.ShowHit(o, from);
        }

        void OnDefeated(ActorDefeated e)
        {
            if (e.Killed) _ransacks.Clear(e.Victim);
            if (!e.Killed) _fellWithoutHit.Add(e.Victim);
            else if (!_diedWithoutHit.Contains(e.Victim)) _diedWithoutHit.Add(e.Victim); // shown in LateUpdate unless its hit comes
        }

        /// <summary>The killer's push for a ragdoll (RagdollRules): its projectile, spell or melee action.</summary>
        RagdollImpulse RagdollPush(CombatFighter victim, CombatFighter attacker)
        {
            bool human = string.Equals(CombatDefOf(victim.Actor)?.Species, "Human", StringComparison.Ordinal);
            if (attacker == null) return RagdollRules.WithoutKiller(human);
            if (attacker.RangedDrawn) return RagdollRules.Projectile(attacker.WeaponClass);
            if (attacker.SpellDrawn) return RagdollRules.Projectile(null);
            return RagdollRules.Melee(attacker.CurrentAction, human);
        }

        /// <summary>The hero's CharacterController: ragdoll parts never block him.</summary>
        Collider HeroCollider() => TryGet(ActorId.Player, out var hero) ? hero.GetComponent<CharacterController>() : null;

        /// <summary>
        /// A knock-out reaches the view through its HitResolved (published after the defeat). One without a hit
        /// (Vitals.KnockOut from a status effect or a script) had no pose: the body stayed standing, e.g. while an NPC finished it.
        /// </summary>
        void LateUpdate()
        {
            if (_diedWithoutHit.Count > 0)
            {
                foreach (var actor in _diedWithoutHit)
                    if (Session != null && TryGet(actor, out var dead) && !dead.Animator.IsDead)
                    {
                        if (!actor.IsPlayer) Drop(dead);
                        // Pushed away from the last hit (else it falls backwards), with the default push.
                        var from = dead.TryGetLastHitFrom(out var hit) ? hit : dead.transform.position + dead.transform.forward;
                        dead.Die(HitReaction.Dead, from, RagdollPush(dead, null), HeroCollider());
                    }
                _diedWithoutHit.Clear();
            }
            if (_fellWithoutHit.Count == 0) return;
            foreach (var actor in _fellWithoutHit)
                if (Session != null && TryGet(actor, out var f)) Restore(f, atOnce: false);
            _fellWithoutHit.Clear();
        }

        void OnGotUp(ActorGotUp e)
        {
            _ransacks.Clear(e.Actor);
            if (!TryGet(e.Actor, out var f)) return;
            f.GetUp();
            // ZS_FetchWeapon: an NPC walks to its weapon and takes it (NpcCombatView). The hero's weapon stays where it fell:
            // he picks it up himself (G3: no automatic pickup; Docs/formats-items.md §3.6).
            if (e.Actor.IsPlayer) _dropped.Remove(e.Actor);
        }

        /// <summary>Where the actor's dropped weapon lies (ZS_FetchWeapon), false when nothing of its own is on the ground.</summary>
        public bool TryGetDroppedWeapon(ActorId actor, out Vector3 at)
        {
            at = default;
            if (!TryGetLying(actor, out var lying)) return false;
            at = new Vector3(lying.Position[0], lying.Position[1], lying.Position[2]);
            return true;
        }

        /// <summary>
        /// The actor's first dropped weapon of this knock-out that still lies in the world (the hero or a ransacker may have
        /// taken it); drops that are gone are forgotten.
        /// </summary>
        bool TryGetLying(ActorId actor, out DroppedItem lying)
        {
            lying = null;
            if (Session == null || !_dropped.TryGetValue(actor, out var list)) return false;
            list.RemoveAll(d => !Session.Items.World.TryGetDropped(d.WorldItem, out _));
            if (list.Count == 0)
            {
                _dropped.Remove(actor);
                return false;
            }
            return Session.Items.World.TryGetDropped(list[0].WorldItem, out lying);
        }

        /// <summary>_AI_TakeItem of ZS_FetchWeapon: the dropped weapons go back to their owner (carried again).</summary>
        public void PickUpDropped(ActorId actor)
        {
            if (!_dropped.TryGetValue(actor, out var list)) return;
            foreach (var d in list) Session?.Items.World.TakeDropped(d.WorldItem, actor, intoInventory: actor.IsPlayer);
            _dropped.Remove(actor);
            RefreshCarried(actor);
        }

        /// <summary>
        /// ZS_FetchWeapon with the weapon farther than 20 m: the owner goes on without it; it stays lying as a world item
        /// (anyone may pick it up) and is no longer in the NPC's loadout until G3's AssureItems gives it a new one.
        /// </summary>
        public void AbandonDropped(ActorId actor)
        {
            if (!_dropped.TryGetValue(actor, out var list) || actor.IsPlayer) return;
            _dropped.Remove(actor);
            foreach (var d in list) Session?.NpcCombat.TakeItem(actor, d.Item);
            RefreshCarried(actor);
        }

        // ------------------------------------------------------------------------------------------- weapons on the ground

        /// <summary>
        /// G3 DropHandItems (Defeat and Kill): the droppable weapon in the hand becomes a world item owned by the fighter. A
        /// sheathed weapon stays on its carry slot (combat-6: the hero lost his sheathed staff to a ransack this way). The
        /// hero's weapon leaves his inventory (unequipped); an NPC's leaves its loot while it lies there.
        /// </summary>
        void Drop(CombatFighter victim)
        {
            var item = victim.HeldWeaponItem;
            if (item != null && !_dropped.ContainsKey(victim.Actor) && Session != null)
            {
                // fix-149: DropHandItems drops the left hand too - the shield beside a one-handed weapon, a second blade.
                var left = LeftHandItem(victim, item);
                DropItem(victim, item, 0.5f);
                if (left != null) DropItem(victim, left, -0.5f);
            }
            victim.DropWeapon();
            RefreshCarried(victim.Actor);
        }

        /// <summary>What <paramref name="victim"/> holds in the left hand beside the drawn <paramref name="held"/> (DropHandItems).</summary>
        ItemDef LeftHandItem(CombatFighter victim, ItemDef held)
        {
            if (victim.SpellDrawn) return null;
            if (!victim.Actor.IsPlayer) return WeaponHold.LeftHandDrop(Session.NpcCombat.LoadoutOf(victim.Actor), held);
            // The hero: the worn shield while it is in his left hand (not beside a torch: PlayerCombat.ShowShield).
            if (!victim.TryGetComponent<PlayerCombat>(out var pc) || !pc.ShieldInHand || pc.TorchInLeftHand) return null;
            string id = Session.Items.Equipment.In(WornSlot.Shield);
            return id != null && Session.Content.Database.TryGet(id, out ItemDef shield) ? shield : null;
        }

        /// <summary>
        /// One hand's weapon becomes a world item owned by the fighter, beside it (<paramref name="side"/> m to its right). The
        /// hero's leaves his inventory (unequipped); an NPC's leaves its loot while it lies there.
        /// </summary>
        void DropItem(CombatFighter victim, ItemDef item, float side)
        {
            int quality = 0;
            if (victim.Actor.IsPlayer)
            {
                var items = Session.Items;
                var inventory = items.Inventories.Of(ActorId.Player);
                quality = inventory.QualityOf(item.Id);
                if (items.Equipment.IsWorn(item.Id) && inventory.Count(item.Id) <= 1) items.Equipment.UnequipItem(item.Id);
                if (items.Inventories.Consume(ActorId.Player, item.Id, 1) <= 0) return;
            }
            var p = victim.transform.position + victim.transform.right * side;
            p.y = NpcWalker.GroundHeight(p, p.y) + 0.03f;
            var world = Session.Items.World.Drop(victim.Actor, item.Id, 1, quality, new[] { p.x, p.y, p.z }, victim.transform.eulerAngles.y);
            if (world == null) return;
            if (!_dropped.TryGetValue(victim.Actor, out var list)) _dropped[victim.Actor] = list = new List<Dropped>();
            list.Add(new Dropped { Item = item.Id, WorldItem = world.Id });
        }

        /// <summary>
        /// ZS_RansackBody: the winner takes half of a knocked-out victim's gold (at least 1) and its dropped weapon.
        /// Returns a short description for the log.
        /// </summary>
        /// <summary>
        /// G3 WantToRansack's victim check: the winner may go and rob the body only when nobody else claimed it during this
        /// knock-out (the claim is made now). A second winner of the same fight leaves the body alone.
        /// </summary>
        public bool ClaimRansack(ActorId winner, ActorId victim) => _ransacks.TryClaim(victim, winner);

        public string Ransack(ActorId winner, ActorId victim)
        {
            if (Session == null || !Session.Combat.Vitals.IsDown(victim)) return "victim is up again";
            var inventories = Session.Items.Inventories;
            string gold = Session.Items.GoldItem;
            int have = gold != null ? inventories.Of(victim).Count(gold) : 0;
            int taken = have > 0 ? inventories.Transfer(victim, winner, gold, Math.Max(1, have / 2)) : 0;
            string weapon = null;
            // ZS_FetchWeapon with the victim as the other target: the winner takes the victim's weapon from the ground into
            // his inventory ("RansackWeapon"); the hero can win it back by looting him.
            // G3 FetchWeapon takes one weapon (the nearest); a dropped shield stays lying (fix-149).
            if (TryGetLying(victim, out var d))
            {
                if (Session.Items.World.TakeDropped(d.Id, winner)) weapon = d.Item;
                if (weapon != null && !victim.IsPlayer) Session.NpcCombat.TakeItem(victim, d.Item);
                if (_dropped.TryGetValue(victim, out var list))
                {
                    list.RemoveAll(x => x.WorldItem == d.Id);
                    if (list.Count == 0) _dropped.Remove(victim);
                }
                RefreshCarried(victim);
            }
            return $"{winner} ransacked {victim}: {taken} gold{(weapon != null ? ", " + weapon : "")}";
        }
    }
}
