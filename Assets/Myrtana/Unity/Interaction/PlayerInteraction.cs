using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using G3.Unity;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The player's focus: of the NPCs with conversation topics, the downed or dead NPCs, the world items (dropped weapons
    /// included) and the interactive objects within reach, the one most directly in front of the hero; the use key talks to
    /// the NPC, loots the body (<see cref="HeroObjectUse.LootBody"/>), picks the item up or uses the object. G3 binds that
    /// Use to the left mouse button (session key Use1, <see cref="G3Input"/>) with empty hands; a drawn stance keeps the button for combat
    /// (<see cref="HeroUseInput"/>). Every candidate needs a free line of sight from the hero (G3 FocusCheck → IsFreeLOS,
    /// <see cref="FocusLineOfSight"/>, fix-104): no focus, talk or pick-up through floors and walls. The G3 HUD shows the focus as a name above
    /// the target (<see cref="G3Hud"/>); the plain HUD shows "[key] Name" in its prompt label.
    /// </summary>
    public sealed class PlayerInteraction : MonoBehaviour, ISessionView
    {
        static readonly TextId TakenText = new TextId("GO_ItemTaken_Formatted");

        [Tooltip("Maximum horizontal distance to talk, metres (G3 NormalModeCharacterScanRange 500 cm, fix-247).")]
        public float reach = CharacterFocus.ScanRangeM;
        [Tooltip("Maximum distance to use a chest, door, bed or station, metres.")]
        public float objectReach = 2.5f;
        [Tooltip("Minimum cosine between the hero's facing and the direction to the target.")]
        public float facingCosine = 0.3f;

        GameSession _session;
        NpcViewDirectory _views;
        WorldItemViews _items;
        HudMessages _messages;
        HeroControlLock _controls;
        Transform _player;
        CharacterController _heroBody;
        readonly FocusLineOfSight _sight = new FocusLineOfSight();
        Vector3 _heroSight;
        Label _prompt;
        InteractiveObjectViews _objects;
        HeroObjectUse _objectUse;
        int _dialogEndedFrame = -1;
        System.IDisposable _endedSubscription;

        public ActorId Focus { get; private set; }
        /// <summary>The world item in focus (null when an NPC or nothing is).</summary>
        public string FocusItem { get; private set; }
        /// <summary>The interactive object in focus (chest, door, bed, station), or null.</summary>
        public string FocusObject { get; private set; }

        /// <param name="prompt">The plain HUD's prompt label, or null when the G3 HUD names the focus.</param>
        public void Init(NpcViewDirectory views, WorldItemViews items, HeroControlLock controls, HudMessages messages, Label prompt)
        {
            _views = views;
            _items = items;
            _controls = controls;
            _messages = messages;
            _player = controls.Player.transform;
            // fix-205: Sneak shares Ctrl with the quick-loot modifier; facing loot, the sneak toggle waits for Ctrl's release
            controls.Player.DeferSneakToggle = DeferSneakToggle;
            _heroBody = _player.GetComponent<CharacterController>();
            _prompt = prompt;
        }

        /// <summary>
        /// Port option "Focus decorative objects" (fix-79, <see cref="PortOptionsApplier"/>): true = G3 (every object); false =
        /// seats and the looping activities are not focused (<see cref="ObjectFocusRules"/>).
        /// </summary>
        public static bool FocusDecorativeObjects { get; set; } = true;

        /// <summary>
        /// Port option "Cook on any fire" (fix-88, [owner] default on, <see cref="CookFires"/>): lit fireplaces, braziers
        /// and fire basins are cooking stations; off = original (they are not focused or used).
        /// </summary>
        public static bool CookOnAnyFire { get; set; } = true;

        /// <summary>
        /// [owner] fix-206 (default on; off = G3: only a use opens them): an invisible talking object (the ancestor tombs' rune
        /// pedestals) speaks its News entry when the hero comes up to it (<see cref="SpeakerApproach"/>).
        /// </summary>
        public static bool SpeakersAnnounceOnApproach { get; set; } = true;

        /// <summary>CP option QuickLoot (ge3.ini [Game], default on): right click empties a focused chest.</summary>
        public bool QuickLootEnabled { get; set; } = true;

        /// <summary>
        /// Port option "Take all: Ctrl or RMB + click" (fix-205, [owner] default on, not G3; <see cref="PortOptionsApplier"/>):
        /// Use1 with the quick-loot modifier (<c>SessionKey.QuickLootModifier</c>, default Ctrl) or "Aktion 2" held takes
        /// everything from the focused body, robbable sleeper or chest (<see cref="QuickLootFocus"/>); off = G3.
        /// </summary>
        public static bool QuickLootChord { get; set; } = true;

        /// <summary>
        /// fix-238: what the right button's press of the current hold quick-looted (<see cref="LootKey"/>), else null; the
        /// left click of that hold skips only this target (<see cref="QuickLootChordTarget.SwallowsLeftClick"/>).
        /// </summary>
        string _secondaryLooted;

        /// <summary>The hero holds a weapon, bow or spell (quick loot works only with empty hands).</summary>
        public System.Func<bool> WeaponDrawn { get; set; }

        /// <summary>Adds the interactive objects (chests, doors, beds, stations) to the focus; Use1 uses them.</summary>
        public void AddObjects(InteractiveObjectViews objects, HeroObjectUse use)
        {
            _objects = objects;
            _objectUse = use;
        }

        public void Bind(GameSession session)
        {
            _endedSubscription?.Dispose();
            _session = session;
            // The key that closed a conversation must not open the next one in the same frame.
            _endedSubscription = session.Events.Subscribe<DialogEnded>(_ => _dialogEndedFrame = Time.frameCount);
        }

        void OnDestroy() => _endedSubscription?.Dispose();

        void Update()
        {
            if (_session == null || _player == null) return;
            var lastFocus = Focus;
            var lastItem = FocusItem;
            var lastObject = FocusObject;
            Focus = default;
            FocusItem = null;
            FocusObject = null;
            // fix-90: with a weapon, bow or spell drawn G3 focuses fight targets only (PlayerCombat), no items or objects.
            if (!_session.Dialog.IsActive && !_controls.IsLocked && !_controls.Player.Swimming && !_session.SpellEffects.Hero.Restricted &&
                CombatFocus.InteractionFocus(WeaponDrawn != null && WeaponDrawn()))
            {
                FindFocus();
                AnnounceSpeakers();
            }
            // fix-102: a newly focused locked chest whose lock refuses the hero gets its map mark
            if (FocusObject != null && FocusObject != lastObject) _session.Interaction.ChestMarks.Discover(FocusObject);
            // gamepad-1: the prompt names the pad button after a device switch too
            var device = global::G3.Unity.G3Pad.LastInput;
            bool deviceChanged = device != _promptDevice;
            _promptDevice = device;
            if (_prompt != null && (!Focus.Equals(lastFocus) || FocusItem != lastItem || FocusObject != lastObject || deviceChanged))
                _prompt.text = !Focus.IsEmpty ? (NpcUsable(Focus) ? UseKeyPrefix : string.Empty) + HudFocus.NpcName(_session, Focus)
                             : FocusItem != null ? UseKeyPrefix + HudFocus.WorldItem(_session, FocusItem).Name
                             : FocusObject != null ? UseKeyPrefix + ObjectName(FocusObject) : string.Empty;
            // G3 Use1 (left button) / Use2 (right button) in PS_Normal (HeroUseInput routes the press).
            if (!G3Input.IsDown(G3.Player.SessionKey.Use2)) _secondaryLooted = null;
            var input = PressedInput();
            if (input == null || Time.frameCount == _dialogEndedFrame) return;
            bool stance = WeaponDrawn != null && WeaponDrawn();
            // fix-205 [owner]: Use1 with Ctrl or the right button held takes all (QuickLootChord)
            bool modifier = G3Input.IsDown(G3.Player.SessionKey.QuickLootModifier);
            bool secondary = input == HeroInput.Primary && G3Input.IsDown(G3.Player.SessionKey.Use2);
            bool chord = G3.Player.QuickLootChord.Active(QuickLootChord, modifier, secondary);
            var route = HeroUseInput.Route(input.Value, stance, global::G3.Unity.G3Cursor.Captured, QuickLootEnabled, chord);
            switch (route)
            {
                case HeroInputRoute.Use: UseFocus(); break;
                case HeroInputRoute.QuickLoot:
                    if (input == HeroInput.Primary)
                    {
                        if (modifier) _controls.Player.ConsumeSneakKey(); // the Ctrl press toggles no sneaking
                        // fix-238: only the target the right button of this hold just emptied is skipped; another body
                        // or chest is looted (several bodies in one hold); fix-234: a focused floor item is still taken
                        if (QuickLootChordTarget.SwallowsLeftClick(secondary && !modifier, _secondaryLooted, ChordTargetKey()))
                        {
                            Debug.Log($"[Myrtana] take-all chord: skipped (the right button just emptied {_secondaryLooted})");
                            break;
                        }
                        if (!Chord(modifier, stance: false)) UseFocus();   // nothing to loot: a plain use
                    }
                    else
                    {
                        string key = FocusLootKey();
                        _secondaryLooted = QuickLootFocus(chord: false) ? key : null;
                    }
                    break;
            }
        }

        /// <summary>
        /// fix-205: Sneak (Ctrl) went down — hold its toggle back while the chord could follow: the option is on, the
        /// pressed key is also the quick-loot modifier, the hands are empty and the focus has something to take.
        /// </summary>
        bool DeferSneakToggle()
        {
            if (!QuickLootChord || _session == null || _objectUse == null || !G3Input.WasPressed(G3.Player.SessionKey.QuickLootModifier))
                return false;
            bool stance = WeaponDrawn != null && WeaponDrawn();
            ChordTarget(stance, out var focus, out var body);
            return QuickLootChordTarget.DefersSneak(stance, focus, body);
        }

        /// <summary>The focus as the chord sees it: a body, a robbable sleeper or a chest is what it empties.</summary>
        ChordFocus FocusKind()
        {
            if (FocusObject != null)
                return _session.Interaction.Objects.TryGet(FocusObject, out var def) && def.Kind == InteractiveKind.Chest ? ChordFocus.Lootable : ChordFocus.Other;
            if (!Focus.IsEmpty) return _objectUse.IsBody(Focus) || _objectUse.CanRobSleeper(Focus) ? ChordFocus.Lootable : ChordFocus.Other;
            return FocusItem != null ? ChordFocus.Item : ChordFocus.None; // fix-234: a floor item always wins
        }

        /// <summary>
        /// The chord's view of the focus and the best body in front (fix-226). With a drawn stance (no interaction focus)
        /// fix-234: a world item lying in front more directly than that body counts as an <see cref="ChordFocus.Item"/>
        /// focus, so the body fallback does not take a click aimed at the item.
        /// </summary>
        ActorId ChordTarget(bool stance, out ChordFocus focus, out ChordBody kind)
        {
            focus = stance ? ChordFocus.None : FocusKind();
            kind = ChordBody.None;
            if (!stance && (focus == ChordFocus.Lootable || focus == ChordFocus.Item)) return default;
            var body = FindChordBody(out kind, out float bodyFacing);
            if (stance && kind != ChordBody.None && ItemInFront(bodyFacing)) focus = ChordFocus.Item;
            return body;
        }

        /// <summary>fix-234: a shown world item in reach that the hero faces at least as directly as <paramref name="best"/>, in sight.</summary>
        bool ItemInFront(float best)
        {
            if (_items == null) return false;
            foreach (var item in _items.Shown)
                if (item.Value != null && FacingItem(item.Value.transform, best, out _) && SeesItem(item.Value.transform))
                    return true;
            return false;
        }

        /// <summary>
        /// fix-226: the chord's press (<see cref="QuickLootChordTarget"/>): empties the lootable focus, else the body with
        /// loot in front of the hero (a plant, item or object by a dead animal often took the focus; a drawn stance focuses
        /// no bodies). False when the press is no chord loot (the caller uses the focus or attacks).
        /// </summary>
        bool Chord(bool modifier, bool stance)
        {
            var body = ChordTarget(stance, out var focus, out var kind);
            var action = QuickLootChordTarget.Decide(true, modifier, stance, focus, kind);
            if (action != ChordAction.Default || kind != ChordBody.None)
                Debug.Log($"[Myrtana] take-all chord: {action} (stance {stance}, focus {focus} {FocusName()}, body {kind} {body})");
            switch (action)
            {
                case ChordAction.LootFocus: return QuickLootFocus(chord: true);
                case ChordAction.LootBody: return _objectUse.QuickLootBody(body);
                case ChordAction.TakeItem:
                    PickUp(FocusItem);
                    return true;
                default: return false;
            }
        }

        /// <summary>fix-238: an id for a lootable target (chest object or character), unique across both kinds.</summary>
        static string LootKey(string obj, ActorId actor) => obj != null ? "o:" + obj : actor.IsEmpty ? null : "a:" + actor;

        /// <summary>fix-238: the chest or character the right button's quick loot (<see cref="QuickLootFocus"/>) takes from.</summary>
        string FocusLootKey() => LootKey(FocusObject, FocusObject != null ? default : Focus);

        /// <summary>
        /// fix-238: what an empty-hands chord press would take from now (the lootable focus, else the body in front); null for
        /// a floor item or nothing (never swallowed).
        /// </summary>
        string ChordTargetKey()
        {
            var body = ChordTarget(stance: false, out var focus, out var kind);
            if (focus == ChordFocus.Item) return null;
            if (focus == ChordFocus.Lootable) return FocusLootKey();
            return kind == ChordBody.None ? null : LootKey(null, body);
        }

        string FocusName() => FocusObject ?? FocusItem ?? (Focus.IsEmpty ? "-" : Focus.ToString());

        /// <summary>
        /// fix-226: PlayerCombat asks on a Use1 press in a drawn stance: with the quick-loot modifier (Ctrl, never the right
        /// button: the parade) held and a body with loot in front, the press empties it instead of attacking. True = taken.
        /// </summary>
        public bool StanceChord()
        {
            if (!QuickLootChord || _session == null || _objectUse == null || _controls == null || _player == null) return false;
            if (_session.Dialog.IsActive || _controls.IsLocked || _controls.Player.Swimming) return false;
            if (!G3Input.IsDown(G3.Player.SessionKey.QuickLootModifier)) return false;
            _controls.Player.ConsumeSneakKey();
            return Chord(modifier: true, stance: true);
        }

        /// <summary>
        /// fix-226: the downed or dead NPC in reach the hero faces most directly with a free line of sight, preferring one
        /// with loot (<see cref="NpcLoot.HasLoot"/>: what the loot page would show, late trophies included). A corpse is
        /// measured at its origin (CorpseRest puts it under the lying body) and, while a ragdoll has it, at the pelvis.
        /// </summary>
        ActorId FindChordBody(out ChordBody kind, out float bestFacing)
        {
            kind = ChordBody.None;
            bestFacing = facingCosine;
            if (_session == null || _objectUse == null || _player == null || _views == null) return default;
            UpdateHeroFrame();
            RefreshNearbyNpcs();
            var loot = _session.Items.NpcLoot;
            float bestLoot = facingCosine, bestEmpty = facingCosine;
            ActorId withLoot = default, empty = default;
            for (int i = 0; i < _nearbyNpcs.Count; i++)
            {
                var (actor, view) = _nearbyNpcs[i];
                if (view == null || !_objectUse.IsBody(actor)) continue;
                bool has = loot != null && loot.HasLoot(actor);
                if (!FacesBody(view, has ? bestLoot : bestEmpty, out float facing)) continue;
                if (has) (bestLoot, withLoot) = (facing, actor);
                else (bestEmpty, empty) = (facing, actor);
            }
            if (!withLoot.IsEmpty)
            {
                (kind, bestFacing) = (ChordBody.WithLoot, bestLoot);
                return withLoot;
            }
            if (!empty.IsEmpty) (kind, bestFacing) = (ChordBody.Empty, bestEmpty);
            return empty;
        }

        bool FacesBody(Transform view, float best, out float facing)
        {
            facing = best;
            bool found = false;
            if (Facing(view.position, reach, facing, out float f) && Sees(FocusLineOfSight.CharacterPoint(view), view))
            {
                facing = f;
                found = true;
            }
            var ragdoll = view.GetComponentInChildren<Ragdoll>();
            if (ragdoll != null)
            {
                var pelvis = ragdoll.PelvisPosition;
                if (Facing(pelvis, reach, facing, out f) && Sees(pelvis + Vector3.up * 0.2f, view))
                {
                    facing = f;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>The hero's position, flat forward and sight point for the focus tests.</summary>
        void UpdateHeroFrame()
        {
            _player.GetPositionAndRotation(out _heroPosition, out var heroRotation);
            _heroForward = Vector3.ProjectOnPlane(heroRotation * Vector3.forward, Vector3.up).normalized;
            _heroSight = HeroSightPoint(_player, _heroBody);
        }

        /// <summary>"[Mouse 0]  ": the first key of Use1 for the plain HUD's prompt.</summary>
        static string UseKeyPrefix => global::G3.Unity.G3InputPrompts.Prefix(G3.Player.SessionKey.Use1);

        G3.Player.ActiveInput _promptDevice;

        /// <summary>
        /// The action input pressed this frame (session key Use1 before Use2; G3 defaults left / right mouse button), or
        /// null. E is G3's "Camera Rotation Rechts" and no longer a use key (par-controls).
        /// </summary>
        static HeroInput? PressedInput()
        {
            if (G3Input.WasPressed(G3.Player.SessionKey.Use1)) return HeroInput.Primary;
            if (G3Input.WasPressed(G3.Player.SessionKey.Use2)) return HeroInput.Secondary;
            return null;
        }

        /// <summary>G3 PS_Normal_Loop action Use on the focus: loot a body, talk, take the item, use the object.</summary>
        void UseFocus()
        {
            if (!Focus.IsEmpty) UseCharacter(Focus);
            else if (FocusItem != null) PickUp(FocusItem);
            else if (FocusObject != null && _objectUse != null && _objectUse.IsShrine(FocusObject)) _objectUse.Use(FocusObject); // pray first
            else if (FocusObject != null && SpeakerOf(FocusObject) is ActorId speaker) _session.Dialog.Talk(speaker);
            else if (FocusObject != null) _objectUse?.Use(FocusObject);
        }

        /// <summary>
        /// G3 PS_Normal_Loop Use on a character: loot its body, rob it asleep (G3 Perk_MasterThief; [owner] fix-113 any human
        /// held by the sleep spell, then a theft), else talk (never to a spell sleeper: Dialog.TalkBlocked).
        /// </summary>
        public void UseCharacter(ActorId npc)
        {
            if (npc.IsEmpty || _session == null) return;
            if (_objectUse != null && _objectUse.IsBody(npc)) _objectUse.LootBody(npc);
            else if (_objectUse != null && _objectUse.RobSleeper(npc)) { }
            else _session.Dialog.Talk(npc);
        }

        /// <summary>Use1 does something with this character: talk, loot its body or rob it asleep.</summary>
        bool NpcUsable(ActorId actor) =>
            _session.Dialog.CanTalkTo(actor) || _objectUse != null && (_objectUse.IsBody(actor) || _objectUse.CanRobSleeper(actor));

        /// <summary>
        /// CP quick loot (action 0x90): the right button empties a focused chest or body with empty hands; fix-205's chord
        /// (<paramref name="chord"/>) also robs a helpless NPC the hero may rob (<see cref="HeroObjectUse.QuickRobSleeper"/>).
        /// False when the focus is nothing to loot.
        /// </summary>
        bool QuickLootFocus(bool chord)
        {
            if (_objectUse == null) return false;
            if (FocusObject != null) return _objectUse.QuickLoot(FocusObject);
            if (Focus.IsEmpty) return false;
            if (_objectUse.IsBody(Focus)) return _objectUse.QuickLootBody(Focus);
            return chord && _objectUse.QuickRobSleeper(Focus);
        }

        static readonly global::Unity.Profiling.ProfilerMarker s_npcsMarker = new global::Unity.Profiling.ProfilerMarker("Focus.Npcs"),
            s_itemsMarker = new global::Unity.Profiling.ProfilerMarker("Focus.ItemsObjects");

        /// <summary>The target within reach the hero looks at most directly (G3 focuses what is in front, not what is nearest).</summary>
        void FindFocus()
        {
            // The hero's position and flat forward once per frame, not per candidate (hundreds of shown items/objects, perf-8).
            UpdateHeroFrame();
            float bestFacing = facingCosine;
            // The NPCs near the hero, in the order of the spawned views, then the baked ones (perf-3: NpcNearby).
            using (s_npcsMarker.Auto())
            {
                RefreshNearbyNpcs();
                for (int i = 0; i < _nearbyNpcs.Count; i++)
                {
                    var (actor, view) = _nearbyNpcs[i];
                    if (view != null) ConsiderNpc(actor, view, ref bestFacing);
                }
            }
            using var itemsMarker = s_itemsMarker.Auto();
            if (_items != null)
                foreach (var item in _items.Shown)
                {
                    if (item.Value == null || !FacingItem(item.Value.transform, bestFacing, out float facing)) continue;
                    var at = item.Value.transform;
                    if (!SeesItem(at)) continue; // fix-104: no pick-up through walls; fix-114: the shelf it lies on does not block
                    bestFacing = facing;
                    Focus = default;
                    FocusItem = item.Key;
                }
            if (_objects == null || (_objectUse != null && _objectUse.Busy)) return;
            foreach (var obj in _objects.Shown)
            {
                if (obj.Value == null || !Facing(obj.Value.FocusPoint, objectReach, bestFacing, out float facing)) continue;
                if (!ObjectFocusRules.Focusable(obj.Value.Def, FocusDecorativeObjects)) continue; // fix-79 port option
                if (!CookFires.Usable(obj.Value.Def, CookOnAnyFire)) continue;                    // fix-88 port option
                // A talking object (shrine, altar, ore furnace) is focusable while it has something to say.
                if (obj.Value.Def.Kind == InteractiveKind.Speaker && !_session.Dialog.CanTalkTo(new ActorId(obj.Value.Def.Name))) continue;
                // G3 GetFocusEntityPriority: an object someone else uses (its User) cannot be focused (§9; not a campfire)
                if (_objectUse != null && _objectUse.IsOccupied(obj.Key)) continue;
                var view = obj.Value.transform;
                // fix-104; an object of the cell scene: its own mesh does not block, nor does what it stands on
                if (!Sees(obj.Value.SightPoint, view, view.position, obj.Value.IsFocusPointOnly ? FocusSight.CellObjectEndToleranceM : FocusSight.EndToleranceM)) continue;
                bestFacing = facing;
                Focus = default;
                FocusItem = null;
                FocusObject = obj.Key;
            }
        }

        // ------------------------------------------------------------------------------- talking objects on approach (fix-206)

        readonly SpeakerApproach _speakerApproach = new SpeakerApproach();
        float _announceAt = float.NegativeInfinity;

        /// <summary>
        /// fix-206: 4 times a second (<see cref="NearbySeconds"/>), an invisible talking object in reach of the hero with an
        /// available News entry and a free line of sight starts its conversation (once per visit, <see cref="SpeakerApproach"/>).
        /// Runs after <see cref="FindFocus"/> (same gate: no dialog, controls free, no weapon drawn), which set the hero's sight point.
        /// </summary>
        void AnnounceSpeakers()
        {
            if (!SpeakersAnnounceOnApproach || _objects == null || (_objectUse != null && _objectUse.Busy)) return;
            if (Time.frameCount == _dialogEndedFrame || Time.unscaledTime < _announceAt + NearbySeconds) return;
            _announceAt = Time.unscaledTime;
            foreach (var obj in _objects.Shown)
            {
                var view = obj.Value;
                if (view == null || !SpeakerApproach.Announces(view.Def)) continue;
                var pivot = view.transform.position;
                float flat = Flat(pivot - _heroPosition).magnitude;
                float dy = _heroPosition.y - pivot.y;
                bool can = SpeakerApproach.InReach(flat, dy);
                var speaker = new ActorId(view.Def.Name);
                if (can) can = _session.Dialog.HasNews(speaker) && Sees(view.SightPoint, view.transform, pivot, FocusSight.CellObjectEndToleranceM);
                if (!_speakerApproach.ShouldAnnounce(obj.Key, flat, dy, can)) continue;
                _session.Dialog.Talk(speaker);
                return;
            }
        }

        // ------------------------------------------------------------------------------------------- nearby NPCs (perf-3)

        /// <summary>Seconds between two passes over all NPC views for the ones near the hero.</summary>
        const float NearbySeconds = 0.25f;
        /// <summary>
        /// Margin over <see cref="reach"/> of the nearby list (m): a body (or the hero) would have to cover it within
        /// <see cref="NearbySeconds"/> to come into reach unseen (20 m/s); the hero moving 1.5 m renews the list at once.
        /// </summary>
        const float NearbyMargin = 5f, NearbyHeroMove = 1.5f;

        readonly List<(ActorId actor, Transform view)> _nearbyNpcs = new List<(ActorId, Transform)>();
        float _nearbyAt = float.NegativeInfinity;
        Vector3 _nearbyFrom;
        int _nearbySpawned = -1, _nearbyBaked = -1;
        NpcViewManager _nearbyOf;

        /// <summary>
        /// perf-3: FindFocus tested every shown NPC view each frame (~350 at the Nordmar camp, 0.5 ms in the editor); the views
        /// within reach + <see cref="NearbyMargin"/> are now gathered 4 times a second (and when the hero moved
        /// <see cref="NearbyHeroMove"/> or a view came, went or was rebound), in the same order, and only those are tested each frame.
        /// </summary>
        void RefreshNearbyNpcs()
        {
            var spawned = _views.Spawned;
            var baked = _views.Baked;
            int spawnedVersion = spawned != null ? spawned.Version : 0;
            if (Time.unscaledTime < _nearbyAt + NearbySeconds && ReferenceEquals(spawned, _nearbyOf) && spawnedVersion == _nearbySpawned &&
                _views.BakedVersion == _nearbyBaked &&
                (Flat(_heroPosition - _nearbyFrom)).sqrMagnitude < NearbyHeroMove * NearbyHeroMove)
                return;
            _nearbyAt = Time.unscaledTime;
            _nearbyFrom = _heroPosition;
            _nearbyOf = spawned;
            _nearbySpawned = spawnedVersion;
            _nearbyBaked = _views.BakedVersion;
            _nearbyNpcs.Clear();
            float range = reach + NearbyMargin;
            // mt-1 C (-g3mt bit 1): the spawned views' positions in one Burst job (same values as their transforms now)
            bool snapshot = spawned != null && G3.Unity.G3Mt.On(G3.Unity.MtBits.BodySnapshot);
            if (snapshot) NpcView.Snapshot.Read(false);
            // The spawned views, then the baked ones (NpcViewDirectory.Views without its iterators: per frame, perf-9).
            if (spawned != null)
                foreach (var view in spawned.ViewComponents)
                {
                    if (view.Value == null) continue;
                    if (!snapshot) AddNearby(view.Key, view.Value.Body, range);
                    else
                    {
                        var at = view.Value.SnapshotPosition;
                        if (G3.Unity.G3Mt.Verifying) G3.Unity.G3Mt.Check(G3.Unity.G3Mt.Same(at, view.Value.Body.position), "focus", $"{view.Key} focus position");
                        if (Flat(at - _heroPosition).sqrMagnitude <= range * range) _nearbyNpcs.Add((view.Key, view.Value.Body));
                    }
                }
            foreach (var view in baked)
                if (view.Value != null && (spawned == null || !spawned.TryGetView(view.Key, out _)))
                    AddNearby(view.Key, view.Value, range);
        }

        void AddNearby(ActorId actor, Transform view, float range)
        {
            if (Flat(view.position - _heroPosition).sqrMagnitude <= range * range) _nearbyNpcs.Add((actor, view));
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>The dialog owner of a talking object (<see cref="InteractiveKind.Speaker"/>); null for other objects.</summary>
        ActorId? SpeakerOf(string id) =>
            _session.Interaction.Objects.TryGet(id, out var def) && def.Kind == InteractiveKind.Speaker ? new ActorId(def.Name) : (ActorId?)null;

        string ObjectName(string id) =>
            _session.Interaction.Objects.TryGet(id, out var def) ? HudFocus.ObjectName(_session, def) : string.Empty;

        /// <summary>True if the target is within reach and the hero faces it more directly than <paramref name="bestFacing"/>.</summary>
        Vector3 _heroPosition, _heroForward;

        void ConsiderNpc(ActorId actor, Transform view, ref float bestFacing)
        {
            bool atRoot = Facing(view.position, reach, bestFacing, out float rootFacing);
            // fix-247: a sleeper lies in its bed while its root stays at the slot beside it (G3 aims at the lying body's
            // box): its spine bone counts as well (CharacterFocus.AimsAtBody), the more directly faced visible point wins
            Vector3 body = default;
            float bodyFacing = 0f;
            bool atBody = CharacterFocus.AimsAtBody(_session.SleeperTheft.IsHelpless(actor)) &&
                          view.TryGetComponent<CombatFighter>(out var fighter) &&
                          Facing(body = fighter.SpinePoint, reach, bestFacing, out bodyFacing);
            if (!atRoot && !atBody) return;
            // G3 GetFocusEntityPriority (PS_Normal): every character is focusable (priority 3, downed/dead 1), with or without
            // dialog — its focus name shows either way (fix-84); Use1 then talks, loots a body or robs a sleeper, or does nothing.
            // G3 FindFocusEntity: a looted-out corpse is not focusable any more (fix-68; NpcLoot.BodyFocusable).
            var loot = _session.Items.NpcLoot;
            if (loot != null && !loot.BodyFocusable(actor, _session.Combat.Vitals.IsDead(actor))) return;
            // G3 FocusCheck → eCEntity::IsFreeLOS (fix-104): not through a floor, ceiling or wall
            if (atRoot && !Sees(FocusLineOfSight.CharacterPoint(view), view)) atRoot = false;
            if (atBody && (atRoot && rootFacing >= bodyFacing || !Sees(body + Vector3.up * CharacterFocus.BodyPointLiftM, view))) atBody = false;
            if (!atRoot && !atBody) return;
            bestFacing = atBody ? bodyFacing : rootFacing;
            Focus = actor;
        }

        /// <summary>G3 IsFreeLOS from the hero's sight point to the candidate's (fix-104); <paramref name="ownMesh"/>: its mesh pivot in the cell scene.</summary>
        bool Sees(Vector3 point, Transform target, Vector3? ownMesh = null, float endTolerance = FocusSight.EndToleranceM) =>
            _sight.IsFree(_heroSight, point, _player, target, ownMesh, endTolerance);

        /// <summary>G3 IsFreeLOS to a world item's view (fix-104), the solid shapes it lies on or in excluded (fix-114).</summary>
        bool SeesItem(Transform item)
        {
            var col = item.GetComponent<Collider>();
            if (col == null || !col.enabled) return Sees(FocusLineOfSight.ColliderPoint(item), item);
            var box = col.bounds;
            return _sight.IsFreeToItem(_heroSight, FocusLineOfSight.Point(box), _player, item, box);
        }

        /// <summary>The hero's sight point: 3/4 up his controller's box (G3: the hero entity's box), else 1.35 m over his feet.</summary>
        internal static Vector3 HeroSightPoint(Transform hero, CharacterController body) =>
            body != null && body.enabled ? FocusLineOfSight.Point(body.bounds) : FocusLineOfSight.CharacterPoint(hero);

        /// <summary>
        /// A world item in G3's reach (<see cref="ItemFocus"/>: 300 cm in the plane, less its box's half side) that the hero
        /// faces at least as directly as <paramref name="bestFacing"/> (and within <see cref="facingCosine"/>: nothing behind him).
        /// </summary>
        bool FacingItem(Transform item, float bestFacing, out float facing)
        {
            facing = 0f;
            var p = item.position;
            float dx = p.x - _heroPosition.x, dz = p.z - _heroPosition.z;
            if (dx * dx + dz * dz > MaxItemReachSq) return false; // cheap cut before the collider lookup
            float hx = 0f, hz = 0f;
            var col = item.GetComponent<Collider>();
            if (col != null && col.enabled) { var e = col.bounds.extents; hx = e.x; hz = e.z; }
            return Facing(p, ItemFocus.ReachM(hx, hz), bestFacing, out facing);
        }

        /// <summary>The planar reach for an item with a 1.5 m half side (a lying two-hander or a spear), squared.</summary>
        static readonly float MaxItemReachSq = ItemFocus.ReachM(1.5f, 1.5f) * ItemFocus.ReachM(1.5f, 1.5f);

        bool Facing(Vector3 target, float maxDistance, float bestFacing, out float facing)
        {
            facing = 0f;
            var offset = Vector3.ProjectOnPlane(target - _heroPosition, Vector3.up);
            float distance = offset.magnitude;
            if (distance > maxDistance || distance < 0.01f) return false;
            facing = Vector3.Dot(_heroForward, offset / distance);
            return facing >= bestFacing;
        }

        void PickUp(string worldItem)
        {
            string item;
            int amount;
            if (_session.Content.Database.TryGet(worldItem, out WorldItemDef def)) (item, amount) = (def.Item, def.Amount);
            else if (_session.Items.World.TryGetDropped(worldItem, out var dropped)) (item, amount) = (dropped.Item, dropped.Amount);
            else return;
            if (!_session.Items.World.PickUp(worldItem)) return;
            _messages.Show(_session.Localizer.Text(TakenText).Replace("$(amount)", amount.ToString()).Replace("$(name)", ItemName(item)));
        }

        string ItemName(string item) =>
            _session.Content.Database.TryGet(item, out ItemDef def) && !def.Name.IsEmpty ? _session.Localizer.Text(def.Name) : item;
    }
}
