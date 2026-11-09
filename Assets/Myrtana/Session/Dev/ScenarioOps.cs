#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Linq;

namespace Myrtana.Session.Dev
{
    /// <summary>The argument shape of one scenario op: positional names (in order) and the other accepted names.</summary>
    public sealed class OpSpec
    {
        public OpSpec(string name, string positional, string named, string required, string help)
        {
            Name = name;
            Positional = Split(positional);
            Named = Split(named);
            Required = Split(required);
            Help = help;
        }

        public string Name { get; }
        public IReadOnlyList<string> Positional { get; }
        public IReadOnlyList<string> Named { get; }
        public IReadOnlyList<string> Required { get; }
        public string Help { get; }

        public bool Accepts(string arg) =>
            Positional.Contains(arg, StringComparer.OrdinalIgnoreCase) || Named.Contains(arg, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(arg, Unless, StringComparison.OrdinalIgnoreCase) || string.Equals(arg, Answer, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Every op takes <c>unless=cond</c> (a scenario condition, as <c>expect</c>): the step is skipped, logged, when the
        /// condition holds as it starts (a kill quest already done on the way: the defends of an earlier escort killed its targets).
        /// </summary>
        public const string Unless = "unless";

        /// <summary>
        /// Every op takes <c>answer=e1,e2</c> (fix-54): dialog entries an NPC-initiated talk during this step should take
        /// (<see cref="DialogAutoAnswer"/>), before the scenario's own "answers".
        /// </summary>
        public const string Answer = "answer";

        public string Usage =>
            Name + string.Concat(Positional.Select(p => Required.Contains(p) ? $" <{p}>" : $" [{p}]")) +
            string.Concat(Named.Select(n => $" {n}=")) + "  — " + Help;

        static string[] Split(string s) => string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Catalogue of scenario ops (Docs/dev-scenarios.md). Pure: binds positional arguments to names and validates; the
    /// Unity side (Myrtana.Unity.Dev.ScenarioRunner) executes them. Actors are ActorIds ("Gorn", "Bloodfly#01ea064d"),
    /// "hero", or "$var"; targets are "npc:Id", "spot:id[.point]", "x,y,z", "x,z" or a bare actor id.
    /// </summary>
    public static class ScenarioOps
    {
        static readonly Dictionary<string, OpSpec> _ops = new Dictionary<string, OpSpec>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["teleport"] = "tp", ["goto"] = "tp", ["say"] = "log", ["screenshot"] = "shot", ["dump"] = "probe",
            ["knockout"] = "ko", ["sleep"] = "wait", ["passive"] = "calm", ["remove"] = "despawn", ["check"] = "expect",
            ["heal"] = "restore", ["perk"] = "learn", ["run"] = "scenario", ["fly"] = "noclip", ["threats"] = "enemies",
        };

        static ScenarioOps()
        {
            void Op(string name, string positional, string named, string required, string help) =>
                _ops[name] = new OpSpec(name, positional, named, required, help);

            // world / hero placement
            Op("safe", "fame", "alarm", "", "Orc political fame (default 80), end all fights and forget their grudges, crimes short of murder and running warnings (fix-65), hide tutorial tips, timeScale 1; alarm=1 also puts enclaves in Attack back to Routine");
            Op("god", "on", "", "", "hero takes no damage (a damage filter, HP untouched; off: ends it and a tp's short immunity)");
            Op("noclip", "on", "speed", "", "fix-131: toggle noclip (on|off; alias fly): the hero flies through geometry, no collision, gravity, ground check, world catch or fall damage; W/S along the look, A/D strafe, Space up, Ctrl down, Shift x5; speed= m/s (default 10); off drops him onto the floor below on the same side (never inside geometry)");
            Op("fps", "on", "", "", "fix-172: the FPS overlay (top-right: FPS of 0.5 s, frame ms avg / 1% low / max of 5 s, CPU main thread and GPU ms from FrameTimingManager, the remaster parts on) on|off (empty toggles); Ctrl+F hides / shows it while this or the port option \"Show FPS\" is on; not stored");
            Op("perfreport", "secs", "delay wait", "", "fix-172: record every frame for secs s (default 30, after delay s, default 1, to close the console), then write avg / p50 / p95 / p99 / max frame ms, hitches > 50 ms with timestamps, CPU / GPU ms, remaster parts, scene / cell / position and quality settings (view distance, URP, menu options) to Player.log ([PerfReport]) and persistentDataPath/perf_<date>.txt (LocalLow/G3World/G3World); wait=1 holds the scenario until written");
            Op("perfhere", "rounds", "warmup wait", "", "fix-171: the remaster A/B where the hero stands (G3PerfHere): after warmup s (default 5, to close the console) with the game running, VSync off, every lighting feature, every other lighting quality level, Modern lighting, the Lighting part and the whole remaster switched off and on in turn (~4 min per round, default 1); table to Player.log and persistentDataPath/perf_here.txt (LocalLow/G3World/G3World); prefs untouched; wait=1 holds the scenario until done");
            Op("tp", "target", "spot npc pos dist side yaw face swim zone", "", "teleport: waits for streaming, snaps to flat ground (spot/npc/pos=x,y,z|x,z, place:Id, zone:Name, obj:Name); swim=1 keeps y at the water; zone=Name: beside an npc only inside that AI zone; obj:Name dist= side=front|back|left|right (fix-103)");
            Op("time", "hour", "", "hour", "skip the session clock forward to this hour (0-24)");
            Op("weather", "state", "", "", "force weather: Sunny|Rainy|Foggy|Snowy (empty = schedule)");
            Op("notips", "", "", "", "hide tutorial tips (they pause the game on first draw/spell)");
            // NPCs
            Op("spawn", "template", "near dist attitude as count side", "template", "new NPC from a template id/name/species near the hero (attitude hostile|neutral|friendly); $as = its id (count>1: also $as1, $as2 ..., fix-188)");
            Op("bring", "npc", "dist near as side", "npc", "move an existing NPC next to the hero (or near=target); its routine restarts there");
            Op("calm", "radius", "except only", "", "NPCs within radius (default 40 m; only=a,b) neither start nor return fights; ends their fights");
            Op("uncalm", "", "", "", "undo calm");
            Op("freeze", "radius", "except only", "", "calm + stop movement (FullStop every second) for NPCs within radius");
            Op("unfreeze", "", "", "", "undo freeze");
            Op("despawn", "target", "radius except", "", "remove an NPC (or every NPC within radius) from the simulation");
            Op("engage", "npc target", "reason", "npc", "NPC fights target (default hero), reason=Enemy|Murder|Duel|...");
            Op("disengage", "npc", "", "", "end the fights of one NPC (default all)");
            Op("party", "npc mode", "type", "npc", "mode add|remove|wait; type Party|Slave|Mob");
            Op("touchwatch", "on", "victim", "", "fix-59: count every weapon/missile/spell touch on the victim (default hero) per attacker with its gate and damage (conditions touches:X, hurtby:X); off logs the tally");
            // vitals
            Op("hp", "actor value", "max", "actor", "set HP (actor hero or npc); value default 3");
            Op("ko", "actor", "", "actor", "knock out (no hit, so no weapon drop)");
            Op("kill", "actor killer", "aim", "actor", "kill; killer=hero counts as the hero's party (default none); aim=s (hero, fix-101): hold the left button that long with the bow drawn first (killed while drawing it)");
            Op("restore", "actor", "", "", "refill HP/stamina/mana (default hero)");
            Op("joinnpc", "npc leader", "", "npc leader", "par-npc2: CP JoinNPC — npc follows the NPC leader (ZS_FollowNPC) until the leader reached its destination and waited 5 s");
            Op("npctask", "npc", "", "npc", "par-npc2: log an NPC's routine task: activity, station + interaction, work task/step, listening, sermon line, NPC leader, fight watch");
            Op("corpse", "npc hours", "", "npc", "par-npc: a dead NPC died hours ago (default 121) and enters the processing range now: CP Respawn keeps, removes or revives it (logs the verdict)");
            Op("sinkbody", "npc", "depth", "npc", "fix-163: push the NPC's body depth m (default 0.6) into the ground: a corpse is lifted by its rest check, a station NPC put back on its floor");
            Op("signpost", "name", "dist side", "name", "fix-80: stand dist m (default 1.8) in front of (side=back: behind) the nearest signpost board of that name (Sign_Faring …) and face it");
            Op("fight", "npc", "hp secs hold sheathe style kill", "npc", "hero melees the NPC (HP lowered to hp, default 5) until Down/Dead or secs (kill=0: a survivor is no failure, fix-184); focus pinned on it, closes in (stuck: put 1.6 m from it), ends when it stays > 2 m above/below for 3 s; a protected bystander (neutral, fighting someone else) is turned on the hero first; sheathes after (sheathe=0 keeps it drawn); style=melee|fists|bow|crossbow|spell (default: the loadout's; fix-54) shoots from 6-20 m or casts the loadout spell");
            Op("finisher", "npc", "hold tries", "npc", "hero finishes a downed NPC: stands 1.3 m off, block + primary held (hold s, default 1.6)");
            // items / progression / story
            Op("spell", "spell", "learn", "spell", "give a spell item (id or name part), learn what it needs (learn=0: not its spell), ready it in the hand");
            Op("give", "item n", "", "item", "give items to the hero (id, id part or localized name part)");
            Op("equip", "item", "", "item", "give if missing and equip (id or name part)");
            Op("unequip", "item", "", "item", "fix-97: take a worn item off (id or name part; every slot it is worn in), it stays carried");
            Op("xpmark", "", "", "", "fix-97: remember the hero's XP now; the condition xpgain is the XP since (the hero starts with the CP start XP, 2500)");
            Op("quick", "item", "", "item", "press a quick-slot key with this weapon (give if missing): G3 OnPlayerUseQuickSlot readies it; with Perk_1H1H_1 and a one-handed main weapon a second one-handed weapon goes into the left hand (dual wielding; its own key again puts the left blade away; fix-32)");
            Op("docs", "kind", "open", "", "give one of every readable item of a kind the hero lacks (all|maps|books|letters|recipes|alchemy|cooking|smithing; default all); open=maps|books|alchemy|cooking|smithing then opens the documents page on that tab (fix-33)");
            Op("draw", "kind", "", "", "draw melee (kind melee|ranged|fist; default: the loadout's kind, else melee; melee without a weapon equips It_1H_Sword_01, fist unequips it; fix-54: waits out a freeze on the hero, up to 60 s)");
            Op("sheathe", "", "", "", "put the weapon away");
            Op("learn", "skills", "", "skills", "comma list of skills/perks/spell skills");
            Op("attr", "name value", "atleast", "name", "set a stat (STR, DEX, INT, MP, ...) value and maximum; atleast=1 only raises it");
            Op("level", "n", "", "n", "award XP until the hero has this level");
            Op("xp", "n", "", "n", "award this much XP (with level-ups), e.g. a quest reward the hero missed (fix-147)");
            Op("quest", "id status", "", "id", "set quest status (Running default); id may be a comma list");
            Op("flag", "name on", "", "name", "set (on) or clear a story flag; name may be a comma list");
            Op("sector", "name on", "", "name", "enable/disable a story sector");
            Op("fame", "group value", "kind", "group", "add fame (kind Political|Enclave)");
            Op("enclave", "id status", "", "id", "Routine|Attack|Flee|liberate");
            Op("torch", "on", "", "", "take (on) or release the hero torch (gives It_Torch)");
            Op("lang", "voice text", "", "", "voice/text language, e.g. English, Russian");
            Op("post", "on", "", "", "camera post-processing on (default) or off, for A/B shots (fix-50; -g3nopost starts with it off)");
            // dialog
            Op("talk", "npc", "", "npc", "start a dialog (npc or Speaker object name)");
            Op("choose", "entry", "", "entry", "choose an option by entry id or index");
            Op("lines", "max", "", "", "complete spoken lines until a choice or the end");
            Op("abort", "", "", "", "abort the dialog");
            Op("forget", "entry", "", "entry", "forget a given dialog entry (replay an Important info)");
            Op("ask", "npc entry", "end until given", "npc entry", "play one dialog entry through the real dialog: talk, complete lines, choose its parent chain and the entry, complete lines, leave (end=0 keeps the dialog open; until=cond aborts the talk as soon as cond holds; given=1 accepts an entry that already ran); fails when it was not played or was refused");
            Op("played", "entries", "", "entries", "setup: mark dialog entries (comma list) as already played and their owners as talked to, as an earlier chapter would have");
            Op("talked", "npcs", "", "npcs", "setup: mark NPCs (comma list) as having talked to the hero (talkedToPlayer conditions)");
            Op("topics", "npc", "", "npc", "log the dialog entries of an NPC/Speaker that are available now");
            // story
            Op("pickup", "item", "radius", "item", "pick up the nearest world item of that id (or id part) within radius m (default 15) as the hero's E would");
            Op("raid", "enclave", "max", "enclave", "the hero defeats an enclave: alarm (Attack), then its loyal defenders are killed by the hero one by one until it gives up (Flee = liberated) or max kills");
            Op("escort", "npc", "to near secs walk route rejoin", "npc to", "the hero travels with the NPC to the target (place:/zone:/obj:/npc:/x,z): keeps behind a guide, leads a follower along the NavMesh path (route=0: NavMesh only, no G3 route legs; fix-190); a guide whose FollowNpc quest got Lost is asked rejoin=entry (at most 12 times) or the step fails; fails on timeout with the NPC's distance; releases a frozen NPC (the dry run fails on a frozen NPC or one that neither guides nor follows, then moves both there)");
            Op("navislands", "radius", "to links max", "", "NavMesh islands per cell among routine points/NPCs within radius (default 150) and to=targets (guide routes); links=1 (default) adds drop links first; fails above max (default 4) islands in a cell");
            Op("use", "target", "free", "target", "use an interactive object by name (nearest) as the hero's E would and wait until done (item:Id uses up an inventory item; a station returns once its action loop runs with its page open, fix-61; free=1: NPCs holding the object are removed first, fix-188); the dry run reads bookstands only");
            Op("standup", "", "", "", "the hero gets up from a seat (as a movement key would; par-interact) and waits until he stands");
            Op("closepage", "", "", "", "close the open page (a station's craft/sharpen page) and wait until the hero's use has ended (fix-61)");
            Op("takeall", "", "", "", "fix-111: press the open loot page's Take all (it closes the page) and wait until the hero's use has ended (a chest, a dug-up treasure spot)");
            Op("eat", "item", "", "item", "eat or drink an inventory item with the motion (HeroConsumption: stop, use motion, then the effect and its messages) and wait until done; fix-68: with a weapon drawn it fails unless the weapon left the hand during the use and is back in the hand after");
            Op("rob", "npc", "", "npc", "Use on a helpless (sleeping or spell-slept) human as the sneaking hero with Perk_MasterThief: its loot page opens (take all, close)");
            Op("usenpc", "npc", "", "npc", "fix-113: the Use key on a character as PlayerInteraction routes it (loot a body, rob a sleeper, else talk); logs dialog/page and leaves them open");
            Op("loot", "target", "", "target", "take everything through the real loot path: a downed/dead NPC (body) or a chest by object name (nearest; unlocks with the key or a lock pick roll)");
            // inputs / camera
            Op("focus", "npc", "", "npc", "keep the hero facing + combat focus on the NPC until 'focus off'");
            Op("look", "target", "", "target", "turn the hero to face a target");
            Op("aimat", "target", "secs", "target", "fix-90: put the crosshair (screen centre) on the target's body centre for secs (default 0.5) as a player's mouse does (G3PlayerCamera.LookFor); logs the combat focus");
            Op("loose", "target", "ammo at draw wait", "target", "fix-176: one full-draw shot of the equipped (ammo=Id: equipped first) arrow or bolt with the crosshair held over the target (actor body centre; at=ground: the ground 2 m short of it; or x,y,z / x,z on the ground); waits up to wait= s (4) for its touch; logs its end Stick/StickToBone/Vanish/Drop, the impact effect and its parts (probes looserest, looseimpact, looseparts)");
            Op("turnkeys", "orbit", "", "", "fix-73: the port option for the Camera rotation keys (G3 Q / E) for this scenario: orbit=1 (default) orbits the camera round the hero, orbit=0 turns him (original); the stored option comes back at the end");
            Op("decorfocus", "on", "", "", "fix-79: the port option \"Focus decorative objects\" for this scenario: on=1 (default, original) focuses every object, on=0 skips seats and the looping activities (ObjectFocusRules); the stored option comes back at the end");
            Op("cookfire", "on", "", "", "fix-88: the [owner] port option \"Cook on any fire\" for this scenario: on=1 (default) makes lit fireplaces, braziers and fire basins cooking stations (CookFires), on=0 is the original (decoration); the stored option comes back at the end");
            Op("chestfilter", "on", "", "", "fix-116: the map page's chest filter for this scenario (the [owner] marks of locked chests the hero found but could not open, LockedChestMarks; default hidden): on=1 (default) shows them, on=0 hides them; with a map view open it clicks the filter bar's chest button (fix-126); the stored filter comes back at the end");
            Op("windowcam", "mode", "", "", "fix-122: the [owner] port option \"Camera in windows\" for this scenario: still (default) keeps the play camera for pages and object uses (no zoom, FOV or pivot change, no blend), original is G3's CamModeInventory (1.5 m, FOV 45); the stored option comes back at the end");
            Op("cammark", "", "", "", "fix-122: remembers the hero camera's boom length, FOV, wished zoom and pose relative to the hero, read by the condition camchange:<dist|fov|zoom|pos|rot>");
            Op("mapfilter", "name", "on", "name", "fix-126: one filter of the map views' filter bar (inside the map's top-right corner) for this scenario: name=chests|names|companions|quests, on=1 (default) shows, on=0 hides; with the documents map or the quest log open it clicks the bar's button; restored at the end");
            Op("questlog", "", "", "", "fix-126: open the quest log page (L) on the Running tab with all places (its world map: quest talk marks, filter bar)");
            Op("shieldbeasts", "on", "", "", "fix-109: the [owner] port option \"Shield protects from beasts\" for this scenario: on=1 (default) lets a raised shield block animals and fist fighters as UP 1.04 (ShieldCreatureParade; not trolls or monsters, demons and ogres only with Perk_Shield_2, a Minecrawler only with a shield), on=0 is the original (CP 1.75: no parade stops a creature); the stored option comes back at the end");
            Op("forgivehits", "on", "", "", "fix-162: the [owner] port option \"Forgive accidental hits\" for this scenario: on=1 (default) forgives the hero's stray hits (up to 3 within 8 s) on an NPC fighting his enemy, and on his party (AccidentalHits: a complaint line, no fight back, no fight sound), on=0 is the original (CP 1.75 AssessAgressor + fight sound); the stored option comes back at the end");
            Op("strayhit", "npc", "count dmg", "npc", "fix-162: the hero's arrow lands on the NPC count times at once (default 1, dmg=1), through the presenter's hit snapshot and CombatService.Strike; logs each outcome, whether it was forgiven and the NPC's fight state");
            Op("latetrophies", "on", "", "", "fix-143: the [owner] port option \"Late trophies\" for this scenario: on=1 (default) lets a corpse rolled before the hero learnt a trophy perk give that perk's teeth, skin or fur when looked at again, once per perk (NpcLoot.GrantLateTrophies), on=0 is the original (CP 1.75: trophies decided once, at death); the stored option comes back at the end");
            Op("forcedrelease", "on", "", "", "fix-161: the [owner] port option \"Freeing by force counts\" for this scenario: on=1 (default) makes the hero's side killing the keeper of a running release quest (Gorn_FreeHim: Potros, Lars_FreeHim: Zapotek) settle it as Success without the ransom (ForcedReleases), on=0 is the original (CP 1.75: the Report quest fails); switching on re-judges such quests already failed; the stored option comes back at the end");
            Op("lockedchest", "", "difficulty maxdist refused", "", "fix-102: put the hero 1.5 m from the nearest chest in the world whose lock is shut, has picking difficulty (default 1) and no key, within maxdist m (default 3000), facing it; refused=1 (default, fix-105) takes his lockpicks so the lock refuses him; sets $chest (entity name) and $chestid; conditions chestmarked / chestlocked read that chest; picks / pickbreaks / pickopens / pickrolls / pickchance read the pick attempts on it since");
            Op("relockchest", "", "clear", "", "fix-121: shut the lock of the 'lockedchest' chest again (its start state), so the next 'use' is a fresh pick attempt; clear=1 also forgets the attempts counted so far (picks / pickbreaks / pickopens / pickrolls / pickchance)");
            Op("losfloor", "npc", "height dist", "npc", "fix-104: a solid 8 x 0.3 x 8 m test floor whose top is height m (default 3) over the NPC's feet, centred over it; the hero stands on it dist m (default 2) from the NPC, facing it (upstairs in a house); removed by losclear or at the end");
            Op("loswall", "npc", "item", "", "fix-104: a solid 4 x 4 x 0.3 m test wall halfway between the hero and the NPC (item=1: the item of lositem); removed by losclear or at the end");
            Op("lositem", "item", "dist", "", "fix-104: drop an item (default It_1H_Sword_01, no owner) dist m (default 1.2) in front of the hero on the ground; sets $item; condition ifocusitem=item while it is the normal focus");
            Op("losshelf", "", "size", "", "fix-114: a solid box (a wall shelf, default size 1.2,1.4,0.5 m) around the item of lositem, the item inside it 0.4 m over its bottom as G3's furniture boxes hold the items on them; the hero must still focus it; removed by losclear or at the end");
            Op("losclear", "", "", "", "fix-104: remove the test floors and walls of losfloor / loswall");
            Op("quickslot", "item", "slot", "item", "fix-105: put the item (given when missing) on quick slot slot (default 9, the 0 key) and press its key the player's way (QuickSlotInput.Use: weapons and spells via PlayerCombat.TryReady, the torch via HeroItemUse); waits for the hands to settle; conditions torch / torchbusy");
            Op("messhero", "", "", "", "fix-105: leave the hero as a broken game might before a load: a session control lock nobody releases, scripted input, walk-only, forced walk, held gait, body yaw, look-behind, the torch's take running; condition herotransient lists what is not at rest ('clean')");
            Op("losebone", "mode", "", "", "fix-105: add = a bone under the hero's Spine_2 and the overlay masks forgotten (the next overlay builds them with it); destroy = that bone goes, as the MetaHuman head swap removes head bones from the live model");
            Op("fry", "item n", "", "", "fix-88: on the open fry page fry n (default 1) of the item's stack (default It_Meat_Raw), as the page's list and amount dialog would");
            Op("synthselect", "n", "", "", "fix-125: select row n of the open craft page's recipe list (it scrolls by whole rows to show it); conditions synth, synthcraftable, synthdimmed, synthvisible, synthfirst, synthhalfrows, synthrow:<recipe>");
            Op("turnkey", "dir secs", "", "", "fix-73: hold the Camera rotation key left (Q, default) or right (E) for secs (default 1) as a player; logs the hero's turn and the camera's orbit (probes keyturnhero, camrel)");
            Op("camprobe", "mode", "label", "", "fix-76: camprobe on starts recording the camera every frame (relative to the hero, in his yaw frame), the hero's pelvis height and locomotion phase while he runs; camprobe off stops and logs the jitter metrics (step, jump, yjerk, rotjump, pelvisdip, phasejump, drift, yawdrift), read by the condition camjitter:<metric>");
            Op("cursorwatch", "", "", "", "fix-87: start a new record of the cursor modes (gameplay, hidden, pointer) the CursorPolicy picks frame by frame; the condition cursorseen lists them since then, cursor is the current one");
            Op("caveplace", "npc", "at wait", "", "fix-128: puts the NPC's body exactly at x,y,z (no ground snap: in the rock behind a cave wall, on a cave roof top) and waits wait= s (4) for the cave guard; logs where it ended and how the cave rules judge both places");
            Op("slopeground", "npc", "at idle walkto secs speed max slide", "", "fix-108: puts the NPC at x,y,z on a slope (idle, optionally walks to walkto=) and fails unless its feet stay within max= of the floor; slide=1 expects it to slide off a face steeper than 49 deg");
            Op("torsoprobe", "mode", "label trace", "", "fix-89: torsoprobe on records the hero's pelvis (Spine_1), chest (Spine_3) and head every frame he runs (>= 2.5 m/s), split into plain run frames and frames with an upper-body overlay (draw/sheathe while running; trace=1 logs each overlay frame); torsoprobe off logs the metrics (shrink: m the pelvis-head distance fell below the run's minimum, bend: degrees beyond the run's maximum bend, runmin, runmax, runbend, overlaymin, overlaybend, runframes, overlayframes), read by the condition torso:<metric>");
            Op("slopeback", "", "radius min max near far pitch secs", "", "fix-94: stand the hero on flat ground at the foot of a min..max deg (45..70) slope within radius m (80), the slope near..far m (1..2.5) behind his head, facing away; hold the look (pitch, + down) for secs (2) and record the camera: smallest eye distance, frames with the hero hidden / in the first-person view (conditions slopemineye, slopehidden, slopefp)");
            Op("move", "dir secs", "walk", "", "scripted move fwd|back|left|right for secs (default 1)");
            Op("walkto", "target secs", "near walk expect", "target", "fix-49: the hero runs (walk=1: walks) straight to the target (x,z / x,y,z / spot / npc), turning to it every frame, for at most secs (default 15; stuck = 0.5 m closer in 4 s ends it): passes within near m (default 1.5, flat), fails with the distance left and where he stopped; expect=blocked passes when he does not get there and climbs at most 1 m (cliffs, walls); logs metres walked, climb, step-ups and landscape folds passed");
            Op("drawtrace", "kind", "move sheathe", "", "fix-42: draw (kind melee|ranged|fist; move=fwd|back|left|right|backleft… walks meanwhile) and sheathe (sheathe=0: not), logging each draw/sheathe motion (reversed, overlay, body held) and when the weapon changes hands; fails unless the hand changes between the two motions");
            Op("switchtrace", "item", "during max tol", "item", "fix-101: press the quick-slot key of item (a weapon or spell; the real TryReady path) with the hands as they are, during=idle (default) | reload (after a shot) | aim (aim held) | draw (0.5 s into drawing the bow string, the button kept held: acts at the full draw) | attack (a melee attack runs); logs press -> taken -> old weapon off the hand -> put away -> draw -> new weapon in the hand -> ready (s) and G3's numbers (WeaponSwitchTimeline); fails when not ready within max s (4), when a waiting key does not act as the body is free (G3 one-slot queue), when a frame passes between put-away and draw, or taken -> ready exceeds G3 + tol (0.1 s)");
            Op("aimstrafe", "dir secs", "", "", "fix-42: bow/crossbow drawn (drawn when not), aim held, walk dir (default backleft) for secs (default 1.5): logs gait, speed, body vs camera yaw, locomotion set; fails when the body turns or the hero runs");
            Op("gaitcheck", "gait dir secs", "speed sp expectgait", "", "fix-44: move dir (default fwd) in gait sprint|run|walk (default sprint: the Walk/Sprint key pressed while moving) with the hands as they are for secs (default 1.5): logs state, gait, average speed, locomotion set, SP; sp=n sets SP first; fails unless the gait is expectgait (default the asked one) and the speed is within 0.25 m/s of speed");
            Op("hold", "keys secs", "parade", "keys", "hold attack,block (comma) for secs (default 1); logs the action and whether the block is up at the release; parade=1|0 fails unless it is up|down then (fix-68: the block stays up as long as the key is held)");
            Op("click", "keys times", "gap len block", "keys", "click attack or block (one key) times (default 1): press len s (default 0.04), the next press gap s after the last (default 0.3); block=1 holds the block through attack clicks; logs each combo stage (fix-31)");
            Op("enemies", "mode", "", "", "fix-151 (alias threats): list every NPC keeping the hero in combat (the save block 'inCombat', fight music, fights against him or his party, a companion's target) with id, name, distance, compass direction, position, phase, NavMesh path and place problems (inside a collider, cave roof / rock, off the NavMesh, no path); also to Player.log ([threats]); mode on (default) also marks them on screen through walls (refreshed, changes logged) until 'enemies off'; list: only the list; fix: puts stuck ones onto a free floor (placement / cave rescue / nearest NavMesh); kill: kills them (dev)");
            Op("unstuck", "", "", "", "fix-123 safety valve: the hero on a ceiling top inside the rock goes down to the floor below, inside geometry to the nearest free floor on his level or below, else to his last valid standing place (logs what it did)");
            Op("climbto", "target", "secs near maxy expect", "target", "fix-130: the hero runs at target x,y,z (flat steering, the real controller), jumping whenever he gains no ground for 0.35 s on the ground (a crate stack); reached = within near m (0.8) flat and 0.6 m up/down; fails when his feet ever got above maxy (a roof over the climb) or when not reached (expect=blocked: passes when not reached; expect=any: only maxy counts); logs the highest feet (condition climbmaxy), jumps, step-ups and guards");
            Op("drop", "pos", "", "pos", "fix-123: put the hero's feet at x,y,z as they are (no ground search, no streaming wait): on a cave roof's top, under the world; the ground guard and the world catch then act");
            Op("getup", "", "", "", "safety valve: the hero leaves any sit, seat, object use or knock-out at once (logs what it ended)");
            Op("sit", "", "", "", "knock the hero into the sitting knock-down (G3 ZS_SitKnockDown): he sits until a click (attack = GetUpAttack, block = GetUpParade)");
            Op("sneak", "on", "", "", "toggle sneaking on (default) or off as the sneak key; with a melee weapon drawn the hero sneaks in the Parade stance");
            Op("cast", "spell", "target hold", "spell", "give + ready a spell item (waits for the hand), face target, press LMB, hold hold s (default 1), let go; fix-114: the spell goes off during its motion (condition casttiming, $castleft); a power cast let go before 0.7 s restarts plain");
            Op("teach", "spell", "force", "spell", "teach a spell through TeachingService (G3 LearnSpell); force=1 (default) first gives the INT, required spell and learning points it needs");
            Op("spellbook", "open", "select slot ready", "", "open the spell book page (key B; open=0 closes it), select=spell, slot=digit puts it on the quick bar (learned only), ready=1 readies it like Enter");
            Op("staffblock", "secs", "cast", "", "in the magic stance hold the block button (RMB) for secs (default 1): with a staff in the left hand the staff block rises (PS_Magic_StaffParade); cast=1 clicks cast half-way");
            Op("save", "slot", "wait", "", "save to a named slot through the game's save system (no slot: QuickSave); waits up to wait= s (default 10) while saving is blocked (a dialog, a fight), then fails with the reason; a scenario run records the slot and the next step in Work/scenario-runs/<name>.json (resume point; fix-54)");
            Op("load", "slot", "", "", "load a named slot (no slot: QuickLoad); the run goes on in the loaded game with the next step (dev state calm/freeze/focus is dropped; fix-54)");
            Op("autosave", "", "every quick", "", "fix-82: write the autosave now through the game's save system (refused while saving is blocked: logged; conditions autosaved, wrote); every=secs sets the autosave interval instead (timer restarts, 0 = off); quick=1 does a quicksave (F5) instead (fix-107: one file each, quick / auto)");
            Op("recovery", "", "every offer reply", "", "fix-155: crash recovery - take the hidden recovery snapshot now (refused while saving is blocked; logs its main-thread serialization ms and size); every=secs sets the snapshot interval instead (timer restarts); offer=1 opens the main menu and asks the title's restore question as after a crash (the runner's stand-in for an unclean exit); reply=yes|no answers it (yes: loads the snapshot as slot 'recovered', no: discards it); conditions recovery:exists|listed|continue|ms|kb|count|due|asked|text");
            Op("menu", "page", "key", "", "fix-107: open the game menu at a page (main | load | save | options …; close shuts it) and/or send key=Up|Down|PageDown|Home|End|Return|Delete … to the save/load list (condition fileslist:visible|count|selected|first|page)");
            Op("loadout", "name", "", "name", "fix-54: grant a weapon set with the attributes and perks it needs and make it the fight op's attack style: " + ScenarioLoadout.Names);
            Op("autodialog", "on", "answers", "", "fix-54: answer dialogs the scenario did not open (NPCs that start talking): on (default) | off; answers=e1,e2 entries to take first");
            Op("dialogstate", "", "", "", "fix-54: log whether a dialog is open: speaker, who opened it, phase, entry, options");
            // fix-75
            Op("dropprobe", "", "min tilt", "", "fix-75: every item dropped during the game shown near the hero lies flat on the ground: long mesh axis within tilt= degrees (20) of the ground, lowest point -3..+10 cm from it; min= views (1)");
            Op("invquick", "item digit", "hover expect close", "item", "fix-75: open the inventory, select (hover=1: hover) the item's stack, check the item panel shows it, press digit= (G3 OnPlayerAssignStackKey); expect=assigned (default) | refused; close=0 keeps the page open");
            Op("invdrop", "item amount", "at expect close", "item", "fix-85/92 [owner]: open the inventory on the item's tab and let its dragged tile go at a screen point found on the live UI (the mouse release's own path): at=world (default; a point outside every UI element) drops amount= (1; a stack goes through the amount dialog, set to the amount, then OK) in front of the hero; at=sidebar (the HUD page sidebar) must keep it; expect=dropped (default at world) | refused (quest items, deny sound) | kept (default at sidebar); checks the inventory count and a new dropped world item");
            Op("invsort", "", "clicks close", "", "fix-92 [owner]: open the inventory and click the selected category tab clicks= times (4): each click must move the sort mode on (G3, name, value, newest), put that mode's theme arrow on the tab (none in G3 order) and list the tab's rows in that order");
            Op("blocklag", "", "after max combo quick hold", "", "fix-75: with a melee weapon drawn start an attack (combo=1 queues the next), press and hold the block after= s (0.1): logs input -> parade-up ms, fails over max= ms (700) or when the queued attack still starts; quick=1 taps a quick attack and expects the block back while held; hold= s more (1), then released");
            Op("cancel", "", "expect", "", "fix-75: the secondary button during an object use or its approach (G3 action 0x66): waits until the use ended (10 s), fails when it did not or the block went up");
            Op("bump", "npc secs", "expect tol walk", "npc", "fix-86: the hero runs at the NPC's current position every frame for secs (default 3): expect=blocked (default) passes when its body stopped him and he never came closer than contact (his radius + its footprint) - tol (0.08 m); expect=pass passes when he got within 0.4 m (a lying or dead body is walked over); logs closest/end distance, contact and blocks");
            // fix-93
            Op("npcpass", "npc", "dist secs expect speed", "npc", "fix-93: the NPC is put dist m (5) in front of the standing hero and walks (routine paused) to dist m behind him (expect=round, default) or onto his spot (expect=wait): round passes when it arrives after a detour round him and never came closer than contact - 0.05 m; wait passes when it stops at him (within contact + 0.6 m) without entering; logs closest, detours, holds, side");
            Op("arrowdodge", "npc", "shots secs dodge step", "npc", "fix-93: the archer engaged on the hero shoots shots= (3) arrows; at each release the hero runs sideways for step= s (0.5; dodge=1, alternating) or stands (dodge=0): dodge=1 passes with at most one hit, dodge=0 with at least shots-1 hits");
            Op("strike", "attacker victim", "dmg", "attacker victim", "fix-93: one melee hit of the attacker's weapon on the victim resolved now (BuildHit -> Strike; dmg= overrides the weapon damage); logs the outcome and whether the hero has hurt the victim");
            Op("useprobe", "obj", "walked snap clear", "", "fix-75: the last approach walk to a used object: metres walked (fails under walked= m, 1) and placed at once (fails over snap= m, 0.3); obj= logs the distance and facing to it; fix-103: logs how far the use spot was moved out of the object's collision and the end-of-use push-out; clear=1 fails when the hero stands inside a collider or no free spot was found");
            // flow / output
            Op("wait", "secs", "until timeout soft", "", "wait game seconds, or until a condition (timeout default 15; soft=1: a timeout is only logged)");
            Op("expect", "cond", "", "cond", "check a condition now; failures are listed in the result line");
            Op("shot", "name", "look source view dist height target", "name",
               "screenshot Docs/screens/<name>.png, optionally facing look=target first; view=front|back|left|right|orbit frames the camera on target= (else look=, else the hero) at dist= m (2.5), height= m (1.6); orbit = four shots <name>_front/_right/_back/_left");
            Op("probe", "actor", "", "", "log vitals, AI, animator, water of an actor (default hero)");
            Op("log", "text", "", "", "log a line");
            Op("fxlist", "top", "", "", "fix-58: log the live G3 effects grouped by name (effects, parts of the budget, stopped, endless, target gone, oldest); console: fx [top]");
            Op("scenario", "name", "", "name", "run the steps of another scenario file here (include)");
            Op("set", "name value", "from add", "name", "set a $variable; fix-96: from=<probe> (a condition's left side, e.g. has:Arrow) takes its current value, add= adds a number to it (relative counts: the hero may carry items before the run)");
        }

        public static IEnumerable<string> AliasNames => _aliases.Keys;

        public static IEnumerable<OpSpec> All => _ops.Values.OrderBy(o => o.Name, StringComparer.Ordinal);

        public static bool TryGet(string op, out OpSpec spec)
        {
            if (op != null && _aliases.TryGetValue(op, out var real)) op = real;
            return _ops.TryGetValue(op ?? "", out spec);
        }

        /// <summary>
        /// Binds raw tokens (positional + key=value) to a step; errors name the statement. Extra positional values are
        /// joined into the last positional argument (so "log some words" works).
        /// </summary>
        public static ScenarioStep Bind(string op, IReadOnlyList<string> positional, IReadOnlyDictionary<string, string> named,
                                        string source, List<string> errors)
        {
            if (!TryGet(op, out var spec))
            {
                errors?.Add($"'{source}': unknown op '{op}'");
                return null;
            }
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < positional.Count; i++)
            {
                if (spec.Positional.Count == 0)
                {
                    errors?.Add($"'{source}': {spec.Name} takes no positional argument ('{positional[i]}')");
                    return null;
                }
                int slot = Math.Min(i, spec.Positional.Count - 1);
                string name = spec.Positional[slot];
                args[name] = args.TryGetValue(name, out var prev) && i >= spec.Positional.Count ? prev + " " + positional[i] : positional[i];
            }
            foreach (var kv in named ?? new Dictionary<string, string>())
            {
                if (!spec.Accepts(kv.Key))
                {
                    errors?.Add($"'{source}': {spec.Name} has no argument '{kv.Key}' (usage: {spec.Usage})");
                    return null;
                }
                args[kv.Key] = kv.Value;
            }
            foreach (var r in spec.Required)
                if (!args.ContainsKey(r) || string.IsNullOrEmpty(args[r]))
                {
                    errors?.Add($"'{source}': {spec.Name} needs '{r}' (usage: {spec.Usage})");
                    return null;
                }
            return new ScenarioStep(spec.Name, args, source);
        }
    }

    /// <summary>
    /// Camera framing of the scenario 'shot' op (view=front|back|left|right|orbit, dist=, height=): the camera stands
    /// <c>dist</c> m from the target on the chosen side of the target's facing, <c>height</c> m above its feet, looking at
    /// its chest. Yaw in degrees clockwise from +z (Unity/G3 world yaw).
    /// </summary>
    public static class ShotFraming
    {
        public const float DefaultDistance = 2.5f, DefaultHeight = 1.6f, LookHeight = 1.2f;

        /// <summary>The four views of an orbit, in order.</summary>
        public static readonly string[] OrbitViews = { "front", "right", "back", "left" };

        /// <summary>Yaw offset of a view from the target's facing (front = in front of it, looking back at it).</summary>
        public static bool TryViewOffset(string view, out float degrees)
        {
            switch ((view ?? "").ToLowerInvariant())
            {
                case "front": degrees = 0f; return true;
                case "right": degrees = 90f; return true;
                case "back": degrees = 180f; return true;
                case "left": degrees = -90f; return true;
                default: degrees = 0f; return false;
            }
        }

        /// <summary>Camera position for a target at (x, y, z) facing <paramref name="targetYaw"/>.</summary>
        public static void CameraPosition(float x, float y, float z, float targetYaw, float viewOffset, float dist, float height,
                                          out float cx, out float cy, out float cz)
        {
            double a = (targetYaw + viewOffset) * (Math.PI / 180.0);
            cx = x + (float)Math.Sin(a) * dist;
            cy = y + height;
            cz = z + (float)Math.Cos(a) * dist;
        }
    }
}
#endif
