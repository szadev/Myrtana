#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-105 ops and probes (Docs/dev-scenarios.md): the quick-slot key as a player presses it (<see cref="QuickSlotInput.Use"/>:
    /// weapons and spells through PlayerCombat.TryReady, the rest — the torch — through HeroItemUse), and a hero bone lost after
    /// the overlay masks were built (the owner's Demo_2026-10-18c: G3MotionPlayer.MaskFor threw on a destroyed bone of the
    /// MetaHuman head, HeroTorchView waited for an overlay that never started and the torch keys did nothing).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        const int Fix105Slot = 9; // the 0 key: the dev loadouts fill the first slots
        GameObject _fix105Bone;

        /// <summary>
        /// 'quickslot item [slot=9]': puts the item on the quick slot (given when missing) and presses its key the player's way
        /// (QuickSlotInput.Use), then waits until the hands settle (a weapon switch, the torch's take or put-away).
        /// </summary>
        IEnumerator QuickSlotStep(ScenarioStep st)
        {
            string item = ResolveItem(st.Get("item"));
            if (item == null) yield break;
            int slot = st.Int("slot", Fix105Slot);
            var inv = _session.Items.Inventories.Of(ActorId.Player);
            if (inv.Count(item) == 0) _session.Items.Inventories.Give(ActorId.Player, item, 1);
            if (!_session.Items.QuickSlots.Assign(slot, item)) { Fail($"quickslot: {item} cannot go on slot {slot}"); yield break; }
            var keys = FindAnyObjectByType<QuickSlotInput>();
            if (keys == null) { Fail("quickslot: no QuickSlotInput"); yield break; }
            var torch = HeroTorchView.Instance;
            bool before = torch != null && torch.Torch.InHand;
            keys.Use(slot);
            float t0 = Time.time;
            yield return new WaitForSeconds(0.2f);
            while (Time.time - t0 < 4f && (Combat.Fighter.Animator.Busy || torch != null && torch.OverlayRunning)) yield return null;
            yield return new WaitForSeconds(0.2f);
            Log($"quickslot {item} (slot {slot}): torch {(before ? "in hand" : "-")} -> {(torch != null && torch.Torch.InHand ? torch.Torch.Item : "-")}, " +
                $"hand {Combat.Fighter.Animator.StateText}{(torch != null && torch.OverlayRunning ? ", torch overlay STILL RUNNING" : "")}");
        }

        /// <summary>
        /// 'losebone add|destroy': add = a bone under the hero's Spine_2 and the overlay masks forgotten, so the next overlay
        /// builds them with it; destroy = that bone goes (as the MetaHuman head swap removes head bones from the live model) while the bone list built
        /// with it stays cached; the masks go, so the next overlay builds one from that list.
        /// </summary>
        void LoseBone(ScenarioStep st)
        {
            var motions = HeroMotions();
            if (motions == null) { Fail("losebone: no hero G3MotionPlayer"); return; }
            if (st.Get("mode", "add") == "destroy")
            {
                if (_fix105Bone == null) { Fail("losebone destroy: no bone added"); return; }
                // The bone list stays cached, the masks built from it go (dev reflection): the next overlay builds a mask for
                // its motion from a list with a destroyed bone, as a motion first played after the MetaHuman head swap did
                DestroyImmediate(_fix105Bone);
                _fix105Bone = null;
                const System.Reflection.BindingFlags Bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var masks = typeof(G3MotionPlayer).GetField("_masks", Bf)?.GetValue(motions) as System.Collections.IDictionary;
                masks?.Clear();
                typeof(G3MotionPlayer).GetField("_spineMask", Bf)?.SetValue(motions, null);
                var bones = typeof(G3MotionPlayer).GetField("_maskBones", Bf)?.GetValue(motions) as Transform[];
                Log($"losebone: the added bone is destroyed; the cached bone list ({(bones == null ? "none" : bones.Length + " bones")}) still lists it, the masks are forgotten");
                return;
            }
            Transform spine = null;
            foreach (var t in motions.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith("Spine_Spine_2", System.StringComparison.Ordinal)) { spine = t; break; }
            if (spine == null) { Fail("losebone: no Spine_Spine_2"); return; }
            _fix105Bone = new GameObject("[fix105 bone]") { hideFlags = HideFlags.DontSave };
            _fix105Bone.transform.SetParent(spine, false);
            motions.RigChanged();
            Log($"losebone: a bone under {spine.name}, overlay masks forgotten");
        }

        G3MotionPlayer HeroMotions()
        {
            foreach (var h in Hero.GetComponentsInChildren<G3HeroAnimator>())
                if (h.isActiveAndEnabled && h.TryGetComponent<G3MotionPlayer>(out var m)) return m;
            return Hero.GetComponentInChildren<G3MotionPlayer>();
        }

        /// <summary>A control holder of the running game the runner leaves behind ('messhero': a use or talk that never ended).</summary>
        sealed class StaleControlHolder : ISessionControlHolder
        {
        }

        /// <summary>
        /// 'messhero': leaves the hero as a broken game might before a load — a session control lock nobody releases, scripted
        /// input, walk-only, forced walk, held gait, a body yaw, look-behind, a running motion overlay (the torch's take, its
        /// view's flag set). The load or new game must clean it all up (probe herotransient, 'locked').
        /// </summary>
        IEnumerator MessHeroStep()
        {
            if (_boot == null || _boot.Controls == null) { Fail("messhero: no controls"); yield break; }
            _boot.Controls.Acquire(new StaleControlHolder());
            var p = Hero;
            p.scriptedMove = new Vector2(0.3f, 1f);
            p.scriptedOnly = true;
            p.forceWalk = p.holdGait = p.paradeWalk = true;
            p.bodyYaw = 33f;
            p.LookingBehind = true;
            var torch = HeroTorchView.Instance;
            if (torch != null && !torch.Torch.InHand)
            {
                if (_session.Items.Inventories.Of(ActorId.Player).Count("It_Torch_Burning") == 0) _session.Items.Inventories.Give(ActorId.Player, "It_Torch_Burning", 1);
                torch.TryUse("It_Torch_Burning"); // its take runs into the load
            }
            yield return null;
            Log($"messhero: {HeroTransient()}; locked {_boot.Controls.IsLocked} ({_boot.Controls.HoldersText})");
        }

        /// <summary>The hero's transient state that is not at rest ("clean" when all is).</summary>
        string HeroTransient()
        {
            var p = Hero;
            var bad = new System.Collections.Generic.List<string>();
            if (p.scriptedMove != Vector2.zero) bad.Add("scriptedMove");
            if (p.scriptedOnly) bad.Add("scriptedOnly");
            if (p.scriptedWalk || p.scriptedSprint || p.scriptedJump) bad.Add("scripted gait");
            if (p.forceWalk) bad.Add("forceWalk");
            if (p.holdGait) bad.Add("holdGait");
            if (p.paradeWalk) bad.Add("paradeWalk");
            if (p.bodyYaw.HasValue) bad.Add("bodyYaw");
            if (p.LookingBehind) bad.Add("lookingBehind");
            if (!p.moveInputEnabled || !p.lookInputEnabled) bad.Add("input off");
            if (!p.enabled) bad.Add("controller off");
            if (Combat != null && Combat.WaitingWeaponKey != null) bad.Add("weapon key waiting");
            if (Combat != null && Combat.Fighter != null && Combat.Fighter.Animator.Busy) bad.Add("combat motion");
            var torch = HeroTorchView.Instance;
            if (torch != null && torch.OverlayRunning) bad.Add("torch overlay");
            if (_boot != null && _boot.Controls != null && _boot.Controls.IsLocked) bad.Add("locked:" + _boot.Controls.HoldersText);
            return bad.Count == 0 ? "clean" : string.Join(",", bad);
        }

        static bool IsFix105Probe(string kind) => kind == "torch" || kind == "torchbusy" || kind == "torchfails" || kind == "herotransient";

        string Fix105Probe(string kind)
        {
            if (kind == "herotransient") return HeroTransient();
            var view = HeroTorchView.Instance;
            if (view == null) return "-";
            switch (kind)
            {
                case "torch": return view.Torch.InHand ? view.Torch.Item : "-";
                case "torchfails": return view.OverlayFailures.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: return view.OverlayRunning.ToString();
            }
        }
    }
}
#endif
