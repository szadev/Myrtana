using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// How NPCs show their perception's reactions to the player: a warner or watcher turns to face him (G3
    /// ZS_ObserveSuspect / ZS_ReactToZone look at the suspect) and calls out (G3 ZS_ReactToZone / ZS_ReactToWeapon /
    /// ZS_ReactToSneak shared lines): "GetLost" then "ISaidGetOutOfHere" to an intruder, "WeaponDown" then
    /// "ISaidWeaponDown" to a drawn weapon, "WhatsThisSupposedToBe" to a sneaker; a zone observer watches silently
    /// (<see cref="CrimeSystem.ObserveLine"/>, fix-78). The voice
    /// plays at the NPC. Combat lines (<see cref="CombatShout"/>: "FinishingMove", the "Dead"/"Down" cries, the AI's
    /// shouts, the fight watchers' cheers) play the same way, for the hero and shown NPCs.
    /// <para>
    /// <b>Speech voices</b> (fix-199): a few 3D voices, each kept by its speaker for the line
    /// (<see cref="G3.Audio.SpeakerVoiceSlots"/>) and moved to his head every frame (<see cref="LateUpdate"/>), so the line
    /// is heard where he stands now, panned by the camera's AudioListener as in G3. Before, one shared voice per kind was
    /// put where the speaker stood when the line was asked for, a line of an NPC without a view kept the previous
    /// speaker's place, and a line asked for while another one still loaded moved that voice: lines of far NPCs could be
    /// heard on the wrong side. Doppler is off (a voice jumping to its speaker read as a huge velocity).
    /// </para>
    /// <para>
    /// <b>Texts of NPC lines</b> (fix-156): in G3 every AI SVM line goes through one say helper (Script_Game
    /// FUN_1000baf0) that calls <c>Entity::StartSaySVM(…, false)</c>; the false skips StartSayEx's only text output (the
    /// GUI log "Name: text"), and <c>gCScriptProcessingUnit::StartOutput</c> only plays the 3D stream, so warnings,
    /// cheers, comments and smalltalk are heard, never shown. The port option "NPC shout subtitles"
    /// (<see cref="NpcShoutSubtitles"/>, [owner] default on) shows them in the HUD log, but only the speaker's own voice
    /// set's text (none when its set has no text for the line; G3's Russian table has e.g. Ooh_0n only for Ass_Old_Hard);
    /// off = G3. Both need G3's Subtitles option. The hero's own lines keep their text, else another voice set's
    /// [interp, unchanged] (<see cref="PortOptions.SharedLineText"/>).
    /// </para>
    /// </summary>
    public sealed class NpcReactionView : MonoBehaviour, ISessionView
    {
        /// <summary>An NPC that keeps watching the player says its line again at most this often (seconds).</summary>
        const float ObserveRepeat = 20f;
        /// <summary>How long one reaction keeps the NPC looking at the player (G3 observe task time 4 s; a warning lasts 8 s).</summary>
        const float ObserveSeconds = 5f, WarnSeconds = 9f;

        readonly Dictionary<ActorId, float> _lastObserve = new Dictionary<ActorId, float>();
        static readonly Dictionary<ActorId, int> Calls = new Dictionary<ActorId, int>();

        /// <summary>
        /// Perception lines (warnings, observing calls) <paramref name="npc"/> said to the hero since the session was bound
        /// (scenario probe <c>calls:X</c>, fix-78).
        /// </summary>
        public static int CallsOf(ActorId npc) => Calls.TryGetValue(npc, out int n) ? n : 0;
        NpcViewManager _npcs;
        Transform _player;
        HudMessages _messages;

        /// <summary>The Subtitles option (G3 shows spoken lines only while it is on); null = on.</summary>
        public Func<bool> SubtitlesOn { get; set; }
        VoiceResolver _voices;
        StreamingVoiceCatalog _catalog;
        GameSession _session;
        SharedLines _lines;
        IDisposable _subscription;

        public void Init(NpcViewManager npcs, Transform player, HudMessages messages, StreamingVoiceCatalog catalog, LanguageSettings languages)
        {
            _npcs = npcs;
            _player = player;
            _messages = messages;
            _catalog = catalog;
            _voices = new VoiceResolver(languages, catalog);
            for (int i = 0; i < SpeechVoices; i++)
            {
                _speech.Add(CreateVoice("NPC speech " + i));
                _speakers.Add(null);
                _speakerIds.Add(default);
                _busy.Add(false);
                _started.Add(0f);
            }
        }

        /// <summary>Speech voices: lines of this many speakers sound at once (a death cry does not cut a warning short).</summary>
        const int SpeechVoices = 6;
        readonly List<VoicePlayer> _speech = new List<VoicePlayer>();
        readonly List<string> _speakers = new List<string>();
        readonly List<ActorId> _speakerIds = new List<ActorId>();
        readonly List<bool> _busy = new List<bool>();
        readonly List<float> _started = new List<float>();

        VoicePlayer CreateVoice(string name)
        {
            var voice = new GameObject(name).AddComponent<VoicePlayer>();
            voice.transform.SetParent(transform, false);
            var source = voice.GetComponent<AudioSource>();
            source.spatialBlend = 1f; // heard where the speaker stands
            source.minDistance = 3f;
            source.maxDistance = 30f;
            source.dopplerLevel = 0f; // the voice moves to each new speaker in one step
            return voice;
        }

        /// <summary>
        /// Logs every voiced line with its speaker's side as the camera's listener hears it ("[Myrtana] speech …"; command
        /// line <c>-logspeech</c>).
        /// </summary>
        public static bool LogSpeech { get; set; } = Array.IndexOf(Environment.GetCommandLineArgs(), "-logspeech") >= 0;

        /// <summary>The speaker's head now: the hero, his shown view, else the simulation's pose; false = unknown.</summary>
        bool TryHeadOf(ActorId speaker, out Vector3 head)
        {
            head = default;
            if (speaker.IsPlayer) head = _player.position;
            else if (_npcs.TryGetView(speaker, out var view)) head = view.position;
            else if (_session != null && _session.Npcs.TryGet(speaker, out var agent)) { var pose = _session.Npcs.PoseOf(agent); head = new Vector3(pose.X, pose.Y, pose.Z); }
            else return false;
            head += Vector3.up * RaycastLineOfSight.EyeHeight;
            return true;
        }

        /// <summary>Every frame: a speaking voice follows its speaker (he walks; a far view steps at its LOD rate).</summary>
        void LateUpdate()
        {
            for (int i = 0; i < _speech.Count; i++)
            {
                if (_speakers[i] == null || !_speech[i].Busy) continue;
                if (TryHeadOf(_speakerIds[i], out var head)) _speech[i].transform.position = head;
            }
        }

        /// <summary>The voice for the speaker's new line (<see cref="G3.Audio.SpeakerVoiceSlots"/>); -1 = none.</summary>
        int VoiceFor(ActorId speaker, bool mayInterrupt)
        {
            for (int i = 0; i < _speech.Count; i++) _busy[i] = _speech[i].Busy;
            return G3.Audio.SpeakerVoiceSlots.Choose(_speakers, _busy, _started, speaker.Name, mayInterrupt);
        }

        public void Bind(GameSession session)
        {
            _subscription?.Dispose();
            _session = session;
            _lastObserve.Clear();
            Calls.Clear();
            session.Content.Database.TryGet(SharedLines.DefinitionId, out _lines);
            _subscription = session.Events.Subscribe<NpcReacted>(OnReacted);
            _combatSubscription?.Dispose();
            _combatSubscription = session.Events.Subscribe<CombatShout>(OnShout);
            _applaudSubscription?.Dispose();
            _applaudSubscription = session.Events.Subscribe<NpcApplauds>(OnApplauds);
            _ambientSubscription?.Dispose();
            _ambientSubscription = session.Events.Subscribe<NpcAmbientLine>(OnAmbientLine);
            session.Npcs.HasLine = HasLine; // ZS_Preach: a voice with "Speech" lines preaches aloud (HasSVMBlock)
        }

        IDisposable _combatSubscription, _applaudSubscription, _ambientSubscription;

        void OnDestroy()
        {
            _subscription?.Dispose();
            _combatSubscription?.Dispose();
            _applaudSubscription?.Dispose();
            _ambientSubscription?.Dispose();
        }

        /// <summary>A combat line of the hero or a shown NPC (unseen fights stay silent).</summary>
        void OnShout(CombatShout e)
        {
            if (string.IsNullOrEmpty(e.Line)) return;
            if (e.Speaker.IsPlayer || _npcs.TryGetView(e.Speaker, out _)) Say(e.Speaker, e.Line, true);
        }

        void OnReacted(NpcReacted e)
        {
            if (e.Reaction.Kind == ReactionKind.Watch)
            {
                WatchFight(e.Npc, e.Reaction.Target, e.Reaction.Other);
                return;
            }
            if (e.Reaction.Kind == ReactionKind.Comment)
            {
                Comment(e.Npc, e.Reaction.Target, e.Reaction.Other);
                return;
            }
            if (!e.Reaction.Target.IsPlayer) return;
            if (e.Reaction.Kind == ReactionKind.Warn || e.Reaction.Kind == ReactionKind.Observe) Watch(e.Npc, e.Reaction.Kind == ReactionKind.Warn);
            string line = LineOf(e);
            if (line != null) Calls[e.Npc] = CallsOf(e.Npc) + 1;
            if (line != null) Say(e.Npc, line, true);
        }

        /// <summary>
        /// ZS_WatchFight: a bystander that heard the fight (FightSound) keeps 5.5–6.5 m from the nearer fighter, cheers and
        /// applauds the winner (<see cref="NpcView.WatchFight"/>, <see cref="FightWatcher"/>); every further hit renews it.
        /// An arena fight is watched as a spectator from a free spectator point or from where the NPC stands
        /// (ZS_WatchFight_Spectator, <see cref="SpectatorRules"/>, fix-85).
        /// </summary>
        void WatchFight(ActorId npc, ActorId fighter, ActorId other)
        {
            if (!_npcs.TryGetView(npc, out var view) || !view.TryGetComponent<NpcView>(out var npcView)) return;
            bool spectator = IsArenaFight(fighter, other);
            Vector3? spot = null;
            if (spectator && npcView.WatchedFight == null && _session.Npcs.TryGet(npc, out var agent))
            {
                var here = view.position;
                var station = SpectatorRules.FindPoint(_session.Npcs.Points, _session.Npcs.Claims, npc, agent.Station,
                                                       new WorldPose(here.x, here.y, here.z, 0));
                if (!station.IsNone && _session.Npcs.Points.TryGetPose(station, out var pose)) spot = new Vector3(pose.X, pose.Y, pose.Z);
            }
            npcView.WatchFight(fighter, other, BodyOf, id => _session.Combat.Vitals.StateOf(id) != VitalState.Alive, spectator, spot);
        }

        /// <summary>ZS_WatchFight's spectator test: a fighter waits in the ring (ZS_Arena) or fights with the reason Arena.</summary>
        bool IsArenaFight(ActorId a, ActorId b)
        {
            var ba = a.IsPlayer ? null : _session.NpcCombat.Get(a);
            var bb = b.IsPlayer ? null : _session.NpcCombat.Get(b);
            return SpectatorRules.IsArenaFight(!a.IsPlayer && _session.Npcs.InArena(a) == true, ba != null && ba.IsFighting ? ba.State.Reason : (AttackReason?)null,
                                               !b.IsPlayer && _session.Npcs.InArena(b) == true, bb != null && bb.IsFighting ? bb.State.Reason : (AttackReason?)null);
        }

        Transform BodyOf(ActorId id) => id.IsPlayer ? _player : _npcs.TryGetView(id, out var v) ? v : null;

        /// <summary>ZS_Applaud (a liberated enclave's oppressed, ZS_RevolutionSuccess): face the target and applaud.</summary>
        void OnApplauds(NpcApplauds e)
        {
            var target = BodyOf(e.Target);
            if (target != null && _npcs.TryGetView(e.Npc, out var view) && view.TryGetComponent<NpcView>(out var npcView)) npcView.Applaud(target);
        }

        /// <summary>Ambient lines (smalltalk, sermons) are heard within this distance of the hero (m) [interp].</summary>
        const float AmbientHearing = 25f;
        /// <summary>
        /// An ambient SVM line (<see cref="NpcAmbientLine"/>): played at a shown speaker near the hero on one of a few
        /// free speech voice; when all of them speak, the line is skipped [interp: G3 plays every NPC's line].
        /// </summary>
        void OnAmbientLine(NpcAmbientLine e)
        {
            if (!_npcs.TryGetView(e.Speaker, out var view) || (view.position - _player.position).sqrMagnitude > AmbientHearing * AmbientHearing) return;
            if (VoiceFor(e.Speaker, false) < 0) return;
            Say(e.Speaker, e.Line, false);
        }

        /// <summary>
        /// StartSayDefeatComment (a friend of the winner, a watcher of a duel or arena bout, a witness of a killing it
        /// approves of): a fight watcher who targeted the loser says "FriendWon" / "FriendLost" / "NotBad" … in 80 % of the
        /// cases (<see cref="BattleLines.DefeatCommentLine"/>), through the say gate; it turns to the winner.
        /// </summary>
        void Comment(ActorId npc, ActorId winner, ActorId loser)
        {
            if (!_session.Actors.TryGet(npc, out var state) || !CombatWiring.CombatDefOf(state.Definition).Humanoid) return;
            var talk = _session.NpcCombat.Talk;
            var attitudes = _session.Crime.Attitudes;
            string line = BattleLines.DefeatCommentLine(talk.Watches(npc, loser), !winner.IsEmpty && winner != loser,
                                                        attitudes.Of(npc, loser), attitudes.Of(npc, winner), _session.Random);
            if (line == null || !talk.Gate.TrySay(DefeatCommentKind, SpeechGate.LongCooldownHours)) return; // kind 7
            if (!_npcs.TryGetView(npc, out var view)) return;
            Transform face = winner.IsPlayer ? _player : _npcs.TryGetView(winner, out var w) ? w : null;
            if (face != null && view.TryGetComponent<NpcView>(out var npcView)) npcView.Watch(face, ObserveSeconds);
            Say(npc, line, true);
        }

        const string DefeatCommentKind = "DefeatComment";

        void Watch(ActorId npc, bool warning)
        {
            if (_npcs.TryGetView(npc, out var view) && view.TryGetComponent<NpcView>(out var npcView))
                npcView.Watch(_player, warning ? WarnSeconds : ObserveSeconds);
        }

        string LineOf(NpcReacted e)
        {
            var r = e.Reaction;
            if (r.Line != null) return r.Line; // the crime rules chose it (warnings, "WiseMove", "YesGetLost")
            switch (r.Kind)
            {
                case ReactionKind.Observe:
                    // ZS_ObserveSuspect is silent (a zone observer, someone else's warning); ZS_ReactToSneak speaks (fix-78)
                    string said = CrimeSystem.ObserveLine(r, e.Cause);
                    if (said == null) return null;
                    if (_lastObserve.TryGetValue(e.Npc, out var last) && Time.time - last < ObserveRepeat) return null;
                    _lastObserve[e.Npc] = Time.time;
                    return said;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The subtitle of a shared line: the voice set's own text, else the same line's text of another voice set (G3's
        /// string table has texts for only a few voice sets of e.g. "ISaidWeaponDown", and none at all for "WeaponDown",
        /// "WiseMove", "YesGetLost", "StopMagic": those are heard, not shown, as in G3) <b>[interp]</b>.
        /// </summary>
        bool TryShownText(TextId text, string line, out string shown)
        {
            if (_session.Localizer.TryText(text, out shown) && !string.IsNullOrWhiteSpace(shown)) return true;
            shown = null;
            return _session.SharedLineTexts != null && _session.SharedLineTexts.TryText(null, line, out shown);
        }

        /// <summary>
        /// The port option "NPC shout subtitles" (<see cref="PortOptions.NpcShoutSubtitles"/>, set by PortOptionsApplier;
        /// fix-156, [owner] default on; off = G3: voice only).
        /// </summary>
        public static bool NpcShoutSubtitles { get; set; } = true;

        /// <summary>The speaker's voice set has a recording of the shared line.</summary>
        bool HasLine(ActorId speaker, string line)
        {
            if (_lines == null || !_session.Content.Database.TryGet(speaker.Name, out NpcDef def) || string.IsNullOrEmpty(def.VoiceSet)) return false;
            return _voices.TryResolve(_lines.TextOf(def.VoiceSet, line).Value, out _);
        }

        /// <summary>
        /// Says a shared line of <paramref name="speaker"/>'s voice set (the hero's content entry has "Player"); its HUD text
        /// by <see cref="PortOptions.SharedLineText"/> (see the class remarks).
        /// </summary>
        void Say(ActorId speaker, string line, bool mayInterrupt)
        {
            if (_lines == null || !_session.Content.Database.TryGet(speaker.Name, out NpcDef def) || string.IsNullOrEmpty(def.VoiceSet)) return;
            var text = _lines.TextOf(def.VoiceSet, line);
            var shows = PortOptions.SharedLineText(speaker.IsPlayer, SubtitlesOn?.Invoke() != false, NpcShoutSubtitles);
            string shown = null;
            bool hasText = shows == PortOptions.LineText.OwnVoiceSet ? _session.Localizer.TryText(text, out shown)
                         : shows == PortOptions.LineText.OwnOrAnotherVoiceSet && TryShownText(text, line, out shown);
            if (hasText) _messages.Show($"{HudFocus.NpcName(_session, speaker)}: {shown}");
            if (!_voices.TryResolve(text.Value, out var clip)) return;
            if (!TryHeadOf(speaker, out var head)) return; // nowhere to be heard from (never another speaker's old place)
            int slot = VoiceFor(speaker, mayInterrupt);
            if (slot < 0) return;
            var voice = _speech[slot];
            _speakers[slot] = speaker.Name;
            _speakerIds[slot] = speaker;
            _started[slot] = Time.unscaledTime;
            voice.transform.position = head;
            voice.Play(_catalog.PathOf(clip), () => { });
            if (LogSpeech) LogSide(speaker, line, head, slot);
        }

        void LogSide(ActorId speaker, string line, Vector3 head, int slot)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var t = cam.transform;
            var at = new G3.Audio.V3(t.position.x, t.position.y, t.position.z);
            var fwd = new G3.Audio.V3(t.forward.x, t.forward.y, t.forward.z);
            var up = new G3.Audio.V3(t.up.x, t.up.y, t.up.z);
            var src = new G3.Audio.V3(head.x, head.y, head.z);
            float az = G3.Audio.ListenerPan.Azimuth(at, fwd, up, src);
            Debug.Log($"[Myrtana] speech {speaker.Name} {line} voice {slot}: {Vector3.Distance(t.position, head):0.0} m, " +
                      $"azimuth {az:0} ({G3.Audio.ListenerPan.Side(az)}), pan {G3.Audio.ListenerPan.Pan(at, fwd, up, src):+0.00;-0.00}");
        }
    }
}
