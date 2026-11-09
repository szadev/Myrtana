using System.Collections;
using System.IO;
using System.Linq;
using G3.Unity.Characters;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero uses an interactive object (G3 player states <c>PS_Interact_Chest</c>, <c>_Door</c>, <c>_SleepBed</c>,
    /// station states): he steps to the object, plays his use motions and the session changes the state at the same
    /// moments as G3 — the lock is tried after the pick animation, the chest's contents are rolled when it opens, the door
    /// toggles in the middle of the push, the clock jumps while he sleeps. Refusals are the hero's SVM lines and HUD
    /// messages. Saving is blocked while a use runs. par-interact adds seats (the hero sits until a movement or use key),
    /// ore veins (pickaxe), treasure spots (shovel, then the loot page), water barrels, shrines (pray, then the dialog; an
    /// opposed god refuses) and robbing sleepers with Perk_MasterThief.
    /// </summary>
    public sealed class HeroObjectUse : MonoBehaviour, ISessionView, ISessionControlHolder
    {
        const float Fade = 0.2f, PickSeconds = 2.5f, StationSeconds = 4f, SleepSeconds = 3f, BoulderHoldSeconds = 1.5f;
        static readonly TextId MissingSkill = new TextId("HUD_MissingSkill"), MissingItem = new TextId("HUD_MissingItem_Interact"),
                               LockpickUsed = new TextId("GO_LockpickUsed"), NotDuringBattle = new TextId("HUD_NotDuringBattle"),
                               ChestLocked = new TextId("HUD_ChestLocked");

        GameSession _session;
        HeroControlLock _controls;
        InteractiveObjectViews _views;
        GameScreens _screens;
        G3LootScreen _loot;
        HudMessages _messages;
        VoicePlayer _voice;
        LanguageSettings _languages;
        G3MotionPlayer _motions;
        CraftingHost _crafting;
        G3SynthScreen _synth;
        G3TransformScreen _transform;
        HeroLines _lines;
        HeroHandProp _prop, _leftProp;
        bool _standUp, _inActivity, _activityLooping;
        // the used object's side of the use (fix-61): its own part of every hero motion (the grindstone's wheel, a chest
        // lid, the barbecue spit, the water pipe, the orc boulder), played by Play() and settled by Finish()
        InteractiveObjectView _used;
        ObjectPartState _parts;
        string _partInfo = "-";
        bool _stationLooping;
        // the loops the last Repeat / TimedLoop played (LastWork, fix-111)
        int _repeated;
        // the bookstand reading whose attribute raise waits for the end of the reading aloud (fix-77)
        BookstandReading _reading;
        // the object's slot the hero holds while he uses it (Docs/interactive-objects.md §9); None without routine data or
        // at a campfire whose every slot is taken
        Simulation.Station _held = Simulation.Station.None;

        /// <summary>A station's action loop runs (its page is open, or the 4 s use of a station without one); fix-61 dev runner.</summary>
        public bool StationLooping => Busy && _stationLooping;

        /// <summary>The used object's animation (dev probe <c>objpart</c>): "Playing &lt;part&gt;", after the use "Resting &lt;part&gt;", "-" without parts.</summary>
        public string ObjectPart => _partInfo;

        /// <summary>
        /// The last tool work (dev probe <c>lastwork</c>, fix-111/139): "&lt;use type&gt; loops=n window=10s took=t left=&lt;item|-&gt;
        /// right=&lt;item|-&gt; motion=&lt;yes|no&gt;" — the Loop cycles played, the command time, the work's length, the hand
        /// props that were shown, whether the motion existed.
        /// </summary>
        public string LastWork { get; private set; } = "-";

        public bool Busy { get; private set; }

        /// <summary>The station pages and the crafting services (Docs/interactive-objects.md §6); without them stations only play motions.</summary>
        public void InitStations(CraftingHost crafting, G3SynthScreen synth, G3TransformScreen transform, HeroLines lines)
        {
            _crafting = crafting;
            _synth = synth;
            _transform = transform;
            _lines = lines;
        }

        public void Init(HeroControlLock controls, InteractiveObjectViews views, GameScreens screens, G3LootScreen loot,
                         HudMessages messages, VoicePlayer voice, LanguageSettings languages)
        {
            _controls = controls;
            _views = views;
            _screens = screens;
            _loot = loot;
            _messages = messages;
            _voice = voice;
            _languages = languages;
        }

        public void Bind(GameSession session)
        {
            StopAllCoroutines();
            if (Busy) Finish();
            _session = session;
            session.AddSaveBlocker(SaveBlocker.When(() => Busy, "interaction"));
        }

        /// <summary>
        /// Why <see cref="Use"/> would refuse now (null = it would start): "busy", "no session", "no view" (the object is not
        /// shown near the hero yet: views appear within 40 m, checked every 0.5 s), "in combat", "occupied" or "cook fire off"
        /// (a port cooking fire with the option off, fix-88).
        /// </summary>
        public string UseRefusal(string id) =>
            Busy ? "busy" : _session == null ? "no session" : !_views.TryGet(id, out _) ? "no view"
            : _session.NpcCombat.IsPlayerInCombat ? "in combat" : IsOccupied(id) ? "occupied"
            : _views.TryGet(id, out var v) && !CookFires.Usable(v.Def, PlayerInteraction.CookOnAnyFire) ? "cook fire off" : null;

        /// <summary>
        /// Someone else uses the object (Docs/interactive-objects.md §9, G3 <c>gCInteraction_PS.User</c>): the hero can
        /// neither focus nor use it. Never true for a campfire, which everyone shares.
        /// </summary>
        public bool IsOccupied(string id) =>
            _session != null && _session.Npcs.Claims.IsOccupiedFor(new Id<RoutinePointDef>(id), ActorId.Player);

        /// <summary>
        /// Starts using the object; false when the hero is busy, may not use objects now, or someone else uses it (G3
        /// refuses silently: such an object cannot even be focused). The hero takes the object's slot nearest to him, so
        /// NPCs keep off it until he is done (<see cref="Finish"/> releases it, also when the use is cut short).
        /// </summary>
        public bool Use(string id)
        {
            if (Busy || _session == null || !_views.TryGet(id, out var view)) return false;
            if (view.Def.Kind == InteractiveKind.Sign) return false; // G3 UseType None: a signpost is only read (fix-80)
            if (!CookFires.Usable(view.Def, PlayerInteraction.CookOnAnyFire)) return false; // fix-88: option off = decoration
            if (_session.NpcCombat.IsPlayerInCombat)
            {
                _messages.Show(Text(NotDuringBattle), NoticeKind.Error);
                return false;
            }
            var from = _controls.Player.transform.position;
            if (!_session.Npcs.Claims.TryOccupy(new Id<RoutinePointDef>(id), ActorId.Player, from.x, from.z, out _held)) return false;
            Busy = true;
            HeroTorchView.Instance?.BeginInteraction(CookFires.StationUseType(view.Def), view.Def.Name); // G3: stowed, kept or released to the inventory (effects-2, fix-4)
            // G3 PS_Interact: the hero first walks to the object (_AI_Goto_Ext, fix-75): the keys no longer steer him, but the
            // body stays his own until he is there (GoTo); then the use takes it (EndWalk)
            _controls.AcquireWalking(this);
            _walking = true;
            _cancelled = _cancelRequest = false;
            _motions = _controls.Player.GetComponentInChildren<G3MotionPlayer>();
            _used = view;
            // fix-103: a seat or a bed is used on the furniture itself; every other use stands beside the object, outside it
            _clearSpot = view.Def.Kind != InteractiveKind.Seat && view.Def.Kind != InteractiveKind.Bed;
            LastObject = id;
            _parts = new ObjectPartState(view.HasPart);
            _partInfo = "-";
            switch (view.Def.Kind)
            {
                case InteractiveKind.Seat: StartCoroutine(Seat(view)); break;
                case InteractiveKind.OreVein: StartCoroutine(OreVein(view)); break;
                case InteractiveKind.Treasure: StartCoroutine(Treasure(view)); break;
                case InteractiveKind.WaterBarrel: StartCoroutine(WaterBarrel(view)); break;
                case InteractiveKind.Activity: StartCoroutine(Activity(view)); break;
                case InteractiveKind.Speaker: StartCoroutine(Shrine(view)); break;
                case InteractiveKind.Chest: StartCoroutine(Chest(view)); break;
                case InteractiveKind.Door:
                case InteractiveKind.Switch: StartCoroutine(Door(view)); break;
                case InteractiveKind.Bed: StartCoroutine(Bed(view)); break;
                case InteractiveKind.Boulder: StartCoroutine(Boulder(view)); break;
                default: StartCoroutine(Station(view)); break;
            }
            return true;
        }

        /// <summary>
        /// CP quick loot (ge3.ini QuickLoot, "right click looting"; PS_Normal Use2 on a focused chest): without kneeling
        /// or opening the loot page, a locked chest says <c>HUD_ChestLocked</c>, during a fight <c>HUD_NotDuringBattle</c>
        /// and the hero's "doesn't work"; otherwise the chest opens (its owner may see the theft, its treasure is rolled
        /// the first time) and every stack goes to the hero, announced grouped (one <c>GO_ItemTaken_Formatted</c> line per item, fix-205). The hero
        /// keeps walking. False when the object is no chest or the hero is busy.
        /// </summary>
        public bool QuickLoot(string id)
        {
            if (Busy || _session == null || !_views.TryGet(id, out var view) || view.Def.Kind != InteractiveKind.Chest) return false;
            return Present(new Myrtana.Session.QuickLoot(_session).Chest(id));
        }

        /// <summary>
        /// The hero's side of a <see cref="Myrtana.Session.QuickLoot"/> result: the fight's and the lock's messages, "NothingToGet";
        /// the taken items are announced grouped by GameNotices (QuickLootFinished). G3 has no animation for it (CP's quick
        /// loot leaves the hero walking; a body is looted standing). False when the target was not lootable.
        /// </summary>
        bool Present(QuickLootResult r)
        {
            switch (r.Outcome)
            {
                case QuickLootOutcome.InBattle:
                    _messages.Show(Text(NotDuringBattle), NoticeKind.Error);
                    _lines?.SayAny(_session, HeroSpokenLines.DoesntWork);
                    return true;
                case QuickLootOutcome.Locked:
                    _messages.Show(Text(ChestLocked), NoticeKind.Error);
                    return true;
                case QuickLootOutcome.Nothing:
                    _lines?.SayAny(_session, HeroSpokenLines.NothingToGet);
                    return true;
                case QuickLootOutcome.Taken: return true;
                default: return false;
            }
        }

        /// <summary>
        /// The hero loots a downed or dead NPC (G3 PS_Normal Use on a body → PS_GonnaLoot → PlayerOpenLoot, CP 1.75): the
        /// loot page opens when anything is lootable (no merchandise, no spell books), else he says "NothingToGet". He
        /// stands where he is (no motion) and the body stays down while the page is open. No crime. False when busy.
        /// </summary>
        public bool LootBody(ActorId npc)
        {
            var loot = _session?.Items.NpcLoot;
            if (Busy || loot == null || !IsBody(npc)) return false;
            if (!loot.HasLoot(npc))
            {
                _lines?.SayAny(_session, HeroSpokenLines.NothingToGet);
                return true;
            }
            if (_loot == null)
            {
                loot.TakeAll(npc); // no G3 GUI import: take everything at once; GameNotices announces each stack
                return true;
            }
            Busy = true;
            _motions = null;
            _controls.Acquire(this, () => Busy); // fix-201: the lock never outlives the use
            StartCoroutine(Body(npc));
            return true;
        }

        /// <summary>CP quick loot (action 0x90) on a downed or dead NPC: every lootable stack, grouped "taken" lines.</summary>
        public bool QuickLootBody(ActorId npc)
        {
            if (Busy || _session == null || !IsBody(npc)) return false;
            return Present(new Myrtana.Session.QuickLoot(_session).Body(npc));
        }

        /// <summary>
        /// fix-205 [owner] chord on a helpless NPC the hero may rob (<see cref="CanRobSleeper"/>): robbed as by Use (a spell
        /// sleeper's stacks are thefts) and emptied without the loot page.
        /// </summary>
        public bool QuickRobSleeper(ActorId npc)
        {
            if (!CanRobSleeper(npc)) return false;
            return Present(new Myrtana.Session.QuickLoot(_session).Sleeper(npc, _controls.Player.Sneaking));
        }

        /// <summary>
        /// A shrine (talking object of UseType Shrine): E prays and then talks (<see cref="Use"/>) instead of talking at once.
        /// </summary>
        public bool IsShrine(string id) => _session != null && _session.Interaction.Uses.IsShrine(id);

        /// <summary>
        /// A helpless human the hero can rob (<see cref="SleeperTheft"/>): G3 sneaking with Perk_MasterThief, or [owner]
        /// fix-113 any human held by the sleep spell (then a theft).
        /// </summary>
        public bool CanRobSleeper(ActorId npc) =>
            _session != null && !Busy && _controls != null && _session.SleeperTheft.CanRob(npc, _controls.Player.Sneaking);

        /// <summary>
        /// CP 1.75 PS_Normal_Loop Use on a helpless NPC: its plunder is rolled once (CleanUpPlunderInv) and the loot page opens
        /// on it like on a body (PS_GonnaLoot), or the hero says "NothingToGet". The victim sleeps on. False when not allowed.
        /// </summary>
        public bool RobSleeper(ActorId npc)
        {
            if (!CanRobSleeper(npc)) return false;
            if (!_session.SleeperTheft.Rob(npc, _controls.Player.Sneaking))
            {
                _lines?.SayAny(_session, _session.Interaction.Uses.Rules.NothingLine);
                return true;
            }
            if (_loot == null)
            {
                _session.Items.NpcLoot.TakeAll(npc);
                return true;
            }
            Busy = true;
            _motions = null;
            _controls.Acquire(this, () => Busy); // fix-201: the lock never outlives the use
            StartCoroutine(Body(npc, sleeper: true));
            return true;
        }

        /// <summary>A downed (knocked-out) or dead NPC: G3 AIMode 8 or 9.</summary>
        public bool IsBody(ActorId npc) =>
            _session != null && !npc.IsEmpty && !npc.IsPlayer && (_session.Combat.Vitals.IsDown(npc) || _session.Combat.Vitals.IsDead(npc));

        IEnumerator Body(ActorId npc, bool sleeper = false)
        {
            _loot.WithBody(npc);
            _screens.Open(_loot);
            while (_loot.IsOpen)
            {
                // fix-113: a robbed sleeper that wakes up (or is knocked out meanwhile) ends the robbery [interp]
                if (sleeper && !_session.SleeperTheft.IsHelpless(npc) && _screens.Current == _loot) { _screens.CloseCurrent(); break; }
                yield return null;
            }
            Finish();
        }

        IEnumerator Chest(InteractiveObjectView view)
        {
            var objects = _session.Interaction.Objects;
            yield return GoTo(PlaceChest(view));
            if (_cancelled) { Finish(); yield break; }
            yield return Once(G3HeroUseMotions.Kneel);
            if (objects.IsLocked(view.Id))
            {
                bool opened = false;
                yield return Unlock(view, kneeling: true, result => opened = result);
                if (!opened)
                {
                    yield return Once(G3HeroUseMotions.StandUpFromKneel);
                    Finish();
                    yield break;
                }
            }
            objects.OpenContainer(view.Id);
            // G3: the lid moves with the hero's OpenChest motions (their _I_ parts); a chest prefab built without them
            // swings the lid by hand
            bool lidByHand = !view.HasUsePart(G3HeroUseMotions.OpenChest("Begin"));
            _parts.RestAt(G3HeroUseMotions.OpenChest("End"));
            if (lidByHand) view.SetLid(true);
            yield return Once(G3HeroUseMotions.OpenChest("Begin"));
            Loop(G3HeroUseMotions.OpenChest("Loop"));
            if (_loot != null)
            {
                _loot.With(view.Id);
                _screens.Open(_loot);
                while (_loot.IsOpen) yield return null;
            }
            else objects.TakeAll(view.Id); // no G3 GUI import: take everything at once
            if (lidByHand) view.SetLid(false);
            yield return Once(G3HeroUseMotions.OpenChest("End"));
            yield return Once(G3HeroUseMotions.StandUpFromKneel);
            Finish();
        }

        IEnumerator Door(InteractiveObjectView view)
        {
            var objects = _session.Interaction.Objects;
            yield return GoTo(Place(view, 0f));
            if (_cancelled) { Finish(); yield break; }
            if (objects.IsLocked(view.Id))
            {
                bool opened = false;
                yield return Unlock(view, kneeling: false, result => opened = result);
                if (!opened)
                {
                    Finish();
                    yield break;
                }
            }
            if (Play(G3HeroUseMotions.OpenDoor("Begin"))) yield return UntilFinished(1.5f);
            if (view.Def.Kind == InteractiveKind.Switch) objects.UseSwitch(view.Id);
            else if (objects.ToggleDoor(view.Id)) Sound(view, DoorSound);   // G3 PS_Interact_Door: same effect both ways
            yield return Once(G3HeroUseMotions.OpenDoor("End"));
            Finish();
        }

        IEnumerator Bed(InteractiveObjectView view)
        {
            var spot = Place(view, 0f);
            string side = spot.Side;
            yield return GoTo(spot);
            if (_cancelled) { Finish(); yield break; }
            if (side != "Right") side = "Left";
            yield return Once(G3HeroUseMotions.LieDownInBed(side));
            Loop(G3HeroUseMotions.SleepInBed(side));
            // G3 PS_Interact_SleepBed (Script 0x10075d40): lying down, AssessTalk(hero, bed) opens the bed's dialog when it
            // has infos — "sleep until morning / noon / evening / midnight" (info command Sleep → PlayerSleep(hour)) and
            // the hero's "End", which gets up without sleeping; only a bed without infos sleeps straight to 08:00.
            var bed = new ActorId(view.Def.Name);
            if (_session.Dialog.CanTalkTo(bed))
            {
                InBedDialog = true;
                _session.Dialog.Talk(bed);
                yield return null;
                while (_session.Dialog.IsActive) yield return null;
                InBedDialog = false;
            }
            else
            {
                // [owner] fix-75: the secondary button gets him up before the night has passed (no time skip)
                bool getUp = false;
                for (float until = Time.time + SleepSeconds * 0.5f; Time.time < until && !getUp; )
                {
                    getUp = CancelPressed();
                    yield return null;
                }
                if (!getUp)
                {
                    _session.Interaction.Sleep.SleepUntilMorning();
                    yield return new WaitForSeconds(SleepSeconds * 0.5f);
                }
            }
            yield return Once(G3HeroUseMotions.GetUpFromBed(side));
            Finish();
        }

        /// <summary>The hero lies in a bed and its sleep dialog is open (fix-77; dev runner).</summary>
        public bool InBedDialog { get; private set; }

        /// <summary>
        /// G3 <c>PS_Interact_TiltOrcBoulder → HoldOrcBoulder → LiftOrcBoulder</c>: the hero tilts the boulder; taking it up
        /// checks the tilt strength, lifting it the lift strength (a story boulder's game event is set then); too heavy → he
        /// says "TooHeavy" and lets go. Once it is up he holds it a moment (the first lift of each boulder raises STR) and puts
        /// it down. The boulder is not attached to a hand: every hero motion has the object's own part (the <c>_I_</c>
        /// variant, keyed to the boulder actor's <c>OrcBoulder_Stone</c> bone), played on the boulder in step with the hero
        /// (<see cref="Play"/>), who stands exactly at the boulder's origin (its <c>G3_Dummy_Offset_Exact_01</c>) facing it.
        /// </summary>
        IEnumerator Boulder(InteractiveObjectView view)
        {
            var boulders = _session.Interaction.Boulders;
            yield return GoTo(PlaceExact(view));
            if (_cancelled) { Finish(); yield break; }
            string stance = "Stand";
            yield return Once(G3HeroUseMotions.BoulderNext(stance, "TiltOrcBoulder"));
            stance = "TiltOrcBoulder";
            _parts.RestAt(G3HeroUseMotions.BoulderLetGo(stance)); // cut short from here on: the stone lies down again
            Loop(G3HeroUseMotions.BoulderLoop(stance));
            yield return new WaitForSeconds(BoulderHoldSeconds);
            foreach (var (stage, next) in new[] { (BoulderStage.Tilt, "HoldOrcBoulder"), (BoulderStage.Lift, "LiftOrcBoulder") })
            {
                if (boulders.Check(view.Id, stage) != BoulderVerdict.Ok)
                {
                    _lines?.SayAny(_session, boulders.Rules.RefusalLine);
                    break;
                }
                yield return Once(G3HeroUseMotions.BoulderNext(stance, next));
                stance = next;
                _parts.RestAt(G3HeroUseMotions.BoulderLetGo(stance));
                Loop(G3HeroUseMotions.BoulderLoop(stance));
                yield return new WaitForSeconds(BoulderHoldSeconds);
                if (stage == BoulderStage.Lift) boulders.FinishLift(view.Id);
            }
            yield return Once(G3HeroUseMotions.BoulderLetGo(stance));
            Finish();
        }

        /// <summary>
        /// G3 exact placement: the hero stands on the object actor's <c>G3_Dummy_Offset_Exact_01</c>
        /// (<see cref="InteractiveObjectView.ExactUsePoint"/>; the boulder's lies on its origin, the stone 95 cm along −Z;
        /// the water pipe's 0.99 m in front of the pipe, fix-189: he sat inside the pipe on its origin) and faces along the
        /// object's −Z, so the object's part of the motion meets his hands.
        /// </summary>
        UseSpot PlaceExact(InteractiveObjectView view)
        {
            var t = view.transform;
            var face = Vector3.ProjectOnPlane(-t.forward, Vector3.up);
            float yaw = face.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(face).eulerAngles.y : _controls.Player.Yaw;
            return new UseSpot(view.ExactUsePoint, yaw, true, "Fwd", exact: true);
        }

        /// <summary>
        /// fix-243 (owner: the hero knelt inside a small chest while picking it): G3 kneels the user on the chest actor's
        /// <c>G3_Dummy_Offset_Exact_01</c>, 1.41 m in front of its origin, facing the chest (<see cref="ChestUsePose"/>), so
        /// the kneel motions' forward lean (pelvis 33 cm ahead of his origin) ends at the lid. The prefab's dummy when the
        /// visual has one, else the same point from the object's pose; the floor under it (a chest on a slope). Not exact:
        /// a dummy inside a wall is still moved out (fix-103), facing the chest.
        /// </summary>
        UseSpot PlaceChest(InteractiveObjectView view)
        {
            var t = view.transform;
            var (x, y, z, yaw) = ChestUsePose.Spot(view.Def.Position, view.Def.Forward, view.Def.Scale);
            var at = new Vector3(x, y, z);
            if (view.TryExactUsePoint(out var dummy))
            {
                at = dummy;
                yaw = ChestUsePose.FaceYaw(at.x, at.z, t.position.x, t.position.z, yaw);
            }
            var player = _controls.Player;
            if (player != null && FloorBelow(at, player.transform, t, out float floor, StepAbove)) at.y = floor + 0.05f;
            return new UseSpot(at, yaw, true, "Fwd");
        }

        /// <summary>
        /// G3 <c>PS_Interact</c> for stations: the checks first (recipes, tool, skill, blade weapon; a refusal ends the use
        /// before any motion), then Begin → Loop while the page is open (or while the hero reads a bookstand aloud) → End.
        /// </summary>
        IEnumerator Station(InteractiveObjectView view)
        {
            var services = _crafting?.Services;
            // fix-88: a cooking fire is a campfire (the same fry page and motion, CookFires)
            string useType = CookFires.StationUseType(view.Def);
            StationCheck check = services != null ? services.Crafting.Check(useType) : default;
            if (services != null && !check.Opens)
            {
                if (check.Verdict != StationVerdict.NotUsable) RefuseStation(check);
                Finish();
                yield break;
            }
            yield return GoTo(CookFires.IsCookFire(view.Def) ? CookFireSpot(view) : Place(view, 0f));
            if (_cancelled) { Finish(); yield break; }
            var keys = G3HeroUseMotions.Station(useType);
            // G3 PS_Interact_Campfire sits down and fries (Hero_SitGround_Meat_Tool_*); the other stations stand
            var campfire = keys == null && string.Equals(useType, CookFires.Campfire, System.StringComparison.OrdinalIgnoreCase)
                ? G3HeroUseMotions.Campfire : null;
            if (campfire != null)
            {
                yield return Once(campfire.Enter);
                Hold(G3HeroUseMotions.CampfireProps.left, G3HeroUseMotions.CampfireProps.right); // _AI_HoldInventoryItems It_Meat, It_Stick
                yield return Once(campfire.Begin);
                Loop(campfire.Loop);
            }
            // G3 PS_Interact_<Station> -> PS_Interact_Loop: the action loop with a 10 % chance of the Ambient variant per cycle
            // while the page is open; the object's own parts follow (the grindstone's wheel and pedal, the barbecue spit)
            var looping = keys != null ? G3HeroUseMotions.Looping(useType) : null;
            if (keys != null)
            {
                // the station tools in the hands (_AI_HoldItems: PS_Interact_Anvil It_Sword + It_Hammer_Smith, GrindStone
                // It_Sword, Cauldron It_Scoop, Alchemy It_Potion)
                if (G3InteractionMotions.TryGet(useType, out var held)) Hold(held.LeftProp, held.RightProp);
                _parts.RestAt(keys[2]);
                yield return Once(keys[0]);
                Loop(keys[1]);
            }
            var page = check.Rule?.Page ?? StationPage.None;
            GameScreen screen = null;
            if (page == StationPage.Synth && _synth != null)
            {
                _synth.With(check.Rule);
                screen = _synth;
            }
            else if ((page == StationPage.Sharpen || page == StationPage.Fry) && _transform != null)
            {
                _transform.With(check.Rule);
                screen = _transform;
            }
            _stationLooping = page != StationPage.Read;
            if (screen != null)
            {
                _screens.Open(screen);
                yield return LoopWhile(looping, () => screen.IsOpen);
            }
            else if (page == StationPage.Read && services != null)
            {
                // G3 BS_*: the hero reads the pages aloud, then AddIntelligence/AddAlchemy/AddSmithing raise the attribute
                // and print "<attribute> + n = value" (fix-77: the raise used to come silently before the reading)
                var reading = services.Read(view.Id, deferGain: true);
                _reading = reading; // Finish learns it when the use is cut short: the stand is used up already
                foreach (var line in reading.Lines)
                    yield return _lines != null ? _lines.SayAndWait(_session, line) : null;
                if (reading.Lines.Count == 0) yield return new WaitForSeconds(StationSeconds);
                services.Learn(reading);
                _reading = null;
            }
            else
            {
                float end = Time.time + StationSeconds;
                // [owner] fix-75 / G3 action 0x66: the secondary button ends the station's work early (its End motion plays)
                yield return LoopWhile(looping, () => Time.time < end && !CancelPressed());
            }
            if (keys != null) yield return Once(keys[2]);
            if (campfire != null)
            {
                yield return Once(campfire.End);
                Unhold();
                yield return Once(campfire.Leave);
            }
            Finish();
        }

        // ------------------------------------------------------------------------------------------------ par-misc uses

        /// <summary>
        /// G3 PS_Interact cases 0x48 / 0x55 / 100 + PS_Interact_SawLog / UseWaterpipe / OrcDrum: a log needs It_Saw, a drum
        /// two It_DrumStick (else HUD_MissingItem_Interact + the tool's name), a water pipe nothing. The hero takes the tools
        /// (_AI_HoldItems), sits down for the pipe (_AI_ChangeAction SitGround), plays Begin and then PS_Interact_Loop until
        /// the exit action (a movement or use key, a fight).
        /// </summary>
        IEnumerator Activity(InteractiveObjectView view)
        {
            var uses = _session.Interaction.Uses;
            var verdict = uses.CheckActivity(view.Id);
            if (verdict != ObjectUseVerdict.Ok)
            {
                RefuseUse(verdict, uses.ActivityToolOf(view.Id));
                Finish();
                yield break;
            }
            var motions = G3HeroUseMotions.Looping(view.Def.UseType);
            // an object with its own part of the motion (the animated water pipe) needs the exact stand point, as the boulder
            bool parts = motions != null && view.HasUsePart(motions.Begin);
            yield return GoTo(parts ? PlaceExact(view) : PlaceAtSpot(view, 0.9f));
            if (_cancelled) { Finish(); yield break; }
            if (motions != null) _parts.RestAt(motions.End);
            if (G3InteractionMotions.TryGet(view.Def.UseType, out var pose)) Hold(pose.LeftProp, pose.RightProp);
            _inActivity = true;
            if (motions != null)
            {
                if (motions.Enter != null) yield return Once(motions.Enter);
                yield return Once(motions.Begin);
            }
            yield return LoopUntilExit(motions);
            if (motions != null) yield return Once(motions.End);
            Unhold();
            if (motions?.Leave != null) yield return Once(motions.Leave);
            Finish();
        }

        /// <summary>True while the hero smokes, saws or drums.</summary>
        public bool InActivity => _inActivity;

        /// <summary>The activity's action loop runs (after the sit-down and the Begin motion; fix-46: the dev runner's 'use' waits
        /// for it, r28 par_misc saw SitGround_Begin and the camera mode before its first LateUpdate).</summary>
        public bool ActivityLooping => _inActivity && _activityLooping;

        /// <summary>
        /// G3 PS_Interact_Loop: the action loop, each cycle with a 10 % chance of the Ambient variant
        /// (GetRandomNumber(100) &lt; 10), until the exit action 0x66 (<see cref="ExitPressed"/>, <see cref="StandUp"/>, a
        /// fight; never during a dialog).
        /// </summary>
        IEnumerator LoopUntilExit(G3HeroUseMotions.LoopUse motions)
        {
            _activityLooping = true;
            _standUp = false;
            int dialogEnded = -1;
            bool Exit()
            {
                if (_session.Dialog.IsActive) dialogEnded = Time.frameCount;
                else if (Time.frameCount > dialogEnded + 1 && (_standUp || ExitPressed())) return true;
                return false;
            }
            yield return LoopWhile(motions, () => !Exit());
        }

        /// <summary>
        /// G3 PS_Interact_Loop: the action loop while <paramref name="going"/> holds, each cycle with a 10 % chance of the
        /// Ambient variant (GetRandomNumber(100) &lt; 10, when the pose has one), back to the loop after one cycle of it
        /// (<see cref="ActionLoop"/>; the Ambient motions loop in the data, fix-61: before, the hero stayed in it for good).
        /// </summary>
        IEnumerator LoopWhile(G3HeroUseMotions.LoopUse motions, System.Func<bool> going)
        {
            var cycle = new ActionLoop();
            if (motions != null) Loop(motions.Loop);
            yield return null;
            while (going())
            {
                if (motions != null && _motions != null)
                {
                    switch (cycle.Step(_motions.CurrentPhase, _motions.CurrentFinished, Random.value))
                    {
                        case ActionLoopStep.Ambient:
                            if (!Play(motions.Ambient)) cycle.AmbientMissing();
                            break;
                        case ActionLoopStep.Loop:
                            Loop(motions.Loop);
                            break;
                    }
                }
                yield return null;
            }
        }

        /// <summary>Items in the hands during a use (null = that hand empty): G3 _AI_HoldItems / _AI_HoldInventoryItems.</summary>
        /// <returns>Which of the asked props got a mesh in the hand (left, right).</returns>
        (bool left, bool right) Hold(string left, string right)
        {
            var hero = _controls.Player.transform;
            _prop ??= new HeroHandProp(hero);
            _leftProp ??= new HeroHandProp(hero);
            bool l = false, r = false;
            if (left != null) l = _leftProp.Show(_session, left, leftHand: true);
            else _leftProp.Hide();
            if (right != null) r = _prop.Show(_session, right, leftHand: false);
            else _prop.Hide();
            return (l, r);
        }

        void Unhold()
        {
            _prop?.Hide();
            _leftProp?.Hide();
        }

        // ------------------------------------------------------------------------------------------- par-interact uses

        /// <summary>
        /// G3 PS_Interact_SitBench / SitStool / SitThrone: the hero sits down on the seat's use spot and stays until a movement
        /// or use key (PS_Interact_WaitForExit; a fight against him also gets him up). While he sits, CP 1.75 regenerates
        /// min(5, HPmax/100) HP a second without Perk_MasterGladiator (<see cref="HeroRegeneration.Sitting"/>).
        /// </summary>
        IEnumerator Seat(InteractiveObjectView view)
        {
            string stance = G3InteractionMotions.TryGet(view.Def.UseType, out var pose) ? pose.Stance : "SitBench";
            yield return GoTo(PlaceAtSpot(view, 0f));
            if (_cancelled) { Finish(); yield break; }
            yield return Once(G3HeroUseMotions.SitDown(stance));
            Loop(G3HeroUseMotions.Sitting(stance));
            _session.Regeneration.Sitting = true;
            _standUp = false;
            int dialogEnded = -1;
            yield return null;
            while (true)
            {
                if (_session.Dialog.IsActive) dialogEnded = Time.frameCount;
                else if (Time.frameCount > dialogEnded + 1 && (_standUp || ExitPressed())) break;
                yield return null;
            }
            _session.Regeneration.Sitting = false;
            // A hit knocked him off the seat: the combat body has him (fall, sitting knock-down); no stand-up motion over it.
            if (!CombatHasBody()) yield return Once(G3HeroUseMotions.StandUpFrom(stance));
            Finish();
        }

        /// <summary>Gets the sitting hero up (as a movement key would); false when he does not sit.</summary>
        public bool StandUp()
        {
            if (!Busy || _session == null || !(_session.Regeneration.Sitting || _inActivity)) return false;
            _standUp = true;
            return true;
        }

        /// <summary>
        /// The keys that end a sit, through the key map (<see cref="G3Input"/>, par-controls): the movement keys, jump and
        /// Use1 (G3 left button). E is G3's camera rotation and no longer ends a sit. A fight against him or a hit (a stumble,
        /// the sitting knock-down) ends it too (<see cref="HeroSitInput"/>, fix-39).
        /// </summary>
        bool ExitPressed()
        {
            var presses = new HeroSitPresses
            {
                Use1 = G3Input.WasPressed(G3.Player.SessionKey.Use1),
                Use2 = CancelPressed(),
                Move = G3Input.WasPressed(G3.Player.SessionKey.Forward) || G3Input.WasPressed(G3.Player.SessionKey.Backward) ||
                       G3Input.WasPressed(G3.Player.SessionKey.StrafeLeft) || G3Input.WasPressed(G3.Player.SessionKey.StrafeRight),
                Jump = G3Input.WasPressed(G3.Player.SessionKey.Up),
            };
            var state = new HeroSitState
            {
                Seated = true, // an activity loop (smoking, sawing, drumming) ends the same way
                Disturbed = _session.NpcCombat.IsPlayerInCombat || CombatHasBody(),
            };
            return HeroSitInput.Decide(state, presses) == HeroSitExit.LeaveSeat;
        }

        /// <summary>The hero's combat body plays a hit, a fall or the down state (a hit reached him during the use).</summary>
        bool CombatHasBody()
        {
            if (_heroFighter == null && _controls?.Player != null) _heroFighter = _controls.Player.GetComponent<CombatFighter>();
            var a = _heroFighter != null ? _heroFighter.Animator : null;
            return a != null && (a.IsDown || a.InReaction || a.IsDead);
        }

        CombatFighter _heroFighter;

        /// <summary>
        /// Dev safety valve (console <c>getup</c>, fix-39): ends any running use at once (seat, activity, station...),
        /// without its closing motion. False when none runs.
        /// </summary>
        public bool ForceEnd()
        {
            if (!Busy) return false;
            StopAllCoroutines();
            Finish();
            return true;
        }

        /// <summary>
        /// G3 PS_Interact case 67 + PS_Interact_PickOre: a used vein → "NothingToGet", no pickaxe → HUD_MissingItem_Interact;
        /// else the pickaxe work (_AI_HoldItems It_Pickaxe, _AI_Action PickOre for 10 s: 5 strikes), then every stack of the
        /// vein's Mining sets with a GO_Mining message each (GameNotices), the vein used up, PS_Interact_End (the hero stands).
        /// </summary>
        IEnumerator OreVein(InteractiveObjectView view)
        {
            var uses = _session.Interaction.Uses;
            var verdict = uses.CheckMining(view.Id);
            if (verdict != ObjectUseVerdict.Ok)
            {
                RefuseUse(verdict, uses.Rules.Pickaxe);
                Finish();
                yield break;
            }
            yield return GoTo(PlaceAtSpot(view, 1.2f));
            if (_cancelled) { Finish(); yield break; }
            yield return Work("PickOre", uses.Rules.WorkSeconds);
            var mined = uses.Mine(view.Id);
            Debug.Log($"[use] mined {view.Def.Name} {view.Id}: {(mined.Count == 0 ? "nothing" : string.Join(", ", mined.Select(m => $"{m.Item} x{m.Amount}")))}");
            Finish();
        }

        /// <summary>
        /// G3 PS_Interact case 87 + PS_Interact_PirateTreasure: an emptied spot → "NothingToGet", no shovel →
        /// HUD_MissingItem_Interact; else the shovel work (_AI_Action DigGround for 10 s), the plunder is generated and the loot page opens.
        /// </summary>
        IEnumerator Treasure(InteractiveObjectView view)
        {
            var uses = _session.Interaction.Uses;
            var verdict = uses.CheckDig(view.Id);
            if (verdict != ObjectUseVerdict.Ok)
            {
                RefuseUse(verdict, uses.Rules.Shovel);
                Finish();
                yield break;
            }
            yield return GoTo(PlaceAtSpot(view, 1.0f));
            if (_cancelled) { Finish(); yield break; }
            yield return Work("DigGround", uses.Rules.WorkSeconds);
            if (uses.Dig(view.Id))
            {
                if (_loot != null)
                {
                    _loot.With(view.Id);
                    _screens.Open(_loot);
                    while (_loot.IsOpen) yield return null;
                }
                else _session.Interaction.Objects.TakeAll(view.Id);
            }
            Finish();
        }

        /// <summary>G3 PS_Interact_DrinkWaterBarrel: the drink motion once, then +10 HP.</summary>
        IEnumerator WaterBarrel(InteractiveObjectView view)
        {
            yield return GoTo(PlaceAtSpot(view, 0.7f));
            if (_cancelled) { Finish(); yield break; }
            var keys = G3HeroUseMotions.Station(view.Def.UseType);
            if (keys != null)
            {
                yield return Once(keys[0]);
                yield return Repeat(keys[1], 1);
                yield return Once(keys[2]);
            }
            else yield return new WaitForSeconds(1.5f);
            _session.Interaction.Uses.DrinkFromBarrel(view.Id);
            Finish();
        }

        /// <summary>
        /// G3 PS_Interact case 107 + PS_Interact_Shrine: a shrine of a god opposed to the hero's chosen one → he says "No";
        /// else he kneels to pray (ani state action), the shrine's dialog runs with teaching enabled, and he gets up after it.
        /// A talking object without UseType Shrine is talked to at once.
        /// </summary>
        IEnumerator Shrine(InteractiveObjectView view)
        {
            var uses = _session.Interaction.Uses;
            if (!uses.IsShrine(view.Id))
            {
                Finish();
                _session.Dialog.Talk(new ActorId(view.Def.Name));
                yield break;
            }
            if (uses.ShrineRefuses(view.Id))
            {
                _lines?.SayAny(_session, uses.Rules.ShrineRefusalLine);
                Finish();
                yield break;
            }
            yield return GoTo(PlaceAtSpot(view, 0.9f));
            if (_cancelled) { Finish(); yield break; }
            yield return Once(G3HeroUseMotions.Kneel);
            _session.Dialog.Talk(new ActorId(view.Def.Name));
            yield return null;
            while (_session.Dialog.IsActive) yield return null;
            yield return Once(G3HeroUseMotions.StandUpFromKneel);
            Finish();
        }

        /// <summary>
        /// A tool work of a use type's pose (<see cref="G3InteractionMotions"/>: PickOre holds the pickaxe, DigGround the
        /// shovel), G3 <c>_AI_Action(self, action, seconds)</c>: Begin, then cycles of the Loop (10 % the Ambient variant) while
        /// the state time since the work began is within <paramref name="seconds"/> of the loop's start (<see cref="TimedAction"/>),
        /// End when the pose has one. fix-139: <paramref name="seconds"/> was played as a loop count (10 PickOre strikes, 20+ s).
        /// </summary>
        IEnumerator Work(string useType, int seconds)
        {
            float stateStart = Time.time; // GetStateTime of PS_Interact_<use> counts from here
            var keys = G3HeroUseMotions.Station(useType);
            (bool left, bool right) shown = default;
            string leftProp = null, rightProp = null;
            if (G3InteractionMotions.TryGet(useType, out var pose))
            {
                leftProp = pose.LeftProp;
                rightProp = pose.RightProp;
                shown = Hold(leftProp, rightProp);
            }
            _repeated = 0;
            bool motion = keys != null && _motions != null && _motions.library != null && _motions.library.Find(keys[1]) != null;
            if (motion)
            {
                yield return Once(keys[0]);                                   // _AI_ChangeAction: the Begin transition
                yield return TimedLoop(keys[1], keys[3], stateStart, seconds); // _AI_LoopAction
                yield return Once(keys[2]); // PickOre and DigGround have no End motion: the hero goes back from the last loop
            }
            else yield return new WaitForSeconds(seconds);
            float took = Time.time - stateStart;
            LastWork = $"{useType} loops={_repeated} window={seconds}s took={took:0.0}s left={(shown.left ? leftProp : "-")} right={(shown.right ? rightProp : "-")} motion={(motion ? "yes" : "no")}";
            Debug.Log($"[use] {LastWork}");
            Unhold();
        }

        /// <summary>
        /// G3 <c>_AI_LoopAction</c>: at the start of each cycle, while <see cref="TimedAction.PlaysAgain"/> holds, one more
        /// cycle of the Loop, or of the Ambient variant when <see cref="TimedAction.PicksAmbient"/> and the pose has one; a
        /// begun cycle plays to its end (the phase wraps). <see cref="_repeated"/> counts the Loop cycles (the strikes).
        /// </summary>
        IEnumerator TimedLoop(string loop, string ambient, float stateStart, int seconds)
        {
            var action = new TimedAction(Time.time - stateStart, seconds);
            bool hasAmbient = ambient != null && _motions.library.Find(ambient) != null;
            string current = null;
            int done = 0;
            while (_motions != null && action.PlaysAgain(Time.time - stateStart))
            {
                string next = hasAmbient && TimedAction.PicksAmbient(Random.Range(0, 100)) ? ambient : loop;
                if (next != current || !_motions.CurrentLoops)
                {
                    if (!Play(next)) break;
                    current = next;
                }
                var entry = _motions.library.Find(next);
                float limit = Time.time + (entry != null ? entry.Length : 2f) + 1f;
                float last = _motions.CurrentPhase;
                yield return null;
                while (_motions != null && Time.time < limit)
                {
                    float phase = _motions.CurrentPhase;
                    if (phase < last || _motions.CurrentFinished) break;
                    last = phase;
                    yield return null;
                }
                if (next == loop) done++;
            }
            _repeated = done;
        }

        /// <summary>Plays a loop until it has wrapped <paramref name="times"/> times (or a timeout of 6 s per cycle).</summary>
        IEnumerator Repeat(string key, int times)
        {
            if (!Play(key))
            {
                yield return new WaitForSeconds(StationSeconds);
                yield break;
            }
            float end = Time.time + times * 6f, last = 0f;
            int done = 0;
            yield return null;
            while (done < times && Time.time < end && _motions != null)
            {
                float phase = _motions.CurrentPhase;
                if (phase < last) done++;
                last = phase;
                yield return null;
            }
            _repeated = done;
        }

        void RefuseUse(ObjectUseVerdict verdict, string tool)
        {
            if (verdict == ObjectUseVerdict.MissingTool) _messages.Show(Text(MissingItem) + ItemName(tool), NoticeKind.Error);
            else if (verdict == ObjectUseVerdict.NothingToGet) _lines?.SayAny(_session, _session.Interaction.Uses.Rules.NothingLine);
        }

        /// <summary>
        /// Where the hero cooks at a port cooking fire (fix-88, <see cref="CookFires.HeroSpot"/>): in front of a fireplace's
        /// opening, or on his side of a brazier or fire basin, facing the flames; on the floor below that point.
        /// </summary>
        UseSpot CookFireSpot(InteractiveObjectView view)
        {
            var player = _controls.Player.transform;
            var (at, yaw) = CookFires.HeroSpot(view.Def, player.position.x, player.position.z);
            var spot = new Vector3(at[0], at[1], at[2]);
            if (FloorBelow(spot, player, view.transform, out float floor)) spot.y = floor + 0.05f;
            return new UseSpot(spot, yaw, true, "Fwd");
        }

        /// <summary>The object's use spot (<see cref="HeroUseSpots"/>), else <see cref="Place"/> at <paramref name="distance"/>.</summary>
        UseSpot PlaceAtSpot(InteractiveObjectView view, float distance)
        {
            var spot = HeroUseSpots.Find(_session, view.Def, _held);
            return spot == null ? Place(view, distance) : new UseSpot(spot.Value.Position, spot.Value.Yaw, true, spot.Value.Direction);
        }

        void RefuseStation(StationCheck check)
        {
            switch (check.Verdict)
            {
                case StationVerdict.NoRecipes:
                    _messages.Show(StationTexts.Missing(_session, Text(StationTexts.RecipesTitle(check.Rule.Craft))), NoticeKind.Error);
                    break;
                case StationVerdict.MissingTool:
                    _messages.Show(Text(MissingItem) + ItemName(check.Item), NoticeKind.Error);
                    break;
                case StationVerdict.MissingSkill:
                    _messages.Show(StationTexts.MissingSkillText(_session, check.Item), NoticeKind.Error);
                    break;
                case StationVerdict.NoBladeWeapon:
                    _messages.Show(StationTexts.Missing(_session, Text(StationTexts.BladeWeapon)), NoticeKind.Error);
                    break;
            }
        }

        // G3 sounds of doors and locks (Docs/interactive-objects.md §6). Chest lid open/close come from the hero's
        // OpenChest motion effect tracks (G3MotionSounds); a door opened with a key and a broken pick add no sound in G3.
        const string DoorSound = "eff_ani_door_open_01", LockpickSound = "eff_event_lockpick_01", ChestUnlockSound = "eff_ani_chest_unlock_01";

        static void Sound(InteractiveObjectView view, string effect)
        {
            if (view != null) G3EffectSounds.Play(effect, view.transform.position, view.transform);
        }

        /// <summary>The lock part of G3 <c>_AI_Unlock</c>: refusals, the pick animation, the pick result.</summary>
        IEnumerator Unlock(InteractiveObjectView view, bool kneeling, System.Action<bool> done)
        {
            var objects = _session.Interaction.Objects;
            var verdict = objects.CheckLock(view.Id);
            if (verdict != LockVerdict.Key && verdict != LockVerdict.Pick)
            {
                Refuse(verdict, view.Def.Lock);
                _session.Interaction.ChestMarks.Discover(view.Id); // fix-102: a locked chest he could not open, for the map
                done(false);
                yield break;
            }
            if (verdict == LockVerdict.Pick)
            {
                string Pick(string phase) => kneeling ? G3HeroUseMotions.KneelLockpick(phase) : G3HeroUseMotions.StandLockpick(phase);
                yield return Once(Pick("Begin"));
                Loop(Pick("Loop"));
                yield return new WaitForSeconds(PickSeconds);
                yield return Once(Pick("End"));
            }
            var result = objects.Unlock(view.Id);
            if (result.LockpickUsed) LogPick(view, result); // fix-117: every pick in the log, so a run of breaks can be read back
            if (result.LockpickUsed) Sound(view, LockpickSound);   // every attempt (G3 _AI_Unlock); a break adds no sound
            else if (result.Opened && result.Verdict == LockVerdict.Key && view.Def.Kind == InteractiveKind.Chest) Sound(view, ChestUnlockSound);
            if (result.LockpickUsed) _messages.Show(Text(LockpickUsed));
            if (result.Broke) Say(HeroSpokenLines.PickBroke);
            if (!result.Opened) _session.Interaction.ChestMarks.Discover(view.Id); // fix-102: out of lockpicks after the break
            done(result.Opened);
        }

        void LogPick(InteractiveObjectView view, UnlockResult result)
        {
            var rules = _session.Interaction.Objects.Rules;
            int left = _session.Items.Inventories.Of(ActorId.Player).Count(rules.LockpickItem);
            var perks = new System.Collections.Generic.List<string>();
            foreach (var perk in rules.PerksByDifficulty) if (_session.Hero.Knows(perk)) perks.Add(perk);
            string roll = result.Roll < 0 ? "no roll (cannot fail)"
                                          : $"roll {result.Roll}/100, breaks at >= {100 - result.BreakChance} ({result.BreakChance} %)";
            Debug.Log($"[lock] {view.Def.Name} {view.Id}: difficulty {view.Def.Lock?.Difficulty}, perks [{string.Join(",", perks)}], {roll} -> " +
                      $"{(result.Opened ? "opened" : "PickBroke")}; lockpicks left {left}");
        }

        void Refuse(LockVerdict verdict, LockDef lockDef)
        {
            var rules = _session.Interaction.Objects.Rules;
            switch (verdict)
            {
                case LockVerdict.MissingSkill:
                    _messages.Show(Text(MissingSkill).Replace("$(name)", Text(new TextId("FO_" + rules.PerkFor(1)))), NoticeKind.Error);
                    break;
                case LockVerdict.NeedLockpick:
                case LockVerdict.NeedKeyOrLockpick:
                    _messages.Show(Text(MissingItem) + ItemName(rules.LockpickItem), NoticeKind.Error);
                    Say(verdict.ToString());
                    break;
                case LockVerdict.NeedKey:
                    if (lockDef?.Key != null) _messages.Show(Text(MissingItem) + ItemName(lockDef.Key), NoticeKind.Error);
                    Say(HeroSpokenLines.NeedKey);
                    break;
                default:
                    Say(verdict.ToString()); // TooHard, Impossible
                    break;
            }
        }

        /// <summary>
        /// Where the hero uses the object: the NPC use slot he holds (beds, stations; the one nearest to him, fix-66) when the
        /// routine data has one, else <paramref name="distance"/> in front of it on his side, facing it (0: where he
        /// stands, only turned to it). <see cref="UseSpot.Side"/> is the slot's side.
        /// </summary>
        UseSpot Place(InteractiveObjectView view, float distance)
        {
            var player = _controls.Player;
            if (!_held.IsNone && _session.Content.Database.TryGet(view.Id, out RoutinePointDef point) && _held.Slot < point.Slots.Count)
            {
                var slot = point.Slots[_held.Slot];
                return new UseSpot(new Vector3(slot.X, slot.Y, slot.Z), slot.Yaw, true, point.DirectionOf(_held.Slot));
            }
            var target = view.FocusPoint;
            var flat = Vector3.ProjectOnPlane(player.transform.position - target, Vector3.up);
            if (flat.sqrMagnitude < 0.01f) flat = -view.transform.forward;
            float yaw = Quaternion.LookRotation(-flat).eulerAngles.y;
            if (distance <= 0f) return new UseSpot(player.transform.position, yaw, false, "Fwd");
            var spot = new Vector3(target.x, player.transform.position.y, target.z) + flat.normalized * distance;
            // the floor at the spot (editor-runner-23: keeping the height without a ground check could leave the hero
            // above a hut floor and land him on the roof): the nearest floor below a head height above his feet
            if (FloorBelow(spot, player.transform, view.transform, out float floor)) spot.y = floor + 0.05f;
            return new UseSpot(spot, yaw, true, "Fwd");
        }

        /// <summary>Where and how the hero stands to use an object.</summary>
        readonly struct UseSpot
        {
            public UseSpot(Vector3 position, float yaw, bool move, string side, bool exact = false)
            {
                Position = position;
                Yaw = yaw;
                Move = move;
                Side = side;
                Exact = exact;
            }

            /// <summary>At the object's own origin (a boulder, a saw log: its animated part moves with the hero): never moved out (fix-103).</summary>
            public bool Exact { get; }

            public Vector3 Position { get; }
            public float Yaw { get; }
            /// <summary>False: the hero stays where he is and only turns to the object.</summary>
            public bool Move { get; }
            public string Side { get; }
        }

        bool _walking, _cancelled;

        /// <summary>The secondary button ended the use during the walk (G3 action 0x66 -> PS_Interact_End).</summary>
        public bool Cancelled => _cancelled;

        /// <summary>The hero still walks to the used object (dev probe).</summary>
        public bool Approaching => Busy && _walking;

        /// <summary>The last approach decision (dev probe / log).</summary>
        public ApproachStep LastApproach { get; private set; }

        /// <summary>
        /// G3 PS_Interact -> _AI_Goto_Ext (fix-75, <see cref="UseApproach"/>): the hero walks to the use spot (the keys do not
        /// steer him; what is left when he gets stuck, e.g. at the object's own collider, is placed at once), then stands on
        /// it facing the object's use direction and the use takes the body. The secondary button cancels (the caller ends
        /// the use without a motion, the slot is freed). Before, he was put there at once.
        /// </summary>
        IEnumerator GoTo(UseSpot spot)
        {
            var player = _controls.Player;
            ApproachWalked = ApproachSnapped = 0f;
            SpotMoved = 0f;
            if (spot.Move && _clearSpot && !spot.Exact && player != null) spot = Cleared(spot, player);
            if (spot.Move && _walking && player != null)
            {
                var approach = new UseApproach(player.walkSpeed);
                var start = player.transform.position;
                player.scriptedWalk = true;
                while (true)
                {
                    var flat = Vector3.ProjectOnPlane(spot.Position - player.transform.position, Vector3.up);
                    bool moving = new Vector2(player.Velocity.x, player.Velocity.z).sqrMagnitude > 0.01f;
                    var step = approach.Next(Time.timeAsDouble, flat.magnitude, moving, CancelPressed());
                    LastApproach = step;
                    if (step == ApproachStep.Cancelled)
                    {
                        _cancelled = true;
                        break;
                    }
                    if (step == ApproachStep.Arrived || step == ApproachStep.GiveUp) break;
                    if (step == ApproachStep.Walk)
                    {
                        player.TurnTo(Quaternion.LookRotation(flat.normalized).eulerAngles.y, player.Pitch); // fix-76: the camera swings round
                        player.scriptedMove = new Vector2(0f, 1f);
                    }
                    else player.scriptedMove = Vector2.zero;
                    yield return null;
                }
                player.scriptedMove = Vector2.zero;
                ApproachWalked = Vector3.ProjectOnPlane(player.transform.position - start, Vector3.up).magnitude;
                ApproachSnapped = Vector3.ProjectOnPlane(spot.Position - player.transform.position, Vector3.up).magnitude;
            }
            else if (_walking && CancelPressed()) _cancelled = true;
            if (_cancelled)
            {
                Debug.Log("[use] cancelled by the secondary button during the approach");
                yield break;
            }
            EndWalk();
            if (spot.Move) player.Teleport(spot.Position);
            player.TurnTo(spot.Yaw, 0f); // fix-76: the camera swings round
        }

        bool _clearSpot;

        /// <summary>How far the last use spot was moved out of the object's collision (fix-103 dev probe), m; -1: no free spot.</summary>
        public float SpotMoved { get; private set; }

        /// <summary>The hero was pushed out of a collider when the last use ended (fix-103 dev probe), m.</summary>
        public float LastDepenetration { get; private set; }

        static readonly Collider[] Overlaps = new Collider[16];
        /// <summary>fix-123: a spot at the hero's own height finds its floor from at most a step (G3 StepHeight 65 cm) above.</summary>
        const float StepAbove = 0.65f;

        /// <summary>
        /// fix-103 (owner: locked inside a Beliar shrine): the spot moved out of the used object's collision and the world's
        /// (<see cref="UseSpotClearance"/>): the shrine's 0.9 m in front of its bounds centre lay inside its slab, the walk
        /// stuck at the slab, gave up and the teleport put the hero inside it. No free spot near: he stays where he is.
        /// </summary>
        UseSpot Cleared(UseSpot spot, G3Player player)
        {
            var centre = _used != null ? _used.FocusPoint : spot.Position;
            float y = spot.Position.y;
            var hero = player.transform;
            var used = _used != null ? _used.transform : null;
            var cc = player.GetComponent<CharacterController>();
            bool Free(float x, float z) => HasRoom(new Vector3(x, y, z), hero, used, cc, out _);
            bool Walkable(float x, float z) =>
                UnityEngine.AI.NavMesh.SamplePosition(new Vector3(x, y, z), out var hit, 0.3f, UnityEngine.AI.NavMesh.AllAreas) && Mathf.Abs(hit.position.y - y) < 0.5f;
            if (Free(spot.Position.x, spot.Position.z)) return spot;
            if (!UseSpotClearance.TryClear(spot.Position.x, spot.Position.z, centre.x, centre.z, Free, Walkable, out float rx, out float rz, out int tried))
            {
                SpotMoved = -1f;
                Debug.LogWarning($"[use] fix-103: no free use spot near {spot.Position} ({tried} tried): the hero stays where he is");
                return new UseSpot(hero.position, spot.Yaw, false, spot.Side);
            }
            var p = new Vector3(rx, y, rz);
            if (FloorBelow(p, hero, used, out float floor, StepAbove)) p.y = floor + 0.05f;
            SpotMoved = Vector3.ProjectOnPlane(p - spot.Position, Vector3.up).magnitude;
            Debug.Log($"[use] fix-103: use spot {spot.Position} inside a collider, moved {SpotMoved:0.00} m out to {p}");
            // still facing the object
            var to = Vector3.ProjectOnPlane(centre - p, Vector3.up);
            float yaw = to.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(to).eulerAngles.y : spot.Yaw;
            return new UseSpot(p, yaw, true, spot.Side);
        }

        /// <summary>
        /// Room for the hero's body standing at <paramref name="at"/> (feet, its floor below): his core above step height
        /// meets no solid collider but his own (the used object's count). <paramref name="blocker"/>: the first in the way.
        /// <paramref name="worldOnly"/>: characters and moving bodies (an NPC's capsule, a ragdoll) do not count;
        /// <paramref name="onFloor"/> false: the feet as they are (the hero's own spot: a slab's top above them is no floor).
        /// </summary>
        static bool HasRoom(Vector3 at, Transform hero, Transform used, CharacterController cc, out Collider blocker, bool worldOnly = false,
                            bool onFloor = true)
        {
            blocker = null;
            if (onFloor && FloorBelow(at, hero, used, out float floor)) at.y = floor;
            // the core round his axis (UseSpotClearance.CoreRadius): a shallow overlap of the full capsule (G3 poses the body
            // against a table or a barrel) is the controller's own overlap recovery and keeps the pose
            float r = UseSpotClearance.CoreRadius, h = cc != null ? cc.height : 1.95f;
            int n = Physics.OverlapCapsuleNonAlloc(at + Vector3.up * (0.45f + r), at + Vector3.up * (h - r), r, Overlaps, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = Overlaps[i];
                if (c == null || c.transform.IsChildOf(hero) || c is TerrainCollider) continue;
                if (worldOnly && (c is CharacterController || (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic))) continue;
                blocker = c;
                return false;
            }
            return true;
        }

        /// <summary>fix-103 dev probe: the hero's body has room where he stands (no world collider inside it), else its name.</summary>
        public bool HeroHasRoom(out string blocker)
        {
            blocker = null;
            var player = _controls?.Player;
            if (player == null) return true;
            bool room = HasRoom(player.transform.position, player.transform, null, player.GetComponent<CharacterController>(), out var c, true, false);
            if (!room) blocker = c.name;
            return room;
        }

        /// <summary>
        /// fix-103: the hero stands inside a collider (a use that placed or moved him into the object): he is put on the
        /// nearest free spot round it (<see cref="UseSpotClearance"/>). The returned distance is 0 when he had room.
        /// </summary>
        public float Depenetrate()
        {
            var player = _controls?.Player;
            if (player == null) return 0f;
            var hero = player.transform;
            var cc = player.GetComponent<CharacterController>();
            if (HasRoom(hero.position, hero, null, cc, out var blocker, true, false)) return 0f;
            var from = hero.position;
            var centre = blocker.bounds.center;
            var inside = blocker.transform; // its top is no floor to stand on
            bool Free(float x, float z) => HasRoom(new Vector3(x, from.y, z), hero, inside, cc, out _, true);
            bool Walkable(float x, float z) =>
                UnityEngine.AI.NavMesh.SamplePosition(new Vector3(x, from.y, z), out var hit, 0.3f, UnityEngine.AI.NavMesh.AllAreas) && Mathf.Abs(hit.position.y - from.y) < 0.5f;
            if (!UseSpotClearance.TryClear(from.x, from.z, centre.x, centre.z, Free, Walkable, out float rx, out float rz, out _))
            {
                Debug.LogWarning($"[use] fix-103: the hero is inside {blocker.name} at {from} and no free spot is near (F10: getup; tp hero)");
                return 0f;
            }
            var p = new Vector3(rx, from.y, rz);
            if (FloorBelow(p, hero, inside, out float floor, StepAbove)) p.y = floor + 0.05f;
            player.Teleport(p);
            float moved = Vector3.ProjectOnPlane(p - from, Vector3.up).magnitude;
            Debug.Log($"[use] fix-103: the hero stood inside {blocker.name}: pushed {moved:0.00} m out to {p}");
            return moved;
        }

        /// <summary>The walk is over: the use takes the body (control lock, motion player).</summary>
        void EndWalk()
        {
            if (!_walking) return;
            _walking = false;
            _controls.StopWalking(this);
            _motions?.Claim(this);
        }

        /// <summary>
        /// The secondary button went down and belongs to the use (no page or conversation has it): G3 OnPlayerSecondaryAction
        /// in task PS_Interact queues action 0x66, which ends the walk and the waiting / looping uses (PS_Interact_End).
        /// </summary>
        bool CancelPressed()
        {
            if (_screens?.Current != null || _session == null || _session.Dialog.IsActive) return false;
            if (_cancelRequest)
            {
                _cancelRequest = false;
                return true;
            }
            return G3Input.WasPressed(G3.Player.SessionKey.Use2);
        }

        bool _cancelRequest;

        /// <summary>The id of the object the last use was of (dev probe).</summary>
        public string LastObject { get; private set; }

        /// <summary>As a press of the secondary button during the use (dev runner, tests); false when no use runs.</summary>
        public bool RequestCancel()
        {
            if (!Busy) return false;
            _cancelRequest = true;
            return true;
        }

        /// <summary>Metres the hero walked during the last approach, and what was left to place at once (dev probe).</summary>
        public float ApproachWalked { get; private set; }
        public float ApproachSnapped { get; private set; }

        static readonly RaycastHit[] FloorHits = new RaycastHit[16];

        /// <summary>
        /// The walkable surface nearest below <paramref name="at"/> + 1.2 m (not the topmost one: roofs and ceilings above
        /// stay out), within 3 m below the feet; the hero and the used object are ignored. False: nothing walkable there.
        /// fix-123: a free spot searched round the hero's own feet (the cleared use spot, the push-out) looks from a step
        /// (<see cref="StepAbove"/>) up only: the collision is two-sided, so from 1.2 m up the top of a low cave ceiling or
        /// a beam over him was the nearest "floor" (never up through a ceiling).
        /// </summary>
        static bool FloorBelow(Vector3 at, Transform hero, Transform used, out float y, float above = 1.2f)
        {
            y = at.y;
            const float Below = 3f;
            int n = Physics.RaycastNonAlloc(at + Vector3.up * above, Vector3.down, FloorHits, above + Below, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = FloorHits[i];
                var t = h.collider.transform;
                if (t.IsChildOf(hero) || (used != null && t.IsChildOf(used)) || h.normal.y < 0.6f) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return best < float.MaxValue;
        }

        /// <summary>
        /// Starts a hero motion; the used object plays its own part of it (G3 stores it as the motion's <c>_I_</c> variant,
        /// <see cref="ObjectPartState"/>): the wheel turns with GrindStone_Loop, the lid opens with OpenChest_Begin.
        /// </summary>
        bool Play(string key)
        {
            EndWalk(); // a motion always belongs to the use
            if (_motions == null || !_motions.Play(key, Fade, restart: true)) return false;
            if (_used != null && _parts != null)
            {
                _used.Apply(_parts.HeroPlays(key), Fade);
                if (_parts.Mode != ObjectPartMode.Still) _partInfo = $"{_parts.Mode} {_parts.Part}";
            }
            return true;
        }

        void Loop(string key) => Play(key);

        IEnumerator Once(string key)
        {
            if (!Play(key)) yield break;
            yield return UntilFinished(4f);
        }

        IEnumerator UntilFinished(float timeout)
        {
            float end = Time.time + timeout;
            yield return null;
            while (_motions != null && !_motions.CurrentFinished && Time.time < end) yield return null;
        }

        void Finish()
        {
            if (_reading != null) _crafting?.Services?.Learn(_reading);
            _reading = null;
            InBedDialog = false;
            // the object settles: after its closing part it holds the last frame; cut short it shows the closing part's
            // last frame (lid shut, boulder down) or stops (the wheel stands still)
            if (_used != null && _parts != null)
            {
                _used.Apply(_parts.UseEnded(), 0f);
                if (_parts.Mode != ObjectPartMode.Still) _partInfo = $"{_parts.Mode} {_parts.Part}";
            }
            _used = null;
            _parts = null;
            // the slot goes back however the use ended (done, a hit, a fight, the hero down, a forced end, a new session)
            _session?.Npcs.Claims.Release(_held, ActorId.Player);
            _held = Simulation.Station.None;
            _stationLooping = false;
            Unhold();
            _inActivity = false;
            _activityLooping = false;
            if (_session != null) _session.Regeneration.Sitting = false;
            if (_motions != null) _motions.Release(this);
            _walking = false;
            _controls.Release(this);
            Busy = false;
            // fix-103: never leave the hero inside the object (or anything else) when a use ends
            LastDepenetration = Depenetrate();
            HeroTorchView.Instance?.EndInteraction();
        }

        /// <summary>The hero's lock line (fix-235: with its subtitle, as every other hero line, when it has a text).</summary>
        void Say(string line)
        {
            if (_lines != null)
            {
                _lines.Say(_session, line);
                return;
            }
            if (_voice == null || _languages == null) return;
            string path = Path.Combine(ContentLocations.VoiceRoot, _languages.VoiceLanguage, "SVM_Player_" + line + ".ogg");
            if (File.Exists(path)) _voice.Play(path, () => { });
        }

        string Text(TextId id) => _session.Localizer.Text(id);

        string ItemName(string item) =>
            _session.Content.Database.TryGet(item, out ItemDef def) && !def.Name.IsEmpty ? Text(def.Name) : item;
    }
}
