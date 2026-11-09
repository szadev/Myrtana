using System;
using System.Collections.Generic;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero's burning torch on screen (rules: <see cref="HeroTorch"/>, Docs/formats-effects.md §8): using a torch item
    /// (inventory or quick slot) plays <c>Hero_Stand_Torch_None_P0_HoldLeft_Begin</c> (<c>Torch_1H</c> with a drawn 1H
    /// weapon; the overlay variant while moving so the legs keep walking, the full-body variant while standing), then the
    /// torch appears in <c>Slot_LeftHand_Weapon</c> and <c>HoldLeft_End</c> raises it (G3 HoldStacks between the two),
    /// with the item's effect <c>eff_item_torch_02</c> 45 cm up the shaft and its flickering point light
    /// (<see cref="G3ItemEffect"/>); the locomotion and combat motions then use the Torch sets. Using it again plays
    /// HoldLeft_End backwards, the torch goes back into the inventory, then HoldLeft_Begin backwards. Drawing a bow, two-handed weapon, staff or spell (or a
    /// 1H weapon with a shield) puts it back into the inventory at once, and holstering a melee weapon takes it again;
    /// entering deep water lets go of it (PS_Swim, back into the inventory); interactions stow or release it
    /// (<see cref="HeroTorch.BeginInteraction"/>). A torch is never used up. The held torch is saved (<see cref="SaveSection"/>).
    /// </summary>
    public sealed class HeroTorchView : MonoBehaviour, ISessionView
    {
        public const string TorchField = "Torch";
        const float HoldFade = 0.15f;

        /// <summary>The one torch view of the game (HeroItemUse, HeroObjectUse call it).</summary>
        public static HeroTorchView Instance { get; private set; }

        public readonly HeroTorch Torch = new HeroTorch();
        TorchSaveSection _save;
        public TorchSaveSection SaveSection => _save ??= new TorchSaveSection(() => Torch);

        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        GameSession _session;
        G3Player _player;
        ItemVisualCatalog _catalog;
        G3HeroAnimator _hero;
        G3CombatAnimator _combat;
        G3MotionPlayer _motions;
        Transform _slot;
        GameObject _prop;
        string _shown;
        // A running take/put-away: two overlay motions, the hand changes between them (G3.Player.HoldMotionRules).
        bool _overlayRunning, _puttingAway, _swapped;
        G3.Player.HoldPart[] _parts;
        int _part;
        // The weapon state seen last frame (draw releases, holster re-holds).
        bool _wasDrawn;
        string _lastField;
        // fix-252: the weapon left the stance; the torch its draw released comes back once the put-away is over
        // (TorchHolsterRules), not over the sheathe's own overlay. _holsterField = the field that was put away.
        bool _holsterPending;
        string _holsterField;
        // fix-252: a torch key pressed while the combat hand draws or sheathes acts when that ends (it would cut it short).
        string _waitingUse;
        string _overlayKey;
        // Logged once per take: the stance check after the torch reached the hand.
        bool _stanceChecked, _offHandRestoredLogged;

        public void Init(G3Player player, ItemVisualCatalog catalog)
        {
            _player = player;
            _catalog = catalog;
            Instance = this;
            if (_player != null) _player.WaterChanged += OnWaterChanged;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_player != null) _player.WaterChanged -= OnWaterChanged;
            foreach (var s in _subscriptions) s.Dispose();
        }

        public void Bind(GameSession session)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            _session = session;
            _overlayRunning = false;
            _wasDrawn = false;
            _lastField = null;
            _holsterPending = false;
            _holsterField = null;
            _waitingUse = null;
            if (session == null) return;
            var db = session.Content.Database;
            Torch.Bind(session.Items.Inventories.Of(ActorId.Player), id => db.TryGet(id, out ItemDef d) ? d : null, session.Events,
                       SaveSection.TakeRestored());
            _subscriptions.Add(session.Events.Subscribe<TorchChanged>(_ => { if (!_overlayRunning) Show(Torch.InHand ? Torch.Item : null); }));
            Show(Torch.InHand ? Torch.Item : null);
        }

        /// <summary>True when <paramref name="item"/> is a torch (then this view handled the use: taken, put away or refused).</summary>
        public bool TryUse(string item)
        {
            if (_session == null || !Torch.IsTorch(item)) return false;
            // fix-105: an overlay that never ended (it threw, or its motion was lost without notice) must not swallow every
            // later key: past its longest length it is given up
            if (_overlayRunning && G3.Player.HoldMotionRules.OverlayStuck(_overlayStarted, Time.time))
            {
                Debug.LogWarning($"[Myrtana] torch: the {(_puttingAway ? "put-away" : "take")} overlay {_overlayKey ?? "-"} ran {Time.time - _overlayStarted:F1} s: given up");
                CancelOverlay(finish: true);
            }
            if (_overlayRunning) return true;
            Resolve();
            if (_combat != null && TorchHolsterRules.TorchKeyWaits(_combat.Hand.Settled))
            {
                _waitingUse = item; // fix-252: the latest torch key waits for the draw / sheathe of the weapon hand
                return true;
            }
            if (Torch.InHand && Torch.Item == item)
            {
                BeginOverlay(putAway: true);
                return true;
            }
            if (Torch.InHand) Torch.Release(); // another torch item: the old one goes back into the inventory
            var r = Torch.Take(item, DrawnField(), LeftHandBusy(), _player != null && _player.Swimming);
            if (r == TorchTakeResult.Taken)
            {
                Show(null);
                BeginOverlay(putAway: false);
            }
            else if (r == TorchTakeResult.HandsBusy) G3EffectSounds.Play(HeroItemUse.DenyEffect, Camera.main != null ? Camera.main.transform.position : transform.position);
            return true;
        }

        /// <summary>An object use starts (HeroObjectUse): the torch stays, is stowed or goes back into the inventory.</summary>
        public void BeginInteraction(string useType, string objectName)
        {
            if (_session == null) return;
            CancelOverlay();
            Torch.BeginInteraction(useType, objectName);
            Show(Torch.InHand ? Torch.Item : null);
        }

        public void EndInteraction()
        {
            if (_session == null) return;
            Torch.EndInteraction();
            Show(Torch.InHand ? Torch.Item : null);
        }

        string DrawnField()
        {
            Resolve();
            return _combat != null && _combat.Drawn ? _combat.Weapon : null;
        }

        /// <summary>
        /// G3: drawing a 1H weapon puts the worn shield into the left hand — or a dual wielder's second blade (gESlot_BackLeft,
        /// fix-32; the torch is handled as with the shield [interp]).
        /// </summary>
        bool LeftHandBusy() =>
            DrawnField() == "1H" && _session != null &&
            (_session.Items.Equipment.In(WornSlot.Shield) != null || _session.Items.Equipment.DualWieldReady);

        void OnWaterChanged(G3.Player.WaterMode old, G3.Player.WaterMode now)
        {
            if (now != G3.Player.WaterMode.Swim || _session == null) return;
            CancelOverlay();
            if (Torch.Item != null) Torch.Release(); // PS_Swim: both hands emptied, the torch goes back into the inventory
            Show(null);
        }

        /// <summary>The burning torch is held in the left hand (G3 gESlot_LeftHand): the quick bar and inventory tint it.</summary>
        void LateUpdate()
        {
            if (_session != null) _session.Items.Hands.SetLeft(Torch.InHand ? Torch.Item : null);
        }

        void Update()
        {
            if (_session == null) return;
            Resolve();
            bool drawn = _combat != null && _combat.Drawn;
            string field = drawn ? _combat.Weapon : null;
            if (drawn && !_overlayRunning && (Torch.InHand || !_wasDrawn || field != _lastField) &&
                Torch.OnWeaponDrawn(field, LeftHandBusy()))
                Show(null); // the left hand gets the bow / shield, or the weapon needs both hands: back into the inventory
            if (!drawn && _wasDrawn)
            {
                _holsterPending = true;
                _holsterField = _lastField;
            }
            _wasDrawn = drawn;
            if (drawn) _lastField = field;
            RunHolsterTake();
            RunWaitingUse();
            UpdateOverlay();
            KeepTorchStance();
        }

        /// <summary>
        /// fix-252: holstered from melee, the torch released by the draw comes back after the put-away (G3
        /// _AI_HoldInventoryItems: the right hand's pair to its end, then the left hand's); taken at the sheathe's first
        /// frame, its overlay cut the sheathe and left the weapon in the hand.
        /// </summary>
        void RunHolsterTake()
        {
            if (!_holsterPending || _combat == null) return;
            var step = TorchHolsterRules.Decide(_combat.Drawn, _combat.WeaponInHand, _combat.Hand.Settled, _combat.Busy);
            if (step == TorchHolsterStep.Wait) return;
            _holsterPending = false;
            if (step == TorchHolsterStep.Cancel || _overlayRunning) return;
            if (!Torch.OnWeaponHolstered(_holsterField, _player != null && _player.Swimming)) return;
            Show(null);
            BeginOverlay(putAway: false);
        }

        /// <summary>fix-252: the torch key that waited for the weapon hand's draw or sheathe acts now.</summary>
        void RunWaitingUse()
        {
            if (_waitingUse == null || _combat == null || _overlayRunning || _holsterPending) return;
            if (TorchHolsterRules.TorchKeyWaits(_combat.Hand.Settled)) return;
            string item = _waitingUse;
            _waitingUse = null;
            TryUse(item);
        }

        /// <summary>
        /// While the torch is in the hand the hero's locomotion must use the Torch set (the arm holds it up, the hand grips):
        /// re-applies the off-hand field when something cleared it, and logs once per take which set is in use.
        /// </summary>
        void KeepTorchStance()
        {
            if (!Torch.InHand || _shown == null || _hero == null || _overlayRunning && !_swapped)
            {
                if (!Torch.InHand) _stanceChecked = _offHandRestoredLogged = false;
                return;
            }
            if (_hero.OffHand != TorchField)
            {
                if (!_offHandRestoredLogged)
                    Debug.LogWarning($"[Myrtana] torch in hand but hero off-hand was '{_hero.OffHand ?? "none"}' on {_hero.name}: set back to {TorchField}");
                _offHandRestoredLogged = true;
                _hero.SetOffHand(TorchField);
                if (_combat != null) _combat.OffHand = TorchField;
            }
            if (_stanceChecked) return;
            _stanceChecked = true;
            if (_hero.leftItem != TorchField) _hero.SetOffHand(TorchField); // once more, now that the model is set up
            if (_hero.leftItem == TorchField)
                Debug.Log($"[Myrtana] torch in hand: {_shown} on {_hero.name}/{(_slot != null ? _slot.name : "-")}, locomotion {_hero.LocomotionKey}");
            else
                Debug.LogWarning($"[Myrtana] torch in hand but the locomotion set is {_hero.LocomotionKey} (no Hero_Stand_Torch_{_hero.rightItem}_ set " +
                                 $"in the library {(_motions != null && _motions.library != null ? _motions.library.name : "-")}, or a ranged/spell hand): the arm hangs");
        }

        // ------------------------------------------------------------------ take / put away motion

        /// <summary>
        /// Takes the torch (<paramref name="putAway"/> false: <c>HoldLeft_Begin</c>, the torch in the hand, <c>HoldLeft_End</c>)
        /// or puts it away (<c>HoldLeft_End</c> backwards, the torch gone, <c>HoldLeft_Begin</c> backwards), as G3
        /// _AI_HoldLeftInventoryItem_Base does around HoldStacks (fix-42).
        /// </summary>
        void BeginOverlay(bool putAway)
        {
            Resolve();
            _puttingAway = putAway;
            _swapped = false;
            _parts = G3.Player.HoldMotionRules.Parts(!putAway);
            _part = -1;
            _overlayRunning = true;
            _overlayStarted = Time.time;
            try
            {
                NextPart();
            }
            catch (Exception ex)
            {
                // fix-105 (owner, Demo_2026-10-18c: the overlay mask threw on a destroyed bone, _overlayRunning stayed set and
                // the torch keys did nothing for the rest of the game): the hand changes at once, the view is free again
                Debug.LogException(ex);
                OverlayFailures++;
                CancelOverlay(finish: true);
            }
        }

        /// <summary>A take or put-away motion of the torch runs (the dev probe torchbusy; keys wait for it).</summary>
        public bool OverlayRunning => _overlayRunning;

        /// <summary>Takes / put-aways whose motion threw and were finished at once (fix-105; the dev probe torchfails).</summary>
        public int OverlayFailures { get; private set; }

        float _overlayStarted; // fix-105: HoldMotionRules.OverlayStuck

        /// <summary>Plays the next motion of the take/put-away; a missing one is skipped (its hand change still happens).</summary>
        void NextPart()
        {
            while (++_part < _parts.Length)
            {
                var part = _parts[_part];
                string right = DrawnField() == "1H" ? "1H" : "None";
                string key = null;
                // G3 _AI_HoldLeftInventoryItem_Base (FUN_100031a0): the overlay ("O") motion while the hero moves, the
                // full-body ("N") one while he stands. Both play on the upper-body layer here: standing, the legs show the
                // idle either way.
                bool moving = _player != null && G3.Player.HoldMotionRules.UseOverlay(new Vector2(_player.Velocity.x, _player.Velocity.z).magnitude);
                if (_motions != null && _motions.library != null)
                    foreach (var variant in moving ? new[] { "O", "N" } : new[] { "N", "O" })
                    {
                        var k = new G3MotionKey("Hero", "HoldLeft", part.Word, "Fwd", "Stand", TorchField, right, "P0", variant).ToString();
                        if (_motions.library.Find(k) != null) { key = k; break; }
                    }
                float fade = _part == 0 ? HoldFade : 0f;
                if (key != null && _motions.PlayOverlay(key, fade, restart: true, reverse: part.Reverse))
                {
                    _overlayKey = key;
                    return;
                }
                if (part.SwitchAfter && !_swapped) FinishSwap(); // no motion imported: the hand changes at once
            }
            if (!_swapped) FinishSwap();
            if (_motions != null && _overlayKey != null && _motions.OverlayKey == _overlayKey) _motions.StopOverlay(HoldFade);
            _overlayRunning = false;
        }

        void UpdateOverlay()
        {
            if (!_overlayRunning) return;
            if (_motions == null || _motions.OverlayKey != _overlayKey)
            {
                if (!_swapped) FinishSwap();
                _overlayRunning = false;
                return;
            }
            if (!_motions.OverlayFinished)
            {
                if (G3.Player.HoldMotionRules.OverlayStuck(_overlayStarted, Time.time)) CancelOverlay(finish: true); // fix-105: never stuck
                return;
            }
            if (_parts[_part].SwitchAfter && !_swapped) FinishSwap();
            try
            {
                NextPart();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                OverlayFailures++;
                CancelOverlay(finish: true);
            }
        }

        void FinishSwap()
        {
            _swapped = true;
            if (_puttingAway) Torch.Release();
            Show(Torch.InHand ? Torch.Item : null);
        }

        /// <param name="finish">fix-105: the hand change of the unfinished take or put-away happens (the torch shown or released).</param>
        void CancelOverlay(bool finish = false)
        {
            if (!_overlayRunning) return;
            _overlayRunning = false;
            if (finish && !_swapped) FinishSwap();
            else if (!_swapped && _puttingAway) Torch.Release();
            if (_motions != null && _motions.OverlayKey == _overlayKey) _motions.StopOverlay(HoldFade);
        }

        // ------------------------------------------------------------------ the torch in the hand

        void Show(string item)
        {
            Resolve();
            string field = item != null ? TorchField : null;
            if (_hero != null) _hero.SetOffHand(field);
            if (_combat != null) _combat.OffHand = field;
            if (item == _shown && (_prop == null || _prop.activeSelf == (item != null))) return;
            _shown = item;
            if (item == null)
            {
                if (_prop != null) _prop.SetActive(false);
                return;
            }
            ItemDef def = null;
            _session?.Content.Database.TryGet(item, out def);
            string mesh = CombatFighter.VisualKey(def);
            if (_slot == null || _catalog == null || mesh == null || !_catalog.TryGetMesh(mesh, out var entry))
            {
                Debug.LogWarning($"[Myrtana] torch '{item}' not shown: slot {(_slot != null ? "ok" : "missing")}, mesh {mesh ?? "-"} {(_catalog == null ? "(no catalog)" : "")}");
                return;
            }
            if (_prop == null)
            {
                _prop = new GameObject("Torch L");
                _prop.AddComponent<MeshFilter>();
                _prop.AddComponent<MeshRenderer>();
            }
            if (_prop.transform.parent != _slot) _prop.transform.SetParent(_slot, false); // the item mesh is modelled in the slot frame
            _prop.GetComponent<MeshFilter>().sharedMesh = entry.mesh;
            _prop.GetComponent<MeshRenderer>().sharedMaterials = entry.materials;
            _prop.SetActive(true);
            G3ItemEffect.Sync(_prop, item);
        }

        /// <summary>
        /// The hero model's animators and hand slot. The active model wins: an inactive one (a body kept aside by a form
        /// change) would get the off-hand field while the visible body keeps its free-hand set.
        /// </summary>
        void Resolve()
        {
            if (_player == null) return;
            if (_hero == null || !_hero.isActiveAndEnabled)
            {
                G3HeroAnimator pick = null;
                foreach (var h in _player.GetComponentsInChildren<G3HeroAnimator>(true))
                    if (pick == null || h.isActiveAndEnabled && !pick.isActiveAndEnabled) pick = h;
                if (pick != _hero)
                {
                    if (_hero != null && pick != null) Debug.Log($"[Myrtana] torch view: hero model {_hero.name} -> {pick.name}");
                    _hero = pick;
                    _combat = null;
                    _motions = null;
                    _slot = null;
                    _stanceChecked = false;
                }
            }
            if (_combat == null) _combat = _hero != null ? _hero.GetComponentInChildren<G3CombatAnimator>(true) : null;
            if (_combat == null) _combat = _player.GetComponentInChildren<G3CombatAnimator>(true);
            if (_motions == null) _motions = _hero != null ? _hero.GetComponent<G3MotionPlayer>() : null;
            if (_motions == null) _motions = _player.GetComponentInChildren<G3MotionPlayer>(true);
            if (_slot == null && _hero != null) _slot = FindDeep(_hero.transform, "Slot_LeftHand_Weapon");
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
