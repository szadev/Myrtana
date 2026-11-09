#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity.Dev
{
    /// <summary>par-magic ops: teaching a spell, the spell book page (key B) and the staff block in the magic stance.</summary>
    public sealed partial class ScenarioRunner
    {
        /// <summary>
        /// `teach spell=X` — the real teaching path (TeachingService.Teach, G3 gCInfo_PS::OnEndInfo → LearnSpell): the hero gets
        /// the learning points and INT the lesson needs (force=1, default), then learns the spell; it fails on a refusal.
        /// </summary>
        void TeachStep(ScenarioStep st)
        {
            var s = _session;
            string query = st.Get("spell");
            string spell = s.HeroSpells.SpellOf(query) ?? (s.Content.Database.TryGet(query, out SkillDef _) ? query : null);
            if (spell == null)
            {
                string item = ResolveItem(query, spellsOnly: true);
                spell = s.HeroSpells.SpellOf(item);
            }
            if (spell == null || !s.Content.Database.TryGet(spell, out SkillDef skill)) { Fail($"teach: no spell '{query}'"); return; }
            var lesson = new Lesson(null, 0, spell);
            if (st.Bool("force", true))
            {
                foreach (var r in skill.Requirements)
                    if (s.Hero.Stats.Value(r.Attribute) < r.Minimum) s.Hero.Stats.Apply(r.Attribute, ModifierOp.SetVal, r.Minimum);
                if (!string.IsNullOrEmpty(skill.RequiredSkill)) s.Progression.LearnSkill(skill.RequiredSkill);
                int lp = s.Items.Teaching.LearningPointCost(lesson);
                if (s.Hero.LearningPoints < lp) s.Progression.AddLearningPoints(lp - s.Hero.LearningPoints);
            }
            var result = s.Items.Teaching.Teach(lesson);
            if (result == TeachResult.AlreadyKnown) Log($"teach {spell}: already known"); // fix-57: a loadout on a save that has it
            else if (result != TeachResult.Done) Fail($"teach {spell}: {result}");
            else Log($"teach {spell}: learned, carries {s.HeroSpells.ItemOf(spell)} x{s.Items.Inventories.Of(ActorId.Player).Count(s.HeroSpells.ItemOf(spell))}");
        }

        /// <summary>
        /// `spellbook open select slot ready` — opens the spell book as key B does (open=0 closes it), selects a spell, puts it on
        /// quick slot `slot` (1…9, 0; a refusal is logged, not failed) and readies it like Enter (ready=1).
        /// </summary>
        IEnumerator SpellBookStep(ScenarioStep st)
        {
            var screens = FindAnyObjectByType<GameScreens>();
            var page = screens != null ? screens.ByAction(G3.Player.SessionKey.JournalModeMagBook) as G3SpellBookScreen : null;
            if (page == null) { Fail("spellbook: no spell book page (G3 GUI not imported?)"); yield break; }
            HideTips();
            if (!st.Bool("open", true))
            {
                if (screens.Current == page) screens.CloseCurrent();
                Log("spellbook closed");
                yield break;
            }
            if (screens.Current != page) screens.Toggle(G3.Player.SessionKey.JournalModeMagBook);
            yield return new WaitForSecondsRealtime(0.4f);
            if (screens.Current != page) { Fail("spellbook: the page did not open"); yield break; }
            if (st.Has("select"))
            {
                string query = st.Get("select");
                string item = _session.HeroSpells.ItemOf(query) ?? query;
                if (!page.SelectSpell(item)) { item = ResolveItem(query, spellsOnly: true); if (item == null || !page.SelectSpell(item)) { Fail($"spellbook: no spell '{query}' on the page"); yield break; } }
                var sel = page.Selected;
                Log($"spellbook: selected {sel.Item} (learned {sel.Learned}, coloured {sel.Coloured}): {string.Join(", ", System.Linq.Enumerable.Select(sel.Requirements, r => r.Label + " " + r.Value))}");
            }
            if (st.Has("slot"))
            {
                int slot = QuickSlots.SlotOfDigit(st.Int("slot", 1));
                bool ok = page.AssignQuickSlot(slot);
                Log($"spellbook: quick slot {st.Get("slot")} ← {page.Selected?.Item ?? "-"}: {(ok ? "assigned" : "refused")}");
            }
            if (st.Bool("ready", false))
            {
                bool ok = page.Activate();
                Log($"spellbook: ready {page.Selected?.Item ?? "-"}: {(ok ? "readied, page closed" : "refused")}");
                yield return new WaitForSeconds(1.2f);
            }
        }

        /// <summary>
        /// `staffblock secs cast` — in the magic stance presses the block button for secs (default 1) as the right mouse button
        /// does; cast=1 clicks the cast button half-way (the cast leaves the block); logs whether the block came up.
        /// </summary>
        IEnumerator StaffBlockStep(ScenarioStep st)
        {
            if (Combat == null || !Combat.Fighter.SpellDrawn) { Fail("staffblock: no spell drawn"); yield break; }
            float secs = st.Float("secs", 1f);
            bool cast = st.Bool("cast", false);
            Combat.SecondaryDown();
            bool up = false, casted = false;
            float t0 = Time.time;
            while (Time.time - t0 < secs)
            {
                up |= Combat.StaffBlocking;
                if (cast && !casted && Time.time - t0 >= secs / 2)
                {
                    casted = true;
                    Combat.PrimaryDown();
                    yield return new WaitForSeconds(0.1f);
                    Combat.PrimaryUp();
                }
                yield return null;
            }
            bool stillUp = Combat.StaffBlocking;
            Combat.SecondaryUp();
            yield return new WaitForSeconds(0.5f);
            Log($"staffblock: staff in left {Combat.StaffInLeftHand}, block up {up}, still up at release {stillUp}, spell {Combat.ReadiedSpell}, drawn {Combat.Fighter.SpellDrawn}");
        }
    }
}
#endif
