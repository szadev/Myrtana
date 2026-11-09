#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using Myrtana.Story;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    public sealed partial class ScenarioRunner
    {
        const string DefaultMelee = "It_1H_Sword_01";

        IEnumerator Execute(ScenarioStep st)
        {
            var s = _session;
            var P = ActorId.Player;
            switch (st.Op)
            {
                // ------------------------------------------------ world / hero
                case "safe":
                {
                    int fame = st.Int("fame", 80);
                    int have = s.Fame.Of(FameKind.Political, "Orc");
                    if (have < fame) s.Fame.Add(FameKind.Political, "Orc", fame - have);
                    s.NpcCombat.DisengageAll();
                    // fix-65 (R32-4, r32 object_anims: 13 Faring NPCs attacked again 1.2 s after 'safe'): the ended fights left
                    // grudges (Cancelled, reason Enemy: G3 IsUpset attacks at once) - they go, with the crimes short of
                    // murder and the warnings/observations in progress. alarm=1 also calms enclaves in Attack (not by
                    // default: a boss kill's alarm is how an enclave gets liberated).
                    int forgiven = s.Crime.Ledger.ForgiveGrudges();
                    s.Crime.Crime.ClearTransient();
                    var calmed = new List<string>();
                    if (st.Bool("alarm", false))
                        foreach (var kv in s.Enclaves.NotQuiet.ToList())
                            if (kv.Value.Status == EnclaveStatus.Attack)
                            {
                                s.Enclaves.SetStatus(kv.Key, EnclaveStatus.Routine);
                                calmed.Add(kv.Key);
                            }
                    HideTips();
                    GamePause.NormalSpeed(); // never past a pause owner (menu, intro, loading)
                    Log($"safe: Orc fame {s.Fame.Of(FameKind.Political, "Orc")}, fights ended, {forgiven} grudges/crimes forgiven" +
                        $"{(calmed.Count > 0 ? ", alarm ended in " + string.Join(",", calmed) : "")}, tips hidden");
                    break;
                }
                case "notips":
                    HideTips();
                    GamePause.NormalSpeed(); // never past a pause owner (menu, intro, loading)
                    Log("tips hidden");
                    break;
                case "god":
                    // fix-132: a damage filter (ActorVitals.PlayerInvulnerable), the HP attribute stays the hero's own
                    _god = st.Bool("on", true);
                    if (!_god) _godUntil = 0;
                    Log($"god {(_god ? "on" : "off")}: hp {s.Combat.Vitals.HitPoints(ActorId.Player)}/{s.Combat.Vitals.HitPointsMax(ActorId.Player)}");
                    break;
                case "noclip":
                {
                    if (!G3.Player.NoclipRules.TryParse(st.Get("on"), out bool? want)) { Fail($"noclip: '{st.Get("on")}' is not on/off"); break; }
                    var hero = Hero;
                    if (hero == null) { Fail("noclip: no hero"); break; }
                    if (st.Has("speed")) hero.noclipSpeed = G3.Player.NoclipRules.Clamp(st.Float("speed", G3.Player.NoclipRules.Speed));
                    bool on = G3.Player.NoclipRules.Next(hero.IsNoclip, want);
                    string where = hero.SetNoclip(on);
                    Log(on ? $"noclip on ({hero.noclipSpeed:0} m/s; WASD, Space up, Ctrl down, Shift fast)"
                           : "noclip off" + (string.IsNullOrEmpty(where) ? "" : ": " + where));
                    break;
                }
                case "fps": // fix-172: the FPS overlay on|off (console; not stored)
                    FpsStep(st);
                    break;
                case "perfreport": // fix-172: frame-time report to Player.log + persistentDataPath/perf_<date>.txt
                    yield return PerfReportStep(st);
                    break;
                case "perfhere": // fix-171: the remaster A/B at the hero's place (G3PerfHere)
                {
                    string r = G3.Unity.G3PerfHere.Run(Mathf.Max(1, (int)st.Float("rounds", 1f)), 3f, 1.5f, st.Float("warmup", 5f), true);
                    Log("perfhere: " + r + " (report: perf_here.txt in the player data folder, ~4 min per round)");
                    if (st.Bool("wait", false)) { yield return null; while (G3.Unity.G3PerfHere.Running) yield return null; Log(G3.Unity.G3PerfHere.LastReport); }
                    break;
                }
                case "tp":
                    yield return TeleportStep(st);
                    break;
                case "time":
                {
                    float hour = st.Float("hour", 12f);
                    if (s.Clock is GameClock clock)
                    {
                        clock.Skip(clock.SecondsUntilHour(hour));
                        Log(FormattableString.Invariant($"time {s.Clock.HourOfDay:0.00} (day {s.Clock.Day})"));
                    }
                    else Fail("time: session clock is not a GameClock");
                    yield return null;
                    break;
                }
                case "weather":
                {
                    string state = st.Get("state");
                    G3WeatherDirector.Force(state);
                    _weatherForced = !string.IsNullOrEmpty(state);
                    Log("weather " + (state ?? "schedule"));
                    break;
                }

                // ------------------------------------------------ NPCs
                case "spawn":
                    yield return SpawnStep(st);
                    break;
                case "bring":
                    yield return BringStep(st);
                    break;
                case "calm":
                case "freeze":
                {
                    var ids = Selection(st, st.Float("radius", st.Op == "calm" ? 40f : 60f));
                    SetCalm(ids, st.Op == "freeze");
                    Log($"{st.Op} {ids.Count}: {Short(ids)}");
                    break;
                }
                case "uncalm":
                    ClearCalm(false);
                    Log("uncalm");
                    break;
                case "unfreeze":
                    ClearCalm(true);
                    Log("unfreeze");
                    break;
                case "despawn":
                {
                    var ids = st.Has("target") ? new List<ActorId> { Actor(st.Get("target")) } : Selection(st, st.Float("radius", 20f));
                    int n = 0;
                    foreach (var id in ids)
                    {
                        // The session's one removal path: every per-actor store forgets it (a dev spawn's definition goes too).
                        bool simulated = s.Npcs.TryGet(id, out _);
                        if (s.Removal.Remove(id) && simulated) n++;
                    }
                    Log($"despawn {n}: {Short(ids)}");
                    yield return null;
                    break;
                }
                case "engage":
                {
                    var npc = Actor(st.Get("npc"));
                    var target = Actor(st.Get("target", "hero"));
                    var reason = Enum.TryParse<AttackReason>(st.Get("reason", "Enemy"), true, out var r) ? r : AttackReason.Enemy;
                    _calm.Remove(npc); _frozen.Remove(npc);
                    bool ok = s.NpcCombat.Engage(npc, target, reason);
                    if (!ok) Fail($"engage {npc} -> {target}: refused (no brain or paralysed)");
                    else Log($"engage {npc} -> {target} ({reason})");
                    break;
                }
                case "disengage":
                    if (st.Has("npc")) s.NpcCombat.Get(Actor(st.Get("npc")))?.Disengage();
                    else s.NpcCombat.DisengageAll();
                    Log("disengage " + st.Get("npc", "all"));
                    break;
                case "party":
                {
                    var npc = Actor(st.Get("npc"));
                    string mode = st.Get("mode", "add").ToLowerInvariant();
                    var type = Enum.TryParse<PartyMemberType>(st.Get("type", "Party"), true, out var t) ? t : PartyMemberType.Party;
                    if (mode == "add") s.Party.JoinCompanion(npc, type);
                    else if (mode == "wait") s.Party.Members.Wait(npc);
                    else s.Party.Dismiss(npc);
                    Log($"party {mode} {npc}: {s.Party.Members.TypeOf(npc)}");
                    break;
                }

                case "touchwatch":
                {
                    if (st.Bool("on", true))
                    {
                        var victim = Actor(st.Get("victim", "hero"));
                        StartTouchWatch(victim);
                        Log($"touchwatch {victim}: counting weapon, missile and spell touches on it");
                    }
                    else StopTouchWatch(log: true);
                    break;
                }

                // ------------------------------------------------ vitals
                case "hp":
                {
                    var id = Actor(st.Get("actor"));
                    int value = st.Int("value", 3);
                    if (id.IsPlayer)
                    {
                        GodOff();
                        s.Hero.Stats.Set("HP", new StatValue(value, st.Int("max", Math.Max(value, s.Hero.Stats.Maximum("HP")))));
                    }
                    else s.Combat.Vitals.AddHitPoints(id, value - s.Combat.Vitals.HitPoints(id));
                    Log($"hp {id} = {s.Combat.Vitals.HitPoints(id)}");
                    break;
                }
                case "ko":
                {
                    var id = Actor(st.Get("actor"));
                    if (id.IsPlayer) GodOff();
                    s.Combat.Vitals.AddHitPoints(id, -s.Combat.Vitals.HitPoints(id));
                    s.Combat.Vitals.KnockOut(id, !id.IsPlayer);
                    Log($"ko {id}: {s.Combat.Vitals.StateOf(id)}");
                    yield return null;
                    break;
                }
                case "kill":
                {
                    var id = Actor(st.Get("actor"));
                    if (id.IsPlayer && st.Has("aim"))
                    {
                        // fix-101: killed while drawing the bow (aim=s: the left button held that long first)
                        if (!Combat.Fighter.RangedDrawn) { Fail("kill aim=: no bow or crossbow drawn"); break; }
                        Combat.PrimaryDown();
                        yield return new WaitForSeconds(st.Float("aim", 0.6f));
                        Log($"kill {id}: aiming ({Combat.Fighter.Animator.StateText})");
                    }
                    if (id.IsPlayer) GodOff();
                    s.Combat.Vitals.Kill(id, string.Equals(st.Get("killer"), "hero", StringComparison.OrdinalIgnoreCase));
                    Log($"kill {id}: {s.Combat.Vitals.StateOf(id)}");
                    yield return null;
                    break;
                }
                case "joinnpc":
                {
                    var id = Actor(st.Get("npc"));
                    var leader = Actor(st.Get("leader"));
                    Log($"joinnpc {id} → {leader}: {s.Life.JoinNpc(id, leader)}");
                    break;
                }
                case "npctask":
                {
                    var id = Actor(st.Get("npc"));
                    if (!s.Npcs.TryGet(id, out var agent)) { Log($"npctask {id}: no agent"); break; }
                    string leaderName = s.Npcs.TryGetNpcLeader(id, out var npcLeader) ? npcLeader.Id.Name : "-";
                    var watch = Views != null && Views.TryGetView(id, out var tv) && tv.TryGetComponent<NpcView>(out var nv) && nv.WatchedFight != null
                        ? $"{nv.WatchedFight.Step} {nv.WatchedFight.Focus}" : "-";
                    Log($"npctask {id}: {agent.Activity} {agent.Station} '{s.Npcs.InteractionOf(agent)}' task {agent.Task}#{agent.TaskStep} " +
                        $"listens {(agent.ListeningTo.IsEmpty ? "-" : agent.ListeningTo.Name)} sermon {s.Npcs.SermonLine(id)} leader {leaderName} watch {watch} " +
                        // fix-46: a bystander that joined the fight shows here (r28 npc_watch_fight: task None, watch -)
                        $"fight {(s.NpcCombat.Get(id) is NpcCombatBrain fb && fb.IsFighting ? $"{fb.State.Phase}->{fb.State.Target}" : "-")} " +
                        $"detail {agent.Detail}");
                    break;
                }
                case "corpse":
                {
                    var id = Actor(st.Get("npc"));
                    double hours = st.Float("hours", 121f);
                    s.Life.Corpses.Stamp(id, s.Clock.GameSeconds / 3600.0 - hours);
                    var verdict = s.Life.EnterProcessingRange(id);
                    Log($"corpse {id} died {hours:0.#} h ago: {verdict}, state {s.Combat.Vitals.StateOf(id)}, present {s.Npcs.TryGet(id, out _)}");
                    yield return null;
                    break;
                }
                case "sinkbody":
                {
                    // fix-163: pushes an NPC's body depth m into the ground (the safety net / station floor must bring it back)
                    var id = Actor(st.Get("npc"));
                    float depth = st.Float("depth", 0.6f);
                    if (!TryActorPosition(id, out var at, out var body) || body == null) { Fail($"sinkbody: no view of {id}"); break; }
                    body.position = at + Vector3.down * depth;
                    Physics.SyncTransforms();
                    Log($"sinkbody {id}: {V(at)} -> {V(body.position)}");
                    yield return null;
                    break;
                }
                case "restore":
                    s.Combat.Vitals.RestoreFully(Actor(st.Get("actor")));
                    Log("restore " + st.Get("actor", "hero"));
                    break;
                case "bump": // fix-86
                    yield return BumpStep(st);
                    break;
                case "npcpass": // fix-93
                    yield return NpcPassStep(st);
                    break;
                case "slopeground": // fix-108
                    yield return SlopeGroundStep(st);
                    break;
                case "caveplace": // fix-128
                    yield return CavePlaceStep(st);
                    break;
                case "arrowdodge": // fix-93
                    yield return ArrowDodgeStep(st);
                    break;
                case "loose": // fix-176
                    yield return LooseStep(st);
                    break;
                case "strike": // fix-93
                    StrikeStep(st);
                    break;
                case "unequip": // fix-97
                    UnequipStep(st);
                    break;
                case "xpmark": // fix-97
                    _xpMark = _session.Hero.Xp;
                    Log($"xpmark {_xpMark}");
                    break;
                case "signpost": // fix-80
                    yield return SignpostStep(st);
                    break;
                case "fight":
                    yield return FightStep(st);
                    break;
                case "finisher":
                    yield return FinisherStep(st);
                    break;

                // ------------------------------------------------ items / progression / story
                case "give":
                {
                    string item = ResolveItem(st.Get("item"));
                    if (item == null) break;
                    s.Items.Inventories.Give(P, item, st.Int("n", 1));
                    Log($"give {item}: {s.Items.Inventories.Of(P).Count(item)}");
                    break;
                }
                case "docs":
                {
                    var items = DocumentsPage.AllOfKind(s, st.Get("kind", "all"));
                    if (items == null) { Fail("docs: unknown kind " + st.Get("kind") + " (" + string.Join("|", DocumentsPage.GiveKinds) + ")"); break; }
                    int given = 0;
                    foreach (var item in items)
                        if (s.Items.Inventories.Of(P).Count(item) == 0) { s.Items.Inventories.Give(P, item, 1); given++; }
                    Log($"docs {st.Get("kind", "all")}: {given} given, {items.Count} of the kind");
                    string open = st.Get("open");
                    if (string.IsNullOrEmpty(open)) break;
                    var tab = DocumentsPage.ParseTab(open);
                    var screens = FindAnyObjectByType<GameScreens>();
                    if (tab == null || screens == null || !(screens.ByKey(UnityEngine.InputSystem.Key.M) is G3MapScreen page))
                    {
                        Fail("docs: cannot open the documents page on '" + open + "'");
                        break;
                    }
                    if (screens.Current != page) screens.Open(page);
                    page.ShowTab(tab.Value);
                    yield return new WaitForSeconds(0.3f);
                    break;
                }
                case "equip":
                {
                    string item = ResolveItem(st.Get("item"));
                    if (item == null) break;
                    var eq = s.Items.Equipment;
                    var inv = s.Items.Inventories.Of(P);
                    var wornIn = ((WornSlot[])Enum.GetValues(typeof(WornSlot))).Where(sl => eq.In(sl) == item).ToList();
                    bool weaponSlot = s.Content.Database.TryGet(item, out ItemDef eqDef) &&
                                      (eqDef.Slot == EquipSlot.MeleeWeapon || eqDef.Slot == EquipSlot.RangedWeapon || eqDef.Slot == EquipSlot.Shield || eqDef.Slot == EquipSlot.Ammo);
                    // fix-65 (R32-5): the rotation's loadout keeps its weapons; the scenario's item is still carried
                    if (ScenarioLoadout.KeepsRotationWeapons(_loadoutRequested, _inLoadoutOps > 0, weaponSlot))
                    {
                        if (inv.Count(item) == 0) s.Items.Inventories.Give(P, item, 1);
                        Log($"equip {item}: kept the loadout {_opt.Loadout}'s weapons (melee {eq.In(WornSlot.MeleeWeapon) ?? "-"}, ranged {eq.In(WornSlot.RangedWeapon) ?? "-"}, " +
                            $"off-hand {eq.In(WornSlot.OffHand) ?? "-"}, shield {eq.In(WornSlot.Shield) ?? "-"}); {item} carried");
                        break;
                    }
                    // fix-65 (R32-5, r32 ch04/05/14/15 "equip It_1H_Sword_01: NotCarried"): worn already (by the loadout) = done
                    if (wornIn.Any(sl => sl != WornSlot.OffHand))
                    {
                        Log($"equip {item}: already worn ({string.Join(",", wornIn)})");
                        break;
                    }
                    // worn only in the left hand: equipping it as the main weapon needs a second one
                    if (inv.Count(item) < wornIn.Count + 1) s.Items.Inventories.Give(P, item, wornIn.Count + 1 - inv.Count(item));
                    var res = eq.Equip(item);
                    if (res != EquipResult.Done) Fail($"equip {item}: {res}");
                    else Log("equip " + item);
                    yield return new WaitForSeconds(0.3f);
                    break;
                }
                case "quick":
                {
                    // A quick-slot key with a weapon (PlayerCombat.TryReady, G3 OnPlayerUseQuickSlot): `quick item=X` — with
                    // Perk_1H1H_1 and a one-handed main weapon a second one-handed weapon goes into the left hand (fix-32).
                    string item = ResolveItem(st.Get("item"));
                    if (item == null) break;
                    if (s.Items.Inventories.Of(P).Count(item) == 0) s.Items.Inventories.Give(P, item, 1);
                    float t0 = Time.time;
                    while (Combat.Fighter.Animator.Busy && Time.time - t0 < 3f) yield return null;
                    bool ok = Combat.TryReady(item);
                    Log($"quick {item}: {(ok ? "readied" : "refused")}, main {s.Items.Equipment.In(WornSlot.MeleeWeapon) ?? "-"}, " +
                        $"off-hand {s.Items.Equipment.In(WornSlot.OffHand) ?? "-"}");
                    t0 = Time.time;
                    do yield return new WaitForSeconds(0.1f);
                    while ((Combat.Fighter.Animator.Busy || !Armed(Combat.Fighter.Animator) && Combat.Fighter.Animator.Drawn) && Time.time - t0 < 4f);
                    Log($"quick {item}: dual {Combat.Fighter.DualWielding}, hand {Combat.Fighter.Animator.StateText}");
                    break;
                }
                case "draw":
                    yield return DrawStep(st.Get("kind") ?? StyleDrawKind());
                    break;
                case "sheathe":
                    yield return SheatheStep("sheathe");
                    break;
                case "drawtrace":
                    yield return DrawTraceStep(st);
                    break;
                case "switchtrace":
                    yield return SwitchTraceStep(st);
                    break;
                case "aimstrafe":
                    yield return AimStrafeStep(st);
                    break;
                case "gaitcheck":
                    yield return GaitCheckStep(st);
                    break;
                case "learn":
                    foreach (var skill in st.Get("skills").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                    {
                        // a misspelt perk (Perk_2H_1, Perk_CrossBow_1) used to be learned silently and the equip failed later
                        if (!SkillCatalog.Exists(s.Content.Database, skill, out var hint))
                        {
                            Fail($"learn: '{skill}' is not a G3 skill" + (hint != null ? $" (did you mean {hint}?)" : ""));
                            break;
                        }
                        s.Progression.LearnSkill(skill);
                    }
                    Log("learn " + st.Get("skills"));
                    break;
                case "attr":
                {
                    string name = st.Get("name").ToUpperInvariant();
                    int value = st.Int("value", 100);
                    if (st.Bool("atleast", false) && s.Hero.Stats.Value(name) >= value) { Log($"attr {name} = {s.Hero.Stats.Value(name)} (>= {value}, kept)"); break; }
                    s.Hero.Stats.Set(name, new StatValue(value, Math.Max(value, s.Hero.Stats.Maximum(name))));
                    Log($"attr {name} = {s.Hero.Stats.Value(name)}");
                    break;
                }
                case "level":
                {
                    int target = st.Int("n", 3), guard = 0;
                    while (s.Hero.Level < target && guard++ < 200) s.Progression.AwardXp((int)Math.Max(1, s.Progression.XpForNextLevel));
                    Log("level " + s.Hero.Level);
                    break;
                }
                case "xp":
                {
                    int amount = st.Int("n", 0);
                    if (amount <= 0) { Fail("xp n=: a positive amount"); break; }
                    s.Progression.AwardXp(amount);
                    Log($"xp +{amount} = {s.Hero.Xp}, level {s.Hero.Level}");
                    break;
                }
                case "quest":
                {
                    var to = Enum.TryParse<QuestStatus>(st.Get("status", "Running"), true, out var qs) ? qs : QuestStatus.Running;
                    foreach (string id in StoryScenario.List(st.Get("id")))
                    {
                        var q = new Id<QuestDef>(id);
                        if (!s.Content.Database.TryGet(q, out QuestDef _)) { Fail($"quest {id}: unknown"); continue; }
                        bool ok = s.Quests.TrySetStatus(q, to);
                        if (!ok && s.Quests.StatusOf(q) != to) Fail($"quest {q}: {s.Quests.StatusOf(q)} -> {to} refused");
                        else Log($"quest {q} = {s.Quests.StatusOf(q)}");
                    }
                    break;
                }
                case "flag":
                    foreach (string name in StoryScenario.List(st.Get("name")))
                    {
                        if (st.Bool("on", true)) s.Flags.Set(name);
                        else s.Flags.Clear(name);
                        Log($"flag {name} = {s.Flags.IsSet(name)}");
                    }
                    break;
                case "sector":
                {
                    bool ok = s.WorldState.Sectors.Set(st.Get("name"), st.Bool("on", true));
                    if (!ok && !s.WorldState.Sectors.IsKnown(st.Get("name"))) Fail("sector unknown: " + st.Get("name"));
                    else Log($"sector {st.Get("name")} = {s.WorldState.Sectors.IsEnabled(st.Get("name"))}");
                    yield return null;
                    break;
                }
                case "fame":
                {
                    var kind = Enum.TryParse<FameKind>(st.Get("kind", "Political"), true, out var k) ? k : FameKind.Political;
                    s.Fame.Add(kind, st.Get("group"), st.Int("value", 80));
                    Log($"fame {kind} {st.Get("group")} = {s.Fame.Of(kind, st.Get("group"))}");
                    break;
                }
                case "enclave":
                {
                    string id = st.Get("id"), status = st.Get("status", "Routine");
                    if (status.Equals("liberate", StringComparison.OrdinalIgnoreCase)) s.WorldState.Liberation.Liberate(id);
                    else if (Enum.TryParse<EnclaveStatus>(status, true, out var es)) s.Enclaves.SetStatus(id, es);
                    else { Fail("enclave status " + status); break; }
                    Log($"enclave {id} = {s.Enclaves.Of(id).Status}");
                    break;
                }
                case "torch":
                {
                    var view = HeroTorchView.Instance;
                    if (view == null) { Fail("torch: no HeroTorchView"); break; }
                    if (st.Bool("on", true))
                    {
                        // It_Torch is the unlit item (no flame/light data); the quick-slot torch is It_Torch_Burning [runner-16]
                        const string torch = "It_Torch_Burning";
                        if (s.Items.Inventories.Of(P).Count(torch) == 0) s.Items.Inventories.Give(P, torch, 1);
                        if (!(view.Torch.InHand && view.Torch.Item == torch) && !view.TryUse(torch)) Fail("torch: TryUse refused");
                    }
                    else view.Torch.Release();
                    yield return new WaitForSeconds(1.2f);
                    Log("torch in hand=" + view.Torch.InHand);
                    break;
                }
                case "turnkeys": // fix-73: the port option "Q / E orbit the camera" for this scenario
                    SetTurnKeys(st.Bool("orbit", true));
                    Log($"turnkeys {(G3Player.TurnKeysOrbitCamera ? "orbit camera" : "turn hero (original)")}");
                    break;
                case "cookfire": // fix-88: the port option "Cook on any fire" for this scenario (off = original)
                    SetCookFire(st.Bool("on", true));
                    break;
                case "windowcam": // fix-122: the port option "Camera in windows" for this scenario (still = default, original = G3 zoom)
                    SetWindowCam(st.Get("mode", "still"));
                    break;
                case "cammark": // fix-122: remember the hero camera's distance / FOV / pose (condition camchange:)
                    CamMark();
                    break;
                case "shieldbeasts": // fix-109: the port option "Shield protects from beasts" for this scenario (off = original)
                    SetShieldBeasts(st.Bool("on", true));
                    break;
                case "forgivehits": // fix-162: the port option "Forgive accidental hits" for this scenario (off = original)
                    SetForgiveHits(st.Bool("on", true));
                    break;
                case "strayhit": // fix-162: the hero's arrow lands on an NPC (count times at once)
                    StrayHit(Actor(st.Get("npc")), st.Int("count", 1), st.Int("dmg", 1));
                    break;
                case "latetrophies": // fix-143: the port option "Late trophies" for this scenario (off = original)
                    SetLateTrophies(st.Bool("on", true));
                    break;
                case "forcedrelease": // fix-161: the port option "Freeing by force counts" for this scenario (off = original)
                    SetForcedRelease(st.Bool("on", true));
                    break;
                case "chestfilter": // fix-116: the map page's chest filter for this scenario (fix-102's option row is gone)
                    SetChestFilter(st.Bool("on", true));
                    break;
                case "mapfilter": // fix-126: one filter of the map views' filter bar for this scenario
                {
                    var filter = ParseMapFilter(st.Get("name"));
                    if (filter == null) { Fail("mapfilter: name=chests|names|companions|quests, not '" + st.Get("name") + "'"); break; }
                    SetMapFilter(filter.Value, st.Bool("on", true));
                    break;
                }
                case "questlog": // fix-126: the quest log page (its world map with the quest talk marks)
                    yield return OpenQuestLog();
                    break;
                case "lockedchest": // fix-102: the hero beside the nearest locked chest (ScenarioRunner.Fix102.cs)
                    yield return LockedChestStep(st);
                    break;
                case "relockchest": // fix-121: that chest shut again for the next pick attempt
                    RelockChest(st.Bool("clear", false));
                    break;
                case "losfloor": // fix-104: the hero on a test floor over an NPC (ScenarioRunner.Fix104.cs)
                    yield return LosFloorStep(st);
                    break;
                case "loswall": // fix-104: a test wall between the hero and an NPC
                    LosWall(st);
                    yield return null;
                    break;
                case "lositem": // fix-104: an item dropped in front of the hero
                    LosItem(st);
                    yield return null;
                    break;
                case "losshelf": // fix-114: a solid shelf box around the lositem item (ScenarioRunner.Fix104.cs)
                    LosShelf(st);
                    yield return null;
                    break;
                case "quickslot": // fix-105: a quick-slot key the player's way (weapons, spells, the torch)
                    yield return QuickSlotStep(st);
                    break;
                case "messhero": // fix-105: a broken hero state for the load reset to clean up
                    yield return MessHeroStep();
                    break;
                case "losebone": // fix-105: a hero bone lost after the overlay masks were built
                    LoseBone(st);
                    yield return null;
                    break;
                case "losclear": // fix-104: the test geometry goes
                    ClearLosGeometry(true);
                    yield return null;
                    break;
                case "fry": // fix-88: fry on the open fry page
                    yield return FryStep(st);
                    break;
                case "synthselect": // fix-125: a row of the open craft page
                    SynthSelect(st);
                    yield return null;
                    break;
                case "decorfocus": // fix-79: the port option "Focus decorative objects" for this scenario (on = original)
                    SetDecorFocus(st.Bool("on", true));
                    Log($"decorfocus {(PlayerInteraction.FocusDecorativeObjects ? "yes (original)" : "no")}");
                    break;
                case "turnkey": // fix-73: holds the Camera rotation key left (Q) or right (E) as a player would
                {
                    string dir = st.Get("dir", "left").ToLowerInvariant();
                    float yaw0 = Hero.Yaw;
                    Hero.scriptedKeyTurn = dir.StartsWith("r") ? 1f : -1f;
                    yield return new WaitForSeconds(st.Float("secs", 1f));
                    Hero.scriptedKeyTurn = 0f;
                    yield return null;
                    TurnKeyHeroTurn = Mathf.Abs(Mathf.DeltaAngle(yaw0, Hero.Yaw));
                    var cam = FindAnyObjectByType<G3PlayerCamera>();
                    Log($"turnkey {dir}: hero turned {F(TurnKeyHeroTurn)}°, camera orbit {(cam != null ? F(cam.KeyOrbitYaw) : "-")}°, " +
                        $"camera yaw - hero yaw {(cam != null ? F(Mathf.DeltaAngle(Hero.Yaw, cam.transform.eulerAngles.y)) : "-")}°");
                    break;
                }
                case "post":
                    G3.Unity.G3CameraPost.Enabled = st.Bool("on", true);
                    Log($"post {(G3.Unity.G3CameraPost.Enabled ? "on" : "off")}");
                    break;
                case "lang":
                {
                    KeepLanguageBefore();
                    if (st.Has("voice")) s.Settings.Languages.VoiceLanguage = st.Get("voice");
                    if (st.Has("text")) s.Settings.Languages.TextLanguage = st.Get("text");
                    Log($"lang voice={s.Settings.Languages.VoiceLanguage} text={s.Settings.Languages.TextLanguage}");
                    break;
                }

                // ------------------------------------------------ dialog
                case "talk":
                {
                    var id = Actor(st.Get("npc"));
                    if (s.Dialog.IsActive) s.Dialog.Abort();
                    s.Dialog.Talk(id);
                    yield return new WaitForSeconds(0.3f);
                    Log($"talk {id}: {DialogState()}");
                    break;
                }
                case "choose":
                {
                    var c = s.Dialog.Current;
                    if (c == null) { Fail("choose: no dialog"); break; }
                    string e = st.Get("entry");
                    int index = int.TryParse(e, out int i) && i >= 0 && i < c.Options.Count ? i
                        : c.Options.ToList().FindIndex(o => string.Equals(o.Entry.ToString(), e, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) { Fail($"choose {e}: not offered ({DialogState()})"); break; }
                    var option = c.Options[index];
                    s.Dialog.Choose(option.Entry);
                    yield return new WaitForSeconds(0.2f);
                    Log($"choose {option.Entry}: {DialogState()}");
                    break;
                }
                case "lines":
                {
                    int max = st.Int("max", 40), n = 0;
                    while (s.Dialog.IsActive && n < max)
                    {
                        if (s.Dialog.Current.Phase == ConversationPhase.AwaitingLine) { s.Dialog.CompleteLine(); n++; }
                        else if (s.Dialog.Current.Phase == ConversationPhase.AwaitingChoice) break;
                        else if (s.Dialog.Current.Phase == ConversationPhase.AwaitingPage) break; // fix-140: a trade page holds the talk
                        yield return null;
                    }
                    Log($"lines {n}: {DialogState()}");
                    break;
                }
                case "abort":
                    if (s.Dialog.IsActive) s.Dialog.Abort();
                    yield return null;
                    Log("abort: " + DialogState());
                    break;
                case "forget":
                    Log(Forget(st.Get("entry")));
                    break;
                case "ask":
                    yield return AskStep(st);
                    break;
                case "topics":
                    TopicsStep(st);
                    break;
                case "played":
                    PlayedStep(st);
                    break;
                case "talked":
                    TalkedStep(st);
                    break;
                case "loot":
                    yield return LootStep(st);
                    break;
                case "pickup":
                    PickupStep(st);
                    break;
                case "use":
                    yield return UseStep(st);
                    break;
                case "standup":
                    yield return StandUpStep();
                    break;
                case "closepage":
                    yield return ClosePageStep();
                    break;
                case "takeall":
                    yield return TakeAllStep();
                    break;
                case "eat":
                    yield return EatStep(st);
                    break;
                case "rob":
                    yield return RobStep(st);
                    break;
                case "usenpc":
                    yield return UseNpcStep(st);
                    break;
                case "raid":
                    yield return RaidStep(st);
                    break;
                case "escort":
                    yield return EscortStep(st);
                    break;
                case "navislands":
                    yield return NavIslandsStep(st);
                    break;

                // ------------------------------------------------ inputs
                case "focus":
                {
                    string who = st.Get("npc");
                    if (ScenarioStep.TryBool(who, out var on) && !on) { _focus = null; Log("focus off"); break; }
                    _focus = Actor(who);
                    Log("focus " + _focus);
                    yield return null;
                    break;
                }
                case "look":
                {
                    if (!TryTarget(st.Get("target"), out var p, out _, out _, out _, out var err)) { Fail("look: " + err); break; }
                    Face(p);
                    yield return null;
                    Log("look " + st.Get("target"));
                    break;
                }
                case "aimat": // fix-90: put the crosshair (screen centre) on the target's body centre, as a player's mouse does
                {
                    if (!TryTarget(st.Get("target"), out var p, out _, out var aimActor, out _, out var err)) { Fail("aimat: " + err); break; }
                    var f = aimActor.HasValue ? FighterOf(aimActor.Value) : null;
                    float until = Time.time + st.Float("secs", 0.5f);
                    do { AimAt(BodyCentre(p, f)); yield return null; } while (Time.time < until);
                    Log($"aimat {st.Get("target")}: focus {(Combat.Focus != null ? Combat.Focus.Actor.ToString() : "-")}, aimdist {F(Combat.AimDistance())} m; scan {Combat.FocusScanText()}");
                    break;
                }
                case "move":
                {
                    string dir = st.Get("dir", "fwd").ToLowerInvariant();
                    var v = dir.StartsWith("b") ? new Vector2(0, -1) : dir.StartsWith("l") ? new Vector2(-1, 0) : dir.StartsWith("r") ? new Vector2(1, 0) : new Vector2(0, 1);
                    var p0 = Hero.transform.position;
                    Hero.scriptedWalk = st.Bool("walk", false);
                    Hero.scriptedMove = v;
                    yield return new WaitForSeconds(st.Float("secs", 1f));
                    Hero.scriptedMove = Vector2.zero;
                    Hero.scriptedWalk = false;
                    Log($"move {dir} {F((Hero.transform.position - p0).magnitude)} m, water={Hero.Water}");
                    break;
                }
                case "walkto":
                    yield return WalkTo(st);
                    break;
                case "hold":
                {
                    var keys = st.Get("keys").ToLowerInvariant().Split(',').Select(k => k.Trim()).ToList();
                    bool block = keys.Contains("block") || keys.Contains("secondary") || keys.Contains("parade");
                    bool attack = keys.Contains("attack") || keys.Contains("primary");
                    if (block) { Combat.SecondaryDown(); yield return new WaitForSeconds(0.2f); }
                    float secs = st.Float("secs", 1f);
                    string action;
                    bool paradeAtRelease;
                    if (attack && !block && secs < 0.1f)
                    {
                        // A tap shorter than the hold threshold: press and release with their real times in one frame, so a
                        // slow frame between them cannot turn it into a hold (the player's clicks are timed by their events).
                        double t = Time.timeAsDouble;
                        Combat.PrimaryDown(t - secs);
                        action = Combat.Fighter.Animator.Action;
                        Combat.PrimaryUp(t);
                        yield return new WaitForSeconds(secs);
                        paradeAtRelease = Combat.Fighter.Animator.HoldsParade;
                    }
                    else
                    {
                        if (attack) Combat.PrimaryDown();
                        yield return new WaitForSeconds(secs);
                        action = Combat.Fighter.Animator.Action;
                        paradeAtRelease = Combat.Fighter.Animator.HoldsParade;
                        if (attack) Combat.PrimaryUp();
                    }
                    if (block) { yield return new WaitForSeconds(0.2f); Combat.SecondaryUp(); }
                    Log($"hold {st.Get("keys")}: action at release={action}, parade {(paradeAtRelease ? "up" : "down")}{(Combat.Sneaking ? " (sneaking)" : "")}");
                    // fix-68: parade=1 fails unless the block is still up when the held keys are let go (no time limit)
                    if (st.Has("parade") && st.Bool("parade", false) != paradeAtRelease)
                        Fail($"hold {st.Get("keys")} {secs:0.#} s: parade {(paradeAtRelease ? "up" : "down")} at the release, expected {(st.Bool("parade", false) ? "up" : "down")}");
                    break;
                }
                case "click":
                {
                    // Short clicks with their event times (fix-31): repeated right clicks chain quick attacks, left ones attacks.
                    string key = st.Get("keys").ToLowerInvariant().Trim();
                    bool block = key == "block" || key == "secondary" || key == "parade";
                    int times = Math.Max(1, (int)st.Float("times", 1f));
                    float len = st.Float("len", 0.04f), gap = Math.Max(st.Float("gap", 0.3f), len + 0.01f);
                    var stages = new List<string>();
                    int seen = Combat.MovesStarted;
                    void Note()
                    {
                        if (Combat.MovesStarted == seen) return;
                        seen = Combat.MovesStarted;
                        stages.Add($"{Combat.ComboStage}:{Combat.LastStartedMove}");
                    }
                    bool holdBlock = !block && st.Bool("block", false); // the block held through left clicks: quick attacks / whirls
                    if (holdBlock) { Combat.SecondaryDown(); yield return new WaitForSeconds(0.2f); }
                    for (int i = 0; i < times; i++)
                    {
                        if (block) Combat.SecondaryDown(); else Combat.PrimaryDown();
                        yield return new WaitForSeconds(len);
                        if (block) Combat.SecondaryUp(); else Combat.PrimaryUp();
                        Note();
                        for (float until = Time.time + gap - len; Time.time < until; ) { Note(); yield return null; }
                    }
                    for (float until = Time.time + 1.5f; Time.time < until; ) { Note(); yield return null; } // the queued stage
                    if (holdBlock) Combat.SecondaryUp();
                    Log($"click {key} x{times}: stages {string.Join(", ", stages)}");
                    break;
                }
                case "enemies":
                    yield return EnemiesStep(st);
                    break;
                case "unstuck":
                    Result(Hero.Unstuck() + " | " + ProbeNow("hero"));
                    yield return null;
                    break;
                case "drop":
                {
                    if (!TryTarget(st.Get("pos"), out var dp, out bool dropY, out _, out _, out var dropErr) || !dropY) { Fail("drop: " + (dropErr ?? "needs pos=x,y,z")); break; }
                    GodFor(6f);
                    Hero.Teleport(dp);
                    Log($"drop: the hero's feet at {V(dp)}");
                    yield return null;
                    break;
                }
                case "getup":
                {
                    var use = FindAnyObjectByType<HeroObjectUse>();
                    bool leftUse = use != null && use.ForceEnd();
                    string line = Combat != null ? Combat.ForceGetUp() : "getup: no hero combat";
                    if (leftUse) line += " (object use ended)";
                    Result(line + " | " + ProbeNow("hero"));
                    break;
                }
                case "sit":
                {
                    Combat.Fighter.SitDown(false);
                    ScriptedSit(); // this sit is the scenario's own: it presses (click) itself
                    yield return new WaitForSeconds(1.2f);
                    if (!Combat.Fighter.SittingKnockedDown) Fail($"sit: not sitting (animator {Combat.Fighter.Animator.StateText})");
                    else Log($"sit: sitting, key {KeyOf(Hero.transform)}");
                    break;
                }
                case "sneak":
                {
                    // The sneak key's toggle (G3 OnPlayerSneak): with a melee weapon drawn the hero sneaks in the Parade stance.
                    bool on = st.Bool("on", true);
                    Combat.SetSneaking(on);
                    yield return new WaitForSeconds(0.3f);
                    if (Combat.Sneaking != on) Fail($"sneak {(on ? "on" : "off")}: refused (blocking, swimming or in the air?)");
                    else Log($"sneak {(on ? "on" : "off")}: stance {(Combat.SneakingInParade ? "Parade" : on ? "Sneak" : "Stand")}, key {KeyOf(Hero.transform)}");
                    break;
                }
                case "cast":
                    yield return CastStep(st);
                    break;
                case "teach":
                    TeachStep(st);
                    break;
                case "spellbook":
                    yield return SpellBookStep(st);
                    break;
                case "staffblock":
                    yield return StaffBlockStep(st);
                    break;
                case "spell":
                {
                    string item = ResolveItem(st.Get("spell"), spellsOnly: true);
                    if (item == null) break;
                    if (s.Items.Inventories.Of(P).Count(item) == 0) s.Items.Inventories.Give(P, item, 1);
                    if (st.Bool("learn", true) && s.HeroSpells.SpellOf(item) is string spellToLearn) s.Progression.LearnSkill(spellToLearn); // G3: spell books need HasLearnedSpell
                    if (s.Content.Database.TryGet(item, out ItemDef def))
                        foreach (var skill in def.RequiredSkills) s.Progression.LearnSkill(skill);
                    if (s.Hero.Stats.Value("INT") < 300) s.Hero.Stats.Apply("INT", ModifierOp.SetVal, 300);
                    if (s.Hero.Stats.Maximum("MP") < 1000) s.Hero.Stats.Apply("MP", ModifierOp.AddToMax, 1000);
                    s.Hero.Stats.Apply("MP", ModifierOp.SetValToMax, 0);
                    HideTips();
                    bool ready = Combat.ReadySpell(item);
                    // fix-57 (r31 melee_small: the sword was drawn; holstering it took longer than the old 1 s wait)
                    float r0 = Time.time;
                    do yield return new WaitForSeconds(0.1f);
                    while (ready && !(Combat.Fighter.SpellDrawn && Combat.ReadiedSpell == item && !Combat.Fighter.Animator.Busy) && Time.time - r0 < 5f);
                    Log($"spell {item}: ready={ready}, in the hand {Combat.ReadiedSpell ?? "-"} (drawn {Combat.Fighter.SpellDrawn}, {F(Time.time - r0)} s)");
                    break;
                }
                case "scenario":
                {
                    string path = Scenario.ScenarioFilePath(st.Get("name"));
                    if (path == null) { Fail("scenario: no file " + st.Get("name")); break; }
                    var inner = ScenarioParser.ParseJson(File.ReadAllText(path), st.Get("name"));
                    if (!inner.Ok) { Fail("scenario " + st.Get("name") + ": " + string.Join(" | ", inner.Errors)); break; }
                    Log($"scenario {inner.Name}: {inner.Steps.Count} steps");
                    // fix-54 (r30 okara_01 run as 'exec "scenario sideq_okara_01"'): an included step honours unless= like a top-level
                    // one (the Randall_KillMinecrawler steps ran although the quest was already Success), and the dialog handler
                    // runs around it
                    _includeDepth++;
                    string outerOp = _currentOp, outerAnswers = _stepAnswers;
                    for (int k = 0; k < inner.Steps.Count; k++)
                    {
                        var innerStep = inner.Steps[k].Map(v => ScenarioVars.Resolve(v, _vars));
                        _status = $"running {_stepIndex + 1}/{_spec?.Steps.Count} {_spec?.Name}: scenario {inner.Name} {k + 1}/{inner.Steps.Count}: {innerStep.Source}";
                        if (SkipByUnless(innerStep, out bool badUnless)) continue;
                        if (badUnless) break;
                        _currentOp = outerOp;
                        yield return AroundStep(innerStep);
                        _currentOp = innerStep.Op;
                        _stepAnswers = innerStep.Get(OpSpec.Answer) ?? outerAnswers;
                        int before = _failures.Count;
                        yield return Execute(innerStep);
                        if (_failures.Count > before && innerStep.Op != "expect") break;
                    }
                    _currentOp = outerOp;
                    _stepAnswers = outerAnswers;
                    _includeDepth--;
                    break;
                }
                case "save":
                    // fix-54: named slots; the progress file records the next step as the resume point
                    yield return SaveSlot(st.Get("slot"), st.Float("wait", 10f), _stepIndex + 2, "save op", soft: false);
                    break;
                case "load":
                    // fix-54: the run goes on in the loaded game (it used to end here)
                    yield return LoadSlot(st.Get("slot"));
                    break;
                case "menu":
                {
                    // fix-107: open the game menu at a page (main | load | save | close) and/or send a key to the files list
                    var menu = FindAnyObjectByType<GameMenu>();
                    if (menu == null || menu.View == null) { Fail("menu: no G3 game menu"); break; }
                    string page = (st.Get("page") ?? "").ToLowerInvariant();
                    if (page == "close") menu.State.Close();
                    else if (page.Length > 0)
                    {
                        if (!Enum.TryParse(page, true, out MenuPage target)) { Fail($"menu: unknown page '{page}'"); break; }
                        if (!menu.State.IsOpen) menu.State.Open();
                        menu.State.Show(target);
                    }
                    string key = st.Get("key");
                    if (key != null)
                    {
                        if (!Enum.TryParse(key, true, out KeyCode code)) { Fail($"menu: unknown key '{key}'"); break; }
                        var list = menu.View.Files.List;
                        using (var ev = UnityEngine.UIElements.KeyDownEvent.GetPooled(default(char), code, EventModifiers.None))
                        {
                            ev.target = list;
                            list.SendEvent(ev);
                        }
                    }
                    Log($"menu {menu.State.Page}{(key != null ? " key " + key : "")}");
                    break;
                }
                case "autosave":
                {
                    // fix-82: every=secs sets the autosave interval (timer restarts; 0 = off); quick=1 does a quicksave (F5,
                    // fix-112: does not move the autosave)
                    // instead; without these it writes the autosave now (fix-107: one file each, quick / auto)
                    var saves = Saves;
                    if (saves == null) { Fail("autosave: no SaveGameController"); break; }
                    if (st.Bool("quick", false))
                    {
                        string blockedQ = _session.SaveBlockReason;
                        _lastAutosave = blockedQ == null;
                        if (_lastAutosave) saves.QuickSave();
                        _lastWrote = _lastAutosave ? saves.LastWritten : "-";
                        Log(_lastAutosave ? $"quicksave → {_lastWrote}" : $"quicksave: refused ({blockedQ})");
                        break;
                    }
                    if (st.Has("every"))
                    {
                        saves.autosaveMinutes = st.Float("every", 600f) / 60f;
                        saves.AutosaveTimer.IntervalSeconds = saves.autosaveMinutes * 60f;
                        saves.AutosaveTimer.Restart(Time.unscaledTime);
                        Log($"autosave every {saves.autosaveMinutes * 60f:0.#} s (due in {saves.AutosaveTimer.Due - Time.unscaledTime:0.#} s)");
                        break;
                    }
                    string blocked = _session.SaveBlockReason;
                    _lastAutosave = saves.Autosave();
                    _lastWrote = _lastAutosave ? saves.LastWritten : "-";
                    Log(_lastAutosave ? $"autosave → {_lastWrote}" : $"autosave: refused ({blocked ?? "?"})");
                    break;
                }
                case "recovery":
                {
                    // fix-155: the hidden crash-recovery snapshot now; every=secs its interval; offer=1 the title's question as
                    // after a crash; reply=yes|no answers it
                    var saves = Saves;
                    if (saves == null) { Fail("recovery: no SaveGameController"); break; }
                    if (st.Has("every"))
                    {
                        saves.RecoveryTimer.IntervalSeconds = st.Float("every", 120f);
                        saves.RecoveryTimer.Restart(Time.unscaledTime);
                        Log($"recovery snapshot every {saves.RecoveryTimer.IntervalSeconds:0.#} s");
                        break;
                    }
                    if (st.Has("offer"))
                    {
                        var menu = FindAnyObjectByType<GameMenu>();
                        if (menu == null) { Fail("recovery: no GameMenu"); break; }
                        saves.SimulateUncleanExit = st.Bool("offer", true);
                        if (!menu.State.IsOpen) menu.State.Open();
                        _recoveryAsked = menu.OfferRecovery();
                        Log(_recoveryAsked ? "recovery: asked" : "recovery: not offered (no newer snapshot, option off or answered)");
                        break;
                    }
                    if (st.Has("reply"))
                    {
                        var menu = FindAnyObjectByType<GameMenu>();
                        if (menu == null || menu.State.Request == null) { Fail("recovery: no question open"); break; }
                        bool yes = st.Get("reply") == "yes";
                        if (yes)
                        {
                            if (_session.Dialog.IsActive) _session.Dialog.Abort();
                            ReleaseInputs();
                        }
                        menu.State.Answer(yes);
                        Log($"recovery: answered {(yes ? "yes" : "no")}");
                        if (!yes) break;
                        AdoptSession(_boot.Session); // Yes loaded the snapshot: the run goes on in it, as after 'load'
                        yield return null;
                        float tRecovery = Time.realtimeSinceStartup;
                        while (LoadingHold.IsHeld && Time.realtimeSinceStartup - tRecovery < 120f) yield return new WaitForSecondsRealtime(0.25f);
                        yield return new WaitForSeconds(0.5f);
                        break;
                    }
                    string blockedR = _session.SaveBlockReason;
                    bool wrote = saves.WriteRecovery();
                    if (wrote) saves.WaitRecoveryWritten(5000);
                    Log(wrote ? $"recovery snapshot {saves.LastRecoveryBytes / 1024} KB, serialized in {saves.LastRecoveryMs:0.00} ms (main thread; {saves.LastRecoverySections})"
                              : $"recovery: refused ({blockedR ?? "a snapshot is still being written"})");
                    break;
                }
                case "loadout":
                    yield return ApplyLoadout(st.Get("name"));
                    break;
                case "autodialog":
                    _autoDialog = st.Bool("on", true);
                    if (st.Has("answers"))
                    {
                        _autoAnswers.Clear();
                        _autoAnswers.AddRange(st.Get("answers").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                    }
                    Log($"autodialog {(_autoDialog ? "on" : "off")}" + (_autoAnswers.Count > 0 ? " answers " + string.Join(",", _autoAnswers) : ""));
                    break;
                case "dialogstate":
                    Result(DialogNow());
                    break;

                // ------------------------------------------------ fix-75
                case "dropprobe":
                    DropProbeStep(st);
                    break;
                case "invquick":
                    yield return InvQuickStep(st);
                    break;
                case "invdrop": // fix-85 [owner]
                    yield return InvDropStep(st);
                    break;
                case "invsort": // fix-92 [owner]
                    yield return InvSortStep(st);
                    break;
                case "blocklag":
                    yield return BlockLagStep(st);
                    break;
                case "cancel":
                    yield return CancelStep(st);
                    break;
                case "useprobe":
                    UseProbeStep(st);
                    break;

                // ------------------------------------------------ flow / output
                case "wait":
                    yield return WaitStep(st);
                    break;
                case "expect":
                {
                    string cond = st.Get("cond");
                    if (!ScenarioCondition.TryParse(cond, out var c, out var err)) { Fail("expect: " + err); break; }
                    string actual = Evaluate(c);
                    if (c.Test(actual)) Result($"ok {cond} ({actual})");
                    else Fail($"expect {cond}: was {actual}");
                    break;
                }
                case "shot":
                {
                    if (st.Has("look") && TryTarget(st.Get("look"), out var p, out _, out _, out _, out _)) { Face(p, 10f); yield return new WaitForSeconds(0.6f); }
                    string dir = Path.Combine(Scenario.ProjectRoot, "Docs", "screens");
                    Directory.CreateDirectory(dir);
                    if (st.Has("view")) { yield return FramedShots(st, dir); break; }
                    string path = Path.Combine(dir, st.Get("name") + ".png");
                    ScreenCapture.CaptureScreenshot(path, 1);
                    yield return new WaitForEndOfFrame();
                    yield return new WaitForSecondsRealtime(0.5f);
                    Result("shot " + path.Replace('\\', '/'));
                    break;
                }
                case "probe":
                    Result(ProbeNow(st.Get("actor", "hero")));
                    break;
                case "log":
                    Result(st.Get("text", ""));
                    break;
                case "fxlist":
                    Result(G3.Unity.G3EffectSystem.ReportOrNone(st.Int("top", 30)));
                    break;
                case "set":
                {
                    string value = st.Get("value", "");
                    if (st.Has("from")) // fix-96: a probe's current value (+ add=), for counts relative to the start of the run
                    {
                        if (!ScenarioCondition.TryParse(st.Get("from") + "=0", out var fc, out var ferr)) { Fail("set: " + ferr); break; }
                        value = Evaluate(fc);
                    }
                    if (st.Has("add"))
                    {
                        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, Inv, out double baseValue)) { Fail($"set {st.Get("name")}: '{value}' is not a number"); break; }
                        value = (baseValue + st.Float("add", 0f)).ToString(Inv);
                    }
                    _vars[st.Get("name")] = value;
                    if (st.Has("from") || st.Has("add")) Log($"set {st.Get("name")} = {value}");
                    break;
                }
                case "cursorwatch": // fix-87: the cursor modes seen from now on (condition cursorseen)
                    CursorDirector.ResetSeen();
                    Log($"cursorwatch: now {CursorDirector.Seen()}");
                    break;
                case "camprobe": // fix-76: camera jitter probe (ScenarioRunner.Camera.cs)
                    yield return CamProbeStep(st);
                    break;
                case "torsoprobe": // fix-89: torso shape while running (ScenarioRunner.Camera.cs)
                    yield return TorsoProbeStep(st);
                    break;
                case "climbto": // fix-130: run at a target jumping when stalled (a crate stack), never above maxy (ScenarioRunner.Fix130.cs)
                    yield return ClimbToStep(st);
                    break;
                case "slopeback": // fix-94: the hero's back to a steep slope, camera recorded (ScenarioRunner.Fix94.cs)
                    yield return SlopeBackStep(st);
                    break;
                default:
                    Fail("op not implemented: " + st.Op);
                    break;
            }
        }

        // ---------------------------------------------------------------- framed screenshots

        /// <summary>
        /// 'shot view=…': the player camera is paused and the main camera placed by <see cref="ShotFraming"/> on the target
        /// (target=, else look=, else the hero), one shot per view (orbit: front, right, back, left); the camera is handed
        /// back afterwards.
        /// </summary>
        IEnumerator FramedShots(ScenarioStep st, string dir)
        {
            string view = st.Get("view").ToLowerInvariant();
            var views = view == "orbit" ? ShotFraming.OrbitViews : new[] { view };
            if (!ShotFraming.TryViewOffset(views[0], out _)) { Fail($"shot: view={view}? (front|back|left|right|orbit)"); yield break; }
            string targetText = st.Get("target") ?? st.Get("look") ?? "hero";
            if (!TryTarget(targetText, out var target, out _, out var actor, out _, out var err)) { Fail("shot: " + err); yield break; }
            var cam = Camera.main;
            if (cam == null) { Fail("shot: no main camera"); yield break; }
            float dist = st.Float("dist", ShotFraming.DefaultDistance), height = st.Float("height", ShotFraming.DefaultHeight);
            float yaw;
            if (actor.HasValue)
            {
                TryActorPosition(actor.Value, out _, out var actorView);
                yaw = ActorYaw(actor.Value, actorView);
            }
            else
            {
                // a place has no facing: "front" is the side the camera looks from now
                var d = cam.transform.position - target;
                yaw = d.sqrMagnitude > 1e-4f ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg : 0f;
            }
            var rig = cam.GetComponent<G3PlayerCamera>();
            bool rigWasOn = rig != null && rig.enabled;
            if (rig != null) rig.enabled = false;
            var lookAt = target + Vector3.up * ShotFraming.LookHeight;
            foreach (var v in views)
            {
                ShotFraming.TryViewOffset(v, out float offset);
                ShotFraming.CameraPosition(target.x, target.y, target.z, yaw, offset, dist, height, out float cx, out float cy, out float cz);
                cam.transform.position = new Vector3(cx, cy, cz);
                cam.transform.LookAt(lookAt);
                yield return null; // one rendered frame at the new place (streaming, LOD)
                string path = Path.Combine(dir, st.Get("name") + (views.Length > 1 ? "_" + v : "") + ".png");
                ScreenCapture.CaptureScreenshot(path, 1);
                yield return new WaitForEndOfFrame();
                yield return new WaitForSecondsRealtime(0.5f);
                Result($"shot {path.Replace('\\', '/')} view={v} dist={F(dist)}");
            }
            if (rig != null) rig.enabled = rigWasOn;
        }

        // ---------------------------------------------------------------- teleport

        /// <summary>
        /// walkto (fix-49): the hero runs straight at a target with the real controller, turning to it every frame; pass
        /// within near m, fail when stuck (<see cref="WalkToProgress"/>) or out of time. No teleport, no detour.
        /// expect=blocked inverts it for cliffs and walls: pass when he does not get there and climbs at most 1 m.
        /// </summary>
        IEnumerator WalkTo(ScenarioStep st)
        {
            string text = st.Get("target");
            if (!TryTarget(text, out var target, out _, out _, out _, out var err)) { Fail("walkto: " + err); yield break; }
            var progress = new WalkToProgress(st.Float("near", 1.5f), st.Float("secs", 15f));
            var start = Hero.transform.position;
            int steps0 = Hero.StepUps, folds0 = Hero.FoldPasses;
            float t0 = Time.time, walked = 0f;
            var last = start;
            var state = WalkToState.Walking;
            Hero.scriptedWalk = st.Bool("walk", false);
            int talks = 0;
            while (state == WalkToState.Walking)
            {
                // fix-53 (r30 terrain_ledges): the gate guard Garik stops the hero on the first leg ("Halt! Not a step
                // further.", G3 FirstWarn); the talk locks his controls, which read as "Stuck ... grounded=True" on a plain
                // 21 deg slope. A player answers and walks on; the talk is not walking time.
                if (_session.Dialog.IsActive)
                {
                    float talk = 0f;
                    talks++;
                    yield return AnswerUnaskedTalk("walkto " + text, x => talk = x);
                    t0 += talk;
                    last = Hero.transform.position;
                    continue;
                }
                var hero = Hero.transform.position;
                var to = target - hero;
                to.y = 0;
                walked += new Vector2(hero.x - last.x, hero.z - last.z).magnitude;
                last = hero;
                state = progress.Update(to.magnitude, Time.time - t0);
                if (state != WalkToState.Walking) break;
                Hero.SetLook(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 5f);
                Hero.scriptedMove = new Vector2(0, 1);
                yield return null;
            }
            Hero.scriptedMove = Vector2.zero;
            Hero.scriptedWalk = false;
            var end = Hero.transform.position;
            var left = target - end;
            left.y = 0;
            string summary = $"walkto {text}: {state} at {V(end)}, {F(left.magnitude)} m left, walked {F(walked)} m in {F(Time.time - t0)} s, " +
                             $"climbed {F(end.y - start.y)} m, step-ups {Hero.StepUps - steps0}, folds passed {Hero.FoldPasses - folds0}, " +
                             $"grounded={Hero.IsGrounded} sliding={Hero.IsSliding}{ControlsNote()}" + (talks > 0 ? $", {talks} unasked talk(s) answered" : "");
            bool blocked = string.Equals(st.Get("expect"), "blocked", StringComparison.OrdinalIgnoreCase);
            if (WalkToProgress.Passed(state, blocked, end.y - start.y)) Log(summary + (blocked ? " (blocked as expected)" : ""));
            else Fail(summary + (blocked ? " (expected: blocked, no climb)" : ""));
        }

        /// <summary>", controls locked by X" when the hero's controller is switched off (a dialog, a screen), else empty.</summary>
        string ControlsNote() =>
            Hero == null || Hero.enabled ? "" : $", controls locked by {(_boot != null && _boot.Controls != null ? _boot.Controls.HoldersText : "?")}";

        IEnumerator TeleportStep(ScenarioStep st)
        {
            string target = st.Get("target") ?? (st.Has("spot") ? "spot:" + st.Get("spot") : st.Has("npc") ? "npc:" + st.Get("npc") : st.Get("pos"));
            if (!TryTarget(target, out var p, out bool hasY, out var actor, out var spot, out var err)) { Fail("tp: " + err); yield break; }
            // a spot's swim tag applies to the spot itself, not to its points (deep_water_shore.shore is dry land) [runner-16]
            bool atPoint = target != null && target.StartsWith("spot:") && target.IndexOf('.') > 0;
            bool swim = st.Bool("swim", spot != null && spot.HasTag("swim") && !atPoint);
            float? yaw = st.Has("yaw") ? st.Float("yaw", 0f) : spot?.Yaw;
            Vector3? face = null;
            if (spot != null)
            {
                _vars["spot"] = spot.Id;
                if (spot.Npc != null) _vars["spot.npc"] = spot.Npc;
                if (spot.Points != null) foreach (var kv in spot.Points) _vars["spot." + kv.Key] = string.Join(",", kv.Value.Select(v => v.ToString(Inv)));
            }
            if (actor.HasValue && !actor.Value.IsPlayer)
            {
                // stand dist m in front of (side=back|left|right) the NPC, facing it
                TryActorPosition(actor.Value, out var np, out var view);
                float ay = ActorYaw(actor.Value, view);
                string side = st.Get("side", "front").ToLowerInvariant();
                float off = side == "back" ? 180f : side == "left" ? -90f : side == "right" ? 90f : 0f;
                var dir = Quaternion.Euler(0, ay + off, 0) * Vector3.forward;
                face = np + Vector3.up * 1.5f;
                float dist = st.Float("dist", 2f);
                p = np + dir * dist;
                // a NavMesh spot at the NPC's own floor level next to it (editor-runner-22 bakaresh_01: the arena tp landed on
                // the ground 6 m below Achmed); the asked side first, then the others
                // zone=Name: only a spot inside that AI zone (an arena: leaving it loses the fight; editor-runner-24 Mufassa,
                // Makas: the tp beside the opponent left the arena zone)
                Func<Vector3, bool> inside = null, wellInside = null;
                float heading = ay + off;
                if (st.Has("zone"))
                {
                    var zone = _session.Content.Database.All<AiZoneDef>().FirstOrDefault(z => string.Equals(z.Name, st.Get("zone"), StringComparison.OrdinalIgnoreCase));
                    if (zone == null) { Fail("tp: no zone " + st.Get("zone")); yield break; }
                    inside = q => q.y >= zone.Floor - zone.Below && q.y <= zone.Floor + zone.Above && ZoneMap.Contains(zone.Outline, q.x, q.z);
                    // fix-46 (r28 ishtar_01: Lost although Emet went down; the hero was put 2.5 m in front of Emet near the
                    // 13 x 10 m ring's edge and the stumbles of a 6.9 s stagger push away from the attacker): the hero stands on
                    // the opponent's centre side, so hits push him across the ring, not out of it; a spot >= 2 m from the edge first
                    if (!st.Has("side") && ZoneMap.Centre(zone.Outline, out float cx, out float cz) && (cx - np.x) * (cx - np.x) + (cz - np.z) * (cz - np.z) > 0.25f)
                        heading = Mathf.Atan2(cx - np.x, cz - np.z) * Mathf.Rad2Deg;
                    wellInside = q => inside(q) && ZoneMap.EdgeDistance(zone.Outline, q.x, q.z) >= ArenaEdgeMargin;
                }
                // fix-184 (regress-1021b fix175_npc_hp_load: 'tp (75.0,66.4,41.2): steep' - the wolf's cells were not loaded
                // after the Ardea leg, so no NavMesh spot beside it was found and the raw spot 8 m off lay on the hillside):
                // the NPC's cells stream in first (the hero parked above it), then its spot is read again from its view
                if (!NavSpotBeside(np, heading, dist, out _, inside) && FindAnyObjectByType<G3WorldStreamer>() is G3WorldStreamer ws && !ws.IsReady(np))
                {
                    float w0 = Time.realtimeSinceStartup;
                    var park = np + Vector3.up * 300f;
                    while (!ws.IsReady(np) && Time.realtimeSinceStartup - w0 < StreamWaitSeconds)
                    {
                        Hero.Teleport(park);
                        yield return new WaitForSecondsRealtime(0.25f);
                    }
                    yield return new WaitForSecondsRealtime(0.5f); // views bound, NavMesh of the new cells added
                    TryActorPosition(actor.Value, out np, out view);
                    face = np + Vector3.up * 1.5f;
                    p = np + dir * dist;
                }
                if (wellInside != null && NavSpotBeside(np, heading, dist, out var spot1, wellInside)) p = spot1;
                else if (NavSpotBeside(np, heading, dist, out var spot2, inside)) p = spot2;
                else if (inside != null)
                {
                    if (inside(Hero.transform.position)) { Log($"tp {actor.Value}: no spot beside it inside zone {st.Get("zone")}, the hero stays in the zone"); yield break; }
                    if (inside(np)) p = np; // on the opponent's spot: the hop of the fight op steps aside
                    else { Log($"tp {actor.Value}: it stands outside zone {st.Get("zone")}, the hero stays"); yield break; }
                }
            }
            else if (target != null && target.StartsWith("obj:", StringComparison.OrdinalIgnoreCase) && st.Has("dist") &&
                     TryObject(target.Substring(4), out var objDef))
            {
                // fix-96 (r38 fix88: the hero was put onto the chest itself, dist= ignored, and 'look obj' then faced nowhere,
                // so the bed beside it took the focus): stand dist m from the object on its use side (the hero's use spot,
                // else its front), on the NavMesh, facing it
                var op = new Vector3(objDef.Position[0], objDef.Position[1], objDef.Position[2]);
                _pinnedObject = objDef; // fix-105: later obj: targets of this name mean this one
                float side = ObjectSideYaw(objDef, op), want = st.Float("dist", 1.5f);
                // fix-103: side=back|left|right stands on the other sides of the object (an approach from behind a shrine)
                string objSide = st.Get("side", "front").ToLowerInvariant();
                side += objSide == "back" ? 180f : objSide == "left" ? -90f : objSide == "right" ? 90f : 0f;
                if (ObjectSpot(op, side, want, out var objSpot, objDef.Id))
                {
                    p = objSpot;
                    hasY = true;
                    Log($"tp {target}: {F(Flat(objSpot - op))} m from it (asked {F(want)}), side yaw {F(side)}");
                }
                else if (NavSpotBeside(op, side, want, out var near))
                {
                    // fix-105 (r41 fix102: put onto the chest itself, nothing focused): a NavMesh spot at the object's floor,
                    // without the room and line checks, beats standing on the object
                    p = near;
                    hasY = true;
                    Log($"tp {target}: no free spot with a clear line {F(want)} m from it; a NavMesh spot {F(Flat(near - op))} m from it");
                }
                else Log($"tp {target}: no free NavMesh spot {F(want)} m from it, the hero goes to the object");
                face = op + Vector3.up * Mathf.Clamp(objDef.FocusHeight * objDef.Scale * 0.5f, 0.3f, 1.5f);
            }
            yield return TeleportTo(p, hasY, swim, yaw, face);
        }

        /// <summary>
        /// fix-96: a spot about <paramref name="dist"/> m from an object, the <paramref name="yaw"/> side first, then around
        /// it: on the NavMesh close by (0.6 m), at the object's floor (1.2 m), with room for the hero's capsule and nothing of
        /// the world between his chest and the object (r: a 1.2 m spot snapped through the wall of Mitch's house).
        /// </summary>
        bool ObjectSpot(Vector3 obj, float yaw, float dist, out Vector3 spot, string objectId = null)
        {
            // fix-105 (r40 fix88: the hearth's own mesh reaches 1.4 m out from its pivot, every spot was refused and the hero
            // landed on the terrace 5 m below the house): the object's own colliders (its view, its cell mesh) are not in the way
            bool Own(Collider c) => FocusLineOfSight.IsOwnMesh(c, obj) ||
                                    objectId != null && c.GetComponentInParent<InteractiveObjectView>() is InteractiveObjectView v && v.Def != null && v.Id == objectId;
            spot = default;
            var heroRoot = Hero.transform;
            var objTop = obj + Vector3.up * 0.5f;
            foreach (float turn in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 135f, -135f, 180f })
                foreach (float d in new[] { dist, dist * 0.75f, dist * 1.25f })
                {
                    var c = obj + Quaternion.Euler(0, yaw + turn, 0) * Vector3.forward * Mathf.Max(0.6f, d);
                    if (!UnityEngine.AI.NavMesh.SamplePosition(c, out var hit, 0.6f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    var h = hit.position;
                    if (Mathf.Abs(h.y - obj.y) > 1.2f) continue;
                    if (Physics.OverlapCapsule(h + Vector3.up * 0.6f, h + Vector3.up * 1.6f, 0.3f, ~0, QueryTriggerInteraction.Ignore)
                               .Any(col => !col.transform.IsChildOf(heroRoot))) continue;
                    var from = h + Vector3.up * 1.0f;
                    var to = objTop - from;
                    float len = to.magnitude;
                    // the object's own collider is hit last, near it; anything farther than 0.7 m from it is in the way
                    if (len > 0.01f && Physics.RaycastAll(from, to / len, len, ~0, QueryTriggerInteraction.Ignore)
                                               .Any(x => !x.collider.transform.IsChildOf(heroRoot) && !Own(x.collider) && Flat(x.point - obj) > 0.7f)) continue;
                    spot = h;
                    return true;
                }
            return false;
        }

        /// <summary>fix-96: the yaw from an object towards the side the hero uses it from (his use spot, else its forward).</summary>
        float ObjectSideYaw(InteractiveObjectDef def, Vector3 origin)
        {
            var spot = HeroUseSpots.Find(_session, def, Station.None);
            if (spot.HasValue)
            {
                var d = spot.Value.Position - origin;
                d.y = 0f;
                if (d.sqrMagnitude > 0.04f) return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            }
            var f = def.Forward != null && def.Forward.Length >= 3 ? new Vector3(def.Forward[0], 0f, def.Forward[2]) : Vector3.forward;
            return f.sqrMagnitude > 1e-4f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : 0f;
        }

        /// <summary>tp zone=: the hero's spot keeps this far from the zone's edge where it can (fix-46), m.</summary>
        const float ArenaEdgeMargin = 2f;
        /// <summary>fight in an arena: closer than this to the ring's edge the hero steps back towards the middle (fix-46), m.</summary>
        const float ArenaEdgeKeep = 1.2f;

        /// <summary>A NavMesh point about <paramref name="dist"/> m from <paramref name="npc"/> at its floor level (within 1.2 m of its height), trying the heading <paramref name="yaw"/> first, then around it; only where <paramref name="allowed"/> (null = anywhere).</summary>
        static bool NavSpotBeside(Vector3 npc, float yaw, float dist, out Vector3 spot, Func<Vector3, bool> allowed = null)
        {
            spot = default;
            foreach (float turn in new[] { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f })
                foreach (float d in new[] { dist, dist * 0.6f, dist * 1.5f })
                {
                    var c = npc + Quaternion.Euler(0, yaw + turn, 0) * Vector3.forward * Mathf.Max(0.8f, d);
                    if (!UnityEngine.AI.NavMesh.SamplePosition(c, out var hit, 1.2f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    if (Mathf.Abs(hit.position.y - npc.y) > 1.2f) continue;
                    if (allowed != null && !allowed(hit.position)) continue;
                    spot = hit.position;
                    return true;
                }
            return false;
        }

        /// <summary>How long a teleport waits for the cells at its target (real seconds).</summary>
        const float StreamWaitSeconds = 90f;

        internal IEnumerator TeleportTo(Vector3 p, bool hasY, bool swim, float? yaw, Vector3? face)
        {
            GodFor(6f);
            // an open dialog keeps the hero control locked: it runs to its end first (an abort would drop the commands of an
            // info the NPC started by itself, editor-runner-23 Falk)
            if (_session.Dialog.IsActive) yield return FinishTalk("tp");
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            float t0 = Time.realtimeSinceStartup;
            var park = new Vector3(p.x, (hasY ? p.y : 300f) + 300f, p.z);
            // fix-184 (regress-1021b npc_work_cycles, summon_fight_end: Varant cells took 1-4 s each to load with the editor at
            // 13.8 GB, nine of them did not fit into 45 s): 90 s, and the wait does not use up the scenario's time
            float g0 = Time.time;
            while (streamer != null && !streamer.IsReady(p))
            {
                Hero.Teleport(park);
                if (Time.realtimeSinceStartup - t0 > StreamWaitSeconds) { Fail($"tp {V(p)}: world not streamed in {StreamWaitSeconds:0} s"); yield break; }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            CreditClock(Time.time - g0);
            Vector3 dest = p;
            string how;
            if (swim)
            {
                if (!G3Water.TrySurface(new Vector3(p.x, p.y - 20f, p.z), out float sy, 60f)) { Fail($"tp {V(p)}: no water surface for swim"); yield break; }
                dest = new Vector3(p.x, sy - 0.8f, p.z);
                how = $"water surface {F(sy)}";
            }
            else
            {
                string why = null;
                bool found = false;
                RaycastHit hit = default;
                for (int tries = 0; tries < 20 && !found; tries++)
                {
                    found = FindGround(p, hasY, out hit, out why);
                    if (!found) { Hero.Teleport(park); yield return new WaitForSecondsRealtime(0.25f); }
                }
                if (!found) { Fail($"tp {V(p)}: {why}"); yield break; }
                dest = hit.point + Vector3.up * 0.15f;
                how = $"ground slope {F(Vector3.Angle(hit.normal, Vector3.up))}° {why}";
            }
            Hero.Teleport(dest);
            if (yaw.HasValue) Hero.SetLook(yaw.Value, 5f);
            else if (face.HasValue) Face(face.Value);
            yield return new WaitForSeconds(0.7f);
            if (face.HasValue) Face(face.Value);
            _session.Combat.Vitals.RestoreFully(ActorId.Player);
            Log($"tp {V(Hero.transform.position)} ({how}) water={Hero.Water} grounded={Hero.IsGrounded}");
        }

        /// <summary>
        /// Flat, free ground at or near (x, z): the nearest hit below (from y+2.5 when y is known, else from 1000 m),
        /// slope &lt; 37°, not under water deeper than 0.5 m, capsule room for the hero; searched in rings up to 8 m.
        /// </summary>
        /// <param name="ignore">Colliders under this transform are neither ground nor in the way (fix-46: the guide's own capsule
        /// made "no room" at its feet, r28 fireclan_01: the spot 8 m off was the hero's own, the teleport moved him 0.1 m).</param>
        bool FindGround(Vector3 p, bool hasY, out RaycastHit best, out string why, Transform ignore = null)
        {
            best = default;
            why = "no ground hit";
            var heroRoot = Hero.transform;
            bool Skip(Transform t) => t.IsChildOf(heroRoot) || (ignore != null && t.IsChildOf(ignore));
            foreach (float r in new[] { 0f, 1.5f, 3f, 4.5f, 6f, 8f })
            {
                int n = r == 0 ? 1 : 8;
                for (int i = 0; i < n; i++)
                {
                    var c = p + Quaternion.Euler(0, i * 360f / n, 0) * Vector3.forward * r;
                    float top = hasY ? p.y + 2.5f : 1000f;
                    var hits = Physics.RaycastAll(new Vector3(c.x, top, c.z), Vector3.down, hasY ? 10f : 2000f, ~0, QueryTriggerInteraction.Ignore);
                    RaycastHit? h = null;
                    foreach (var x in hits)
                        if (!Skip(x.collider.transform) && (!h.HasValue || x.distance < h.Value.distance)) h = x;
                    if (!h.HasValue) continue;
                    var hit = h.Value;
                    if (hit.normal.y < 0.8f) { why = $"steep at {V(hit.point)}"; continue; }
                    if (G3Water.TrySurface(hit.point, out float sy, 5f) && sy - hit.point.y > 0.5f) { why = $"water {F(sy - hit.point.y)} m at {V(hit.point)}"; continue; }
                    bool blocked = Physics.OverlapCapsule(hit.point + Vector3.up * 0.6f, hit.point + Vector3.up * 1.6f, 0.3f, ~0, QueryTriggerInteraction.Ignore)
                                          .Any(col => !Skip(col.transform));
                    if (blocked) { why = $"no room at {V(hit.point)}"; continue; }
                    best = hit;
                    why = r > 0 ? $"moved {F(r)} m" : "";
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- NPC placement

        IEnumerator SpawnStep(ScenarioStep st)
        {
            var s = _session;
            string template = st.Get("template");
            var all = s.Content.Database.All<NpcDef>();
            var def = DevNpcs.FindTemplate(all, template);
            if (def == null)
            {
                string id = DevLookup.Best(all.SelectMany(n => DevLookup.Names(n.Id.Name, n.DisplayName, s.Localizer)), template, out _);
                def = id != null ? all.FirstOrDefault(n => n.Id.Name == id) : null;
            }
            if (def == null) { Fail("spawn: no NPC template " + template); yield break; }
            var attitude = DevNpcs.ParseAttitude(st.Get("attitude", "default"));
            int count = Math.Max(1, st.Int("count", 1));
            var created = new List<ActorId>();
            var floors = new List<Vector3>();
            string placed = null;
            for (int i = 0; i < count; i++)
            {
                if (!PlacementNear(st, i, out var pose, out var err)) { Fail("spawn: " + err); yield break; }
                floors.Add(new Vector3(pose.X, pose.Y, pose.Z));
                placed = placed ?? _placementHow;
                var id = DevNpcs.NewId(def.Id.Name, a => s.Actors.Exists(a));
                var creature = DevNpcs.CreatureFrom(def, id, pose, attitude);
                if (!s.Actors.Add(creature)) { Fail("spawn: actor id taken " + id); yield break; }
                if (s.Npcs.Add(creature, pose) == null) { s.Actors.Remove(id); Fail("spawn: simulation refused " + id); yield break; }
                s.Items.NpcLoot.CarryAs(id, def.Id); // fix-120: the copy carries the template's stacks, plunder and weaponry
                created.Add(id);
            }
            yield return new WaitForSeconds(1f); // view creation
            for (int i = 0; i < created.Count; i++) CheckSpawnedOnGround(created[i], floors[i]);
            if (attitude == Attitude.Hostile)
                foreach (var id in created) s.NpcCombat.Engage(id, ActorId.Player, AttackReason.Enemy);
            if (st.Has("as"))
            {
                _vars[st.Get("as")] = created[0].Name;
                // fix-188 (fix81_party_animals: the pack mate kept Gorn busy): count>1 also binds $<as>1, $<as>2 ...
                if (created.Count > 1) for (int i = 0; i < created.Count; i++) _vars[st.Get("as") + (i + 1)] = created[i].Name;
            }
            Log($"spawn {string.Join(",", created)} from {def.Id} attitude={attitude?.ToString() ?? "default"} view={(Views != null && Views.TryGetView(created[0], out var v) && v != null)} at {V(floors[0])} ({placed})");
        }

        IEnumerator BringStep(ScenarioStep st)
        {
            var s = _session;
            var id = Actor(st.Get("npc"));
            if (!s.Npcs.TryGet(id, out var agent)) { Fail("bring: no NPC " + id); yield break; }
            if (!PlacementNear(st, 0, out var pose, out var err)) { Fail("bring: " + err); yield break; }
            var def = agent.Definition;
            s.NpcCombat.Get(id)?.Disengage();
            s.Npcs.Remove(id);
            yield return null;
            yield return null;
            var added = s.Npcs.Add(def, pose);
            if (added == null) { Fail("bring: re-adding refused " + id); yield break; }
            // a view that survived the remove stays bound to the old agent and keeps its old place: re-bind it [runner-16]
            if (TryActorPosition(id, out _, out var oldView) && oldView != null && oldView.TryGetComponent<NpcView>(out var nv) && nv.Agent != added)
                nv.Bind(added, s.Npcs);
            yield return new WaitForSeconds(1f);
            if (st.Has("as")) _vars[st.Get("as")] = id.Name;
            TryActorPosition(id, out var now, out var view);
            Log($"bring {id} to {V(now)} view={(view != null)} dist={F((now - Hero.transform.position).magnitude)}");
        }

        string _placementHow;

        /// <summary>A ground pose dist m from near= (default the hero, in front of it), facing the hero.</summary>
        bool PlacementNear(ScenarioStep st, int index, out WorldPose pose, out string error)
        {
            pose = default;
            error = null;
            Vector3 c;
            Vector3 forward = Hero.transform.forward;
            bool knownY = false;
            if (st.Has("near"))
            {
                if (!TryTarget(st.Get("near"), out c, out bool hasY, out _, out _, out error)) return false;
                if (!hasY) c.y = Hero.transform.position.y;
                knownY = hasY;
            }
            else c = Hero.transform.position;
            float dist = st.Float("dist", 6f);
            string side = st.Get("side", "front").ToLowerInvariant(); // side= was listed in the usage but ignored [runner-16]
            float off = side == "back" ? 180f : side == "left" ? -90f : side == "right" ? 90f : 0f;
            var dir = Quaternion.Euler(0, off + index * 40f, 0) * new Vector3(forward.x, 0, forward.z).normalized;
            var p = c + dir * dist;
            // fix-54 (r30 melee_small: a spawned Meatbug ended 54 m below): the floor as G3 places an NPC on it, bodies left out
            if (SpawnGround(p, c.y, true, out var floor, out _placementHow))
            {
                var look = Hero.transform.position - floor;
                pose = new WorldPose(floor.x, floor.y, floor.z, Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg);
                return true;
            }
            _placementHow += " (fallback: the old ray)";
            float y = p.y;
            var hits = Physics.RaycastAll(new Vector3(p.x, p.y + 6f, p.z), Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore)
                              .Where(h => !h.collider.transform.IsChildOf(Hero.transform)).OrderBy(h => h.distance).ToList();
            // the floor at the target's own height (a rock overhang or a roof above a place is not where it stands: a
            // story "near" test measures in 3D; editor-runner-22 fireclan_01 Kaelin), else the topmost
            var level = knownY ? hits.Where(h => Mathf.Abs(h.point.y - c.y) <= 3f).OrderBy(h => Mathf.Abs(h.point.y - c.y)).ToList() : null;
            if (level != null && level.Count > 0) y = level[0].point.y;
            else if (hits.Count > 0) y = hits[0].point.y;
            var toHero = Hero.transform.position - p;
            pose = new WorldPose(p.x, y, p.z, Mathf.Atan2(toHero.x, toHero.z) * Mathf.Rad2Deg);
            return true;
        }

        /// <summary>NPC ids for calm/freeze/despawn: only= list, or every NPC view within radius of the hero minus except=.</summary>
        List<ActorId> Selection(ScenarioStep st, float radius)
        {
            if (st.Has("only")) return st.Get("only").Split(',').Select(x => Actor(x.Trim())).ToList();
            var except = new HashSet<ActorId>((st.Get("except") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Actor(x.Trim())));
            var hero = Hero.transform.position;
            return Views.Views.Where(kv => kv.Value != null && !kv.Key.IsPlayer && !except.Contains(kv.Key) && (kv.Value.position - hero).magnitude <= radius)
                        .Select(kv => kv.Key).ToList();
        }

        static string Short(List<ActorId> ids) =>
            string.Join(",", ids.Take(8)) + (ids.Count > 8 ? $" +{ids.Count - 8}" : "");

        // ---------------------------------------------------------------- combat

        IEnumerator DrawStep(string kind)
        {
            var s = _session;
            bool ranged = kind.StartsWith("r", StringComparison.OrdinalIgnoreCase);
            bool fists = kind.StartsWith("f", StringComparison.OrdinalIgnoreCase);
            if (fists && s.Items.Equipment.In(WornSlot.MeleeWeapon) != null)
            {
                // Fists: take the melee weapon off (a drawn one is put away by PlayerCombat), then draw empty hands.
                s.Items.Equipment.Unequip(WornSlot.MeleeWeapon);
                float t0 = Time.time;
                while (Combat.Fighter.Animator.Drawn || Combat.Fighter.Animator.Busy) { if (Time.time - t0 > 3f) break; yield return null; }
                yield return new WaitForSeconds(0.3f);
            }
            else if (!ranged && !fists && s.Items.Equipment.In(WornSlot.MeleeWeapon) == null)
            {
                // `draw` (melee) without a melee weapon equips the default sword; `draw kind=fist` draws the fists.
                if (s.Items.Inventories.Of(ActorId.Player).Count(DefaultMelee) == 0) s.Items.Inventories.Give(ActorId.Player, DefaultMelee, 1);
                s.Items.Equipment.Equip(DefaultMelee);
                Log("draw: no melee weapon equipped -> equipped " + DefaultMelee + " (draw kind=fist for fists)");
                yield return new WaitForSeconds(0.3f);
            }
            HideTips();
            // Right after a teleport the hero may still be landing or sliding on a slope (editor-runner-22 fireclan_02: draw
            // refused after tp): wait until he stands, out of any dialog, then draw; try again while it is refused.
            if (s.Dialog.IsActive) s.Dialog.Abort();
            // Every refused or unfinished try is logged with PlayerCombat's reason and the animator state (editor-runner-25:
            // one silent "not drawn after 1 tries" in the MoraSul arena); a refusal waits and tries again, like after a tp.
            // Done = the weapon is in the hand and the draw motion over (Armed), not just Drawn: Drawn is true from the first
            // frame of the draw, and a hit reaction in its first half cuts it with empty hands (WeaponHand.Interrupt;
            // editor-runner-26 hammerclan_01, bensala_01, morasul_04: a defend's monster hit the hero right after `draw`).
            var a = Combat.Fighter.Animator;
            float t = Time.time;
            int tries = 0, cuts = 0;
            string why = null;
            // fix-41 (r28 hammerclan_02/04, vangard_01: "draw ... a combat motion runs (Down, hand empty None)"): knocked down,
            // sitting or in a hit reaction the draw waits for the get-up (the runner presses it) and the recovery; that time
            // does not use up the draw's own budget (ScenarioPacing.DrawLimit), and a try cut by a hit is not counted.
            float knocked = 0f, frozen = 0f, unsteady = 0f;
            bool KnockedAbout() => a.IsDown || a.InReaction || Combat.Fighter.SittingKnockedDown;
            // fix-54 (r30 wolfclan_01: "refused: the hero is frozen" 6 times in 12 s, an IceGolem's ice): G3 refuses every action
            // while the hero is frozen (ZS_Freeze); the draw waits for the thaw, which does not use up its tries or time
            while (!Armed(a) && Time.time - t < ScenarioPacing.DrawLimit(knocked, frozen))
            {
                // fix-53 (r30 hammerclan_04): a talk an NPC starts by itself (a gate guard's warning) locks the controls and
                // switches the controller off; after a teleport it never reports the ground again. Answer it first.
                if (s.Dialog.IsActive)
                {
                    float talk = 0f;
                    yield return AnswerUnaskedTalk("draw", x => talk = x);
                    t += talk;
                    continue;
                }
                if (Combat.Fighter.Frozen)
                {
                    if (frozen == 0f) Log($"draw: the hero is frozen ({_session.Magic.Status.IsFrozen(ActorId.Player)}), waits for the thaw");
                    frozen += Time.deltaTime;
                    why = "frozen, waited for the thaw";
                    yield return null;
                    continue;
                }
                if (KnockedAbout()) { knocked += Time.deltaTime; why = "knocked about (" + a.StateText + "), waits to get up"; yield return null; continue; }
                if (a.Drawn) { yield return null; continue; } // a draw runs: wait for the hand
                // G3 has no ground rule for drawing: the wait only lets a landing or slide finish (ScenarioPacing.DrawReady)
                unsteady = Hero.IsGrounded && !Hero.IsSliding ? 0f : unsteady + Time.deltaTime;
                bool ready = ScenarioPacing.DrawReady(Hero.IsGrounded, Hero.IsSliding, a.Busy, unsteady);
                if (ready && tries < 6)
                {
                    tries++;
                    string refusal = Combat.DrawRefusal();
                    bool started = ranged ? Combat.DrawRanged() : Combat.DrawMelee();
                    float t1 = Time.time;
                    while (started && !Armed(a) && a.Drawn && Time.time - t1 < 2f) yield return null;
                    if (Armed(a)) break;
                    why = !started ? "refused: " + (refusal ?? (ranged ? "no bow/crossbow or no ammunition" : "the animator refused"))
                        : a.Drawn ? "started, weapon not in the hand after 2 s" : "started, cut short before the weapon reached the hand";
                    Log($"draw: try {tries} {why} (animator {a.StateText}, busy={a.Busy}, grounded={Hero.IsGrounded} sliding={Hero.IsSliding})");
                    if (started && !a.Drawn && KnockedAbout() && cuts < 10)
                    {
                        cuts++; tries--; // a hit cut it: try again once up
                        // fix-48 (r29 hammerclan_04): a Ripper hit the hero in every draw window for 20 s (G3: each monster hit
                        // stumbles him, a stumble cuts the draw's first half). Every third cut the runner freezes the hero's
                        // attackers for a moment (dev shortcut, the G3 freeze status: they stand still) so the draw can finish.
                        if (cuts % ScenarioPacing.DrawCutsBeforeFreeze == 0) FreezeHeroAttackers();
                    }
                    yield return new WaitForSeconds(0.5f);
                    continue;
                }
                if (!ready) why = a.Busy ? "a combat motion runs (" + a.StateText + ")" : (!Hero.IsGrounded ? "not grounded" : "sliding") + ControlsNote();
                yield return null;
            }
            yield return new WaitForSeconds(0.3f);
            if (!Armed(a))
                Fail($"draw: weapon not drawn after {tries} tries, last: {why ?? "-"} (animator {a.StateText}, busy={a.Busy}, " +
                     $"refusal={Combat.DrawRefusal() ?? "none"}, grounded={Hero.IsGrounded} sliding={Hero.IsSliding} swimming={Hero.Swimming}{ControlsNote()})");
            else Log($"draw {a.Weapon}" + (tries > 1 ? $" ({tries} tries)" : "") + (cuts > 0 ? $" ({cuts} cut by hits)" : "") +
                     (knocked > 0.5f ? FormattableString.Invariant($" (waited {knocked:0.0} s knocked about)") : "") +
                     (frozen > 0.1f ? FormattableString.Invariant($" (waited {frozen:0.0} s frozen)") : ""));
        }

        /// <summary>Freezes the NPCs fighting the hero within 10 m for <see cref="ScenarioPacing.AttackerFreezeSeconds"/> (dev shortcut, logged).</summary>
        void FreezeHeroAttackers()
        {
            var s = _session;
            var hero = Hero.transform.position;
            var frozen = new List<string>();
            foreach (var b in s.NpcCombat.Brains)
            {
                if (!b.IsAttacking || !b.State.Target.IsPlayer || s.Combat.Vitals.StateOf(b.Self) != VitalState.Alive) continue;
                if (Views == null || !Views.TryGetView(b.Self, out var view) || view == null || (view.position - hero).magnitude > 10f) continue;
                s.Magic.Status.Freeze(b.Self, ScenarioPacing.AttackerFreezeSeconds);
                frozen.Add(b.Self.ToString());
            }
            Log($"draw: cut by hits {ScenarioPacing.DrawCutsBeforeFreeze}x, the runner freezes the hero's attackers for " +
                $"{ScenarioPacing.AttackerFreezeSeconds:0} s: {(frozen.Count > 0 ? string.Join(", ", frozen) : "none found")}");
        }

        static Vector2 MoveVector(string dir)
        {
            dir = (dir ?? "none").ToLowerInvariant();
            if (dir == "none" || dir.Length == 0) return Vector2.zero;
            var v = Vector2.zero;
            if (dir.Contains("fwd") || dir.Contains("forward")) v.y += 1;
            if (dir.Contains("back")) v.y -= 1;
            if (dir.Contains("left")) v.x -= 1;
            if (dir.Contains("right")) v.x += 1;
            return Vector2.ClampMagnitude(v, 1f);
        }

        /// <summary>
        /// <c>drawtrace [kind=melee|ranged|fist] [move=none|fwd|back|left|right|backleft…] [sheathe=1]</c> (fix-42): draws
        /// (walking with the keys of <c>move</c> when given) and logs every draw/sheathe motion with its start time, whether
        /// it is reversed and on the overlay, and the moment the weapon changes hands (G3: between the two motions), then the
        /// same for the sheathe. Fails when the hand does not change between the two motions.
        /// </summary>
        IEnumerator DrawTraceStep(ScenarioStep st)
        {
            var a = Combat.Fighter.Animator;
            if (a.Drawn || a.WeaponInHand) yield return SheatheStep("drawtrace");
            string kind = st.Get("kind", "melee");
            bool ranged = kind.StartsWith("r", StringComparison.OrdinalIgnoreCase);
            if (kind.StartsWith("f", StringComparison.OrdinalIgnoreCase) && _session.Items.Equipment.In(WornSlot.MeleeWeapon) != null)
            {
                _session.Items.Equipment.Unequip(WornSlot.MeleeWeapon); // fists: no melee weapon
                yield return new WaitForSeconds(0.3f);
            }
            else if (!ranged && !kind.StartsWith("f", StringComparison.OrdinalIgnoreCase) && _session.Items.Equipment.In(WornSlot.MeleeWeapon) == null)
            {
                if (_session.Items.Inventories.Of(ActorId.Player).Count(DefaultMelee) == 0) _session.Items.Inventories.Give(ActorId.Player, DefaultMelee, 1);
                _session.Items.Equipment.Equip(DefaultMelee);
                yield return new WaitForSeconds(0.3f);
            }
            var move = MoveVector(st.Get("move", "none"));
            Hero.scriptedMove = move;
            if (move != Vector2.zero) yield return new WaitForSeconds(0.5f); // walking already when the draw starts
            HideTips();
            bool started = ranged ? Combat.DrawRanged() : Combat.DrawMelee();
            if (!started) { Hero.scriptedMove = Vector2.zero; Fail($"drawtrace: draw refused ({Combat.DrawRefusal() ?? "-"})"); yield break; }
            yield return TraceHold(a, "draw", true);
            if (st.Bool("sheathe", true))
            {
                yield return new WaitForSeconds(0.5f);
                // fix-57 (r31 draw_aim_strafe: "drawtrace: sheathe refused" 0.5 s after the bow draw): after a ranged draw the
                // arrow or bolt is nocked; the weapon key then waits for the reload's end (fix-101: G3's action queue), the
                // trace presses once it is over. Not a fists bug: the fists loadout only happened to reach this step first.
                float w0 = Time.time;
                while (Combat.DrawRefusal() != null && Time.time - w0 < 4f) yield return null;
                if (Time.time - w0 > 0.05f) Log($"drawtrace: sheathe waited {F(Time.time - w0)} s for the hands ({a.StateText})");
                if (!Combat.ToggleWeapon())
                {
                    Hero.scriptedMove = Vector2.zero;
                    Fail($"drawtrace: sheathe refused ({Combat.DrawRefusal() ?? "-"}, animator {a.StateText})");
                    yield break;
                }
                yield return TraceHold(a, "sheathe", false);
            }
            Hero.scriptedMove = Vector2.zero;
        }

        IEnumerator TraceHold(G3.Unity.Characters.G3CombatAnimator a, string tag, bool draw)
        {
            float t0 = Time.time;
            string last = null;
            bool inHand = a.WeaponInHand;
            int motions = 0, motionsAtSwitch = -1;
            var lines = new List<string>();
            float moved = 0f;
            var p0 = Hero.transform.position;
            while (Time.time - t0 < 4f)
            {
                int before = motions;
                string hold = a.HoldText;
                if (hold != null && hold != last) { motions++; lines.Add($"{(Time.time - t0).ToString("0.00", Inv)} s {hold}{(a.HoldsBody ? " [body held]" : "")}"); }
                last = hold ?? last;
                if (a.WeaponInHand != inHand)
                {
                    inHand = a.WeaponInHand;
                    // fix-46: the animator changes the hand (HoldStacks) and starts the second motion in one Update, so a
                    // frame that shows both had the switch between the motions (r28: 0.68 s / 0.82 s, same timestamp)
                    motionsAtSwitch = before;
                    lines.Add($"{(Time.time - t0).ToString("0.00", Inv)} s weapon {(inHand ? "in the hand" : "on its carry slot")}");
                }
                if (a.Hand.Settled && !(a.HoldText != null)) break;
                yield return null;
            }
            moved = (Hero.transform.position - p0).magnitude;
            Log($"drawtrace {tag} {a.Weapon}: {string.Join("; ", lines)}; moved {F(moved)} m, done after {(Time.time - t0).ToString("0.00", Inv)} s");
            if (inHand != draw) Fail($"drawtrace {tag}: weapon {(inHand ? "still" : "not")} in the hand");
            else if (motions >= 2 && motionsAtSwitch != 1) Fail($"drawtrace {tag}: the hand changed after motion {motionsAtSwitch}, G3 changes it between the two");
        }

        /// <summary>
        /// <c>aimstrafe [dir=backleft] [secs=1.5]</c> (fix-42): with the bow or crossbow drawn (drawn first when not), raises
        /// the aim (left button held), walks with the keys of <c>dir</c> while aiming and logs the body yaw against the
        /// camera yaw, the gait, the speed and the locomotion set; then lowers the aim. Fails when the body turned away
        /// from the camera (G3: it keeps facing it and strafes) or the hero ran (G3: aiming walks).
        /// </summary>
        IEnumerator AimStrafeStep(ScenarioStep st)
        {
            var a = Combat.Fighter.Animator;
            if (!Combat.Fighter.RangedDrawn) yield return DrawStep("ranged");
            if (!Combat.Fighter.RangedDrawn) { Fail("aimstrafe: no bow or crossbow drawn"); yield break; }
            HideTips();
            Combat.PrimaryDown();
            float t0 = Time.time;
            while (!a.AimHeld && Time.time - t0 < 4f) yield return null;
            if (!a.AimHeld) { Combat.PrimaryUp(); Fail($"aimstrafe: the aim was not raised ({a.StateText})"); yield break; }
            var move = MoveVector(st.Get("dir", "backleft"));
            Hero.scriptedMove = move;
            float secs = st.Float("secs", 1.5f), maxTurn = 0f, maxSpeed = 0f;
            var anim = Hero.GetComponentInChildren<G3.Unity.Characters.G3HeroAnimator>();
            var modes = new HashSet<string>();
            for (float until = Time.time + secs; Time.time < until; )
            {
                yield return null;
                maxTurn = Mathf.Max(maxTurn, Mathf.Abs(Mathf.DeltaAngle(Hero.transform.eulerAngles.y, Hero.Yaw)));
                maxSpeed = Mathf.Max(maxSpeed, new Vector2(Hero.Velocity.x, Hero.Velocity.z).magnitude);
                modes.Add(Hero.MoveMode.ToString());
            }
            Hero.scriptedMove = Vector2.zero;
            string set = anim != null ? anim.LocomotionKey : "-";
            Log($"aimstrafe {st.Get("dir", "backleft")}: state {Combat.MoveState}, gait {string.Join("/", modes)}, max speed {F(maxSpeed)} m/s, " +
                $"body vs camera yaw max {F(maxTurn)}°, set {set}, aim {a.StateText}");
            Combat.SecondaryDown(); // lowers the aim, the arrow stays nocked
            yield return new WaitForSeconds(0.1f);
            Combat.SecondaryUp();
            Combat.PrimaryUp();
            if (maxTurn > 1f) Fail($"aimstrafe: the body turned {F(maxTurn)}° away from the camera");
            else if (modes.Contains("Run") || modes.Contains("Sprint")) Fail("aimstrafe: the hero ran while aiming (G3 walks)");
        }

        /// <summary>
        /// <c>gaitcheck [gait=sprint] [dir=fwd] [secs=1.5] [speed=m/s] [sp=n]</c> (fix-44): moves with the hands as they are
        /// (draw first with <c>draw</c> / <c>spell</c>) in the gait (sprint = the Walk/Sprint key held while moving, walk = the
        /// walk toggle, run) and logs the player state, the gaits seen, the average speed, the locomotion set and SP.
        /// <c>sp</c> sets the stamina first (G3: a sprint starts at 21 SP). Fails when the gait is not the wanted one (or
        /// Run with sp &lt; 21 and gait=sprint is expected via <c>expectgait</c>) or the speed differs from <c>speed</c> by &gt; 0.25 m/s.
        /// </summary>
        IEnumerator GaitCheckStep(ScenarioStep st)
        {
            string gait = st.Get("gait", "sprint").ToLowerInvariant(), dir = st.Get("dir", "fwd");
            string expect = st.Get("expectgait", gait == "sprint" ? "Sprint" : gait == "walk" ? "Walk" : "Run");
            if (st.Has("sp")) _session.Hero.Stats.Apply("SP", ModifierOp.SetVal, st.Int("sp", 100));
            HideTips();
            var anim = Hero.GetComponentInChildren<G3.Unity.Characters.G3HeroAnimator>();
            Hero.scriptedMove = MoveVector(dir);
            yield return new WaitForSeconds(0.3f); // moving: the Walk/Sprint key now means sprint (G3 OnPlayerWalk)
            Hero.scriptedSprint = gait == "sprint";
            Hero.scriptedWalk = gait == "walk";
            yield return new WaitForSeconds(0.3f);
            var modes = new HashSet<string>();
            var p0 = Hero.transform.position;
            float secs = st.Float("secs", 1.5f), t0 = Time.time, longest = 0f;
            int frames = 0;
            while (Time.time - t0 < secs)
            {
                yield return null;
                frames++;
                longest = Mathf.Max(longest, Time.deltaTime);
                modes.Add(Hero.MoveMode.ToString());
            }
            var d = Hero.transform.position - p0;
            float speed = new Vector2(d.x, d.z).magnitude / Mathf.Max(1e-3f, Time.time - t0);
            Hero.scriptedMove = Vector2.zero;
            Hero.scriptedSprint = Hero.scriptedWalk = false;
            string set = anim != null ? anim.LocomotionKey : "-";
            Log($"gaitcheck {gait} {dir}: state {Combat.MoveState}, gait {string.Join("/", modes)}, speed {F(speed)} m/s, set {set}, " +
                $"SP {_session.Combat.Vitals.Stamina(ActorId.Player)}, {frames} frames, longest {longest * 1000f:0} ms");
            if (modes.Count != 1 || !modes.Contains(expect)) Fail($"gaitcheck: gait {string.Join("/", modes)}, wanted {expect}");
            else if (st.Has("speed") && Mathf.Abs(speed - st.Float("speed", 0f)) > 0.25f)
                Fail($"gaitcheck: speed {F(speed)} m/s, wanted {F(st.Float("speed", 0f))} (G3 motion speed)");
        }

        /// <summary>The weapon is in the hand and no draw or sheathe motion runs.</summary>
        static bool Armed(G3.Unity.Characters.G3CombatAnimator a) => a.Drawn && a.WeaponInHand && a.Hand.Settled;

        /// <summary>
        /// Puts the weapon away until the hand is empty: waits for a running motion, tries again when a hit reaction cut the
        /// sheathe in its first half (the weapon stays in the hand; editor-runner-26 bensala_01 "sheathe, drawn=True" x3).
        /// Logs "<paramref name="tag"/>: sheathe, drawn=…" with the animator state and the refusal when the hand is not empty.
        /// </summary>
        IEnumerator SheatheStep(string tag)
        {
            var a = Combat.Fighter.Animator;
            float t = Time.time;
            int tries = 0;
            string why = null;
            if (Combat.PrimaryHeld) Combat.PrimaryUp(); // a held aim is let go first
            while ((a.Drawn || a.WeaponInHand) && Time.time - t < 8f && _session.Combat.Vitals.StateOf(ActorId.Player) == VitalState.Alive)
            {
                // fix-57 (r31 crossbow: 4 presses in 0.8 s during the bolt's reload, then "still armed"): the aim, a shot, the
                // reload or a cast refuse the sheathe; they are waited out without using up the presses
                bool ranging = a.InAim || a.InShot || a.InReload || a.InCast;
                if (ScenarioPacing.NextSheathe(a.Drawn, a.Busy, ranging, tries) == ScenarioPacing.SheatheAct.Wait)
                {
                    // !Drawn with the weapon in the hand: the sheathe motion runs (the hand empties between its two motions)
                    if (a.Drawn && a.Busy) why = "a combat motion runs (" + a.StateText + ")";
                    else if (a.Drawn && ranging) why = "aiming, shooting, reloading or casting (" + a.StateText + ")";
                    yield return null;
                    continue;
                }
                tries++;
                string refusal = Combat.DrawRefusal();
                if (!Combat.ToggleWeapon()) why = "refused: " + (refusal ?? "-");
                yield return new WaitForSeconds(0.2f);
            }
            string tail = a.Drawn || a.WeaponInHand
                ? $", still armed: {why ?? "-"} (animator {a.StateText}, busy={a.Busy}, refusal={Combat.DrawRefusal() ?? "none"})"
                : tries > 1 ? $" ({tries} tries)" : "";
            Log($"{tag}: sheathe, drawn={a.Drawn} inHand={a.WeaponInHand}{tail}");
        }

        /// <summary>The last <see cref="FightStep"/>: hit points the target lost after the hp clamp, and whether it was given up as out of reach.</summary>
        int _fightDamage;
        bool _fightUnreachable;

        /// <summary>
        /// <c>fight npc [hp=5] [secs=20] [hold=0.2] [sheathe=1]</c>: the hero fights the NPC in melee until it dies. The focus stays on
        /// it (several foes around: the scan alone picks by facing). Distances are horizontal: the hero closes in to
        /// swing reach (2.4 m to the root, height difference up to 1.6 m); stuck while closing in (less than 0.5 m in 2 s:
        /// a fence, a ledge, a two-sided collider) he is put 1.6 m from the target on his side (a dev shortcut, at most 3
        /// times). A target more than 2 m above or below him for 3 s is out of reach (a crawler on a mine ledge,
        /// editor-runner-23 okara_02): the fight ends there. The log tells swings, bodies the weapon touched and hit points
        /// lost (touched without damage: parried).
        /// </summary>
        /// <param name="mustKill">False for a defending fight inside another op (escort): a survivor is logged, not a failure.</param>
        IEnumerator FightStep(ScenarioStep st, bool mustKill = true, AttackStyle? forceStyle = null)
        {
            const float ReachHeight = 1.6f, Unreachable = 2f, UnreachableSecs = 3f, Hop = 1.6f;
            var s = _session;
            var id = Actor(st.Get("npc"));
            var V0 = s.Combat.Vitals;
            _fightDamage = 0;
            _fightUnreachable = false;
            // fix-54: the loadout's attack style (style= overrides): bows, crossbows and spells fight from a distance
            var style = forceStyle ?? ParseStyle(st.Get("style")) ?? _style;
            if (style == AttackStyle.Bow || style == AttackStyle.Crossbow || style == AttackStyle.Spell)
            {
                yield return DistanceFightStep(st, style, mustKill);
                yield break;
            }
            bool fists = style == AttackStyle.Fists;
            if (!Armed(Combat.Fighter.Animator) || Combat.Fighter.RangedDrawn || Combat.Fighter.SpellDrawn ||
                fists && Combat.Fighter.HeldWeaponClass != WeaponClass.Fist) yield return DrawStep(fists ? "fist" : "melee");
            int hp = st.Int("hp", 5);
            if (V0.HitPoints(id) > hp) V0.AddHitPoints(id, hp - V0.HitPoints(id));
            int hpStart = V0.HitPoints(id);
            string arenaLossBefore = s.WorldState?.ArenaQuests?.LastLoss; // fix-46: an arena fight lost during the fight is logged
            ProvokeProtectedBystander(id);
            float hold = st.Float("hold", 0.2f), t0 = Time.time, limit = st.Float("secs", 20f);
            int swings = 0, touches = 0, hops = 0, swingsAtHop = 0, touchesAtHop = 0;
            float farSince = -1f, progressAt = Time.time, progressDist = float.MaxValue, dy = 0f, h = 0f;
            var target = FighterOf(id);
            // fix-38 (r27 braga_02: 3 touches, 0 damage, "gate now: None"): what became of each touch. A gate is read at the
            // touch itself; the resolved hits (Hit/Blocked) come from the event, an ignored one is never published.
            var gates = new Dictionary<HitGate, int>();
            int blocked = 0, landed = 0;
            var presenter = FindAnyObjectByType<CombatPresenter>();
            void OnTouched(CombatFighter by, CombatBody body, MeleeBlades blade)
            {
                if (body == null || body.Fighter != target) return;
                touches++;
                if (presenter == null || target == null) return;
                var g = CombatResolver.Gate(presenter.BuildHit(Combat.Fighter, target));
                if (g != HitGate.None) gates[g] = gates.TryGetValue(g, out var n) ? n + 1 : 1;
            }
            void OnResolved(HitResolved e)
            {
                if (!e.Attacker.IsPlayer || e.Victim != id) return;
                if (e.Outcome.Result == HitResult.Blocked) blocked++;
                else if (e.Outcome.Result == HitResult.Hit) landed++;
            }
            var resolvedSub = s.Events.Subscribe<HitResolved>(OnResolved);
            // fix-46 (r28 ishtar_01): an arena fight is lost when the hero leaves the ring; hit reactions push him away from
            // the opponent, so near the edge he first steps back towards the middle, as a player would
            string arenaName = s.WorldState?.ArenaQuests?.ArenaOf(id);
            var arena = arenaName == null ? null
                : s.Content.Database.All<AiZoneDef>().FirstOrDefault(z => string.Equals(z.Name, arenaName, StringComparison.OrdinalIgnoreCase));
            float arenaCx = 0f, arenaCz = 0f;
            if (arena != null && !ZoneMap.Centre(arena.Outline, out arenaCx, out arenaCz)) arena = null;
            int arenaSteps = 0;
            bool pierce = false;
            float staggered = 0f; // the hero in a hit reaction, down or getting up: not fight time (ScenarioPacing.FightLimit)
            int refills = 0;
            float busyClose = 0f; // close enough to swing but the hero's animator was busy (r27 silden_02 PRE: 0 swings in 25 s)
            string busyAction = null;
            // fix-67 (r33 ch07 [2H] R33-1: 'swings=0 dist=2.5 hops=3' and nothing else): why the hero never got into reach -
            // busy or held (no movement input) while out of reach, how far he and the target moved meanwhile
            float farBusy = 0f, farLocked = 0f, heroWalked = 0f, targetWalked = 0f;
            string farAction = null;
            Vector3? lastHero = null, lastTarget = null;
            float lastHalf = 1f;
            Combat.Fighter.Touched += OnTouched;
            try
            {
                while (V0.StateOf(id) == VitalState.Alive && Time.time - t0 < ScenarioPacing.FightLimit(limit, staggered))
                {
                    if (!TryActorPosition(id, out var p, out var view)) { Fail("fight: lost " + id); break; }
                    // fix-41 (r28 ch09/ch13): the clamp holds; G3 RefreshHitPoints refilled a boss when its cell streamed in
                    if (ScenarioPacing.Reclamp(V0.HitPoints(id), hpStart))
                    {
                        if (refills++ == 0) Log($"fight {id}: hp {V0.HitPoints(id)} above the clamp {hpStart} (refilled: entered the processing range?), clamped again");
                        V0.AddHitPoints(id, hpStart - V0.HitPoints(id));
                    }
                    var heroAnim = Combat.Fighter.Animator;
                    if (heroAnim.InReaction || heroAnim.IsDown || Combat.Fighter.SittingKnockedDown || Combat.Fighter.Frozen) staggered += Time.deltaTime;
                    var hero = Hero.transform.position;
                    var flat = p - hero;
                    dy = flat.y;
                    flat.y = 0f;
                    h = flat.magnitude;
                    var f = view != null ? view.GetComponent<CombatFighter>() : null;
                    if (f != null) target = f;
                    if (f != null) { Combat.PinnedFocus = f; Combat.SetFocus(f); }
                    Face(p, 8f);
                    // out of reach above or below (a ledge, a mine shelf): a short try, then leave it
                    if (Mathf.Abs(dy) > Unreachable)
                    {
                        if (farSince < 0) farSince = Time.time;
                        if (Time.time - farSince > UnreachableSecs) { _fightUnreachable = true; break; }
                    }
                    else farSince = -1f;
                    // fix-64 (r31 melee_small [dual]: meatbug touched 0 of 8 swings at 2-3 m): swing and stop by the target's size
                    float half = target != null && target.Body != null && target.Body.Trigger.HalfX > 0f ? Mathf.Min(target.Body.Trigger.HalfX, target.Body.Trigger.HalfZ) : 1f;
                    float reach = ScenarioPacing.MeleeReach(half);
                    lastHalf = half;
                    bool close = h <= reach && Mathf.Abs(dy) <= ReachHeight;
                    if (arena != null && !heroAnim.Busy && ZoneMap.Contains(arena.Outline, hero.x, hero.z) &&
                        ZoneMap.EdgeDistance(arena.Outline, hero.x, hero.z) < ArenaEdgeKeep)
                    {
                        if (arenaSteps++ == 0) Log($"fight {id}: the hero is {F(ZoneMap.EdgeDistance(arena.Outline, hero.x, hero.z))} m from the edge of {arena.Name}, steps back to the middle");
                        Face(new Vector3(arenaCx, hero.y + 1.5f, arenaCz), 8f);
                        Hero.scriptedMove = new Vector2(0, 1);
                        yield return null;
                        continue;
                    }
                    Hero.scriptedMove = h > ScenarioPacing.MeleeStop(half) ? new Vector2(0, 1) : Vector2.zero;
                    // closing in: stuck (a fence, a ledge, a wall between) -> next to the target on the hero's side
                    if (h < progressDist - 0.5f) { progressDist = h; progressAt = Time.time; }
                    else if (!close && Time.time - progressAt > 2f && hops < 3 && Mathf.Abs(dy) <= Unreachable && !Combat.Fighter.Animator.Busy)
                    {
                        hops++;
                        var back = h > 0.01f ? -flat / h : -Hero.transform.forward;
                        if (!HopSpot(p, back, view, out var spot, out var hopWhy))
                        {
                            Log($"fight {id}: no closer than {F(h)} m in 2 s (dy {F(dy)}), no ground next to it ({hopWhy})");
                            progressAt = Time.time;
                            yield return null;
                            continue;
                        }
                        Log($"fight {id}: no closer than {F(h)} m in 2 s (dy {F(dy)}), steps next to it ({hops})");
                        Hero.scriptedMove = Vector2.zero;
                        Hero.Teleport(spot);
                        progressDist = Hop;
                        progressAt = Time.time;
                        yield return new WaitForSeconds(0.2f);
                        continue;
                    }
                    // swinging without touching (a boar 0.7 m below on a slope, editor-runner-25 okara_01: 27 swings, 0
                    // touched): the swing arc passes over it -> next to it on its level (counts as a hop)
                    if (close && swings - swingsAtHop >= 6 && touches == touchesAtHop && hops < 3 && !Combat.Fighter.Animator.Busy)
                    {
                        hops++;
                        int missed = swings - swingsAtHop;
                        swingsAtHop = swings;
                        var back = h > 0.01f ? -flat / h : -Hero.transform.forward;
                        if (!HopSpot(p, back, view, out var spot2, out var hopWhy2))
                        {
                            Log($"fight {id}: {missed} swings touched nothing (dist {F(h)}, dy {F(dy)}), no ground next to it ({hopWhy2})");
                            yield return null;
                            continue;
                        }
                        Log($"fight {id}: {missed} swings touched nothing (dist {F(h)}, dy {F(dy)}), steps next to it ({hops})");
                        Hero.scriptedMove = Vector2.zero;
                        Hero.Teleport(spot2);
                        yield return new WaitForSeconds(0.2f);
                        continue;
                    }
                    if (touches != touchesAtHop) { touchesAtHop = touches; swingsAtHop = swings; }
                    // A target that parries every hit (G3 CanParade: a Ranger guard in its parade takes no damage, CP
                    // extended AI: no shortfall either) is attacked as a player would: block + hold = PierceAttack, which
                    // a one-handed blade drives through a parade without a shield (CombatResolver.CanBlockMelee).
                    if (!pierce && blocked >= 2 && landed == 0 && Combat.Fighter.HeldWeaponClass == WeaponClass.OneHanded)
                    {
                        pierce = true;
                        Log($"fight {id}: {blocked} hits parried, attacks with the pierce (block + hold)");
                    }
                    if (!close)
                    {
                        if (Combat.Fighter.Animator.Busy) { farBusy += Time.deltaTime; farAction = Combat.Fighter.Animator.Action ?? Combat.Fighter.Animator.StateText; }
                        if (!Hero.moveInputEnabled) farLocked += Time.deltaTime;
                        if (lastHero.HasValue) heroWalked += Vector3.Distance(lastHero.Value, hero);
                        if (lastTarget.HasValue) targetWalked += Vector3.Distance(lastTarget.Value, p);
                    }
                    lastHero = hero;
                    lastTarget = p;
                    if (close && Combat.Fighter.Animator.Busy)
                    {
                        busyClose += Time.deltaTime;
                        busyAction = Combat.Fighter.Animator.Action ?? "-";
                    }
                    if (close && !Combat.Fighter.Animator.Busy)
                    {
                        if (pierce)
                        {
                            Combat.SecondaryDown();
                            yield return new WaitForSeconds(0.15f);
                            Combat.PrimaryDown();
                            yield return new WaitForSeconds(1.0f);
                            Combat.PrimaryUp();
                            yield return new WaitForSeconds(0.1f);
                            Combat.SecondaryUp();
                        }
                        else
                        {
                            Combat.PrimaryDown();
                            yield return new WaitForSeconds(hold);
                            Combat.PrimaryUp();
                        }
                        swings++;
                    }
                    yield return null;
                }
            }
            finally
            {
                Combat.Fighter.Touched -= OnTouched;
                resolvedSub.Dispose();
                if (pierce) Combat.SecondaryUp();
                Combat.PinnedFocus = null;
                Hero.scriptedMove = Vector2.zero;
            }
            _fightDamage = Mathf.Max(0, hpStart - V0.HitPoints(id));
            if (mustKill) CreditClock(Mathf.Max(0f, Time.time - t0 - limit)); // the stagger extension (a defend's time is the escort's pause)
            string how = _fightUnreachable ? FormattableString.Invariant($", out of reach ({dy:0.0} m above/below for {UnreachableSecs:0} s)") : "";
            if (busyClose > 3f) how += FormattableString.Invariant($", in reach but busy {busyClose:0.0} s (last {busyAction})");
            if (staggered > 1f) how += FormattableString.Invariant($", hero staggered {staggered:0.0} s (limit {ScenarioPacing.FightLimit(limit, staggered):0} s)");
            if (refills > 0) how += $", hp refilled {refills}x";
            if (swings == 0 && V0.StateOf(id) == VitalState.Alive)
                how += FormattableString.Invariant($", out of reach: busy {farBusy:0.0} s (last {farAction ?? "-"}), no move input {farLocked:0.0} s, hero moved {heroWalked:0.0} m, target moved {targetWalked:0.0} m, reach {ScenarioPacing.MeleeReach(lastHalf):0.00}");
            if (touches > 0 && _fightDamage == 0 && V0.StateOf(id) == VitalState.Alive)
            {
                how += ", gate now: " + GateNow(target);
                how += $", outcomes: hit {landed} blocked {blocked}" + (gates.Count > 0 ? " gates " + string.Join(" ", gates.Select(kv => $"{kv.Key} {kv.Value}")) : "") +
                       (pierce ? ", pierce used" : "");
            }
            Log($"fight {id}: {V0.StateOf(id)} hp={V0.HitPoints(id)} swings={swings} touched={touches} lost={_fightDamage} dist={F(h)} dy={F(dy)} hops={hops} {(Time.time - t0).ToString("0.00", Inv)} s{how}");
            string arenaLoss = s.WorldState?.ArenaQuests?.LastLoss;
            if (arenaLoss != null && !ReferenceEquals(arenaLoss, arenaLossBefore)) Log($"fight {id}: arena quest lost: {arenaLoss} (hero at {V(Hero.transform.position)})");
            // kill=0 (fix-184, fix81_party_animals: a few blows that must leave it alive): a survivor is no failure
            if (V0.StateOf(id) == VitalState.Alive && mustKill && st.Bool("kill", true)) Fail(FormattableString.Invariant($"fight {id}: still alive after {Time.time - t0:0} s{how}"));
            yield return SheatheAfterFight(st, id, mustKill);
        }

        /// <summary>
        /// A player puts the weapon away after a fight: a drawn weapon makes NPCs around warn and attack (ReactToWeapon;
        /// editor-runner-24: the guide Mort fought the hero 9 min after a defend). fix-51 (owner): but not while foes are
        /// still about (draw, kill, sheathe, draw again for the next one); sheathe=0|1 overrides.
        /// </summary>
        IEnumerator SheatheAfterFight(ScenarioStep st, ActorId id, bool mustKill)
        {
            var V0 = _session.Combat.Vitals;
            if (!Combat.Fighter.Animator.Drawn || V0.StateOf(ActorId.Player) != VitalState.Alive) yield break;
            var guide = st.Get("guide");
            var style = ParseStyle(st.Get("style")) ?? _style;
            int near = HostilesNear(guide != null ? Actor(guide) : default, ScenarioPacing.KeepDrawnRadiusFor(style));
            string next = mustKill && _spec != null && _stepIndex + 1 < _spec.Steps.Count ? _spec.Steps[_stepIndex + 1].Op : null;
            // fix-57: a defend inside an escort keeps it drawn; the escort sheathes once the area is clear (EscortSheathe)
            bool inEscort = guide != null && !mustKill;
            if (ScenarioPacing.SheatheAfterFight(st.Get("sheathe"), near, next, inEscort)) yield return SheatheStep("fight");
            else if (near > 0 || inEscort)
                Log($"fight {id}: keeps the weapon drawn: {near} hostiles near" + (inEscort ? " (escort: sheathed once clear)" : "") +
                    (st.Get("sheathe") == "0" ? " (sheathe=0)" : ""));
        }

        /// <summary>
        /// Alive NPCs attacking the hero or <paramref name="guide"/> within <paramref name="radius"/> (default
        /// <see cref="ScenarioPacing.KeepDrawnRadius"/>) of the hero or of the guide (fix-57: a wolf biting Mort 20 m ahead counts).
        /// </summary>
        int HostilesNear(ActorId guide, float radius = ScenarioPacing.KeepDrawnRadius)
        {
            var s = _session;
            var at = Hero.transform.position;
            var guideAt = at;
            bool hasGuide = !guide.IsEmpty && TryActorPosition(guide, out guideAt, out _);
            int n = 0;
            foreach (var b in s.NpcCombat.Brains)
            {
                if (!b.IsAttacking || s.Combat.Vitals.StateOf(b.Self) != VitalState.Alive) continue;
                var t = b.State.Target;
                if (!t.IsPlayer && (guide.IsEmpty || t != guide)) continue;
                if (TryActorPosition(b.Self, out var p, out _) && (Flat(p - at) <= radius || hasGuide && Flat(p - guideAt) <= radius)) n++;
            }
            return n;
        }

        /// <summary>
        /// The hero's melee does nothing against a friendly or neutral NPC fighting someone else (G3 friendly-fire rule,
        /// HitGate.ProtectedBystander; editor-runner-24 bensala_01: neutral BenSala zombies fighting Miguel took no damage,
        /// "no hit landed"). A dev shortcut, logged: the NPC turns on the hero first, so the fight can be fought.
        /// </summary>
        void ProvokeProtectedBystander(ActorId id)
        {
            var s = _session;
            var brain = s.NpcCombat.Get(id);
            if (brain == null || !brain.IsAttacking || brain.State.Target.IsPlayer) return;
            if (s.Party?.Members != null && s.Party.Members.IsLedByPlayer(brain.State.Target)) return; // fights the hero's party: no bystander
            var attitude = s.Crime.Attitudes.Of(id, ActorId.Player);
            // a neutral NPC the hero focuses takes his hits (G3 rule, fix-25): only friendly ones need turning
            if (attitude != Attitude.Friendly) return;
            var was = brain.State.Target;
            brain.Disengage();
            bool ok = s.NpcCombat.Engage(id, ActorId.Player, AttackReason.ReactToDamage);
            Log($"fight {id}: {attitude} to the hero and fighting {was} (protected bystander: melee would do nothing), turned on the hero ({(ok ? "ok" : "refused")})");
        }

        /// <summary>Why the hero's next melee hit on <paramref name="target"/> would do nothing (diagnostics after touching hits without damage).</summary>
        /// <summary>
        /// Ground 1.6 m from the target on the hero's side (else around it) on its level, with room for the hero. fix-38
        /// (r27 trelis_01): the blind hop put the hero into Trelis's ground, he fell out of the world and was caught on a
        /// rock 64 m above the escort.
        /// </summary>
        bool HopSpot(Vector3 target, Vector3 back, Transform targetView, out Vector3 spot, out string why)
        {
            const float Hop = 1.6f, Up = 2f, Down = 4f, MaxStep = 1.5f;
            spot = default;
            why = "no ground";
            var heroRoot = Hero.transform;
            bool Ours(Collider c) => c.transform.IsChildOf(heroRoot) || (targetView != null && c.transform.IsChildOf(targetView));
            for (int i = 0; i < 8; i++)
            {
                var dir = Quaternion.Euler(0, (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * 45f, 0) * back;
                var c = target + dir * Hop;
                var hits = Physics.RaycastAll(new Vector3(c.x, target.y + Up, c.z), Vector3.down, Up + Down, ~0, QueryTriggerInteraction.Ignore);
                RaycastHit? best = null;
                foreach (var x in hits)
                    if (!Ours(x.collider) && (!best.HasValue || x.distance < best.Value.distance)) best = x;
                if (!best.HasValue) { why = $"no ground at {V(c)}"; continue; }
                var g = best.Value.point;
                if (Mathf.Abs(g.y - target.y) > MaxStep) { why = $"ground {F(g.y - target.y)} m off at {V(g)}"; continue; }
                if (best.Value.normal.y < 0.6f) { why = $"steep at {V(g)}"; continue; }
                bool blocked = Physics.OverlapCapsule(g + Vector3.up * 0.6f, g + Vector3.up * 1.6f, 0.3f, ~0, QueryTriggerInteraction.Ignore).Any(col => !Ours(col));
                if (blocked) { why = $"no room at {V(g)}"; continue; }
                spot = g + Vector3.up * 0.1f;
                return true;
            }
            return false;
        }

        string GateNow(CombatFighter target)
        {
            var presenter = FindAnyObjectByType<CombatPresenter>();
            if (presenter == null || target == null || Combat == null) return "?";
            var hit = presenter.BuildHit(Combat.Fighter, target);
            var gate = CombatResolver.Gate(hit);
            return gate != HitGate.None ? gate.ToString() : $"None (attitude {hit.VictimToAttacker}, fights other {hit.VictimFightsSomeoneElse}, targeted {hit.AttackerTargetsVictim})";
        }

        IEnumerator FinisherStep(ScenarioStep st)
        {
            var s = _session;
            var id = Actor(st.Get("npc"));
            var V0 = s.Combat.Vitals;
            if (!Armed(Combat.Fighter.Animator)) yield return DrawStep("melee");
            float hold = st.Float("hold", 1.6f);
            int tries = st.Int("tries", 4);
            string last = "";
            for (int k = 0; k < tries && V0.StateOf(id) == VitalState.Down; k++)
            {
                float w = Time.time;
                while (Combat.Fighter.Animator.Busy && Time.time - w < 3f) yield return null;
                if (!TryActorPosition(id, out var p, out var view)) { Fail("finisher: lost " + id); yield break; }
                var away = Hero.transform.position - p;
                away.y = 0;
                if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                Hero.Teleport(p + away.normalized * 1.3f + Vector3.up * 0.1f);
                yield return new WaitForSeconds(0.2f);
                Face(p, 20f);
                var f = view != null ? view.GetComponent<CombatFighter>() : null;
                if (f != null) Combat.SetFocus(f);
                Combat.SecondaryDown();
                yield return new WaitForSeconds(0.2f);
                Combat.PrimaryDown();
                yield return new WaitForSeconds(hold);
                last = $"try {k + 1}: action={Combat.Fighter.Animator.Action} focus={(Combat.Focus != null ? Combat.Focus.Actor.ToString() : "-")} state={V0.StateOf(id)}";
                Log("finisher " + last);
                Combat.PrimaryUp();
                Combat.SecondaryUp();
                float t = Time.time;
                while (Time.time - t < 2.5f && V0.StateOf(id) == VitalState.Down) yield return null;
            }
            // fix-186: soft=1 (the escort's finish of a downed attacker) logs a miss instead of failing the step
            if (V0.StateOf(id) != VitalState.Dead && st.Bool("soft", false)) Log($"finisher {id}: {V0.StateOf(id)} ({last}), soft");
            else if (V0.StateOf(id) != VitalState.Dead) Fail($"finisher {id}: {V0.StateOf(id)} ({last})");
            else Log($"finisher {id}: Dead");
        }

        IEnumerator CastStep(ScenarioStep st)
        {
            var s = _session;
            var P = ActorId.Player;
            string spell = st.Get("spell");
            if (s.Items.Inventories.Of(P).Count(spell) == 0) s.Items.Inventories.Give(P, spell, 1);
            if (s.HeroSpells.SpellOf(spell) is string learnIt) s.Progression.LearnSkill(learnIt); // G3: spell books need HasLearnedSpell
            if (s.Hero.Stats.Value("INT") < 300) s.Hero.Stats.Apply("INT", ModifierOp.SetVal, 300);
            if (s.Hero.Stats.Maximum("MP") < 1000) s.Hero.Stats.Apply("MP", ModifierOp.AddToMax, 1000);
            s.Hero.Stats.Apply("MP", ModifierOp.SetValToMax, 0);
            HideTips();
            bool ready = Combat.ReadySpell(spell);
            yield return new WaitForSeconds(0.5f);
            // fix-114: the hand of the last cast may still be put away (a used-up scroll) and the new one drawn: wait for it
            float until = Time.time + 6f;
            while (Time.time < until && Combat.CastBlocker != null) yield return null;
            if (st.Has("target") && TryTarget(st.Get("target"), out var p, out _, out _, out _, out _))
            {
                Face(p, 5f);
                yield return null;
                yield return null;
            }
            string blocker = Combat.CastBlocker;
            // fix-114: when the spell left the hand, measured from the press, and whether the button was still held then
            float pressed = Time.time, leftAt = -1f;
            bool heldThen = false;
            string focusAtLeft = null;
            System.Action<SpellCast> onLeft = c => { leftAt = Time.time - pressed; heldThen = Combat.PrimaryHeld; focusAtLeft = (Combat.Focus != null ? Combat.Focus.Actor.ToString() : "-") + (c.Powered ? " powered" : ""); };
            Combat.Magic.Released += onLeft;
            Combat.PrimaryDown();
            bool begun = Combat.Magic.Casting || leftAt >= 0f;
            var action = Combat.Magic.Cast?.Action;
            yield return new WaitForSeconds(st.Float("hold", 1f));
            bool released = leftAt >= 0f || Combat.Magic.ReleaseQueued;
            Combat.PrimaryUp();
            var afterUp = Combat.Magic.Cast?.Action;
            float waitUntil = Time.time + 3f;
            while (Time.time < waitUntil && Combat.Magic.Casting) yield return null;
            Combat.Magic.Released -= onLeft;
            _lastCastTiming = leftAt < 0f ? "none" : heldThen ? "held" : "afterup";
            _vars["castleft"] = leftAt < 0f ? "-" : leftAt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string line = $"cast {spell}: ready={ready} begin={begun} release={released} action={action}{(afterUp != null && afterUp != action ? "->" + afterUp : "")} " +
                          $"left {(leftAt < 0f ? "never" : leftAt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s after the press, button " + (heldThen ? "held" : "up"))} " +
                          $"focus={(Combat.Focus != null ? Combat.Focus.Actor.ToString() : "-")}{(focusAtLeft != null ? " (at the release " + focusAtLeft + ")" : "")}{(begun ? "" : " blocker=" + (blocker ?? "pre-check"))}";
            if (!begun) Fail(line); else Log(line);
        }

        /// <summary>
        /// Game time that passed this frame, real time while the game is paused (fix-112: the game menu sets
        /// <c>Time.timeScale = 0</c>, and a scaled <c>wait</c> under it never ended).
        /// </summary>
        static float WaitDelta => Time.timeScale > 0f ? Time.deltaTime : Time.unscaledDeltaTime;
        /// <summary>fix-114: when the last 'cast' let the spell go: held (during the motion, button down), afterup, none.</summary>
        string _lastCastTiming = "-";

        IEnumerator WaitStep(ScenarioStep st)
        {
            float waited = 0f;
            if (!st.Has("until"))
            {
                float secs = st.Float("secs", 1f);
                while (waited < secs)
                {
                    yield return null;
                    waited += WaitDelta;
                }
                yield break;
            }
            if (!ScenarioCondition.TryParse(st.Get("until"), out var c, out var err)) { Fail("wait: " + err); yield break; }
            float timeout = st.Float("timeout", st.Float("secs", 15f));
            string actual = null;
            while (waited < timeout)
            {
                actual = Evaluate(c);
                if (c.Test(actual))
                {
                    Log($"wait {c} after {waited.ToString("0.00", Inv)} s ({actual})");
                    yield break;
                }
                for (float poll = 0f; poll < 0.2f && waited < timeout; )
                {
                    yield return null;
                    float dt = WaitDelta;
                    poll += dt;
                    waited += dt;
                }
            }
            string timedOut = FormattableString.Invariant($"wait {c}: timeout {timeout:0} s (last {actual})");
            if (st.Bool("soft", false)) Log(timedOut + ", soft"); else Fail(timedOut);
        }

        // ---------------------------------------------------------------- small helpers

        /// <summary>An item id from an id, id part or localized name part (Russian or English); null (and a failure) if nothing matches.</summary>
        /// <remarks>
        /// fix-54 (r30: "give It_Arrow" gave It_Book_Arrow, "equip It_Staff_01" took It_Staff_Mana_01): a scenario file needs an
        /// exact id or a unique match; an ambiguous or loose match fails and names the candidates. The dev console
        /// (<see cref="ScenarioSpec.LooseNames"/>) still takes the first match, and says that it was a guess.
        /// </remarks>
        internal string ResolveItem(string query, bool spellsOnly = false)
        {
            var db = _session.Content.Database;
            if (!spellsOnly && db.TryGet(query, out ItemDef exact)) return exact.Id;
            var items = db.All<ItemDef>().Where(i => !spellsOnly || i.Id.StartsWith("It_Spell_", StringComparison.OrdinalIgnoreCase) || i.Id.StartsWith("It_Scroll_", StringComparison.OrdinalIgnoreCase));
            var r = DevLookup.Resolve(items.SelectMany(i => DevLookup.Names(i.Id, i.Name, _session.Localizer)), query);
            if (r.Id == null) { Fail($"no item matches '{query}'"); return null; }
            bool loose = _spec != null && _spec.LooseNames;
            if (!r.Safe && !loose) { Fail("item " + r.Problem); return null; }
            if (r.Match != LookupMatch.Exact)
                Log($"'{query}' -> {r.Id} ({r.Match} match" + (r.Candidates.Count > 1 ? $", +{r.Candidates.Count - 1} more: {string.Join(",", r.Candidates.Skip(1).Take(4))}" : "") +
                    (r.Safe ? ")" : ") - a guess: " + r.Problem));
            return r.Id;
        }

        void HideTips()
        {
            var v = FindAnyObjectByType<G3TutorialView>();
            if (v != null && v.Tips != null) v.Tips.HideTips = true;
            // r31 D1: HideTips stops new tips only; an open one kept the game paused while `safe` forced timeScale 1
            if (v != null) v.CloseOpen();
        }

        string Forget(string entry)
        {
            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
            var world = typeof(DialogService).GetField("_world", BF)?.GetValue(_session.Dialog) as StoryWorld;
            var given = typeof(DialogMemory).GetField("_given", BF)?.GetValue(world?.DialogMemory) as HashSet<Id<DialogEntryDef>>;
            if (given == null) { Fail("forget: DialogMemory._given not found (renamed?)"); return "forget failed"; }
            bool removed = given.Remove(new Id<DialogEntryDef>(entry));
            return $"forget {entry}: {(removed ? "removed" : "was not given")}";
        }

        string DialogState()
        {
            var c = _session.Dialog.Current;
            if (c == null) return "no dialog";
            return $"{c.Partner} {c.Phase} entry={(c.Entry != null ? c.Entry.Id.ToString() : "-")} options=[{string.Join(",", c.Options.Select(o => o.Entry + (o.IsExit ? "(x)" : "")))}]";
        }
    }
}
#endif
