#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using Myrtana.Story;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// Story ops of the chapter walk (Docs/story-walk.md): <c>ask</c> plays one dialog entry through the real dialog,
    /// <c>topics</c> lists what an NPC offers now, <c>loot</c> takes a body or chest through the hero's loot path; story
    /// targets <c>place:</c>, <c>zone:</c>, <c>obj:</c>; conditions <c>given</c>, <c>offered</c>, <c>available</c>,
    /// <c>inarea</c>, <c>present</c>, <c>guide</c>, <c>chapter</c>.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        // ---------------------------------------------------------------- ask

        IEnumerator AskStep(ScenarioStep st)
        {
            var s = _session;
            var npc = Actor(st.Get("npc"));
            string entry = st.Get("entry");
            if (!s.Content.Database.TryGet(new Id<DialogEntryDef>(entry), out DialogEntryDef def)) { Fail($"ask: no dialog entry '{entry}'"); yield break; }
            if (def.Owner != npc && !def.Owner.IsPlayer) Log($"ask: {entry} belongs to {def.Owner}, talking to {npc}");
            var path = StoryScenario.PathTo(entry, id => s.Content.Database.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef d) && !d.Parent.IsEmpty ? d.Parent.Name : null);
            var played = new HashSet<string>(StringComparer.Ordinal);
            string available = Availability(def, npc);

            if (WasGiven(entry) && st.Bool("given", false))
            {
                // given=1: an entry that already ran (a lone sub-choice runs by itself after its parent) counts as played.
                // An NPC that started it by itself (an Important info once the hero stands near) is still talking: the talk
                // runs to its end, else its commands never run (editor-runner-23 Falk BPANKRATZ3235: quest stayed Open)
                if (s.Dialog.IsActive && s.Dialog.Current.Partner == npc) yield return FinishTalk($"ask {npc} {entry}");
                Log($"ask {npc} {entry}: already given");
                yield break;
            }
            if (s.Dialog.IsActive && s.Dialog.Current.Partner != npc) yield return FinishTalk($"ask {npc} {entry}");
            if (!s.Dialog.IsActive)
            {
                s.Dialog.Talk(npc);
                yield return null;
            }
            bool givenBefore = WasGiven(entry);
            _askUntil = null;
            if (st.Has("until") && !ScenarioCondition.TryParse(st.Get("until"), out _askUntil, out string untilError)) { Fail("ask: " + untilError); yield break; }
            for (int i = 0, talks = 1; ; )
            {
                yield return PlayLines(played);
                var c = s.Dialog.Current;
                bool choosing = c != null && c.Phase == ConversationPhase.AwaitingChoice;
                i = StoryScenario.NextChoice(path, i, n => choosing && c.Options.Any(o => o.Entry.Name == n), n => played.Contains(n) || WasGiven(n), out string why);
                if (i >= path.Count) break;
                if (i < 0 && talks < 3 && !played.Contains(entry))
                {
                    // the talk ended by itself (an urgent info that ends the dialog) or shows an older menu: talk again,
                    // as the player would
                    talks++;
                    i = 0;
                    if (s.Dialog.IsActive) { s.Dialog.Abort(); yield return null; }
                    s.Dialog.Talk(npc);
                    yield return null;
                    continue;
                }
                if (i < 0)
                {
                    Fail($"ask {entry}: {why} (available before talk: {available}; {DialogState()})");
                    yield break;
                }
                s.Dialog.Choose(new Id<DialogEntryDef>(path[i]));
                played.Add(path[i]);
                i++;
                yield return null;
            }
            yield return PlayLines(played);
            if (st.Bool("end", true) && s.Dialog.IsActive)
            {
                var exits = s.Dialog.Current.Phase == ConversationPhase.AwaitingChoice ? s.Dialog.Current.Options.Where(o => o.IsExit).ToList() : new List<DialogOption>();
                if (exits.Count > 0)
                {
                    s.Dialog.Choose(exits[0].Entry);
                    yield return PlayLines(played);
                }
                if (s.Dialog.IsActive) s.Dialog.Abort();
                yield return null;
            }
            _askUntil = null;
            // a sub-choice without lines that ran by itself after its parent shows no line to see it by: its memory tells
            if (!givenBefore && WasGiven(entry)) played.Add(entry);
            if (played.Contains("refused:" + entry)) Fail($"ask {entry}: refused by its pre-check (fame, gold, lesson or party room)");
            else if (!played.Contains(entry)) Fail($"ask {entry}: " + (givenBefore ? "already played earlier" : "not played") + $" (available before talk: {available}; played {string.Join(",", played)})");
            else Log($"ask {npc} {entry}: played [{string.Join(",", played)}] {DialogState()}");
        }

        /// <summary>
        /// Ends an open talk as a player would: its lines run to the end (the info's commands with them), then the exit
        /// topic; only a talk still open after that is aborted. An abort remembers the info as given without its commands
        /// (editor-runner-18), so nothing an NPC started by itself is cut off before a tp or another ask.
        /// With <paramref name="passGuards"/> (walkto, escort, draw; fix-53) the hero answers as somebody who wants to
        /// pass: a gate guard's warning gets the answer that switches the guard off (<see cref="UnaskedTalk"/>).
        /// </summary>
        internal IEnumerator FinishTalk(string why, bool passGuards = false)
        {
            var s = _session;
            if (!s.Dialog.IsActive) yield break;
            var partner = s.Dialog.Current.Partner;
            var played = new HashSet<string>(StringComparer.Ordinal);
            var until = _askUntil;
            _askUntil = null;
            yield return PlayLines(played, 20f);
            for (int round = 0; round < UnaskedTalk.MaxChoices && s.Dialog.IsActive && s.Dialog.Current.Phase == ConversationPhase.AwaitingChoice; round++)
            {
                var options = s.Dialog.Current.Options;
                int pick = passGuards
                    ? UnaskedTalk.Pick(options.Select(TalkChoiceOf).ToList())
                    : options.ToList().FindIndex(o => o.IsExit);
                if (pick < 0) break;
                if (!options[pick].IsExit) played.Add("answer:" + options[pick].Entry.Name);
                s.Dialog.Choose(options[pick].Entry);
                yield return PlayLines(played, 20f);
                if (!passGuards) break; // as before: one exit
            }
            bool aborted = s.Dialog.IsActive;
            if (aborted) { s.Dialog.Abort(); yield return null; }
            _askUntil = until;
            Log($"{why}: finished the open talk with {partner} [{string.Join(",", played)}]{(aborted ? " (aborted at a choice)" : "")}");
        }

        /// <summary>A dialog choice as <see cref="UnaskedTalk"/> sees it: End, switches a gate guard off, starts a fight.</summary>
        TalkChoice TalkChoiceOf(DialogOption option)
        {
            if (!_session.Content.Database.TryGet(option.Entry, out DialogEntryDef def)) return new TalkChoice(option.IsExit, false, false);
            return new TalkChoice(option.IsExit, def.Commands.Any(c => c is SetGuardStatus g && !g.Active), def.Commands.Any(c => c is StartFight));
        }

        /// <summary>
        /// fix-53: an NPC talks to the hero by itself (a gate guard's warning, an Important info) while a runner op moves
        /// him. The talk locks his controls, so the op answers it like a player first; <paramref name="spent"/> gets its time.
        /// </summary>
        /// <remarks>
        /// fix-54: one dialog handler. The answer comes from <see cref="DialogAutoAnswer.Pick"/> (the step's answer=, the
        /// scenario's answers, the gate-guard answer of fix-53, the first non-END topic, END), as in the per-frame handler
        /// (<see cref="DialogTick"/>, which waits while this runs); 30 s without the end: force-closed with a BUG line.
        /// </remarks>
        IEnumerator AnswerUnaskedTalk(string op, Action<float> spent)
        {
            var s = _session;
            if (!s.Dialog.IsActive) yield break;
            Hero.scriptedMove = Vector2.zero;
            float t = Time.time;
            var partner = s.Dialog.Current.Partner;
            string why = $"{op}: {partner} talked to the hero";
            DialogSeen(s.Dialog.Current);
            Log($"{why}{ControlsNote()}: {DialogText(s.Dialog.Current)}");
            var played = new List<string>();
            int choices = 0;
            bool aborted = false;
            _dlgAnswering = true;
            try
            {
                while (s.Dialog.IsActive)
                {
                    var c = s.Dialog.Current;
                    if (c.Entry != null && (played.Count == 0 || played[played.Count - 1] != c.Entry.Id.Name)) played.Add(c.Entry.Id.Name);
                    if (Time.time - t >= DialogAutoAnswer.ForceCloseSeconds)
                    {
                        ForceCloseDialog(c, op);
                        aborted = true;
                        break;
                    }
                    if (c.Phase == ConversationPhase.AwaitingLine) s.Dialog.CompleteLine();
                    else if (c.Phase == ConversationPhase.AwaitingPage) CloseDialogPage();
                    else if (c.Phase == ConversationPhase.AwaitingChoice)
                    {
                        if (choices++ >= DialogAutoAnswer.MaxChoices || !ChooseFor(c, out string answer))
                        {
                            s.Dialog.Abort();
                            aborted = true;
                            break;
                        }
                        if (answer != null) played.Add("answer:" + answer);
                    }
                    yield return null;
                }
            }
            finally { _dlgAnswering = false; }
            Log($"{why}: finished the open talk with {partner} [{string.Join(",", played)}]{(aborted ? " (aborted at a choice)" : "")}");
            spent?.Invoke(Time.time - t);
        }

        /// <summary>Completes lines (recording every entry that runs) until a choice or the end of the talk.</summary>
        IEnumerator PlayLines(HashSet<string> played, float timeout = 60f)
        {
            var s = _session;
            float t0 = Time.time;
            while (s.Dialog.IsActive && Time.time - t0 < timeout)
            {
                var c = s.Dialog.Current;
                if (c.Entry != null && c.Purpose == ScriptPurpose.Refusal) played?.Add("refused:" + c.Entry.Id.Name);
                else if (c.Entry != null) played.Add(c.Entry.Id.Name);
                if (_askUntil != null && _askUntil.Test(Evaluate(_askUntil)))
                {
                    Log($"ask: {_askUntil} holds, talk aborted");
                    s.Dialog.Abort();
                    yield break;
                }
                if (c.Phase == ConversationPhase.AwaitingChoice) yield break;
                if (c.Phase == ConversationPhase.AwaitingLine) s.Dialog.CompleteLine();
                else if (c.Phase == ConversationPhase.AwaitingPage) CloseDialogPage();
                yield return null;
            }
        }

        /// <summary>fix-140: the talk waits for its page (trade): the runner closes it as a player would, the talk goes on.</summary>
        void CloseDialogPage()
        {
            var screens = FindAnyObjectByType<GameScreens>();
            if (screens != null && screens.IsDialogPage)
            {
                Log($"dialog page {screens.Current.GetType().Name} closed by the runner");
                screens.CloseCurrent();
            }
        }

        ScenarioCondition _askUntil;

        bool WasGiven(string entry)
        {
            var world = StoryWorldOf();
            return world != null && world.DialogMemory.WasGiven(new Id<DialogEntryDef>(entry));
        }

        // ---------------------------------------------------------------- topics / availability

        StoryWorld StoryWorldOf() =>
            typeof(DialogService).GetField("_world", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_session.Dialog) as StoryWorld;

        TopicListBuilder _topicBuilder;

        bool TryAvailable(DialogEntryDef def, ActorId partner, out bool available)
        {
            available = false;
            var world = StoryWorldOf();
            if (world == null) return false;
            if (_topicBuilder == null) _topicBuilder = new TopicListBuilder(new DialogCatalog(_session.Content.Database.All<DialogEntryDef>()), world.DialogMemory);
            available = _topicBuilder.IsAvailable(def, world.ConditionsFor(def.Owner.IsPlayer ? partner : def.Owner));
            return true;
        }

        string Availability(DialogEntryDef def, ActorId partner)
        {
            var world = StoryWorldOf();
            if (world == null || !TryAvailable(def, partner, out bool ok)) return "?";
            if (ok) return "yes";
            if (!def.Repeatable && world.DialogMemory.WasGiven(def.Id)) return "no, already given";
            var why = ConditionExplain.FalseLeaves(def.Condition, world.ConditionsFor(def.Owner.IsPlayer ? partner : def.Owner));
            return "no, condition false" + (why.Count > 0 ? ": " + string.Join("; ", why) : "");
        }

        void PlayedStep(ScenarioStep st)
        {
            var world = StoryWorldOf();
            if (world == null) { Fail("played: no story world"); return; }
            var done = new List<string>();
            foreach (string id in StoryScenario.List(st.Get("entries")))
            {
                if (!_session.Content.Database.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef def)) { Fail("played: no entry " + id); continue; }
                world.DialogMemory.MarkGiven(def.Id);
                if (!def.Owner.IsPlayer) world.Actors.MarkTalkedToPlayer(def.Owner);
                done.Add(id);
            }
            Log("played " + string.Join(",", done));
        }

        void TalkedStep(ScenarioStep st)
        {
            var world = StoryWorldOf();
            if (world == null) { Fail("talked: no story world"); return; }
            foreach (string npc in StoryScenario.List(st.Get("npcs"))) world.Actors.MarkTalkedToPlayer(Actor(npc));
            Log("talked " + st.Get("npcs"));
        }

        void TopicsStep(ScenarioStep st)
        {
            var npc = Actor(st.Get("npc"));
            var all = _session.Content.Database.All<DialogEntryDef>().Where(e => e.Owner == npc).OrderBy(e => e.Order).ToList();
            var now = all.Where(e => TryAvailable(e, npc, out bool a) && a).Select(e => e.Id.Name + "(" + e.Kind + ")").ToList();
            Log($"topics {npc}: {all.Count} entries, available now [{string.Join(",", now)}]");
        }

        // ---------------------------------------------------------------- loot

        IEnumerator LootStep(ScenarioStep st)
        {
            var s = _session;
            string target = st.Get("target");
            var use = FindAnyObjectByType<HeroObjectUse>();
            if (use == null) { Fail("loot: no HeroObjectUse in the scene"); yield break; }
            string objectName = target.StartsWith("obj:", StringComparison.OrdinalIgnoreCase) ? target.Substring(4) : null;
            if (objectName == null && s.Npcs.TryGet(Actor(target), out _))
            {
                var npc = Actor(target);
                if (!use.IsBody(npc)) { Fail($"loot {npc}: not down/dead ({s.Combat.Vitals.StateOf(npc)})"); yield break; }
                bool ok = use.QuickLootBody(npc);
                yield return new WaitForSeconds(0.3f);
                if (!ok) Fail($"loot {npc}: refused (busy?)");
                else Log($"loot {npc}: done");
                yield break;
            }
            if (!TryObject(objectName ?? target, out var obj)) { Fail($"loot: no NPC or object '{target}'"); yield break; }
            var objects = s.Interaction.Objects;
            if (objects.IsLocked(obj.Id))
            {
                if (obj.Lock?.Key != null && s.Items.Inventories.Of(ActorId.Player).Count(obj.Lock.Key) == 0) Log($"loot {obj.Name}: locked, key {obj.Lock.Key} not carried");
                var r = objects.Unlock(obj.Id);
                Log($"loot {obj.Name}: unlock {r.Verdict} opened={r.Opened}");
            }
            if (objects.IsLocked(obj.Id)) { Fail($"loot {obj.Name}: still locked ({objects.CheckLock(obj.Id)})"); yield break; }
            // fix-105 (r40 fix99: "+0 items" from a chest in an outlaw camp): G3 refuses the loot during a fight (NotDuringBattle)
            if (s.NpcCombat.IsPlayerInCombat) { Fail($"loot {obj.Name}: refused, the hero is in a fight ({FightersOfHero()})"); yield break; }
            var before = s.Items.Inventories.Of(ActorId.Player).Stacks.Sum(x => x.Amount);
            bool done = use.QuickLoot(obj.Id);
            yield return new WaitForSeconds(0.3f);
            var after = s.Items.Inventories.Of(ActorId.Player).Stacks.Sum(x => x.Amount);
            if (!done) Fail($"loot {obj.Name}: refused (no chest view near the hero? tp obj:{obj.Name} first)");
            else Log($"loot {obj.Name}: +{after - before} items");
        }

        /// <summary>The NPCs fighting the hero now (fix-105: why a use or loot was refused "in combat").</summary>
        string FightersOfHero()
        {
            var list = _session.NpcCombat.Brains.Where(b => b.State.Target.IsPlayer && (b.State.Phase == CombatPhase.Fight || b.State.Phase == CombatPhase.Finishing))
                               .Select(b => b.Self.ToString()).ToList();
            return list.Count == 0 ? "none" : string.Join(",", list);
        }

        IEnumerator UseStep(ScenarioStep st)
        {
            string target = st.Get("target");
            if (target.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
            {
                // the hero uses up an inventory item (drinks, eats, reads) as from the inventory
                string item = target.Substring(5);
                if (!_session.Items.Use.Use(item)) Fail($"use {item}: refused (have {_session.Items.Inventories.Of(ActorId.Player).Count(item)})");
                else Log($"use {item}: done");
                yield break;
            }
            string name = target.StartsWith("obj:", StringComparison.OrdinalIgnoreCase) ? target.Substring(4) : target;
            var use = FindAnyObjectByType<HeroObjectUse>();
            if (use == null) { Fail("use: no HeroObjectUse in the scene"); yield break; }
            if (!TryObject(name, out var obj)) { Fail($"use: no object '{name}'"); yield break; }
            // fix-63 (r31 object_anims: the Faring anvil is 68 m from the chest): right after a tp the object's view may not
            // be shown yet (views within 40 m, updated every 0.5 s); a player could not use it before either. Wait for it.
            float viewWait = Time.unscaledTime + 3f;
            while (use.UseRefusal(obj.Id) == "no view" && Time.unscaledTime < viewWait) yield return null;
            string refusal = use.UseRefusal(obj.Id);
            if (refusal == "occupied" && st.Bool("free", false))
            {
                // fix-188 (par_misc: the Faring drum held by a drummer at noon): free=1 removes the NPCs holding the object
                var users = _session.Npcs.Claims.UsersOf(new Id<RoutinePointDef>(obj.Id));
                foreach (var u in users) _session.Removal.Remove(u);
                Log($"use {obj.Name}: occupied by {string.Join(",", users)}: removed (free=1)");
                yield return null;
                refusal = use.UseRefusal(obj.Id);
            }
            if (refusal != null || !use.Use(obj.Id))
            {
                Fail($"use {obj.Name}: refused ({refusal ?? "busy"}{(refusal == "no view" ? $"; tp obj:{obj.Name} first" : "")}{(refusal == "in combat" ? ": " + FightersOfHero() : "")})");
                yield break;
            }
            float until = Time.time + 60f;
            // a seat keeps the hero busy until he gets up (op standup): done once he sits (par-interact)
            // fix-77: a bed is done once its sleep dialog is open (the scenario chooses: op choose)
            // fix-103: a shrine is done once its dialog is open (the scenario chooses or aborts: the use ends after it)
            while (use.Busy && Time.time < until && !(obj.Kind == InteractiveKind.Seat && _session.Regeneration.Sitting) && !use.InBedDialog &&
                   !(obj.Kind == InteractiveKind.Speaker && _session.Dialog.IsActive) &&
                   !(obj.Kind == InteractiveKind.Activity && use.InActivity) && !use.StationLooping && !ChestPageOpen()) yield return null;
            // fix-61: a station is done once its action loop runs, a chest once its loot page is open (the page stays open:
            // op closepage ends the use)
            if (use.StationLooping || ChestPageOpen())
            {
                yield return null;
                Log($"use {obj.Name}: station loop running, page {FindAnyObjectByType<GameScreens>()?.Current?.GetType().Name ?? "-"}, object {use.ObjectPart}");
                yield break;
            }
            if (obj.Kind == InteractiveKind.Seat && _session.Regeneration.Sitting) { Log($"use {obj.Name}: seated"); yield break; }
            if (obj.Kind == InteractiveKind.Speaker && _session.Dialog.IsActive) { Log($"use {obj.Name}: praying, dialog open ({DialogState()})"); yield break; }
            if (use.InBedDialog) { Log($"use {obj.Name}: lying, sleep dialog open ({DialogState()})"); yield break; }
            // an activity (water pipe, saw log, orc drum) loops until the hero leaves it (op standup): done once it runs (par-misc)
            if (obj.Kind == InteractiveKind.Activity && use.InActivity)
            {
                // fix-46 (r28 par_misc): InActivity is set before the sit-down / Begin motion; the op returned in the frame the
                // use started, so 'expect key~Waterpipe' saw SitGround_Begin and 'expect cammode=Inventory' ran before the
                // camera's LateUpdate. Done once the action loop runs, plus a frame for the camera.
                while (use.InActivity && !use.ActivityLooping && Time.time < until) yield return null;
                yield return null;
                Log($"use {obj.Name}: activity running{(use.ActivityLooping ? "" : " (loop not reached)")}");
                yield break;
            }
            if (use.Busy) Fail($"use {obj.Name}: still busy after 60 s");
            else Log($"use {obj.Name}: done");
        }

        bool ChestPageOpen() => FindAnyObjectByType<GameScreens>()?.Current is G3LootScreen;

        /// <summary>fix-61: closes the open page (a station's craft/sharpen page) and waits for the use to end (End motion).</summary>
        IEnumerator ClosePageStep()
        {
            var screens = FindAnyObjectByType<GameScreens>();
            if (screens == null || screens.Current == null) { Fail("closepage: no page open"); yield break; }
            string page = screens.Current.GetType().Name;
            screens.CloseCurrent();
            var use = FindAnyObjectByType<HeroObjectUse>();
            float until = Time.time + 10f;
            while (use != null && use.Busy && Time.time < until) yield return null;
            if (use != null && use.Busy) Fail($"closepage {page}: the use still runs after 10 s");
            else Log($"closepage {page}: done, object {use?.ObjectPart ?? "-"}");
        }

        /// <summary>fix-111: the open loot page's "Take all" button (it closes the page), then waits for the use to end.</summary>
        IEnumerator TakeAllStep()
        {
            if (!(FindAnyObjectByType<GameScreens>()?.Current is G3LootScreen loot)) { Fail("takeall: no loot page open"); yield break; }
            int before = _session.Items.Inventories.Of(ActorId.Player).Stacks.Sum(x => x.Amount);
            loot.TakeAllAndClose();
            var use = FindAnyObjectByType<HeroObjectUse>();
            float until = Time.time + 10f;
            while (use != null && use.Busy && Time.time < until) yield return null;
            int after = _session.Items.Inventories.Of(ActorId.Player).Stacks.Sum(x => x.Amount);
            if (use != null && use.Busy) Fail("takeall: the use still runs after 10 s");
            else Log($"takeall: +{after - before} items");
        }

        /// <summary>par-interact: the hero gets up from a seat.</summary>
        IEnumerator StandUpStep()
        {
            var use = FindAnyObjectByType<HeroObjectUse>();
            if (use == null || !use.StandUp()) { Fail("standup: the hero does not sit"); yield break; }
            float until = Time.time + 10f;
            while (use.Busy && Time.time < until) yield return null;
            if (use.Busy) Fail("standup: still busy after 10 s");
            else Log("standup: done");
        }

        /// <summary>par-interact: eat or drink with the motion (HeroConsumption), as from the inventory.</summary>
        IEnumerator EatStep(ScenarioStep st)
        {
            string item = st.Get("item");
            var eat = HeroConsumption.Instance;
            int before = _session.Items.Inventories.Of(ActorId.Player).Count(item);
            bool drawn = Combat != null && Combat.Fighter.Animator.Drawn;
            if (eat == null || !eat.Consume(item)) { Fail($"eat {item}: refused (have {before})"); yield break; }
            float until = Time.time + 10f;
            bool stowed = eat.HandsStowed, inHandMeanwhile = false;
            while (eat.Busy && Time.time < until)
            {
                stowed |= eat.HandsStowed;
                inHandMeanwhile |= Combat != null && Combat.Fighter.Animator.WeaponInHand;
                yield return null;
            }
            int after = _session.Items.Inventories.Of(ActorId.Player).Count(item);
            bool drawnAfter = Combat != null && Combat.Fighter.Animator.Drawn;
            if (after >= before) Fail($"eat {item}: not used ({before} -> {after})");
            else Log($"eat {item}: {before} -> {after}{(drawn ? $", weapon stowed {stowed}, in the hand meanwhile {inHandMeanwhile}, drawn after {drawnAfter}" : "")}");
            // fix-68: with a weapon drawn G3 takes the item into the hand in its place and gives the weapon back after.
            if (drawn && (!stowed || inHandMeanwhile || !drawnAfter))
                Fail($"eat {item} with the weapon drawn: stowed={stowed} inHandMeanwhile={inHandMeanwhile} drawnAfter={drawnAfter} (G3: weapon out of the hand while drinking, back after)");
        }

        /// <summary>par-interact: Use on a helpless human (Perk_MasterThief, sneaking); the loot page is emptied and closed.</summary>
        IEnumerator RobStep(ScenarioStep st)
        {
            var npc = Actor(st.Get("npc"));
            var use = FindAnyObjectByType<HeroObjectUse>();
            if (use == null) { Fail("rob: no HeroObjectUse"); yield break; }
            if (!use.CanRobSleeper(npc))
            {
                Fail($"rob {npc.Name}: not allowed (helpless={_session.SleeperTheft.IsHelpless(npc)} humanoid={_session.SleeperTheft.IsHumanoid(npc)} " +
                     $"sneaking={Hero.Sneaking} perk={_session.Hero.Knows(PickpocketRules.MasterThiefPerk)})");
                yield break;
            }
            int before = _session.Items.Inventories.Of(ActorId.Player).Stacks.Sum(x => x.Amount);
            use.RobSleeper(npc);
            yield return new WaitForSeconds(0.5f);
            if (use.Busy) _session.Items.NpcLoot.TakeAll(npc);
            FindAnyObjectByType<GameScreens>()?.CloseCurrent();
            float until = Time.time + 5f;
            while (use.Busy && Time.time < until) yield return null;
            int after = _session.Items.Inventories.Of(ActorId.Player).Stacks.Sum(x => x.Amount);
            Log($"rob {npc.Name}: hero items {before} -> {after}");
        }

        /// <summary>
        /// fix-113: the Use key on a character, as PlayerInteraction routes it (loot a body, rob a sleeper, talk); logs what
        /// came of it (dialog / page). Leaves a page or dialog open for the next steps.
        /// </summary>
        IEnumerator UseNpcStep(ScenarioStep st)
        {
            var npc = Actor(st.Get("npc"));
            var interaction = FindAnyObjectByType<PlayerInteraction>();
            if (interaction == null) { Fail("usenpc: no PlayerInteraction"); yield break; }
            if (_session.Dialog.IsActive) _session.Dialog.Abort();
            var theft = _session.SleeperTheft;
            string before = $"helpless={theft.IsHelpless(npc)} spellbound={theft.IsSpellbound(npc)} use={theft.UseOn(npc, Hero.Sneaking)}";
            interaction.UseCharacter(npc);
            yield return new WaitForSeconds(0.5f);
            string page = FindAnyObjectByType<GameScreens>()?.Current?.GetType().Name ?? "-";
            Log($"usenpc {npc.Name}: {before} -> dialog={_session.Dialog.IsActive} page={page} theft={theft.IsTheftVictim(npc)}");
        }

        void PickupStep(ScenarioStep st)
        {
            var s = _session;
            string query = st.Get("item");
            float radius = st.Float("radius", 15f);
            var hp = Hero != null ? Hero.transform.position : Vector3.zero;
            bool Match(string item) => item != null && (string.Equals(item, query, StringComparison.OrdinalIgnoreCase) || item.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            // placed world items still lying there, plus items dropped during the game (drop:N)
            var placed = s.Content.Database.All<WorldItemDef>()
                          .Where(w => w.Position != null && Match(w.Item) && s.Items.World.IsPresent(w.Id))
                          .Select(w => (id: w.Id, item: w.Item, pos: w.Position));
            var dropped = s.Items.World.AllDropped.Where(d => Match(d.Item)).Select(d => (id: d.Id, item: d.Item, pos: d.Position));
            var best = placed.Concat(dropped)
                        .Select(w => (w, d: (new Vector3(w.pos[0], w.pos[1], w.pos[2]) - hp).magnitude))
                        .OrderBy(x => x.d).FirstOrDefault();
            if (best.w.id == null) { Fail($"pickup {query}: no world item present"); return; }
            if (best.d > radius) { Fail($"pickup {best.w.item}: nearest is {F(best.d)} m away ({F0(best.w.pos[0])},{F0(best.w.pos[1])},{F0(best.w.pos[2])})"); return; }
            int before = s.Items.Inventories.Of(ActorId.Player).Count(best.w.item);
            bool ok = s.Items.World.PickUp(best.w.id);
            int after = s.Items.Inventories.Of(ActorId.Player).Count(best.w.item);
            if (!ok) Fail($"pickup {best.w.item}: refused");
            else Log($"pickup {best.w.item} ({best.w.id}) at {F(best.d)} m: {before} -> {after}");
        }

        IEnumerator RaidStep(ScenarioStep st)
        {
            var s = _session;
            string enclave = st.Get("enclave");
            int max = st.Int("max", 300), kills = 0;
            var facts = s.Crime.Attitudes.Facts;
            if (s.Enclaves.Of(enclave).Status == EnclaveStatus.Routine)
            {
                s.Enclaves.SetStatus(enclave, EnclaveStatus.Attack); // the alarm a fight in sight raises
                Log($"raid {enclave}: alarm -> Attack");
            }
            var defenders = s.Content.Database.All<NpcDef>()
                             .Where(n => facts.TryGetProfile(n.Id, out var p) && p.Enclave == enclave && s.Crime.Attitudes.LoyalToEnclave(p))
                             .Where(n => s.Npcs.TryGet(n.Id, out var a) && s.Npcs.IsInWorld(a))
                             .Select(n => n.Id).ToList();
            foreach (var id in defenders)
            {
                if (Freed(enclave) || kills >= max) break;
                if (s.Combat.Vitals.StateOf(id) == VitalState.Dead) continue;
                s.Combat.Vitals.Kill(id, true);
                kills++;
                yield return new WaitForSeconds(0.25f); // the liberation check runs once a second
            }
            float t0 = Time.time;
            while (!Freed(enclave) && Time.time - t0 < 3f) yield return null;
            string line = $"raid {enclave}: {kills}/{defenders.Count} defenders killed, status {s.Enclaves.Of(enclave).Status}" +
                          (s.Enclaves.IsSwitched(enclave) ? " (switched)" : "");
            if (!Freed(enclave)) Fail(line); else Log(line);
        }

        /// <summary>
        /// <c>escort npc to=target [near=5] [secs=600] [walk=0]</c>: the hero travels to the target together with the NPC, as a
        /// player would, always along a NavMesh path (<see cref="EscortPath"/>: the NavMesh agent of the cell NavMesh,
        /// whose radius / height / max slope match the hero's controller; never straight at a slope). A guide
        /// (FollowNpc, EscortMode.Guide) walks ahead on its own and the hero follows the path to the guide, closing up when
        /// more than 6 m behind and waiting when closer than 3 m; any other NPC (a follower in the party) is led: the hero
        /// runs along the path to the target and then steps past it until the NPC is within near= m of it. The path is
        /// re-planned every second or when its end moved more than 2 m; where the target's cell has no NavMesh yet, it ends
        /// at the farthest NavMesh point on the way (a partial path). Stuck (less than 1 m of progress): after 2 s it
        /// re-plans from where the hero is, after 4 s and 6 s it tries a 3 m detour to the left and then to the right, from
        /// 8 s it jumps, after 10 s it hops 6 m along the path; stuck again near the last hop, it teleports 15 m ahead
        /// along the path (last resort). Every stuck stage, path length and teleport is logged; no randomness.
        /// Done when the NPC is within near= m of the target; on timeout it fails with the NPC's distance to the hero and
        /// to the target. A hero on a NavMesh island (no complete path to the NPC: editor-runner-20, gotha_02 "partial path
        /// 2.0 m" for 204 plans) is first put on the nearest NavMesh point connected to the NPC's (<see cref="EscortPath.Connected"/>),
        /// at the start and whenever the path stays partial while he stands at its end. A guide that waits for the hero while
        /// he stays more than 6 m away for <see cref="GuideWaitTeleportSeconds"/> s (below a ledge after a defend) has him
        /// teleported next to it (logged).
        /// </summary>
        IEnumerator EscortStep(ScenarioStep st)
        {
            var s = _session;
            var id = Actor(st.Get("npc"));
            string to = st.Get("to");
            float near = st.Float("near", 5f), limit = st.Float("secs", 600f);
            if (!TryTarget(to, out var goal, out bool hasY, out _, out _, out var err)) { Fail("escort: " + err); yield break; }
            if (!TryActorPosition(id, out _, out _)) { Fail("escort: no NPC " + id); yield break; }
            if (_frozen.Remove(id))
            {
                // a frozen NPC never walks (editor-runner-23: followers stood still, an idle guide stayed): the escort releases it
                _calm.Remove(id);
                Log($"escort {id}: was frozen, released");
            }
            if (s.Dialog.IsActive) s.Dialog.Abort();
            var mode = s.Party.Escorts.ModeOf(id);
            Hero.scriptedWalk = st.Bool("walk", false);
            // fix-190: route=0 walks the NavMesh only (a scenario leading round a camp by its own waypoints); rejoin=entry is
            // the info a player asks the guide when its FollowNpc quest got Lost (G3 NotifyEnclave; CP Wenzel NEWM0121)
            bool useRoute = st.Bool("route", true), routeDropped = false;
            string rejoin = st.Get("rejoin");
            int rejoins = 0;
            var nav = new EscortPath(Hero.GetComponent<CharacterController>());
            Log($"escort {id}: {nav.Agent}");
            // fix-34: the hero's G3 route (the guide's own trail, or the graph's route to the target), its blocked spots and
            // teleports (anti-loop: 3 teleports in 60 s fail the step)
            var route = new HeroRoute();
            var routePts = new List<Vector3>();
            float lastRoutePlan = -10f, loggedRouteLen = -100f;
            string loggedRouteNote = null;
            bool looped = false;
            Vector3 routeGoal = goal;
            void PlanRoute(Vector3 from, Vector3 to2)
            {
                lastRoutePlan = Time.time;
                routeGoal = to2;
                string source = null, note = null;
                if (mode == EscortMode.Guide && TryActorPosition(id, out _, out var gv0) && gv0 != null &&
                    gv0.TryGetComponent<NpcView>(out var gview) && gview.TryGuideTrail(routePts))
                    source = $"{id.Name}'s G3 route";
                else if (G3NavGraphRuntime.TryRoute(from, to2, routePts, out _, out note))
                    source = "G3 route to " + (mode == EscortMode.Guide ? id.Name : to);
                if (source == null)
                {
                    route.ClearRoute();
                    if (note != loggedRouteNote) { loggedRouteNote = note; LogRoute($"hero route: none ({note}), NavMesh only"); }
                    return;
                }
                route.SetRoute(routePts.Select(HP).ToList(), HP(from));
                float len = route.WayLeft(HP(from));
                if (Mathf.Abs(len - loggedRouteLen) > 10f)
                {
                    loggedRouteLen = len;
                    LogRoute($"hero route: {F0(len)} m via G3 graph ({source}, {routePts.Count} points, on at point {route.Next}, {route.Blocked.Count} blocked spots)");
                }
            }
            // a teleport of the hero on this step; the third within 60 s without progress fails the step (the hero loops).
            // Progress (fix-46) = the hero's flat way to the target after the teleport shrank by HeroRoute.LoopProgress.
            bool TeleportLoops(Vector3 from, string what)
            {
                if (!route.RecordTeleport(Time.time, Flat(Hero.transform.position - goal))) return false;
                TryActorPosition(id, out var gp, out _);
                Fail($"escort {id}: {HeroRoute.MaxTeleports} teleports within {F0((float)HeroRoute.TeleportWindowSeconds)} s without {F0(HeroRoute.LoopProgress)} m progress (last: {what}), " +
                     $"the hero loops: hero at {V(from)}, step '{st.Source}', {id.Name} at {V(gp)} ({F(Flat(gp - goal))} m from the target, " +
                     $"{F(Flat(gp - from))} m from the hero), blocked spots {string.Join(" ", route.Blocked)}");
                looped = true;
                return true;
            }
            float lastReconnect = -10f;
            bool Reconnect(string why)
            {
                lastReconnect = Time.time;
                if (!TryActorPosition(id, out var npcAt, out _)) return false;
                var from = Hero.transform.position;
                var p = nav.Connected(from, npcAt, out string note);
                if (p == null) { if (why == "start") return false; Log($"escort {id}: {why}, no connected NavMesh point near the hero ({note})"); return false; }
                if (Flat(p.Value - from) < 0.5f) return false;
                Hero.Teleport(p.Value + Vector3.up * 0.15f);
                Log($"escort {id}: {why}: hero on a NavMesh island ({note}), put on the connected NavMesh {V(from)} -> {V(p.Value)} ({F(Flat(p.Value - from))} m)");
                return true;
            }
            Reconnect("start");
            // fix-37: the step's time grows with the NPC's G3 route (secs= was written for the NavMesh way; Mort's G3 route
            // to FP_Wolfclan is 812 m for 225 m as the crow flies)
            if (TryActorPosition(id, out var npcStart, out _) &&
                G3NavGraphRuntime.TryRoute(npcStart, goal, routePts, out float routeLen, out _) && routeLen / EscortRouteSpeed + 60f > limit)
            {
                float was = limit;
                limit = routeLen / EscortRouteSpeed + 60f;
                Log($"escort {id}: G3 route {F0(routeLen)} m, step time {F0(was)} -> {F0(limit)} s");
            }
            routePts.Clear();
            float t0 = Time.time, lastPlan = -10f, progressAt = Time.time, detourUntil = -1f, lastJump = -1f;
            int hops = 0, past = 0, stuckEvents = 0, stage = 0, detours = 0, teleports = 0;
            Vector3 lastPos = Hero.transform.position, progressRef = lastPos, plannedTo = goal, detourAim = default, progressDest = goal;
            float progressLeft = float.MaxValue; // the path left at the last progress (detours and slides along a slope do not shorten it)
            Vector3? lastHop = null;
            float travelled = 0f;
            bool arrived = false, waiting = false, onRouteLegs = true;
            bool movingTarget = to.StartsWith("npc:", StringComparison.OrdinalIgnoreCase);
            int defended = 0, partialStalls = 0;
            int pastTry = 0;            // fix-186: the turn of the spot past the target tried now (-1: none passes, stand)
            Vector3? pastSpot = null;
            var guideAtStart = s.Party.Members.Guide;
            string guideQuest = guideAtStart != null && guideAtStart.Npc == id ? guideAtStart.Quest : null;
            float nextAtTargetFightLog = 0f;
            float nextDefendCheck = 0f, defendTime = 0f, guideWaitSince = -1f, downSince = -1f, downSeenAt = -1f, paused = 0f;
            // per attacker: defend fights so far; attackers the hero gave up on (editor-runner-22: endless defends of a wolf
            // below in a bush, of OutElite_01 while a Karlen dialog locked the hero)
            var attempts = new Dictionary<ActorId, int>();
            var givenUp = new HashSet<ActorId>();
            var guideHpAtGiveUp = new Dictionary<ActorId, int>(); // the escorted NPC's hp when the hero gave a foe up
            float nextGivenUpCheck = 0f;
            // escort clock pause (s), up to MaxPauseFactor x the step's time in all
            float Pause(float secs)
            {
                float p = Mathf.Clamp(limit * MaxPauseFactor - paused, 0f, secs);
                paused += p;
                return p;
            }
            // fix-46: who hits the escorted NPC (the death line said "attacked by nobody": the killer died in the same exchange
            // or was idle again) and the runner's top-up of a low guide (EscortWatch: G3 has no guide protection)
            var watch = new EscortWatch();
            // fix-57: a teleport next to the guide is checked a moment later (r31 hammerclan_02: the hero fell 31 m off Mort's
            // ledge right after "teleport #1 ... next to Mort"); spots he fell from are not used again in this step
            _fellSpots.Clear();
            float landCheckAt = -1f, clearSince = Time.time;
            Vector3 landSpot = default;
            string landWhat = null;
            void Landed(Vector3 spot, string what)
            {
                landSpot = spot;
                landWhat = what;
                landCheckAt = Time.time + 0.8f;
            }
            float keepRadius = ScenarioPacing.KeepDrawnRadiusFor(_style);
            _escortHits?.Dispose();
            _escortHits = s.Events.Subscribe<HitResolved>(e =>
            {
                if (e.Victim == id) watch.OnHit(e.Attacker.ToString(), e.Outcome.Damage, e.Outcome.Killed, Time.time);
            });
            while (Time.time - t0 < limit)
            {
                GodFor(2f);
                if (!TryActorPosition(id, out var np, out _)) { Fail("escort: lost " + id); break; }
                if (s.Combat.Vitals.StateOf(id) == VitalState.Dead)
                {
                    Fail($"escort: {id} is dead ({watch.Cause(Time.time)}; attacking it now: {AttackersOf(id)}; runner top-ups {watch.TopUps})");
                    break;
                }
                if (s.Combat.Vitals.StateOf(id) == VitalState.Alive)
                {
                    int hpNow = s.Combat.Vitals.HitPoints(id), hpMax = s.Combat.Vitals.HitPointsMax(id);
                    int topUp = watch.TopUp(hpNow, hpMax);
                    if (topUp > hpNow)
                    {
                        s.Combat.Vitals.AddHitPoints(id, topUp - hpNow);
                        Log($"escort {id}: hp {hpNow}/{hpMax} low ({watch.Cause(Time.time)}; attacking it: {AttackersOf(id)}): the runner tops it up to {topUp} " +
                            $"(dev shortcut {watch.TopUps}/{EscortWatch.MaxTopUps}; G3 has no guide protection)");
                    }
                }
                if (givenUp.Count > 0 && Time.time >= nextGivenUpCheck)
                {
                    nextGivenUpCheck = Time.time + 1f;
                    KillGivenUpAttacker(id, givenUp, guideHpAtGiveUp);
                }
                if (landCheckAt > 0f && Time.time >= landCheckAt)
                {
                    landCheckAt = -1f;
                    var nowAt = Hero.transform.position;
                    if (GuideSpots.Fell(landSpot.y, nowAt.y))
                    {
                        _fellSpots.Add(landSpot);
                        Log($"escort {id}: the hero fell {F(landSpot.y - nowAt.y)} m after the teleport to {V(landSpot)} ({landWhat}), now at {V(nowAt)}; " +
                            "the spot is not used again");
                        guideWaitSince = -1f;
                    }
                }
                // fix-57 (r31 bow: 14 sheathes between the defends of one wolf pack): the weapon drawn for a defend stays in the
                // hand until nothing attacks the hero or the guide for a while, then it is put away (a drawn weapon makes NPCs
                // around react, editor-runner-24)
                if (Combat != null && Combat.Fighter.Animator.Drawn && Time.time >= nextDefendCheck)
                {
                    if (HostilesNear(id, keepRadius) > 0) clearSince = Time.time;
                    else if (ScenarioPacing.EscortSheathe(true, Time.time - clearSince))
                    {
                        Hero.scriptedMove = Vector2.zero;
                        Log($"escort {id}: no foe near the hero or {id.Name} for {F0(Time.time - clearSince)} s, the weapon is put away");
                        float sh0 = Time.time;
                        yield return SheatheStep("escort");
                        t0 += Pause(Time.time - sh0);
                        clearSince = Time.time;
                    }
                }
                else if (Combat != null && !Combat.Fighter.Animator.Drawn) clearSince = Time.time;
                // Defend the NPC as a player would: whoever fights it (or whom it fights) near it, the hero fights too, then
                // the escort goes on (editor-runner-20: Arakos died to wolves while the god hero stood by).
                if (Time.time >= nextDefendCheck)
                {
                    nextDefendCheck = Time.time + 0.5f;
                    var foe = GuideAttacker(id, np, givenUp);
                    if (foe != null)
                    {
                        Hero.scriptedMove = Vector2.zero;
                        // an open dialog (the escorted NPC or a bystander talking to the hero) locks the hero: close it first
                        if (s.Dialog.IsActive)
                        {
                            Log($"escort {id}: closing the open dialog ({s.Dialog.Current?.Partner}) to defend");
                            s.Dialog.Abort();
                            yield return null;
                        }
                        defended++;
                        attempts.TryGetValue(foe.Value, out int tries);
                        attempts[foe.Value] = ++tries;
                        int hpBefore = s.Combat.Vitals.HitPoints(foe.Value);
                        Log($"escort {id}: {foe.Value} fights {id} (hp {s.Combat.Vitals.HitPoints(id)}), the hero defends (#{defended}, try {tries}/{DefendTries})");
                        float f0 = Time.time;
                        var args = new Dictionary<string, string> { ["npc"] = foe.Value.Name, ["secs"] = DefendFightSeconds.ToString(Inv), ["guide"] = id.Name };
                        yield return FightStep(new ScenarioStep("fight", args, "escort: defend " + id), mustKill: false);
                        // fix-186 (regress-1021b kapdun_01 43 defends, fireclan_01 39): a blow between humanoids not Hostile to
                        // each other only knocks out (CombatResolver deadly rule); G3 ZS_Unconscious_End (CP @10027360) clears the
                        // target -> ZS_FetchWeapon, the next AssessActivePerception (@100295f0) finds the guide Hostile again ->
                        // ZS_Attack. G3 has no "forget": a player finishes the downed attacker (combat.md §5.3).
                        if (s.Combat.Vitals.StateOf(foe.Value) == VitalState.Down && s.Crime.Attitudes.Of(foe.Value, id) == Attitude.Hostile)
                        {
                            Log($"escort {id}: {foe.Value} is down and still Hostile to {id.Name} (G3 gets it up to attack again): the hero finishes it");
                            yield return FinisherStep(new ScenarioStep("finisher",
                                new Dictionary<string, string> { ["npc"] = foe.Value.Name, ["soft"] = "1" }, "escort: finish " + foe.Value));
                        }
                        clearSince = Time.time;
                        float spent = Time.time - f0;
                        // fighting is not escort time (fix-37: all of it, r27 had 43-52 defends in one step), up to
                        // MaxPauseFactor x the step's time in all (defends, the guide's own fights, the hero down)
                        defendTime += spent;
                        t0 += Pause(spent);
                        bool alive = s.Combat.Vitals.StateOf(foe.Value) == VitalState.Alive;
                        // hit points lost after the fight's hp clamp (hpBefore is before it: it always looked hurt)
                        bool hurt = _fightDamage > 0;
                        // the hero cannot hit it (out of reach above/below, in a bush) or it outlasted the tries: leave it, go on
                        if (alive && (tries >= DefendTries || _fightUnreachable || (!hurt && tries >= 2)))
                        {
                            givenUp.Add(foe.Value);
                            guideHpAtGiveUp[foe.Value] = s.Combat.Vitals.HitPoints(id);
                            Log($"escort {id}: gives up defending against {foe.Value} after {tries} fights (hp {hpBefore} -> {s.Combat.Vitals.HitPoints(foe.Value)}{(hurt ? "" : ", no hit landed")}); the escort goes on");
                        }
                        lastPlan = -10f;
                        progressAt = Time.time;
                        progressRef = lastPos = Hero.transform.position;
                        progressLeft = float.MaxValue;
                        stage = 0;
                        guideWaitSince = -1f;
                        continue;
                    }
                }
                // fix-53 (r30 hammerclan_01/04): an NPC talks to the hero by itself - the gate guards Garik ("Halt! Not a step
                // further.") on the Hammerclan path and Sivert ("Friend or foe?") at the Fire clan gate. The talk locks his
                // controls: he stood still, Mort waited for him, and the stuck teleports moved a locked hero (7 per leg, then
                // the anti-loop). A player answers it and walks on; the talk is not escort time.
                if (s.Dialog.IsActive)
                {
                    float talk = 0f;
                    yield return AnswerUnaskedTalk($"escort {id}", x => talk = x);
                    t0 += Pause(talk);
                    lastPlan = -10f;
                    progressAt = Time.time;
                    progressRef = lastPos = Hero.transform.position;
                    progressLeft = float.MaxValue;
                    stage = 0;
                    guideWaitSince = -1f;
                    continue;
                }
                // fix-37: the hero knocked down (r27 main_02, fireclan_01, nemora_01/02: he sat in the sit knock-down after a
                // defend, every teleport landed a sitting hero, the anti-loop failed the step): no stuck time, no teleport; the
                // runner's get-up press (fix-36) or the motion's end gets him up. Down too long fails the step plainly.
                if (Combat != null && Combat.Fighter != null && (Combat.Fighter.SittingKnockedDown || Combat.Fighter.Animator.IsDown))
                {
                    Hero.scriptedMove = Vector2.zero;
                    // fix-64 (r31 hammerclan_02): one down spell only; a defend fight in between (he sat and got up 15 times
                    // in its 22 s) is not down time of this loop
                    float spell = ScenarioPacing.DownStart(downSince, downSeenAt, Time.time);
                    if (downSince >= 0f && spell != downSince) downSince = -1f;
                    downSeenAt = Time.time;
                    if (downSince < 0f)
                    {
                        downSince = Time.time;
                        Log($"escort {id}: the hero is down at {V(Hero.transform.position)} (animator {Combat.Fighter.Animator.StateText}, sitting {Combat.Fighter.SittingKnockedDown}): waits to get up");
                    }
                    else if (Time.time - downSince > HeroDownLimitSeconds)
                    {
                        Fail($"escort {id}: the hero stays down {F0(Time.time - downSince)} s at {V(Hero.transform.position)} (animator {Combat.Fighter.Animator.StateText}, sitting {Combat.Fighter.SittingKnockedDown}, sits {Combat.SitCount})");
                        looped = true;
                        break;
                    }
                    t0 += Pause(Time.deltaTime);
                    progressAt = Time.time;
                    stage = 0;
                    progressRef = lastPos = Hero.transform.position;
                    progressLeft = float.MaxValue;
                    guideWaitSince = -1f;
                    yield return null;
                    continue;
                }
                if (downSince >= 0f)
                {
                    Log($"escort {id}: the hero is up again after {F(Time.time - downSince)} s");
                    downSince = -1f;
                }
                // the guide's own fight (an enemy the hero gave up on, or one he cannot reach) is not escort time either
                bool guideFights = s.NpcCombat.Get(id) is NpcCombatBrain guideBrain && guideBrain.IsFighting;
                if (guideFights) t0 += Pause(Time.deltaTime);
                if (movingTarget && TryTarget(to, out var moved2, out _, out _, out _, out _)) goal = moved2; // npc:Gamal may walk
                if (!hasY) goal.y = np.y;
                // fix-58: a guide fighting at the target has not arrived (its fight may draw it off; G3 brings it back after)
                // fix-65 (R32-1): only a fight near the guide (a foe on another level 137.8 m below does not hold it)
                // fix-186 (fireclan_01: 'quest Osmund_GoToShaman Success' at the shaman, then 2 min of re-attacks and a
                // timeout 8.1 m from its body): the FollowNpc quest left Running by its delivery info -> StopGuidePlayer
                // (FollowQuestGuides): the guide's walk is over, so is the escort
                // fix-190 (kapdun_01: Wenzel stood 150 s after the defends): the hero's kills alarmed the guide's enclave, G3
                // NotifyEnclave turned the guide away and its FollowNpc quest is Lost. A player takes it up again by talking
                // (rejoin=entry, at most MaxRejoins times); without one the walk is over: fail now, not at the timeout.
                if (mode == EscortMode.Guide && guideQuest != null && !s.Party.Members.IsGuiding(id) &&
                    s.Quests.StatusOf(new Id<QuestDef>(guideQuest)) == QuestStatus.Lost)
                {
                    if (rejoin == null || rejoins >= MaxRejoins)
                    {
                        Fail($"escort {id}: quest {guideQuest} Lost ({id.Name} turned away: the hero attacked its enclave, G3 NotifyEnclave)" +
                             (rejoin != null ? $", {rejoins} rejoin(s) via {rejoin} used" : ", no rejoin= entry") + $", {F(Flat(np - goal))} m from the target");
                        break;
                    }
                    if (s.Combat.Vitals.StateOf(id) != VitalState.Alive || HostilesNear(id, keepRadius) > 0)
                    {
                        // the talk waits until the guide stands and nobody attacks it or the hero (a player would not talk mid-fight)
                        t0 += Pause(Time.deltaTime);
                        yield return null;
                        continue;
                    }
                    rejoins++;
                    Log($"escort {id}: quest {guideQuest} Lost ({id.Name} turned away after the hero's fight in its enclave): the hero asks {rejoin} (rejoin {rejoins}/{MaxRejoins})");
                    float a0 = Time.time;
                    if (Combat != null && Combat.Fighter.Animator.Drawn) yield return SheatheStep("escort");
                    var heroNow = Hero.transform.position;
                    if (Flat(np - heroNow) > 2.5f)
                    {
                        // the talk is in reach as when the player walks up to the guide
                        var off = heroNow - np;
                        off.y = 0f;
                        var near2 = nav.Snap(np + (off.sqrMagnitude > 0.01f ? off.normalized : Vector3.forward) * 1.5f, 3f);
                        if (near2 != null) Hero.Teleport(near2.Value + Vector3.up * 0.15f);
                        yield return null;
                    }
                    yield return AskStep(new ScenarioStep("ask", new Dictionary<string, string> { ["npc"] = id.Name, ["entry"] = rejoin }, "escort: rejoin " + id));
                    t0 += Pause(Time.time - a0);
                    Log($"escort {id}: after {rejoin}: quest {s.Quests.StatusOf(new Id<QuestDef>(guideQuest))}, guiding {s.Party.Members.IsGuiding(id)}");
                    lastPlan = lastRoutePlan = -10f;
                    progressAt = Time.time;
                    progressRef = lastPos = Hero.transform.position;
                    progressLeft = float.MaxValue;
                    stage = 0;
                    guideWaitSince = -1f;
                    continue;
                }
                if (mode == EscortMode.Guide && guideQuest != null && !s.Party.Members.IsGuiding(id) &&
                    s.Quests.StatusOf(new Id<QuestDef>(guideQuest)) == QuestStatus.Success)
                {
                    Log($"escort {id}: quest {guideQuest} Success, {id.Name} stopped guiding ({F(Flat(np - goal))} m from the target)");
                    arrived = true;
                    break;
                }
                bool fightsOrAttacked = EscortFightAtGuide(id, np);
                if (ScenarioPacing.EscortArrived(Flat(np - goal), near, mode == EscortMode.Guide, fightsOrAttacked)) { arrived = true; break; }
                if (Flat(np - goal) <= near && Time.time >= nextAtTargetFightLog)
                {
                    nextAtTargetFightLog = Time.time + 5f;
                    var own = s.NpcCombat.Get(id);
                    Log($"escort {id}: at the target ({F(Flat(np - goal))} m) but fighting (target {(own != null && own.IsFighting ? own.State.Target.ToString() : "-")}, " +
                        $"attacked by {AttackersOf(id)}, the hero attacked by {AttackersOf(ActorId.Player)}): waits for the fight to end");
                }
                var hero = Hero.transform.position;
                bool stand = false;
                Vector3 dest; // where the path goes: the guide, or the target when leading
                if (mode == EscortMode.Guide)
                {
                    // follow the guide with hysteresis: close up when more than 6 m behind, wait when closer than 3 m
                    float d = Flat(np - hero);
                    waiting = d < 3f || (waiting && d < 6f);
                    stand = waiting;
                    dest = np;
                    // The guide waits for the hero and he does not come up (editor-runner-25: after a defend Cole's hero stood
                    // 6 m below a ledge, Merdarion's 25 m away, for minutes): a player would climb back; he is put next to it.
                    bool guideWaits = TryActorPosition(id, out _, out var gv) && gv != null && gv.TryGetComponent<NpcView>(out var guideView) && guideView.GuideWaits;
                    if (!guideWaits || d <= 6f) guideWaitSince = -1f;
                    else if (guideWaitSince < 0f) guideWaitSince = Time.time;
                    else if (Time.time - guideWaitSince > GuideWaitTeleportSeconds)
                    {
                        guideWaitSince = -1f;
                        var back = hero - np;
                        back.y = 0;
                        var spot = np + (back.sqrMagnitude > 0.01f ? back.normalized : -Hero.transform.forward) * 3f;
                        // fix-46 (r28 fireclan_01): Leif stood off the NavMesh on a rock 14 m above the terrain; the only ground
                        // found was 8 m off, the hero's own spot: teleports #2/#3 moved him 0.1 m and failed the step as a loop.
                        // The spot must be within 6 m of the guide (where it stops waiting); its own capsule is not in the way.
                        Vector3? onto = NextToGuide(nav, spot, np, hero, gv, out string besideWhy);
                        if (onto == null)
                        {
                            Log($"escort {id}: the guide waits at {V(np)} and no spot next to it passes ({besideWhy}): the hero is put on its spot");
                            onto = np;
                        }
                        else Log($"escort {id}: spot next to the waiting {id.Name}: {besideWhy}");
                        if (onto != null)
                        {
                            teleports++;
                            Hero.Teleport(onto.Value + Vector3.up * 0.15f);
                            Log($"escort {id}: the guide waits {F(GuideWaitTeleportSeconds)} s and the hero does not come up ({F(d)} m, dy {F(np.y - hero.y)}, " +
                                $"path {(nav.Partial ? "partial" : "complete")}), teleport #{teleports} {V(hero)} -> {V(onto.Value)} next to {id.Name}");
                            Landed(onto.Value, "next to the waiting guide");
                            if (TeleportLoops(hero, "next to the waiting guide")) break;
                            lastRoutePlan = -10f;
                            progressAt = Time.time;
                            progressRef = lastPos = Hero.transform.position;
                            progressLeft = float.MaxValue;
                            stage = 0;
                            lastPlan = -10f;
                            yield return null;
                            continue;
                        }
                    }
                }
                else if (Flat(hero - goal) > 2.5f && past == 0) dest = goal;
                else
                {
                    // at the target: step past it, away from the NPC, so the follower comes up to it; fix-186: only to a spot
                    // on the NavMesh in straight reach of the target (silden_03: the spot past Grompel was in the hut wall),
                    // else the next turn, else the hero stands at the target
                    past++;
                    if (past == 1) { pastTry = 0; pastSpot = null; }
                    if (pastSpot == null && pastTry >= 0)
                    {
                        var away = goal - np;
                        away.y = 0;
                        var fwd0 = away.sqrMagnitude > 0.01f ? away.normalized : Hero.transform.forward;
                        while (pastSpot == null && EscortLegRules.PastTurn(pastTry) is float turn)
                        {
                            var c = goal + Quaternion.Euler(0f, turn, 0f) * fwd0 * EscortLegRules.PastMetres;
                            var on = nav.Snap(c, 0.8f);
                            if (on != null && Mathf.Abs(on.Value.y - goal.y) < 1.2f && nav.Straight(goal, on.Value)) pastSpot = on;
                            else pastTry++;
                        }
                        if (pastSpot == null)
                        {
                            pastTry = -1;
                            Log($"escort {id}: no free spot past the target at {V(goal)}, the hero stands there for {id.Name} to come up");
                        }
                    }
                    stand = pastSpot == null;
                    dest = pastSpot ?? hero;
                    if (past > 600) past = 0; // ~10 s at the target: lead again from where the hero is
                }

                // fix-34: far from where it should lead, the hero goes along the G3 route (a guide's own trail, re-read every
                // 3 s; the graph's route to the target, planned again when the target moved); each leg on the NavMesh below
                Vector3 legTo = dest;
                onRouteLegs = useRoute && !routeDropped && !stand && EscortLegRules.UseRoute(onRouteLegs, Flat(dest - hero)); // fix-48: hysteresis
                if (onRouteLegs)
                {
                    bool due = mode == EscortMode.Guide
                        ? Time.time - lastRoutePlan > 3f
                        : Time.time - lastRoutePlan > 3f && (!route.HasRoute || Flat(dest - routeGoal) > 8f);
                    if (due) PlanRoute(hero, dest);
                    else route.MoveGoal(HP(dest));
                    if (route.HasRoute) legTo = UV(route.Leg(HP(hero)));
                }
                else if (route.HasRoute) route.ClearRoute();

                if (!stand && (Time.time - lastPlan > 1f || Flat(legTo - plannedTo) > 2f))
                {
                    lastPlan = Time.time;
                    plannedTo = legTo;
                    string change = nav.Plan(hero, legTo);
                    // fix-190 (morasul_01/04: Alima/Yasmin escorts stuck 10-17x in the Mora Sul gully): the G3 route's point
                    // (-972,37,-1505) is on the plateau, which the NavMesh joins only from the north-east; the partial leg
                    // led the hero round the south into the gully. A route leg the NavMesh cannot reach while the destination
                    // can (a complete path) is not the hero's way: the step goes on the NavMesh alone.
                    if (onRouteLegs && route.HasRoute && nav.Partial && Flat(legTo - dest) > 0.5f && nav.Joins(hero, dest))
                    {
                        routeDropped = true;
                        onRouteLegs = false;
                        Log($"escort {id}: the G3 route's point {V(legTo)} has no NavMesh way from {V(hero)} but {V(dest)} has: NavMesh only for this step");
                        route.ClearRoute();
                        legTo = plannedTo = dest;
                        change = nav.Plan(hero, dest, force: true);
                    }
                    if (change != null) Log($"escort {id}: {change}");
                    // a new, longer way is not a step back: compare later progress with it (the stuck time runs on)
                    float planned = nav.Remaining(hero, legTo);
                    if (progressLeft != float.MaxValue && planned > progressLeft) progressLeft = planned;
                }
                Vector3 aim = stand ? hero : Time.time < detourUntil ? detourAim : nav.Next(hero, legTo);
                // At the end of a partial path, still far from where it should lead: the hero stands on an island.
                if (!stand && nav.Partial && Flat(aim - hero) <= 0.6f && Flat(dest - hero) > 6f && Time.time - lastReconnect > 3f)
                {
                    // First onto the connected NavMesh; when that finds nothing twice in a row (editor-runner-20 gotha_02:
                    // 204 partial plans, no hop, the hero stood 9 m from Gorn), teleport next to where the path should lead.
                    bool moved = Reconnect("partial path ends at the hero");
                    if (moved) partialStalls = 0;
                    else if (++partialStalls >= 2)
                    {
                        partialStalls = 0;
                        // fix-34: forward along the G3 route (its current point) when there is one, else next to the guide / target
                        bool onRoute = route.HasRoute && Flat(legTo - dest) > 0.5f;
                        var at = onRoute ? legTo : dest;
                        var back = hero - at;
                        back.y = 0;
                        var spot = onRoute ? at : at + (back.sqrMagnitude > 0.01f ? back.normalized : -Hero.transform.forward) * 3f;
                        Vector3? onto = null;
                        if (!onRoute && mode == EscortMode.Guide && TryActorPosition(id, out _, out var gvp))
                        {
                            // fix-57: next to the guide only where the hero can stand at his height and walk to him
                            onto = NextToGuide(nav, spot, at, hero, gvp, out string pWhy);
                            if (onto == null) Log($"escort {id}: no spot next to {id.Name} for the partial-path teleport ({pWhy})");
                        }
                        else onto = nav.Snap(spot, 4f) ?? nav.Snap(at, 4f);
                        if (onto == null && (onRoute || mode != EscortMode.Guide) && FindGround(spot, true, out var g, out _)) onto = g.point;
                        if (onto != null)
                        {
                            teleports++;
                            Landed(onto.Value, "partial path");
                            route.StuckAt(HP(hero), out _);
                            Hero.Teleport(onto.Value + Vector3.up * 0.15f);
                            Log($"escort {id}: the hero's path stays partial, teleport #{teleports} {V(hero)} -> {V(onto.Value)} " +
                                $"({(onRoute ? $"route point {route.Next}" : "next to " + (mode == EscortMode.Guide ? id.Name : "the target"))})");
                            LogRoute($"stuck at {V(hero)} -> tp to {(onRoute ? $"waypoint {route.Next}" : "next to " + (mode == EscortMode.Guide ? id.Name : "the target"))} (partial path)");
                            if (TeleportLoops(hero, "partial path")) break;
                            lastRoutePlan = -10f;
                            progressAt = Time.time;
                            progressRef = lastPos = Hero.transform.position;
                            progressLeft = float.MaxValue;
                        }
                    }
                    lastPlan = -10f;
                    yield return null;
                    continue;
                }

                var dir = aim - hero;
                dir.y = 0;
                if (dir.magnitude > 0.6f)
                {
                    Hero.SetLook(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 5f);
                    Hero.scriptedMove = new Vector2(0, 1);
                }
                else Hero.scriptedMove = Vector2.zero;
                travelled += Flat(hero - lastPos);
                lastPos = hero;

                // progress = 1 m away from the last reference point AND 1 m less path left (or the guide walked on);
                // standing on purpose is progress too. A detour to the side or running into a slope moves the hero without
                // bringing him closer (editor-runner-21 silden_02: 54 detours reset the stuck stages, no hop ever came).
                float left = nav.Remaining(hero, legTo);
                bool closer = progressLeft == float.MaxValue || left < progressLeft - 1f || Flat(legTo - progressDest) > 1f;
                if (Hero.scriptedMove == Vector2.zero || (Flat(hero - progressRef) > 1f && closer))
                {
                    progressRef = hero;
                    progressLeft = left;
                    progressDest = legTo;
                    progressAt = Time.time;
                    stage = 0;
                }
                float stuck = Time.time - progressAt;
                if (stage == 0 && stuck > 2f && past > 0 && pastSpot != null)
                {
                    // fix-186: the spot past the target cannot be reached: the next turn (no detours at the target)
                    Log($"escort {id}: stuck {F(stuck)} s stepping past the target to {V(pastSpot.Value)}, next spot");
                    pastSpot = null;
                    pastTry++;
                    progressAt = Time.time;
                }
                else if (stage == 0 && stuck > 2f && mode != EscortMode.Guide && past == 0 && Flat(hero - goal) < near)
                {
                    // fix-186 (silden_03 in the editor: 25 stuck/26 detours 3.7 m from Grompel in a hut): leading, stuck within
                    // the arrival radius of the target: the hero is there; the spot past the target (or standing) lets the
                    // follower come up
                    Log($"escort {id}: stuck {F(stuck)} s {F(Flat(hero - goal))} m from the target, steps past it");
                    past = 1;
                    pastTry = 0;
                    pastSpot = null;
                    progressAt = Time.time;
                }
                else if (stage == 0 && stuck > 2f)
                {
                    stage = 1;
                    stuckEvents++;
                    lastPlan = Time.time;
                    string change = nav.Plan(hero, legTo, true);
                    Log($"escort {id}: stuck at {V(hero)} (#{stuckEvents}), re-plan: {change ?? nav.Describe()}");
                }
                else if ((stage == 1 && stuck > 4f) || (stage == 2 && stuck > 6f))
                {
                    // a small detour around the obstacle: 3 m to the side and 1 m on, left first, then right
                    stage++;
                    var fwd = dir.sqrMagnitude > 0.01f ? dir.normalized : Hero.transform.forward;
                    var side = Vector3.Cross(Vector3.up, fwd) * (detours % 2 == 0 ? -1f : 1f);
                    detours++;
                    detourAim = nav.Snap(hero + side * 3f + fwd, 2f) ?? hero + side * 3f + fwd;
                    detourUntil = Time.time + 1.5f;
                    Log($"escort {id}: stuck {F(stuck)} s, detour {(detours % 2 == 1 ? "left" : "right")} to {V(detourAim)}");
                }
                else if (stage >= 3 && stuck > 10f)
                {
                    // fix-34: the stuck spot is blocked for this step; teleport forward to the first G3 route point beyond it
                    // (never back to the same approach). Without a route: next to a guide, else the old hop 6 m along the
                    // path / 15 m ahead when stuck again near the last hop. 3 teleports in 60 s fail the step.
                    bool onRoute = route.StuckAt(HP(hero), out int wp);
                    bool again = lastHop.HasValue && Flat(hero - lastHop.Value) < 20f;
                    Vector3 ahead;
                    string how;
                    // fix-48: a guide waiting for the hero nearby: next to it (its trail's next point lies past it)
                    bool guideWaitsNear = TryActorPosition(id, out _, out var gvs) && gvs != null &&
                                          gvs.TryGetComponent<NpcView>(out var gvsView) && gvsView.GuideWaits &&
                                          EscortLegRules.StuckTeleportToGuide(mode == EscortMode.Guide, true, Flat(np - hero));
                    var fromGuide = hero - np;
                    fromGuide.y = 0;
                    Vector3? besideGuide = guideWaitsNear
                        ? NextToGuide(nav, np + (fromGuide.sqrMagnitude > 0.01f ? fromGuide.normalized : -Hero.transform.forward) * 3f, np, hero, gvs, out _)
                        : null;
                    if (besideGuide != null) { onRoute = false; ahead = besideGuide.Value; how = "next to the waiting " + id.Name; }
                    else if (onRoute) { ahead = UV(route.Points[wp]); how = $"waypoint {wp}"; }
                    else if (mode == EscortMode.Guide && Flat(np - hero) > 6f &&
                             NextToGuide(nav, np + (fromGuide.sqrMagnitude > 0.01f ? fromGuide.normalized : -Hero.transform.forward) * 3f, np, hero, gvs, out string sWhy) is Vector3 nextTo)
                    {
                        // fix-57: a validated spot (at his height, on the NavMesh, joined to him), the landing below is that spot
                        ahead = nextTo;
                        besideGuide = nextTo;
                        how = "next to " + id.Name + " (" + sWhy + ")";
                    }
                    else
                    {
                        ahead = again ? nav.Ahead(hero, 15f) : hero + (dir.sqrMagnitude > 0.01f ? dir.normalized : Hero.transform.forward) * 6f;
                        how = again ? "15 m ahead on the NavMesh path" : "6 m ahead";
                    }
                    Vector3? landing = besideGuide ?? (onRoute ? nav.Snap(ahead, 4f) : null);
                    if (landing == null && FindGround(ahead, true, out var hit, out _)) landing = hit.point;
                    if (landing == null) landing = nav.Snap(ahead, 4f);
                    if (landing != null) { Hero.Teleport(landing.Value + Vector3.up * 0.15f); Landed(landing.Value, how); }
                    LogRoute($"stuck at {V(hero)} -> tp to {how} {V(Hero.transform.position)}");
                    if (onRoute || again || how.StartsWith("next")) { teleports++; lastHop = null; Log($"escort {id}: stuck {F(stuck)} s, teleport #{teleports} {V(hero)} -> {V(Hero.transform.position)} ({how})"); }
                    else { hops++; lastHop = Hero.transform.position; Log($"escort {id}: stuck {F(stuck)} s at {V(hero)}, hop {hops} -> {V(Hero.transform.position)}"); }
                    if (TeleportLoops(hero, how)) break;
                    lastRoutePlan = -10f;
                    stage = 0;
                    progressRef = Hero.transform.position;
                    progressLeft = float.MaxValue;
                    progressAt = Time.time;
                    lastPlan = -10f;
                }
                else if (stage >= 3 && stuck > 8f && Time.time - lastJump > 0.7f) { Hero.scriptedJump = true; lastJump = Time.time; }
                yield return null;
            }
            _escortHits?.Dispose();
            _escortHits = null;
            Hero.scriptedMove = Vector2.zero;
            Hero.scriptedWalk = false;
            if (arrived && Combat != null && Combat.Fighter.Animator.Drawn && HostilesNear(id, keepRadius) == 0) yield return SheatheStep("escort end");
            TryActorPosition(id, out var end, out _);
            // The NPC got there without the hero: a guide that could not walk on was put at its destination (NpcView.Lead's
            // fallback; editor-runner-21: Xardas 1946 m, the Geldern slave 297 m, Fraser 53 m ahead). A player would walk
            // after it; the hero is brought next to it, so the steps after the escort (expect dist<=12, the arrival talk) hold.
            if (arrived && Flat(end - Hero.transform.position) > 10f)
            {
                var from = Hero.transform.position;
                var back = from - end;
                back.y = 0;
                var spot = end + (back.sqrMagnitude > 0.01f ? back.normalized : -Hero.transform.forward) * 3f;
                TryActorPosition(id, out _, out var gve);
                Vector3? onto = NextToGuide(nav, spot, end, from, gve, out _) ?? nav.Snap(spot, 4f) ?? nav.Snap(end, 4f);
                if (onto == null && FindGround(spot, true, out var g, out _)) onto = g.point;
                if (onto != null)
                {
                    teleports++;
                    Hero.Teleport(onto.Value + Vector3.up * 0.15f);
                    Log($"escort {id}: arrived without the hero ({F(Flat(end - from))} m), the hero follows: {V(from)} -> {V(onto.Value)}");
                }
            }
            // fix-41: the scenario timeout grants what this step paused and added (r28 nemora_02: 702 s paused, 900 s timeout)
            CreditClock(paused + Mathf.Max(0f, limit - st.Float("secs", 600f)));
            string line = $"escort {id} ({mode}) to {to}: {F(Time.time - t0)} s, hero ran {F(travelled)} m (first path {F(nav.FirstLength)} m, " +
                          $"{nav.Plans} plans), stuck {stuckEvents}, detours {detours}, hops {hops}, teleports {teleports}, defended {defended} ({F0(defendTime)} s), " +
                          $"clock paused {F0(paused)} s of {F0(limit)} s, {id.Name} hit {watch.Hits}x, topped up {watch.TopUps}x, " +
                          $"{id} {F(Flat(end - goal))} m from the target, {F(Flat(end - Hero.transform.position))} m from the hero";
            if (arrived || looped) Log(line); else Fail(line + " (timeout)");
        }

        /// <summary>
        /// The hero's NavMesh path for <c>escort</c>: <c>NavMesh.CalculatePath</c> with the cell NavMesh's agent type (built
        /// by <see cref="CellNavMeshes"/>: radius 0.35, height 1.8, max slope 50°, i.e. the hero's CharacterController;
        /// <see cref="Agent"/> logs both). Where the end has no NavMesh yet (its cell is beyond the build radius) the path
        /// goes to the farthest NavMesh point on the straight line towards it.
        /// </summary>
        /// <summary>
        /// The nearest living NPC within 30 m of the escorted NPC that fights it, or that it fights (never the hero); null
        /// when there is none.
        /// </summary>
        /// <summary>Defend fights per attacker, their length (s), and the defend time that pauses the escort clock (s).</summary>
        const int DefendTries = 3;
        /// <summary>fix-190: times a guide whose quest got Lost is asked to take it up again (escort rejoin=).</summary>
        const int MaxRejoins = 12;
        const float DefendFightSeconds = 25f;
        /// <summary>The escort clock pauses for defends, the guide's own fights and a downed hero, up to this many times the
        /// step's time in all (fix-37; it was 150 s of defends: r27 Mort 43 defends, Georg 52, Finley 45 timed out).</summary>
        const float MaxPauseFactor = 2f;
        /// <summary>The step's time is at least the NPC's G3 route at this speed (m/s) plus a minute (fix-37).</summary>
        const float EscortRouteSpeed = 2f;
        /// <summary>The hero down (knocked down, sitting) this long during an escort fails the step (s).</summary>
        const float HeroDownLimitSeconds = 20f;
        /// <summary>A waiting guide the hero has not come within 6 m of for this long: the hero is put next to it (s).</summary>
        const float GuideWaitTeleportSeconds = 20f;

        static HeroRoutePoint HP(Vector3 v) => new HeroRoutePoint(v.x, v.y, v.z);
        static Vector3 UV(HeroRoutePoint p) => new Vector3(p.X, p.Y, p.Z);

        /// <summary>A hero-route line (fix-34): "[scenario] hero route: ..." / "[scenario] stuck at ... -> tp to waypoint k".</summary>
        void LogRoute(string text)
        {
            _lines.Add(text);
            Debug.Log("[scenario] " + text);
        }

        /// <summary>Spots the hero fell from right after a teleport in this escort step (fix-57).</summary>
        readonly List<Vector3> _fellSpots = new List<Vector3>();
        readonly List<Vector3> _trailBuf = new List<Vector3>();

        /// <summary>
        /// A spot for the hero next to the guide at <paramref name="guide"/> (fix-46; fix-57 r31 hammerclan_02: the old spot
        /// lay on the rim of Mort's ledge, 30 m above the hero, who fell back down at once). Candidates: the guide's own G3
        /// path 3/5/8 m behind him (the way he came), the spot behind him towards the hero (<paramref name="spot"/>), then
        /// rings of 2-4.5 m around him; each snapped to the NavMesh and kept only when <see cref="GuideSpots.Reject"/> finds
        /// nothing: at his height, ground under it, off the NavMesh edge, joined to him by a short complete NavMesh path, not
        /// where the hero stands or fell from. <paramref name="why"/> names the candidate taken or the last refusal. Null = none.
        /// </summary>
        Vector3? NextToGuide(EscortPath nav, Vector3 spot, Vector3 guide, Vector3 hero, Transform guideView, out string why)
        {
            why = "no candidate";
            var cands = new List<(Vector3 p, string src)>();
            if (guideView != null && guideView.TryGetComponent<NpcView>(out var gview) && gview.TryGuideTrail(_trailBuf))
            {
                var trail = _trailBuf.Select(HP).ToList();
                foreach (float d in GuideSpots.TrailDistances)
                    if (GuideSpots.BehindOnTrail(trail, d, out var q)) cands.Add((UV(q), $"{d:0} m back on his path"));
            }
            cands.Add((spot, "behind him"));
            var away = spot - guide;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            away.Normalize();
            foreach (float r in new[] { 2f, 3f, 4.5f })
                for (int k = 0; k < 8; k++)
                    cands.Add((guide + Quaternion.Euler(0f, (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * 45f, 0f) * away * r, $"{r:0.#} m around him"));
            var guideNav = nav.Snap(guide, 2f) ?? nav.Snap(guide, 4f);
            foreach (var (c, src) in cands)
            {
                var q = nav.Snap(c, 1.5f);
                if (q == null) { why = src + ": off the NavMesh"; continue; }
                var qv = q.Value;
                if (_fellSpots.Any(b => Flat(b - qv) < 2.5f && Mathf.Abs(b.y - qv.y) < 3f)) { why = src + ": the hero fell from there"; continue; }
                float? groundY = GroundBelow(qv, guideView);
                float edge = nav.EdgeDistance(qv);
                bool reachable = guideNav != null && nav.Reaches(qv, guideNav.Value, 3f * Flat(qv - guide) + 8f);
                string reason = GuideSpots.Reject(HP(qv), HP(guide), HP(hero), groundY, edge, reachable);
                if (reason == null) { why = src; return qv; }
                why = src + ": " + reason;
            }
            return null;
        }

        /// <summary>The physics floor under a NavMesh point (bodies, triggers and <paramref name="ignore"/> left out), its height or null.</summary>
        float? GroundBelow(Vector3 p, Transform ignore)
        {
            var heroRoot = Hero.transform;
            float? best = null;
            foreach (var x in Physics.RaycastAll(p + Vector3.up * 1.5f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
            {
                var t = x.collider.transform;
                if (t.IsChildOf(heroRoot) || ignore != null && t.IsChildOf(ignore) || x.collider.attachedRigidbody != null) continue;
                if (x.collider.GetComponentInParent<NpcView>() != null) continue;
                if (best == null || Mathf.Abs(x.point.y - p.y) < Mathf.Abs(best.Value - p.y)) best = x.point.y;
            }
            return best;
        }

        /// <summary>The escort's HitResolved subscription (fix-46 EscortWatch); disposed when the step ends or the next starts.</summary>
        System.IDisposable _escortHits;

        ActorId? GuideAttacker(ActorId guide, Vector3 at, HashSet<ActorId> skip = null)
        {
            var s = _session;
            ActorId? best = null;
            float bestD = 30f;
            void Consider(ActorId a)
            {
                if (a.IsEmpty || a.IsPlayer || a == guide || s.Combat.Vitals.StateOf(a) != VitalState.Alive || (skip != null && skip.Contains(a))) return;
                if (!TryActorPosition(a, out var p, out _)) return;
                float d = Flat(p - at);
                if (d < bestD) { bestD = d; best = a; }
            }
            // IsAttacking, not IsFighting: a fleeing NPC's target is the one it runs from (fix-41, r28 main_02 / hammerclan_01:
            // "Deer fights Xardas/Mort" were panicking deer, the hero chased them)
            foreach (var b in s.NpcCombat.Brains)
                if (b.IsAttacking && b.State.Target == guide) Consider(b.Self);
            var own = s.NpcCombat.Get(guide);
            if (own != null && own.IsAttacking) Consider(own.State.Target);
            // fix-186 (wolfclan_01: at Hanson_END 'attacked by nobody', the hero knocked down again and again): a foe fighting the
            // hero near the guide holds the arrival (EscortFightAtGuide); a player fights it, so the defend does too
            if (best == null)
                foreach (var b in s.NpcCombat.Brains)
                    if (b.IsAttacking && b.State.Target.IsPlayer && TryActorPosition(b.Self, out var fp, out _) &&
                        ScenarioPacing.EscortFightNear(true, fp.x - at.x, fp.y - at.y, fp.z - at.z)) Consider(b.Self);
            return best;
        }

        /// <summary>
        /// fix-65 (R32-1): the guide at <paramref name="guideAt"/> is in a fight that holds its arrival
        /// (<see cref="ScenarioPacing.EscortFightNear"/>): its own fight against a foe near it, or a living foe near it that
        /// attacks the guide or the hero.
        /// </summary>
        bool EscortFightAtGuide(ActorId guide, Vector3 guideAt)
        {
            var s = _session;
            bool Near(ActorId foe, bool targetsUs)
            {
                if (!targetsUs || s.Combat.Vitals.StateOf(foe) != VitalState.Alive || !TryActorPosition(foe, out var p, out _)) return false;
                var d = p - guideAt;
                return ScenarioPacing.EscortFightNear(true, d.x, d.y, d.z);
            }
            var own = s.NpcCombat.Get(guide);
            if (own != null && own.IsFighting && !own.State.Target.IsEmpty && Near(own.State.Target, true)) return true;
            foreach (var b in s.NpcCombat.Brains)
                if (b.IsAttacking && b.Self != guide && Near(b.Self, b.State.Target == guide || b.State.Target.IsPlayer)) return true;
            return false;
        }

        /// <summary>The NPCs whose fight targets <paramref name="npc"/> (attacking, not fleeing), for the escort's logs.</summary>
        string AttackersOf(ActorId npc)
        {
            var list = _session.NpcCombat.Brains.Where(b => b.IsAttacking && b.State.Target == npc)
                               .Select(b => $"{b.Self} (hp {_session.Combat.Vitals.HitPoints(b.Self)})").ToList();
            return list.Count > 0 ? string.Join(", ", list) : "nobody";
        }

        /// <summary>
        /// A foe the hero gave up on (out of his reach, no hit landed) that still attacks the escorted NPC and has hurt it
        /// since: the runner takes it out (dev shortcut, logged). fix-41: r28 main_02, a given-up IceWolf (hp clamped to 5)
        /// bit Xardas for minutes until he died; G3 has no protection for Xardas (IsDeadlyDamage: deadly), a player would
        /// have dealt with the wolf. Returns the foe killed, if any.
        /// </summary>
        ActorId? KillGivenUpAttacker(ActorId guide, HashSet<ActorId> givenUp, Dictionary<ActorId, int> guideHpAtGiveUp)
        {
            var s = _session;
            int hp = s.Combat.Vitals.HitPoints(guide);
            foreach (var foe in givenUp)
            {
                if (s.Combat.Vitals.StateOf(foe) != VitalState.Alive) continue;
                var brain = s.NpcCombat.Get(foe);
                if (brain == null || !brain.IsAttacking || brain.State.Target != guide) continue;
                if (!guideHpAtGiveUp.TryGetValue(foe, out int was) || hp >= was) continue;
                s.Combat.Vitals.Kill(foe, true);
                Log($"escort {guide}: {foe} keeps attacking {guide} after the hero gave it up (hp {was} -> {hp}): the runner kills it (dev shortcut)");
                return foe;
            }
            return null;
        }

        sealed class EscortPath
        {
            readonly UnityEngine.AI.NavMeshPath _path = new UnityEngine.AI.NavMeshPath();
            readonly List<Vector3> _corners = new List<Vector3>();
            readonly UnityEngine.AI.NavMeshQueryFilter _filter;
            string _state = "";
            public int Plans { get; private set; }
            public float FirstLength { get; private set; } = -1f;
            public float Length { get; private set; }
            public string Agent { get; }

            public EscortPath(CharacterController cc)
            {
                var settings = UnityEngine.AI.NavMesh.GetSettingsByID(0);
                _filter = new UnityEngine.AI.NavMeshQueryFilter { agentTypeID = settings.agentTypeID, areaMask = UnityEngine.AI.NavMesh.AllAreas };
                Agent = $"NavMesh agent {settings.agentTypeID}" +
                        (cc != null ? $", hero r={F(cc.radius)} h={F(cc.height)} slope={F(cc.slopeLimit)}" : "") +
                        (UnityEngine.Object.FindAnyObjectByType<CellNavMeshes>() is CellNavMeshes m
                            ? $", cell NavMesh r={F(m.agentRadius)} h={F(m.agentHeight)} slope={F(m.agentSlope)}" : ", no CellNavMeshes");
            }

            public Vector3? Snap(Vector3 p, float radius) =>
                UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, radius, _filter) ? hit.position : (Vector3?)null;

            /// <summary>fix-186: the NavMesh between a and b has no edge in the way (a straight walk).</summary>
            public bool Straight(Vector3 a, Vector3 b) =>
                UnityEngine.AI.NavMesh.SamplePosition(a, out var from, 1.5f, _filter) && !UnityEngine.AI.NavMesh.Raycast(from.position, b, out _, _filter);

            /// <summary>Plans from a to b; returns a log line when the path's kind changed (or always when forced), else null.</summary>
            public string Plan(Vector3 a, Vector3 b, bool force = false)
            {
                Plans++;
                _corners.Clear();
                Length = 0f;
                string state;
                var start = Snap(a, 3f) ?? Snap(a, 8f);
                if (start == null) state = "no NavMesh at the hero";
                else
                {
                    // the end, or the farthest NavMesh point on the way to it (its cell may not have a NavMesh yet)
                    Vector3? end = Snap(b, 4f) ?? Snap(b, 12f);
                    bool cut = false;
                    if (end == null)
                    {
                        float d = Flat(b - a);
                        foreach (float k in new[] { 150f, 100f, 60f, 30f })
                            if (k < d && (end = Snap(a + (b - a) * (k / d), 20f)) != null) { cut = true; break; }
                    }
                    if (end == null) state = "no NavMesh towards the target";
                    else if (!CalculateWithDrops(start.Value, end.Value)) state = "no NavMesh path";
                    else
                    {
                        var c = _path.corners;
                        for (int i = 1; i < c.Length; i++) { _corners.Add(c[i]); Length += Vector3.Distance(c[i - 1], c[i]); }
                        state = _path.status == UnityEngine.AI.NavMeshPathStatus.PathPartial ? "partial path" : cut ? "path to the NavMesh edge" : "path";
                    }
                }
                if (FirstLength < 0 && Length > 0) FirstLength = Length;
                bool changed = state != _state;
                _state = state;
                return changed || force || Plans == 1 ? Describe() : null;
            }

            /// <summary>
            /// The path; a partial one first gets a drop link down the terrace step where it ends (<see cref="NavDropLinks"/>,
            /// the same one-way links the NPC walkers use; the hero's controller falls down them) and is planned again.
            /// </summary>
            bool CalculateWithDrops(Vector3 a, Vector3 b)
            {
                bool ok = UnityEngine.AI.NavMesh.CalculatePath(a, b, _filter, _path) && _path.status != UnityEngine.AI.NavMeshPathStatus.PathInvalid;
                if (ok && _path.status == UnityEngine.AI.NavMeshPathStatus.PathPartial && NavDropLinks.TryLinkTowards(a, b, _filter.agentTypeID))
                    ok = UnityEngine.AI.NavMesh.CalculatePath(a, b, _filter, _path) && _path.status != UnityEngine.AI.NavMeshPathStatus.PathInvalid;
                return ok;
            }

            /// <summary>The last plan is a partial path (its end is not the destination's NavMesh).</summary>
            public bool Partial => _state == "partial path";

            /// <summary>
            /// The hero's place when it is connected to <paramref name="anchor"/> (the NPC) on the NavMesh, else the nearest
            /// NavMesh point (rings of 3 to 40 m around him, 12 directions) that is; null when none is found.
            /// </summary>
            public Vector3? Connected(Vector3 hero, Vector3 anchor, out string note)
            {
                var goal = Snap(anchor, 3f) ?? Snap(anchor, 8f);
                if (goal == null) { note = "no NavMesh at the NPC"; return null; }
                var start = Snap(hero, 3f) ?? Snap(hero, 8f);
                if (start != null && Complete(start.Value, goal.Value)) { note = "connected"; return start; }
                note = start == null ? "no NavMesh at the hero" : "no complete path to the NPC";
                foreach (float r in new[] { 3f, 6f, 10f, 15f, 25f, 40f })
                {
                    Vector3? best = null;
                    float bestD = float.MaxValue;
                    for (int k = 0; k < 12; k++)
                    {
                        float a = k * Mathf.PI / 6f;
                        var q = Snap(hero + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * r, 3f);
                        if (q == null || Mathf.Abs(q.Value.y - hero.y) > r + 2f || !Complete(q.Value, goal.Value)) continue;
                        float d = Vector3.Distance(q.Value, hero);
                        if (d < bestD) { bestD = d; best = q; }
                    }
                    if (best != null) return best;
                }
                return null;
            }

            /// <summary>fix-190: a complete NavMesh path joins the NavMesh points near a and b (the last plan's corners stay).</summary>
            public bool Joins(Vector3 a, Vector3 b)
            {
                var from = Snap(a, 3f) ?? Snap(a, 8f);
                var to = Snap(b, 4f) ?? Snap(b, 12f);
                if (from == null || to == null) return false;
                var p = new UnityEngine.AI.NavMeshPath();
                return UnityEngine.AI.NavMesh.CalculatePath(from.Value, to.Value, _filter, p) && p.status == UnityEngine.AI.NavMeshPathStatus.PathComplete;
            }

            bool Complete(Vector3 a, Vector3 b) =>
                UnityEngine.AI.NavMesh.CalculatePath(a, b, _filter, _path) && _path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete;

            /// <summary>fix-57: a complete NavMesh path joins a and b and is at most <paramref name="maxLength"/> m long.</summary>
            public bool Reaches(Vector3 a, Vector3 b, float maxLength)
            {
                if (!Complete(a, b)) return false;
                var c = _path.corners;
                float len = 0f;
                for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
                return len <= maxLength;
            }

            /// <summary>fix-57: distance from a NavMesh point to the NavMesh's edge (a ledge rim, a wall), m; 0 when unknown.</summary>
            public float EdgeDistance(Vector3 p) =>
                UnityEngine.AI.NavMesh.FindClosestEdge(p, out var hit, _filter) ? hit.distance : 0f;

            public string Describe() => _corners.Count > 0 ? $"{_state} {F(Length)} m, {_corners.Count} corners" : _state + ", going straight";

            /// <summary>The next corner to run to (corners reached within 1 m are dropped); straight at dest when there is no path.</summary>
            public Vector3 Next(Vector3 hero, Vector3 dest)
            {
                while (_corners.Count > 1 && Flat(_corners[0] - hero) < 1f) _corners.RemoveAt(0);
                return _corners.Count > 0 ? _corners[0] : dest;
            }

            /// <summary>Metres left along the planned corners (straight to <paramref name="dest"/> without a path), flat.</summary>
            public float Remaining(Vector3 hero, Vector3 dest)
            {
                if (_corners.Count == 0) return Flat(dest - hero);
                float d = Flat(_corners[0] - hero);
                for (int i = 1; i < _corners.Count; i++) d += Flat(_corners[i] - _corners[i - 1]);
                return d;
            }

            /// <summary>The point <paramref name="distance"/> m further along the remaining path (on the NavMesh).</summary>
            public Vector3 Ahead(Vector3 hero, float distance)
            {
                var p = hero;
                foreach (var c in _corners)
                {
                    float seg = Vector3.Distance(p, c);
                    if (seg >= distance) return p + (c - p) * (distance / seg);
                    distance -= seg;
                    p = c;
                }
                return p;
            }
        }
        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        /// <summary>Liberated: fleeing, or already exchanged by the liberation switch (status back to Routine, switched).</summary>
        bool Freed(string enclave) => _session.Enclaves.Of(enclave).Status == EnclaveStatus.Flee || _session.Enclaves.IsSwitched(enclave);

        /// <summary>
        /// fix-105: the object 'tp obj:Name dist=' stood the hero beside. Later 'obj:Name' targets (use, look, useprobe) mean that
        /// one while the hero is within <see cref="PinnedObjectRange"/> m of it, though another of the name may be nearer by
        /// then (r40 fix75: 3 m from one chest put him 1.3 m from its neighbour, and 'use' took the neighbour: walked 0.4 m).
        /// </summary>
        InteractiveObjectDef _pinnedObject;
        const float PinnedObjectRange = 8f;

        /// <summary>The interactive object of that entity name nearest to the hero (names repeat), or the pinned one (fix-105).</summary>
        bool TryObject(string name, out InteractiveObjectDef def)
        {
            var hp = Hero != null ? Hero.transform.position : Vector3.zero;
            var pin = _pinnedObject;
            if (pin != null && string.Equals(pin.Name, name, StringComparison.OrdinalIgnoreCase) && _session.Interaction.Objects.IsInWorld(pin) &&
                (new Vector3(pin.Position[0], pin.Position[1], pin.Position[2]) - hp).sqrMagnitude <= PinnedObjectRange * PinnedObjectRange)
            {
                def = pin;
                return true;
            }
            def = _session.Content.Database.All<InteractiveObjectDef>()
                          .Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) && o.Position != null && _session.Interaction.Objects.IsInWorld(o))
                          .OrderBy(o => (new Vector3(o.Position[0], o.Position[1], o.Position[2]) - hp).sqrMagnitude)
                          .FirstOrDefault();
            return def != null;
        }

        // ---------------------------------------------------------------- story targets

        /// <summary>"place:Id" (named place), "zone:Name" (a point inside the AI zone), "obj:Name" (interactive object).</summary>
        bool TryStoryTarget(string text, out Vector3 position, out bool hasY, out string error)
        {
            position = default;
            hasY = true;
            error = null;
            if (!StoryScenario.TryParseTarget(text, out string kind, out string name)) { error = "not a story target"; return false; }
            switch (kind)
            {
                case "place":
                    if (_session.Party.Positions.TryGetPlace(name, out var pose))
                    {
                        position = new Vector3(pose.X, pose.Y, pose.Z);
                        return true;
                    }
                    error = $"no place '{name}'";
                    return false;
                case "zone":
                {
                    var zone = _session.Content.Database.All<AiZoneDef>().FirstOrDefault(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (zone == null || !StoryScenario.TryInside(zone.Outline, out float x, out float z)) { error = $"no zone '{name}'"; return false; }
                    position = new Vector3(x, zone.Floor + 1f, z);
                    return true;
                }
                default:
                    if (TryObject(name, out var def))
                    {
                        position = new Vector3(def.Position[0], def.Position[1], def.Position[2]);
                        return true;
                    }
                    error = $"no object '{name}'";
                    return false;
            }
        }

        /// <summary>A Speaker object (talking shrine, ore furnace, temple tester) used as an actor: its position.</summary>
        bool TrySpeakerPosition(ActorId id, out Vector3 position)
        {
            position = default;
            if (!TryObject(id.Name, out var def) || def.Kind != InteractiveKind.Speaker) return false;
            position = new Vector3(def.Position[0], def.Position[1], def.Position[2]);
            return true;
        }

        // ---------------------------------------------------------------- conditions

        bool TryStoryCondition(ScenarioCondition c, out string value)
        {
            var s = _session;
            value = null;
            switch (c.Kind)
            {
                case "given":
                {
                    var world = StoryWorldOf();
                    value = world == null ? "-" : world.DialogMemory.WasGiven(new Id<DialogEntryDef>(c.Arg)).ToString();
                    return true;
                }
                case "offered":
                    value = (s.Dialog.Current != null && s.Dialog.Current.Phase == ConversationPhase.AwaitingChoice &&
                             s.Dialog.Current.Options.Any(o => o.Entry.Name == c.Arg)).ToString();
                    return true;
                case "available":
                    value = s.Content.Database.TryGet(new Id<DialogEntryDef>(c.Arg), out DialogEntryDef def) && TryAvailable(def, def.Owner, out bool a) ? a.ToString() : "-";
                    return true;
                case "inarea":
                    value = s.WorldState.Areas.TryIsInside(ActorId.Player, c.Arg, out bool inside) ? inside.ToString() : "-";
                    return true;
                case "present":
                    value = s.Npcs.TryGet(Actor(c.Arg), out var agent) ? s.Npcs.IsInWorld(agent).ToString()
                          : TryObject(c.Arg, out var obj) ? s.Interaction.Objects.IsInWorld(obj).ToString() : "False";
                    return true;
                case "talked":
                {
                    var world = StoryWorldOf();
                    value = world == null ? "-" : world.Actors.HasTalkedToPlayer(Actor(c.Arg)).ToString();
                    return true;
                }
                case "guide":
                    value = s.Party.Members.Guide != null ? s.Party.Members.Guide.Npc.Name : "-";
                    return true;
                case "chapter":
                    value = s.Chapter.Current.ToString();
                    return true;
                default:
                    return false;
            }
        }
    }
}
#endif
