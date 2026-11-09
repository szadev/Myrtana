using System.Collections;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Eating and drinking (G3 PS_HUD_Use / PS_QuickUse → <c>_AI_UseInventoryItem</c>): the hero stops, takes the item into
    /// his right hand, plays the use motion of its use type (<c>Hero_Stand_None_&lt;Potion|Meat|Fruit|Bread|Cup|Bowl&gt;_P0_Use</c>,
    /// the player's at double speed) and only then is the item consumed (<see cref="ItemUse.Use"/>; the "attribute = old + n"
    /// and GO_Learned messages come from <see cref="GameNotices"/>). One item at a time. Swimming, in the air, or without the
    /// motion, the item is used at once.
    /// <para>With a weapon, bow or the spell hand drawn (fix-68) [PS_QuickUse → _AI_UseInventoryItem →
    /// _AI_HoldInventoryItems]: G3 takes the item into the hand in place of the weapon at once (HoldStacks, no sheathe
    /// motion), plays the same use motion, consumes the item, then puts the stacks held before back into the hands (no
    /// draw motion) and returns to PS_Melee / PS_Ranged / PS_Magic. Refused (nothing used) while an attack, a hit reaction,
    /// the block, a draw or sheathe or the aim runs (OnPlayerPotionQuickUse returns 0 in the parade ani state).</para>
    /// <para>fix-201: any interruption (a hit reaction, a knock-down, a fall, death, swimming, a transformation, a dialog,
    /// another owner of the motion) ends the use at once (<see cref="ItemUseFlow"/>): before the consume point the item is
    /// kept, after it the effect stays; the hand prop goes, the weapon comes back (not when he lies), the motion and the
    /// controls are released. The control lock is also watched (<see cref="HeroControlLock"/>): it cannot outlive the use.</para>
    /// </summary>
    public sealed class HeroConsumption : MonoBehaviour, ISessionView, ISessionControlHolder, IActionLockOwner
    {
        const float Fade = 0.15f, PlayerSpeed = 2f;

        /// <summary>The scene's consumption (null before the bootstrap made it); <see cref="HeroItemUse"/> calls it.</summary>
        public static HeroConsumption Instance { get; private set; }

        GameSession _session;
        HeroControlLock _controls;
        GameScreens _screens;
        HeroHandProp _prop;
        G3MotionPlayer _motions;

        /// <summary>True while the hero eats or drinks; another use waits for it.</summary>
        public bool Busy { get; private set; }

        readonly ItemUseFlow _flow = new ItemUseFlow();
        Coroutine _run;
        /// <summary>The motion key the use plays now (null between its motions); another key on the body = the motion was taken.</summary>
        string _playing;
        /// <summary>The use is a dialog's shown use (fix-153): the dialog is its context, not an interruption.</summary>
        bool _inDialog;
        /// <summary>Frame of the last step of the use's coroutine: the control-lock watchdog sees a dead coroutine by it.</summary>
        int _beat;

        /// <summary>How the last use ended and what cut it (runner probe).</summary>
        public string LastEndText => $"{_flow.LastEnd}{(_flow.LastCut != ItemUseCut.None ? " by " + _flow.LastCut : "")}";

        PlayerCombat _combat;

        /// <summary>The hero holds a weapon, bow or spell (the item is used at once then).</summary>
        bool WeaponDrawn()
        {
            if (_combat == null) _combat = _controls.Player.GetComponent<PlayerCombat>();
            return _combat != null && _combat.StanceActive;
        }

        public void Init(HeroControlLock controls, GameScreens screens)
        {
            _controls = controls;
            _screens = screens;
            _prop = new HeroHandProp(controls.Player.transform);
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Bind(GameSession session)
        {
            StopAllCoroutines();
            _run = null;
            _restore = null; // another session: its hands start empty (ResetAll)
            if (Busy) Finish();
            _session = session;
            // CP AB: no regeneration while a GUI page is open (gameplay-data/status_tick.json).
            if (_screens != null) session.Regeneration.PageOpen = () => _screens.IsPageOpen;
        }

        /// <summary>
        /// Consumes <paramref name="item"/> with the motion; false when it is not usable now (the caller then tries other uses).
        /// While another item is being consumed the request is swallowed (true).
        /// </summary>
        public bool Consume(string item) => Consume(item, null);

        /// <summary>
        /// fix-193: drinks <paramref name="item"/> with the potion use motion and then calls <paramref name="apply"/> in place
        /// of <see cref="ItemUse.Use"/> (a transformation potion: the transformation follows the drink, PS_MagicPotion).
        /// Without the motion <paramref name="apply"/> runs at once. False when the session is not bound.
        /// </summary>
        public bool Drink(string item, System.Func<bool> apply) => apply != null && Consume(item, apply);

        bool Consume(string item, System.Func<bool> apply)
        {
            if (_session == null || apply == null && !_session.Items.Use.CanUse(item)) return false;
            if (Busy) return true;
            System.Func<bool> use = apply ?? (() => _session.Items.Use.Use(item));
            var player = _controls.Player;
            _motions = player.GetComponentInChildren<G3MotionPlayer>();
            string field = _session.Content.Database.TryGet(item, out ItemDef def) ? def.Kind : null;
            string begin = MotionKey(field, "Begin");
            bool canPlay = _motions != null && _motions.library != null && begin != null && player.IsGrounded && !player.Swimming;
            if (!canPlay) return use();
            if (WeaponDrawn())
            {
                var hands = _combat.Fighter != null ? _combat.Fighter.Animator : null;
                if (hands == null) return use();
                if (hands.InRecoverTail) hands.CutRecover(); // the last attack's Recover is a tail any action cuts
                if (!hands.StowAtOnce(out string heldField, out string heldTemplate))
                {
                    Debug.Log($"[Myrtana] use {item} refused: the hands are busy ({hands.StateText})");
                    return true; // G3: no use during a move, a reaction, the block or the aim
                }
                _restore = (heldField, heldTemplate);
            }
            if (_motions.Claimed)
            {
                RestoreHands();
                return use();
            }
            Begin(consumes: true, inDialog: false);
            _run = StartCoroutine(Run(item, begin, MotionKey(field, "End"), use: use));
            return true;
        }

        /// <summary>
        /// fix-153: an info script's "Use" by the hero (G3 Use → _AI_UseInventoryItem in the talk: Montera's barkeeper pours him
        /// a schnapps): the core has already applied the item (UseItemHandler), so only the motion with the item in his hand
        /// is played; nothing is consumed here. False when there is no motion (swimming, not grounded, not imported) or the
        /// hero is busy.
        /// </summary>
        public bool ShowUse(string item)
        {
            if (_session == null || Busy || string.IsNullOrEmpty(item)) return false;
            var player = _controls.Player;
            _motions = player.GetComponentInChildren<G3MotionPlayer>();
            string field = _session.Content.Database.TryGet(item, out ItemDef def) ? def.Kind : null;
            string begin = MotionKey(field, "Begin");
            if (_motions == null || _motions.library == null || begin == null || !player.IsGrounded || player.Swimming) return false;
            if (WeaponDrawn()) return false;
            Begin(consumes: false, inDialog: true);
            _run = StartCoroutine(Run(item, begin, MotionKey(field, "End"), consume: false));
            return true;
        }

        /// <summary>The use motion of a hand field, the torch variant while the hero holds a torch; null when not imported.</summary>
        string MotionKey(string field, string phase)
        {
            if (string.IsNullOrEmpty(field) || _motions == null || _motions.library == null) return null;
            bool torch = HeroTorchView.Instance != null && HeroTorchView.Instance.Torch.InHand;
            // herbs (use type Plant) have no hero motion of their own: the fruit one [interp]
            foreach (var hand in field == "Plant" ? new[] { field, "Fruit" } : new[] { field })
            foreach (var left in torch ? new[] { "Torch", "None" } : new[] { "None" })
            {
                string key = G3HeroUseMotions.Consume(left, hand, phase);
                if (_motions.library.Find(key) != null) return key;
            }
            return null;
        }

        void Begin(bool consumes, bool inDialog)
        {
            Busy = true;
            _inDialog = inDialog;
            _playing = null;
            _beat = Time.frameCount;
            _flow.Start(consumes);
            // fix-201: the lock lives only while the use's coroutine steps (a coroutine cut by anything else frees it)
            _controls.Acquire(this, () => Busy && Time.frameCount - _beat <= 2);
            _motions.Claim(this);
        }

        IEnumerator Run(string item, string begin, string end, bool consume = true, System.Func<bool> use = null)
        {
            _prop.Show(_session, item, leftHand: false);
            yield return Once(begin);
            // a cut that came after this frame's check (a later Update took the motion): the item is kept
            var late = _flow.Check(Watch(), 0f);
            if (late != ItemUseCut.None)
            {
                _run = null;
                Interrupt(late);
                yield break;
            }
            // ConsumeItems after the use animation (_AI_UseInventoryItem); once, and only when nothing cut the use before
            if (_flow.ReachConsumePoint() && consume) { if (use != null) use(); else _session.Items.Use.Use(item); }
            _prop.Hide();
            if (end != null && _motions != null && _motions.isActiveAndEnabled) yield return Once(end); // a transformation hid the hero: no end motion
            _run = null;
            _flow.End();
            Finish();
        }

        IEnumerator Once(string key)
        {
            _beat = Time.frameCount;
            if (!_motions.Play(key, Fade, restart: true)) yield break;
            _playing = key;
            _motions.SetSpeed(PlayerSpeed);
            float timeout = Time.time + 4f;
            yield return null;
            while (_motions != null && _motions.CurrentKey == key && !_motions.CurrentFinished && Time.time < timeout)
            {
                _beat = Time.frameCount;
                yield return null;
            }
            _beat = Time.frameCount;
            // a motion taken by another owner stays "playing": the check after the begin motion sees it lost (item kept)
            if (_motions == null || _motions.CurrentKey == key) _playing = null;
        }

        /// <summary>fix-201: every frame of a use, before its coroutine steps: anything that cuts it ends it cleanly now.</summary>
        void Update()
        {
            if (!Busy || _session == null) return;
            var cut = _flow.Check(Watch(), Time.deltaTime);
            if (cut != ItemUseCut.None) Interrupt(cut);
        }

        ItemUseWatch Watch()
        {
            var player = _controls.Player;
            if (_combat == null && player != null) _combat = player.GetComponent<PlayerCombat>();
            var fighter = _combat != null ? _combat.Fighter : null;
            var a = fighter != null ? fighter.Animator : null;
            var vitals = _session.Combat.Vitals;
            return new ItemUseWatch
            {
                Dead = _combat != null && _combat.IsDead || a != null && a.IsDead || vitals.IsDead(ActorId.Player),
                Down = a != null && a.IsDown || fighter != null && fighter.SittingKnockedDown || vitals.IsDown(ActorId.Player),
                Falling = player != null && player.enabled && !player.IsGrounded && player.AirTime > 0.3f,
                Reacting = a != null && a.InReaction,
                Swimming = player != null && player.Swimming,
                Transformed = _flow.Stage == ItemUseStage.Begin && _session.SpellEffects.Hero.Kind != HeroFormKind.Hero,
                DialogStarted = !_inDialog && _session.Dialog.IsActive,
                // the use's own motion is on the body (between its two motions nothing is asked); a transformation potion
                // may hide the body after the consume point
                MotionOwned = _playing == null || _motions != null && (!_motions.isActiveAndEnabled || _motions.CurrentKey == _playing),
            };
        }

        /// <summary>Ends the running use at once (fix-201): the item is kept before the consume point, applied after it.</summary>
        void Interrupt(ItemUseCut cut)
        {
            if (!Busy) return;
            if (_run != null) StopCoroutine(_run);
            _run = null;
            var stage = _flow.Stage;
            var end = _flow.End(cut);
            Debug.Log($"[Myrtana] item use cut by {cut} in {stage}: {end}{(end == ItemUseEnd.CutKept ? " (the item is kept)" : "")}");
            if (!ItemUseFlow.RestoresHands(cut)) _restore = null; // he lies or died: the weapon stays out of the hand
            Finish();
        }

        /// <summary>Control-lock watchdog (fix-201): the use's coroutine stopped stepping without ending the use.</summary>
        void IActionLockOwner.ActionOrphaned()
        {
            if (!Busy) return;
            Debug.LogWarning("[Myrtana] item use lost its coroutine: ended by the control-lock watchdog");
            Interrupt(ItemUseCut.MotionLost);
        }

        (string field, string template)? _restore;

        /// <summary>The weapon (or spell hand) held before the use goes back into the hands at once (G3 HoldStacks).</summary>
        void RestoreHands()
        {
            if (_restore == null) return;
            var (field, template) = _restore.Value;
            _restore = null;
            var hands = _combat != null && _combat.Fighter != null ? _combat.Fighter.Animator : null;
            if (hands != null && !_combat.IsDead && hands.HoldAtOnce(field, template))
                Debug.Log($"[Myrtana] used item: {field} back in the hand");
        }

        /// <summary>The hands were emptied for the use and are given back at its end (runner probe, fix-68).</summary>
        public bool HandsStowed => _restore != null;

        void Finish()
        {
            // every step on its own: one failing must not keep the lock (fix-201)
            Busy = false;
            if (_flow.Running) _flow.End(ItemUseCut.MotionLost);
            try { _prop?.Hide(); } catch (System.Exception e) { Debug.LogException(e); }
            try { RestoreHands(); } catch (System.Exception e) { _restore = null; Debug.LogException(e); }
            if (_motions != null)
            {
                // only the use's own motion goes back to normal speed (a stumble that cut it keeps the combat speed)
                if (_playing != null && _motions.CurrentKey == _playing) _motions.SetSpeed(1f);
                _motions.Release(this);
            }
            _playing = null;
            _controls.Release(this);
        }
    }
}
