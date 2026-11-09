#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using G3.Unity;
using G3.Player;
using G3.Unity.Characters;
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
        /// <summary>One line: vitals, AI, animator and water state of an actor ("hero" or an NPC id).</summary>
        public string ProbeNow(string actor)
        {
            Bind();
            var s = _session;
            var id = Actor(actor);
            var V0 = s.Combat.Vitals;
            if (!TryActorPosition(id, out var p, out var view)) return $"probe {actor}: unknown actor";
            if (id.IsPlayer)
            {
                var a = Combat != null && Combat.Fighter != null ? Combat.Fighter.Animator : null;
                return $"hero {V(p)} hp={V0.HitPoints(id)}/{V0.HitPointsMax(id)} {V0.StateOf(id)} water={Hero.Water} depth={F(Hero.WaterDepth)} " +
                       $"grounded={Hero.IsGrounded} sneak={Hero.Sneaking} drawn={(a != null && a.Drawn)} action={a?.Action ?? "-"} key={KeyOf(Hero.transform)} " +
                       $"focus={(Combat?.Focus != null ? Combat.Focus.Actor.ToString() : "-")} dialog={DialogState()} hour={F(s.Clock.HourOfDay)} " +
                       $"{(Combat != null && Combat.Fighter != null ? Combat.SitStateText : "-")} " +
                       $"use={(FindAnyObjectByType<HeroObjectUse>() is HeroObjectUse u && u.Busy)}";
            }
            var b = s.NpcCombat.Get(id);
            float yaw = ActorYaw(id, view);
            _probeYaw[id] = yaw;
            string sim = s.Npcs.TryGet(id, out var agent) ? $"{agent.Activity}/{agent.Detail}" : "no agent";
            var fighter = view != null ? view.GetComponent<CombatFighter>() : null;
            var npcAnim = view != null ? view.GetComponentInChildren<G3NpcAnimator>() : null;
            string water = G3Water.TrySurface(p + Vector3.down * 20f, out float sy, 40f) ? $"aboveSurface={F(p.y - sy)}" : "dry";
            return $"{id} {V(p)} yaw={F0(yaw)} d={F((p - Hero.transform.position).magnitude)} hp={V0.HitPoints(id)}/{V0.HitPointsMax(id)} {V0.StateOf(id)} sim={sim} " +
                   $"fight={(b != null && b.IsFighting ? b.State.Phase + "->" + b.State.Target + "(" + b.State.Reason + ")" : "no")} " +
                   $"att={s.Crime.Attitudes.Of(id, ActorId.Player)} calm={_calm.Contains(id)} frozen={_frozen.Contains(id)} view={(view != null)} " +
                   $"key={(view != null ? KeyOf(view) : "-")} action={fighter?.Animator?.Action ?? "-"} drawn={(fighter?.Animator != null && fighter.Animator.Drawn)} " +
                   $"wading={(npcAnim != null && npcAnim.Wading)} {water} summons={s.Summons.MembersOf(id).Count} party={s.Party.Members.TypeOf(id)}" +
                   (fighter != null && FindAnyObjectByType<CombatPresenter>() is CombatPresenter cp ? $" routine={cp.InRoutine(fighter)} unaware={cp.Unaware(fighter, Combat.Fighter)}" : "");
        }

        readonly System.Collections.Generic.Dictionary<ActorId, float> _probeYaw = new System.Collections.Generic.Dictionary<ActorId, float>();

        public string TestNow(string condition)
        {
            Bind();
            if (!ScenarioCondition.TryParse(condition, out var c, out var err)) return "error: " + err;
            string actual = Evaluate(c);
            return $"{c.Test(actual).ToString().ToLowerInvariant()} ({actual})";
        }

        /// <summary>The users of an object's slots (fix-66 probe <c>objusers</c>): "Smith_Faring,-" per slot; "-" without routine data.</summary>
        string ObjectUsers(string id)
        {
            var point = new Id<RoutinePointDef>(id);
            if (!_session.Npcs.Points.TryGetPoint(point, out var p)) return "-";
            return string.Join(",", Enumerable.Range(0, p.Slots.Count)
                .Select(i => _session.Npcs.Claims.TryGetUser(new Station(point, i), out var u) ? u.ToString() : "-"));
        }

        /// <summary>fix-99 probe <c>navat:x,y,z</c>: "True" when the NavMesh has a point within 0.5 m of it (above or below).</summary>
        static string NavAt(string arg)
        {
            var v = (arg ?? "").Split(',');
            if (v.Length < 3 || !float.TryParse(v[0], NumberStyles.Float, Inv, out float x) || !float.TryParse(v[1], NumberStyles.Float, Inv, out float y) ||
                !float.TryParse(v[2], NumberStyles.Float, Inv, out float z)) return "bad point";
            var p = new Vector3(x, y, z);
            return (UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 0.5f, UnityEngine.AI.NavMesh.AllAreas) &&
                    Mathf.Abs(hit.position.y - y) <= 0.5f).ToString();
        }

        static string KeyOf(Transform t)
        {
            var mp = t.GetComponentInChildren<G3MotionPlayer>();
            return mp != null ? mp.CurrentKey ?? "-" : "-";
        }

        bool _lastAutosave;
        string _lastWrote = "-";

        /// <summary>fix-82: row <paramref name="index"/> of the load (loadrow, loadname) or save list (saverow), "-" past the end.</summary>
        string SaveRow(string kind, string index)
        {
            if (Saves == null) return "-";
            var rows = SaveGameList.For(kind == "saverow", Saves.Slots(), _session.Localizer, _session.Settings.Languages.TextLanguage);
            int i = int.TryParse(index, NumberStyles.Integer, Inv, out int n) ? n : 0;
            if (i < 0 || i >= rows.Count) return "-";
            return kind == "loadname" ? rows[i].Name : kind == "loaddetail" ? rows[i].Detail : rows[i].Slot;
        }

        /// <summary>
        /// fix-107: the files page's list — <c>visible</c> rows at once, <c>count</c>, <c>selected</c> index, <c>first</c> visible
        /// row, <c>page</c> (Load / Save / closed: the menu's page); "-" without a menu.
        /// </summary>
        string FilesListProbe(string what)
        {
            var menu = FindAnyObjectByType<GameMenu>();
            var list = menu != null && menu.View != null ? menu.View.Files?.List : null;
            if (what == "page") return menu != null ? menu.State.Page.ToString() : "-";
            if (list == null) return "-";
            switch (what)
            {
                case "count": return list.Count.ToString(Inv);
                case "selected": return list.SelectedIndex.ToString(Inv);
                case "first": return list.FirstRow.ToString(Inv);
                // fix-112: lines (quick + auto share one), the selected item's line and column (-1 whole, 0 left, 1 right)
                case "lines": return list.Grid.LineCount.ToString(Inv);
                case "line": return list.SelectedIndex >= 0 ? list.Grid[list.SelectedIndex].Line.ToString(Inv) : "-";
                case "column": return list.SelectedIndex >= 0 ? list.Grid[list.SelectedIndex].Column.ToString(Inv) : "-";
                default: return list.VisibleRows.ToString(Inv);
            }
        }

        /// <summary>
        /// fix-138: the main menu's Continue — <c>slot</c> the save it loads first (<c>-</c>: none), <c>count</c> the saves it
        /// may fall back to, <c>shown</c> / <c>enabled</c> the button now, <c>top</c> its layout top (page px of 768).
        /// </summary>
        string ContinueProbe(string what)
        {
            var menu = FindAnyObjectByType<GameMenu>();
            var slots = Saves != null ? Saves.ContinueSlots() : null;
            var button = menu != null && menu.View != null ? menu.View.ContinueButton : null;
            switch (what)
            {
                case "count": return slots != null ? slots.Count.ToString(Inv) : "-";
                case "shown": return (button != null && button.resolvedStyle.display != UnityEngine.UIElements.DisplayStyle.None &&
                                      menu.State.Page == MenuPage.Main).ToString();
                case "enabled": return (button != null && button.enabledSelf).ToString();
                case "top": return button != null && button.userData is Myrtana.Ui.Skin.WindowData w ? w.Rect[1].ToString(Inv) : "-";
                default: return slots != null && slots.Count > 0 ? slots[0] : "-";
            }
        }

        bool _recoveryAsked;

        /// <summary>
        /// fix-155: crash recovery - <c>exists</c> the hidden snapshot is on disk, <c>listed</c> it shows in the load list,
        /// <c>continue</c> Continue would load it, <c>ms</c> / <c>kb</c> the last snapshot's main-thread serialization and
        /// size, <c>count</c> snapshots this run, <c>due</c> seconds until the next, <c>asked</c> the last offer op opened the
        /// question, <c>text</c> the open confirm box's text.
        /// </summary>
        string RecoveryProbe(string what)
        {
            var saves = Saves;
            if (saves == null) return "-";
            var slots = saves.Slots();
            switch (what)
            {
                case "listed": return SaveGameList.For(false, slots, _session.Localizer).Any(r => r.Slot == CrashRecovery.Slot).ToString();
                case "continue": return saves.ContinueSlots().Contains(CrashRecovery.Slot).ToString();
                case "ms": return saves.LastRecoveryMs.ToString("0.00", Inv);
                case "kb": return (saves.LastRecoveryBytes / 1024).ToString(Inv);
                case "count": return saves.RecoveryCount.ToString(Inv);
                case "due": return (saves.RecoveryTimer.Due - Time.unscaledTime).ToString("0.0", Inv);
                case "asked": return _recoveryAsked.ToString();
                case "text":
                {
                    var request = FindAnyObjectByType<GameMenu>()?.State.Request;
                    return request?.MessageText != null ? request.MessageText() : "-";
                }
                default: return slots.Any(kv => kv.Key == CrashRecovery.Slot).ToString();
            }
        }

        /// <summary>The current value a condition compares (see Docs/dev-scenarios.md "Conditions").</summary>
        /// <summary>The spell id of a spell, spell item or scroll ("It_Scroll_Sleep" → "Spl_Sleep").</summary>
        /// <summary>fix-154: the message holds the template's literal parts (split at its $(...) placeholders) in order.</summary>
        static bool HoldsTemplate(string message, string template)
        {
            int at = 0;
            foreach (var part in System.Text.RegularExpressions.Regex.Split(template, @"\$\([^)]*\)"))
            {
                if (part.Length == 0) continue;
                int found = message.IndexOf(part, at, StringComparison.Ordinal);
                if (found < 0) return false;
                at = found + part.Length;
            }
            return true;
        }

        string SpellIdOf(string itemOrSpell) => _session.Magic.TryGetSpell(itemOrSpell, out var spell) ? spell.Id : itemOrSpell;

        internal string Evaluate(ScenarioCondition c)
        {
            var s = _session;
            var V0 = s.Combat.Vitals;
            string arg = c.Arg;
            ActorId who = Actor(arg);
            if (DialogAutoAnswer.IsDialogCondition(c.Kind)) AdoptDialog(); // fix-54: the scenario reads this dialog, so it is its own
            switch (c.Kind)
            {
                case "frozen": return (who.IsPlayer ? Combat != null && Combat.Fighter.Frozen : s.Magic.Status.IsFrozen(who)).ToString();
                case "poisoned": return s.Magic.Status.IsPoisoned(who).ToString(); // fix-127
                case "diseased": return s.Magic.Status.Has(who, ActorStatus.Diseased).ToString();
                case "burning": return s.Magic.Status.IsBurning(who).ToString();
                case "poisonleft": return s.Magic.Status.PoisonSecondsLeft(who).ToString("0.0", Inv);
                case "npcdialog": return (s.Dialog.Current != null && ReferenceEquals(s.Dialog.Current, _dlg) && !_dlgOwned).ToString();
                case "npctalks": return _npcTalks.ToString(Inv);
                case "dialogbugs": return _dialogBugs.ToString(Inv);
                case "autodialog": return _autoDialog.ToString();
                case "swimming": return Hero.Swimming.ToString();
                case "water": return Hero.Water.ToString();
                case "grounded": return Hero.IsGrounded.ToString();
                case "heroy": return Hero.transform.position.y.ToString("0.00", Inv); // fix-83: the hero's feet height (m), floor checks
                case "worldcatches": return Hero.WorldCatches.ToString(Inv); // fix-123: falls out of the world caught
                case "ceilingguards": return Hero.CeilingGuards.ToString(Inv); // fix-123: lifts onto a ceiling refused, ceiling tops / geometry left
                case "climbmaxy": case "rainin": case "rainshelter": case "rainsurface": case "indoors": case "rainshadow": return Fix130Probe(c.Kind, arg); // fix-130
                case "navat": return NavAt(arg); // fix-99: NavMesh at x,y,z (within 0.5 m up/down): no NavMesh on cave roofs
                case "npcuse": case "npcprop": case "usingitem": return ItemUseProbe(c.Kind, who); // fix-153
                case "npcfootgap": return NpcFootGap(who, out _).ToString("0.000", Inv); // fix-108: feet over the column's floor (m)
                case "npcslope": NpcFootGap(who, out float slope); return slope.ToString("0.0", Inv); // fix-108: that floor's slope (deg)
                case "navonesided": return CellNavMeshes.OneSidedCeilings.ToString(Inv); // fix-99: back copies dropped from the NavMesh sources
                case "cavesnaps": case "caverooftris": case "cavestand": case "npccave": case "npcy": return CaveProbe(c.Kind, who, arg); // fix-128
                case "sneaking": return Hero.Sneaking.ToString();
                case "sitting": return (Combat != null && Combat.Fighter.SittingKnockedDown).ToString();
                case "seated": return s.Regeneration.Sitting.ToString(); // par-interact: on a bench, stool or throne
                case "eating": return (HeroConsumption.Instance != null && HeroConsumption.Instance.Busy).ToString();
                // fix-103: an object use still runs; the hero's controls are locked (any holder)
                case "using": return (FindAnyObjectByType<HeroObjectUse>() is HeroObjectUse hu && hu.Busy).ToString();
                case "locked": return (_boot != null && _boot.Controls != null && _boot.Controls.IsLocked).ToString();
                // fix-68: the weapon left the hand for an eaten/drunk item; arrows stuck in a body / the level; the
                // normal-mode focus (PlayerInteraction); the hero's block is up
                case "stowed": return (HeroConsumption.Instance != null && HeroConsumption.Instance.HandsStowed).ToString();
                case "arrowhits": return (Projectile.BodyHits.TryGetValue(who, out int ah) ? ah : 0).ToString(Inv);
                case "arrowlevel": return Projectile.LevelHits.ToString(Inv);
                // fix-176: shots that ended as Stick / StickToBone / Vanish / Drop since the domain loaded; the last `loose`
                // shot's end, impact effect (- none) and the effect's visual parts at the touch
                case "arrowrest": return (Enum.TryParse(arg, true, out ProjectileRest pr) && Projectile.Rests.TryGetValue(pr, out int rn) ? rn : 0).ToString(Inv);
                case "looserest": return _looseRest ?? "-";
                case "looseimpact": return _looseImpact ?? "-";
                case "looseparts": return _looseParts.ToString(Inv);
                // fix-79: the focused interactive object (its entity name, - = none), whether it is a decorative one
                // (ObjectFocusRules), and the focus name anchor's height over the target's pivot before the view lift (m)
                case "ifocusobj": return FindAnyObjectByType<PlayerInteraction>() is PlayerInteraction po && po.FocusObject != null &&
                                         s.Interaction.Objects.TryGet(po.FocusObject, out var fo) ? fo.Name : "-";
                case "ifocusdecor": return (FindAnyObjectByType<PlayerInteraction>() is PlayerInteraction pd && pd.FocusObject != null &&
                                            s.Interaction.Objects.TryGet(pd.FocusObject, out var fd) && ObjectFocusRules.IsDecorative(fd)).ToString();
                case "focuslift": return FindAnyObjectByType<G3Hud>() is G3Hud hud && hud.FocusTracker != null && !float.IsNaN(hud.FocusTracker.AnchorLift)
                    ? hud.FocusTracker.AnchorLift.ToString("0.00", Inv) : "-";
                case "focusname": return FocusNameProbe();              // fix-88: the HUD focus name text
                case "focusnameraw": return FocusNameRaw().ToString();  // fix-88: it shows an entity name or an FO_ id
                case "rawfocusnames": return UnresolvedFocusNames();   // fix-88: objects without an FO_ text
                case "cookfires": return CookFireCount();              // fix-88: cooking fires in the content
                // fix-125: the open craft page's recipe list (dimmed rows, counts, whole-row viewport)
                case "synth": case "synthcraftable": case "synthdimmed": case "synthvisible": case "synthfirst":
                case "synthhalfrows": case "synthrow": return SynthProbe(c.Kind, arg);
                case "ifocus": return FindAnyObjectByType<PlayerInteraction>() is PlayerInteraction pi && !pi.Focus.IsEmpty ? pi.Focus.ToString() : "-";
                case "parade": return (Combat != null && Combat.Fighter.Animator.HoldsParade).ToString();
                // par-misc: smoking / sawing / drumming; the hero camera's G3 mode and distance; weapon-in-level sounds
                case "looping": return (FindAnyObjectByType<HeroObjectUse>() is HeroObjectUse u && u.InActivity).ToString();
                case "cammode": return FindAnyObjectByType<G3PlayerCamera>() is G3PlayerCamera cm ? cm.mode.ToString() : "-";
                case "camzoom": return FindAnyObjectByType<G3PlayerCamera>() is G3PlayerCamera cz ? cz.distance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-";
                // fix-73: the turn keys' camera orbit (|camera yaw − hero yaw|, degrees) and the hero's turn in the last 'turnkey'
                case "camrel": return FindAnyObjectByType<G3PlayerCamera>() is G3PlayerCamera cr ? Mathf.Abs(Mathf.DeltaAngle(Hero.Yaw, cr.transform.eulerAngles.y)).ToString("0.0", Inv) : "-";
                case "keyturnhero": return TurnKeyHeroTurn.ToString("0.0", Inv);
                // fix-94: camera distance from the hero's eyes / boom length now; how the hero is drawn; the last 'slopeback'
                case "cameye": return CamEyeProbe();
                case "camdist": return CamDistProbe();
                // fix-122: the camera against the last 'cammark' (dist, fov, zoom, pos, rot); whether it is held for a page / use
                case "camchange": return CamChange(arg);
                case "camheld": return (FindAnyObjectByType<G3PlayerCamera>() is G3PlayerCamera ch && ch.HoldingPlayCamera).ToString();
                case "heroview": return HeroViewProbe();
                case "slopemineye": return SlopeProbe("eye");
                case "slopehidden": return SlopeProbe("hidden");
                case "slopefp": return SlopeProbe("fp");
                case "rawloottitles": return RawLootTitles(); // fix-94: loot titles that would show a raw FO_ id
                // fix-76: a metric of the last 'camprobe' (step, jump, yjerk, rotjump, pelvisdip, phasejump, drift, yawdrift)
                case "camjitter": return CamJitter(arg);
                // fix-89: a metric of the last 'torsoprobe' (shrink m, bend °, runmin, runmax, runbend, overlaymin, overlaybend, runframes, overlayframes)
                case "torso": return Torso(arg);
                case "dlgengaged": return s.Dialog.IsEngaged.ToString();
                // fix-84: dialog camera vs level (m beyond the free view; height over the heads), teach shortfalls
                case "dlgcamblock": return FindAnyObjectByType<DialogPresentation>() is DialogPresentation dpb && dpb.Camera != null ? dpb.Camera.Obstruction.ToString("0.00", Inv) : "0";
                case "dlgcamheight": return FindAnyObjectByType<DialogPresentation>() is DialogPresentation dph && dph.Camera != null ? dph.Camera.HeightAboveHeads.ToString("0.00", Inv) : "0";
                case "teachshort": return string.Join(",", s.Items.Teaching.Shortfalls(new Lesson(null, 0, arg)).Select(x => x.ToString())) is string ts && ts.Length > 0 ? ts : "none";
                case "npcruns": return s.Npcs.TryGet(who, out var ra) ? s.Npcs.Runs(ra).ToString() : "-";
                // fix-85: fight watchers now: spectators of an arena fight / ZS_WatchFight_Loop watchers / watchers inside zone <arg>
                case "watchspec": return FightWatchers(w => w.Spectator).Count().ToString(Inv);
                case "watchloop": return FightWatchers(w => !w.Spectator).Count().ToString(Inv);
                case "watchinring": return FightWatchers(_ => true).Count(id => s.WorldState?.Areas != null &&
                                                                              s.WorldState.Areas.TryIsInside(id, arg, out bool inRing) && inRing).ToString(Inv);
                case "levelhits": return CombatSounds.LevelTouches.ToString();
                case "objpart": return FindAnyObjectByType<HeroObjectUse>() is HeroObjectUse ou ? ou.ObjectPart : "-"; // fix-61
                case "sfx": return G3MotionSounds.PlayedCount(arg).ToString(Inv); // fix-61: motion-track sounds of one effect
                case "mined": return TryObject(arg, out var vein) && s.Interaction.Uses.WasMined(vein.Id) ? "True" : "False";
                case "mineverdict": return TryObject(arg, out var mineAt) ? s.Interaction.Uses.CheckMining(mineAt.Id).ToString() : "-"; // fix-139
                // fix-111: the nearest treasure spot of that name was dug up (plunder generated) / what a dig there would do
                // now (Ok, MissingTool, NothingToGet) / the stacks left in it
                case "dug": return TryObject(arg, out var spot) && s.Interaction.Uses.WasDug(spot.Id) ? "True" : "False";
                case "digverdict": return TryObject(arg, out var digAt) ? s.Interaction.Uses.CheckDig(digAt.Id).ToString() : "-";
                case "lastwork": return FindAnyObjectByType<HeroObjectUse>() is HeroObjectUse wu ? wu.LastWork : "-";
                case "spotloot": return TryObject(arg, out var lootAt)
                    ? s.Items.Inventories.Of(InteractiveObjects.ContainerOf(lootAt.Id)).Stacks.Count.ToString(Inv) : "-";
                case "objusers": return TryObject(arg, out var usedObj) ? ObjectUsers(usedObj.Id) : "-"; // fix-66
                case "userefusal": // fix-66: why the hero's use of the nearest such object would be refused, "ok" if not
                    return TryObject(arg, out var refObj) && FindAnyObjectByType<HeroObjectUse>() is HeroObjectUse ru ? ru.UseRefusal(refObj.Id) ?? "ok" : "-";
                case "combo": return Combat != null ? Combat.ComboStage.ToString() : "0";
                case "lastmove": return Combat != null ? Combat.LastStartedMove.ToString() : "-";
                case "dual": return (Combat != null && Combat.Fighter.DualWielding).ToString();
                case "offhand": return s.Items.Equipment.In(WornSlot.OffHand) ?? "-";
                case "stance": return Hero.GetComponentInChildren<G3HeroAnimator>() is G3HeroAnimator ha ? ha.stance : "-";
                // fix-42: gait, facing state and the draw/sheathe hand
                case "movemode": return Hero.MoveMode.ToString();
                case "movestate": return Combat != null ? Combat.MoveState.ToString() : "-";
                case "facing": return Mathf.Abs(Mathf.DeltaAngle(Hero.transform.eulerAngles.y, Hero.Yaw)).ToString("0.0", Inv);
                case "inhand":
                {
                    // the weapon really is in the hand (the draw has put it there); inhand:X for an NPC (fix-164)
                    var f = string.IsNullOrEmpty(arg) || who.IsPlayer ? (Combat != null ? Combat.Fighter : null) : FighterOf(who);
                    return (f != null && f.Animator != null && f.Animator.WeaponInHand).ToString();
                }
                case "holdsbody": return (Combat != null && Combat.Fighter.Animator.HoldsBody).ToString();
                // fix-101: the game-over overlay (GO_GameOver "Конец"), the hero lying in the dead pose, the hand field in use
                case "gameover": return (Combat != null && Combat.GameOverShown).ToString();
                case "liesdead": return (Combat != null && Combat.Fighter.Animator.LiesDead).ToString();
                case "hand": return Combat != null && Combat.Fighter.Animator.WeaponInHand ? Combat.Fighter.Animator.Weapon : "-";
                case "routine":
                {
                    var presenter = FindAnyObjectByType<CombatPresenter>();
                    var f = FighterOf(who);
                    return presenter != null && f != null ? presenter.InRoutine(f).ToString() : "-";
                }
                case "unaware":
                {
                    var presenter = FindAnyObjectByType<CombatPresenter>();
                    var f = FighterOf(who);
                    return presenter != null && f != null ? presenter.Unaware(f, Combat.Fighter).ToString() : "-";
                }
                case "dialog": return s.Dialog.IsActive.ToString();
                case "partner": return s.Dialog.Current != null ? s.Dialog.Current.Partner.ToString() : "-";
                case "entry": return s.Dialog.Current?.Entry != null ? s.Dialog.Current.Entry.Id.ToString() : "-";
                case "options": return s.Dialog.Current != null ? s.Dialog.Current.Options.Count.ToString() : "0";
                // fix-118: the open menu's option X: its menu text (title + price) / its price "<learning points>/<gold>"
                case "label":
                case "cost":
                {
                    var open = s.Dialog.Current;
                    if (open == null) return "-";
                    foreach (var o in open.Options)
                        if (string.Equals(o.Entry.ToString(), arg, StringComparison.OrdinalIgnoreCase))
                            return c.Kind == "label" ? OptionLabel(s.Localizer, o) : o.Cost.LearningPoints.ToString(Inv) + "/" + o.Cost.Gold.ToString(Inv);
                    return "-";
                }
                case "important": return s.Dialog.HasImportantInfo(who).ToString();
                case "hour": return s.Clock.HourOfDay.ToString("0.00", Inv);
                case "state": return V0.StateOf(who).ToString();
                case "hp": return V0.HitPoints(who).ToString(Inv);
                case "hpmax": return V0.HitPointsMax(who).ToString(Inv); // fix-132: the god never touches the maximum
                case "touches": case "hurtby": return TouchValue(c.Kind, who); // fix-59 touchwatch
                case "fighting": { var b = s.NpcCombat.Get(who); return (b != null && b.IsFighting).ToString(); }
                case "phase": return s.NpcCombat.Get(who)?.State.Phase.ToString() ?? "None";
                case "task": return s.Life.Tasks.KindOf(who).ToString();
                // fix-78: perception lines X said to the hero (warnings, observing calls); X's warning step (ZS_ReactToZone/Weapon)
                case "calls": return NpcReactionView.CallsOf(who).ToString(Inv);
                case "warning": return s.Crime.Crime.WarningOf(who).ToString();
                case "target": return s.NpcCombat.Get(who)?.State.Target.ToString() ?? "-";
                case "attitude": return s.Crime.Attitudes.Of(who, ActorId.Player).ToString();
                // fix-94: X is a guard of its enclave (G3 IsGuard) / loyal to it; its attitude to the orc / rebel side
                case "isguard": return (s.Crime.Attitudes.Facts.TryGetProfile(who, out var gp) && gp.Guard).ToString();
                case "loyal": return (s.Crime.Attitudes.Facts.TryGetProfile(who, out var lp) && s.Crime.Attitudes.LoyalToEnclave(lp)).ToString();
                case "politics": return PoliticsProbe(who);
                // fix-129: X's attitude toward Y ("attitudeto:Daryl,Montera_Orc_03=Hostile")
                case "attitudeto":
                {
                    var pair = (arg ?? "").Split(',');
                    return pair.Length == 2 ? s.Crime.Attitudes.Of(Actor(pair[0].Trim()), Actor(pair[1].Trim())).ToString() : "-";
                }
                case "activity": return s.Npcs.TryGet(who, out var ag) ? ag.Activity.ToString() : "-";
                case "arena": return s.Npcs.InArena(who)?.ToString() ?? "-";
                case "summons": return s.Summons.MembersOf(who).Count(m => V0.StateOf(m) == VitalState.Alive).ToString(Inv);
                case "party": return s.Party.Members.TypeOf(who).ToString();
                case "waiting": return s.Party.Members.IsWaiting(who).ToString(); // fix-100: told to wait (still led)
                case "enclave": return s.Enclaves.Of(arg).Status.ToString();
                case "quest": return s.Quests.StatusOf(new Id<QuestDef>(arg)).ToString();
                case "flag": return s.Flags.IsSet(arg).ToString();
                case "sector": return s.WorldState.Sectors.IsEnabled(arg).ToString();
                case "has": return s.Items.Inventories.Of(ActorId.Player).Count(arg).ToString(Inv);
                // scenarios-3: quest rewards (the hero's total XP; political / enclave fame of a group)
                case "xp": return s.Hero.Xp.ToString(Inv);
                // fix-77: hero attributes (value; the maximum of a vital), learning points, the HUD's recent messages and
                // whether one of them announced a raise of an attribute ("<long name> + n = value", PrintAddValue)
                case "attr": return (s.Hero.Stats.HasMaximum(arg) ? s.Hero.Stats.Maximum(arg) : s.Hero.Stats.Value(arg)).ToString(Inv);
                case "lp": return s.Hero.LearningPoints.ToString(Inv);
                case "hudmsg": return string.Join(" | ", HudMessages.Recent);
                // fix-111: a recent HUD message holds the localized text of that string id (any language), e.g. FO_It_Shovel
                case "hudmsgtext":
                {
                    // fix-154: a template ("Добыто: $(value)х $(name).", GO_Mining) matches a message holding its literal parts in order
                    string text = s.Localizer.Text(new TextId(arg));
                    return (!string.IsNullOrEmpty(text) && HudMessages.Recent.Any(m => HoldsTemplate(m, text))).ToString();
                }
                case "raisemsg":
                {
                    string name = s.Content.Database.TryGet(arg, out AttributeDef ad) ? s.Localizer.Text(ad.Name) : arg;
                    return HudMessages.Recent.Any(m => m.StartsWith(name + " + ", StringComparison.Ordinal)).ToString();
                }
                case "inbeddialog": return (FindAnyObjectByType<HeroObjectUse>() is HeroObjectUse bu && bu.InBedDialog).ToString();
                case "fame": return s.Fame.Of(FameKind.Political, arg).ToString(Inv);
                case "efame": return s.Fame.Of(FameKind.Enclave, arg).ToString(Inv);
                // par-magic: the spell book (HeroSpells), the spell hand and the staff block
                case "spellfx": return SpellDeliveryLog.TryGet(SpellIdOf(arg), out var fx) ? fx.Applied.ToString() : "-";
                case "spellroute": return SpellDeliveryLog.TryGet(SpellIdOf(arg), out var fr) ? fr.Route.ToString() : "-";
                case "spellvis": return SpellDeliveryLog.TryGet(SpellIdOf(arg), out var fv) ? fv.Visual.ToString() : "-";
                case "asleep": return s.SpellEffects.World.IsAsleep(who).ToString();
                // fix-113: X can be talked to now / robbing X is a theft / X's lootable stacks / X's personal grudge
                case "talkable": return s.Dialog.CanTalkTo(who).ToString();
                // fix-158: the last refusal line of an NPC with nothing to talk about (G3 StartSayRefuseTalk); "-" = none yet
                case "refuseline": return s.TalkRefusals.LastLine ?? "-";
                // fix-158: the entries a talk with X would offer now, comma-separated
                case "topics": return string.Join(",", s.Dialog.TopicsFor(who).Select(e => e.Id.Name));
                case "robtheft": return s.SleeperTheft.IsTheftVictim(who).ToString();
                case "lootstacks": return s.Items.NpcLoot.Pool(who).Count.ToString(Inv);
                case "grudge": return s.Crime.Ledger.PersonalOf(who).ToString();
                case "misted": return s.SpellEffects.World.InMist(who).ToString();
                case "mist": return (s.SpellEffects.World.Mist != null).ToString();
                case "timebubble": return (s.SpellEffects.World.TimeBubble != null).ToString();
                case "heroform": return s.SpellEffects.Hero.Kind.ToString();
                case "learned": return s.HeroSpells.IsLearned(s.HeroSpells.SpellOf(arg) ?? arg).ToString();
                case "castable": return s.HeroSpells.CanReady(s.HeroSpells.ItemOf(arg) ?? arg).ToString();
                case "readied": return Combat != null && Combat.Fighter.SpellDrawn ? Combat.ReadiedSpell ?? "-" : "-";
                case "quickslot": return s.Items.QuickSlots.SlotOf(s.HeroSpells.ItemOf(arg) ?? arg).ToString(Inv);
                case "staffleft": return (Combat != null && Combat.StaffInLeftHand).ToString();
                case "staffblock": return (Combat != null && Combat.StaffBlocking).ToString();
                case "page": return FindAnyObjectByType<GameScreens>()?.Current?.GetType().Name ?? "-";
                // fix-87: the cursor mode of CursorPolicy (gameplay | hidden | pointer), the modes seen since cursorwatch,
                // and whether the pointer is drawn / the cursor locked right now
                case "cursor": return Myrtana.Ui.Skin.CursorPolicy.Name(CursorDirector.Mode);
                case "cursorseen": return CursorDirector.Seen();
                case "cursorvisible": return Cursor.visible.ToString();
                case "cursorlocked": return (Cursor.lockState == CursorLockMode.Locked).ToString();
                // fix-82: the save/load menu rows (slot of row N, 0-based; loadname: the row's shown name), the save
                // blocker, the autosave timer and whether a slot exists
                case "loadrow": case "saverow": case "loadname": case "loaddetail": return SaveRow(c.Kind, arg);
                case "loadrows": return Saves != null ? SaveGameList.For(false, Saves.Slots(), s.Localizer).Count.ToString(Inv) : "-";
                // fix-107: the Load page's list (rows visible at once, selected row, first visible row)
                case "fileslist": return FilesListProbe(arg);
                case "continue": return ContinueProbe(arg);   // fix-138
                case "recovery": return RecoveryProbe(arg);   // fix-155
                case "saveblock": return s.SaveBlockReason ?? "none";
                case "autosavedue": return Saves != null ? (Saves.AutosaveTimer.Due - Time.unscaledTime).ToString("0.0", Inv) : "-";
                case "slot": return (Saves != null && Saves.Slots().Any(kv => kv.Key == arg)).ToString();
                case "autosaved": return _lastAutosave.ToString();
                case "wrote": return _lastWrote;                   // file of the last 'autosave' op (quick=1: quicksave)
                case "quicklatest": return Saves?.QuickLoadSlot ?? "-"; // the file F9 loads (fix-107: always quick)
                // fix-65: the player is in battle (no object use, no saving: NpcCombatSystem.IsPlayerInCombat)
                case "incombat": return s.NpcCombat.IsPlayerInCombat.ToString();
                case "focus": return Combat.Focus != null ? Combat.Focus.Actor.ToString() : "-";
                case "drawn":
                {
                    var f = who.IsPlayer ? Combat.Fighter : FighterOf(who);
                    return (f != null && f.Animator != null && f.Animator.Drawn).ToString();
                }
                case "action":
                {
                    var f = who.IsPlayer ? Combat.Fighter : FighterOf(who);
                    return f?.Animator?.Action ?? "-";
                }
                case "dist":
                    return TryActorPosition(who, out var p, out _) ? (p - Hero.transform.position).magnitude.ToString("0.00", Inv) : "-";
                case "key":
                    return TryActorPosition(who, out _, out var v) && v != null ? KeyOf(v) : "-";
                case "wading":
                {
                    if (who.IsPlayer) return (Hero.Water == WaterMode.Wade).ToString();
                    if (!TryActorPosition(who, out _, out var view) || view == null) return "-";
                    var an = view.GetComponentInChildren<G3NpcAnimator>();
                    return (an != null && an.Wading).ToString();
                }
                case "yaw": return TryActorPosition(who, out _, out var yv) ? ActorYaw(who, yv).ToString("0", Inv) : "-";
                case "turned":
                {
                    // degrees the actor turned since its last probe
                    if (!_probeYaw.TryGetValue(who, out float before) || !TryActorPosition(who, out _, out var tv)) return "-";
                    return Mathf.Abs(Mathf.DeltaAngle(before, ActorYaw(who, tv))).ToString("0", Inv);
                }
                case "abovewater":
                {
                    if (!TryActorPosition(who, out var p2, out _)) return "-";
                    return G3Water.TrySurface(p2 + Vector3.down * 20f, out float sy, 40f) ? (p2.y - sy).ToString("0.00", Inv) : "-";
                }
                case var k86 when IsFix86Probe(k86): return Fix86Probe(k86, who); // bodies stop the hero (fix-86)
                case var k142 when IsFix142Probe(k142): return Fix142Probe(k142, who); // combat chase way / retreat (fix-142)
                case var k149 when IsFix149Probe(k149): return Fix149Probe(k149, who); // weapons on a body / lying (fix-149)
                case var k93 when IsFix93Probe(k93): return Fix93Probe(k93, who, arg); // evasion, item skills, hurt mark (fix-93)
                case var k80 when IsFix80Probe(k80): return Fix80Probe(k80, who); // corpses at rest, signposts (fix-80)
                case var k102 when IsFix102Probe(k102): return Fix102Probe(k102); // locked chests on the map (fix-102)
                case var k126 when IsFix126Probe(k126): return Fix126Probe(k126, arg); // map filter bar, names, quest talks (fix-126)
                case var k104 when IsFix104Probe(k104): return Fix104Probe(k104); // focus line of sight (fix-104)
                case var k105 when IsFix105Probe(k105): return Fix105Probe(k105); // the torch in the hand (fix-105)
                default: return TryStoryCondition(c, out var story) ? story : $"unknown condition '{c.Kind}'";
            }
        }

        /// <summary>fix-85: the NPCs whose view watches a fight now, by the watch's kind.</summary>
        IEnumerable<ActorId> FightWatchers(Func<FightWatcher, bool> kind) =>
            Views == null ? Enumerable.Empty<ActorId>()
                : Views.Views.Where(kv => kv.Value != null && !kv.Key.IsPlayer && kv.Value.TryGetComponent<NpcView>(out var nv) &&
                                          nv.WatchedFight != null && kind(nv.WatchedFight)).Select(kv => kv.Key).ToList();
    }
}
#endif
