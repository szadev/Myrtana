using System;
using System.Collections;
using System.Collections.Generic;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;
using UnityEngine.Networking;

namespace Myrtana.Unity
{
    /// <summary>
    /// The sounds of fights (Docs/combat.md §13), chosen by <see cref="CombatSoundRules"/> as G3's scripts do: impact of
    /// resolved hits and blocks, draw and sheathe, bow string and release, arrows in the level, the victims' cries
    /// (shared voice lines in the voice language) and the creatures' own flinch and death sounds. Swings, creature attacks
    /// and stumbles sound through the motions' effect tracks (<see cref="G3MotionSounds"/>, also put on the hero here).
    /// Effects play through <see cref="G3EffectSounds"/> (pooled voices); voice clips are loaded once and kept.
    /// </summary>
    public sealed class CombatSounds : MonoBehaviour, ISessionView
    {
        const float VoiceMinDistance = 2f, VoiceMaxDistance = 30f; // SVMs are 3D in G3; its ranges are the engine's [interp]
        const float StaleVoiceSeconds = 0.6f;                     // a cry still loading after this is dropped
        const float NpcArrowSoundRange = 5f;                       // OnItemTouchesLevel: NPC shots only near the player

        CombatPresenter _presenter;
        G3Player _player;
        StreamingVoiceCatalog _catalog;
        VoiceResolver _voices;
        GameSession _session;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly HashSet<ActorId> _down = new HashSet<ActorId>();
        readonly Dictionary<G3CombatAnimator, CombatFighter> _fighterOf = new Dictionary<G3CombatAnimator, CombatFighter>();
        readonly Dictionary<CombatFighter, int> _bowString = new Dictionary<CombatFighter, int>();
        readonly Dictionary<ActorId, int> _speaking = new Dictionary<ActorId, int>();
        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly PainCryCycle _painCries = new PainCryCycle();
        readonly SeededRandom _random = new SeededRandom((ulong)Environment.TickCount);
        bool _heroSounds;
        G3MotionPlayer _heroMotions;

        [Tooltip("Log every combat sound decision (verification)")] public bool log;

        public void Init(CombatPresenter presenter, G3Player player, StreamingVoiceCatalog catalog, LanguageSettings languages)
        {
            _presenter = presenter;
            _player = player;
            _catalog = catalog;
            _voices = new VoiceResolver(languages, catalog);
        }

        public void Bind(GameSession session)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            _session = session;
            _down.Clear();
            _speaking.Clear();
            _painCries.Reset();
            var events = session.Events;
            _subscriptions.Add(events.Subscribe<HitResolved>(OnHitResolved));
            _subscriptions.Add(events.Subscribe<ActorDefeated>(e => { if (!e.Killed) _down.Add(e.Victim); }));
            _subscriptions.Add(events.Subscribe<ActorGotUp>(e => _down.Remove(e.Actor)));
        }

        void OnEnable()
        {
            G3CombatAnimator.Cue += OnCue;
            Projectile.HitLevel += OnProjectileHitLevel;
            CombatFighter.WeaponTouchedLevel += OnWeaponTouchedLevel;
        }

        void OnDisable()
        {
            G3CombatAnimator.Cue -= OnCue;
            Projectile.HitLevel -= OnProjectileHitLevel;
            CombatFighter.WeaponTouchedLevel -= OnWeaponTouchedLevel;
        }

        void OnDestroy()
        {
            foreach (var s in _subscriptions) s.Dispose();
            foreach (var c in _clips.Values) if (c != null) Destroy(c);
            _clips.Clear();
        }

        void Update()
        {
            // The hero's motions carry the swing whooshes and the dual-sword draw (NPC views get G3MotionSounds from their pool).
            // The tool sounds of his object uses (grindstone, anvil, saw, chest lid …) are on the same tracks. fix-61: checked
            // again whenever the hero's motion player changes (a rebuilt hero visual would otherwise stay silent).
            if (_player == null || Time.frameCount % 30 != 0) return;
            var motions = _player.GetComponentInChildren<G3MotionPlayer>();
            if (motions == null || (_heroSounds && ReferenceEquals(motions, _heroMotions))) return;
            if (!motions.TryGetComponent<G3MotionSounds>(out var sounds)) sounds = motions.gameObject.AddComponent<G3MotionSounds>();
            sounds.AsHero(); // his own actions outrank the world's loops (fix-61)
            _heroMotions = motions;
            _heroSounds = true;
        }

        // ------------------------------------------------------------------------------------------- hits

        void OnHitResolved(HitResolved e)
        {
            var o = e.Outcome;
            if (o.Result == HitResult.Ignored || _presenter == null || !_presenter.TryGet(e.Victim, out var victim)) return;
            // A downed body killed now is the finishing blow: its sound is OnAI_FinishingAttack's (presenter), and the
            // victim dies silently (ZS_Dead from the down state, no "Dead" line).
            if (o.Killed && _down.Contains(e.Victim)) return;
            Vector3 at = victim.Body != null ? victim.Body.Center : victim.transform.position + Vector3.up;
            _presenter.TryGet(e.Attacker, out var attacker);
            // The collision effects (sound + blood/sparks visuals) start where G3 starts them: on the attacker's weapon
            // (DoLogicalDamage, AssessHit); missiles and spells on the body surface facing the shooter. At the body centre
            // the particles stayed hidden inside the mesh (fix-55, r30: a parade showed no sparks).
            bool melee = attacker != null && !attacker.RangedDrawn && !attacker.SpellDrawn;
            Vector3 blow = attacker != null ? CombatFighter.BlowPoint(attacker, victim, melee) : at;

            if (o.Result == HitResult.Blocked && attacker != null && !attacker.RangedDrawn && !attacker.SpellDrawn)
            {
                bool shield = _session != null && _presenter.Snapshot(victim).Shield; // G3 shields are all wooden
                string clash = CombatSoundRules.ParadeClashEffect(_session?.Settings.Rules.ExtendedAI ?? true,
                    victim.WeaponClass == WeaponClass.Fist, CombatSoundRules.IsMetal(attacker.WeaponItem),
                    !shield && CombatSoundRules.IsMetal(victim.WeaponItem));
                PlayEffect(clash, blow, "parade");
            }
            PlayEffect(CombatSoundRules.HitEffect(o.Reaction, o.PowerDelta), blow, $"hit {e.Victim} {o.Reaction} D={o.PowerDelta}");
            PlayEffect(CombatSoundRules.CreatureEffect(o.Reaction, victim.Humanoid, victim.Animator != null ? victim.Animator.Rig : null),
                       at, "creature");
            var line = CombatSoundRules.VictimLine(o.Reaction, victim.Humanoid);
            if (line != CombatLine.None) Say(victim, line == CombatLine.Aargh ? _painCries.Next(_random) : line.ToString());
        }

        /// <summary>The attacker's "FinishingMove" line (ZS_Attack_FinishingAttack), for the finishing move's view.</summary>
        public void SayFinishingMove(ActorId attacker)
        {
            if (_presenter != null && _presenter.TryGet(attacker, out var f) && f.Humanoid) Say(f, CombatLine.FinishingMove.ToString());
        }

        void PlayEffect(string effect, Vector3 at, string why, Transform follow = null)
        {
            if (effect == null) return;
            bool played = G3EffectSounds.Play(effect, at, follow);
            if (log) Debug.Log($"[CombatSounds] {why}: {effect}{(played ? "" : " (silent)")}");
        }

        // ------------------------------------------------------------------------------------------- draw, bow, arrows

        void OnCue(G3CombatAnimator animator, G3CombatCue cue)
        {
            var f = FighterOf(animator);
            if (f == null) return;
            Vector3 hand = f.transform.position + Vector3.up * 1.2f;
            switch (cue)
            {
                case G3CombatCue.WeaponDrawn:
                case G3CombatCue.WeaponSheathed:
                    var item = f.WeaponItem;
                    PlayEffect(CombatSoundRules.DrawEffect(item?.Damage?.Type, item?.Kind, cue == G3CombatCue.WeaponDrawn), hand,
                               $"{cue} {f.Actor} {item?.Id ?? "fists"}", f.transform);
                    // _AI_HoldInventoryItems sounds per hand item: a dual wielder's second blade too (fix-32; the Hold_End
                    // motion's own draw/undraw track plays on top, as in G3).
                    var off = f.OffHandItem;
                    if (off != null && G3CombatMotions.IsDualOffHand(animator.OffHand))
                        PlayEffect(CombatSoundRules.DrawEffect(off.Damage?.Type, off.Kind, cue == G3CombatCue.WeaponDrawn), hand,
                                   $"{cue} {f.Actor} {off.Id} (left)", f.transform);
                    if (cue == G3CombatCue.WeaponDrawn) Preload(f);
                    break;
                case G3CombatCue.LeftDrawn:
                case G3CombatCue.LeftSheathed:
                    // The left blade alone (HoldLeft, the off-hand weapon's quick-slot key).
                    var left = f.OffHandItem;
                    if (left != null)
                        PlayEffect(CombatSoundRules.DrawEffect(left.Damage?.Type, left.Kind, cue == G3CombatCue.LeftDrawn), hand,
                                   $"{cue} {f.Actor} {left.Id}", f.transform);
                    break;
                case G3CombatCue.AimRaised:
                    if (f.WeaponClass != WeaponClass.Bow) break; // the string sound is the bow's (PS_Ranged_PowerAim)
                    StopBowString(f);
                    _bowString[f] = G3EffectSounds.Start(CombatSoundRules.BowRaiseEffect, hand, f.transform);
                    if (log) Debug.Log($"[CombatSounds] aim {f.Actor}: {CombatSoundRules.BowRaiseEffect}");
                    break;
                case G3CombatCue.ShotReleased:
                    StopBowString(f);
                    PlayEffect(CombatSoundRules.ShootEffect, hand, $"shot {f.Actor}", f.transform);
                    break;
            }
        }

        void StopBowString(CombatFighter f)
        {
            if (_bowString.TryGetValue(f, out int handle)) G3EffectSounds.Stop(handle);
            _bowString.Remove(f);
        }

        CombatFighter FighterOf(G3CombatAnimator animator)
        {
            if (_fighterOf.TryGetValue(animator, out var f) && f != null) return f;
            f = animator.GetComponentInParent<CombatFighter>();
            if (f == null && _player != null && animator.transform.IsChildOf(_player.transform)) f = _player.GetComponentInChildren<CombatFighter>();
            if (f != null) _fighterOf[animator] = f;
            return f;
        }

        /// <summary>Weapon-in-level touches heard since the domain loaded (scenario condition <c>levelhits</c>).</summary>
        public static int LevelTouches;

        /// <summary>G3 OnItemTouchesLevel for a held weapon (Docs/combat.md section 13).</summary>
        void OnWeaponTouchedLevel(CombatFighter fighter, MeleeBlades blade, RaycastHit hit)
        {
            if (fighter == null || _player == null) return;
            if (!fighter.Actor.IsPlayer && (fighter.transform.position - _player.transform.position).sqrMagnitude >
                CombatSoundRules.LevelTouchSoundRange * CombatSoundRules.LevelTouchSoundRange) return;
            string surface = SurfaceProbe.Classify(hit, out _);
            bool metal = CombatSoundRules.IsMetal(fighter.BladeItem(blade));
            LevelTouches++;
            PlayEffect(CombatSoundRules.WeaponHitsLevelEffect(metal, surface, G3EffectSounds.Has), hit.point, "weapon in " + (surface ?? "?"));
        }

        void OnProjectileHitLevel(Projectile p, RaycastHit hit)
        {
            var shot = p.Info;
            if (shot == null || shot.IsSpell) return;
            var shooter = shot.Shooter;
            if (shooter != null && !shooter.Actor.IsPlayer && _player != null &&
                (shooter.transform.position - _player.transform.position).sqrMagnitude > NpcArrowSoundRange * NpcArrowSoundRange) return;
            string surface = SurfaceProbe.Classify(hit, out _);
            PlayEffect(CombatSoundRules.ArrowImpactEffect(surface, G3EffectSounds.Has), hit.point, $"arrow in {surface ?? "?"}");
        }

        // ------------------------------------------------------------------------------------------- voice lines

        string VoiceSetOf(CombatFighter f)
        {
            if (f.Actor.IsPlayer) return "Player";
            return _session != null && _session.Content.Database.TryGet(f.Actor.Name, out NpcDef def) ? def.VoiceSet : null;
        }

        void Say(CombatFighter f, string line)
        {
            string key = CombatSoundRules.ClipKey(VoiceSetOf(f), line);
            if (key == null || _voices == null || !_voices.TryResolve(key, out var voice))
            {
                if (log) Debug.Log($"[CombatSounds] {f.Actor} says {line}: no clip ({key ?? "no voice set"})");
                return;
            }
            string path = _catalog.PathOf(voice);
            if (log) Debug.Log($"[CombatSounds] {f.Actor} says {voice.Language}/{key}");
            if (_clips.TryGetValue(path, out var clip)) PlayVoice(f, clip);
            else StartCoroutine(LoadAndSay(f, path, Time.time));
        }

        void PlayVoice(CombatFighter f, AudioClip clip)
        {
            if (clip == null || f == null) return;
            if (_speaking.TryGetValue(f.Actor, out int previous)) G3SoundPool.Instance.Stop(previous); // one cry at a time
            Vector3 head = f.Body != null ? f.Body.Center + Vector3.up * 0.6f : f.transform.position + Vector3.up * 1.7f;
            if (!G3SoundPool.Audible(head, VoiceMaxDistance)) return;
            _speaking[f.Actor] = G3SoundPool.Instance.Play(clip, head, f.transform, G3SoundPool.VoiceVolume, VoiceMinDistance, VoiceMaxDistance, G3AudioBus.Voice);
        }

        /// <summary>Loads the fighter's combat lines ahead of the first hit (when it draws).</summary>
        void Preload(CombatFighter f)
        {
            if (!f.Humanoid || _voices == null) return;
            string set = VoiceSetOf(f);
            if (set == null) return;
            foreach (var line in CombatSoundRules.SvmNames)
                if (_voices.TryResolve(CombatSoundRules.ClipKey(set, line), out var voice))
                {
                    string path = _catalog.PathOf(voice);
                    if (!_clips.ContainsKey(path) && !_loading.Contains(path)) StartCoroutine(LoadAndSay(null, path, Time.time));
                }
        }

        IEnumerator LoadAndSay(CombatFighter f, string path, float asked)
        {
            if (_loading.Contains(path))
            {
                while (_loading.Contains(path)) yield return null;
            }
            else
            {
                _loading.Add(path);
                using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS))
                {
                    yield return request.SendWebRequest();
                    AudioClip clip = null;
                    if (request.result == UnityWebRequest.Result.Success) clip = DownloadHandlerAudioClip.GetContent(request);
                    else Debug.LogWarning($"[CombatSounds] voice clip {path}: {request.error}");
                    _clips[path] = clip;
                }
                _loading.Remove(path);
            }
            if (f == null || Time.time - asked > StaleVoiceSeconds) yield break;
            if (_clips.TryGetValue(path, out var loaded) && loaded != null)
            {
                while (loaded.loadState == AudioDataLoadState.Loading) yield return null;
                PlayVoice(f, loaded);
            }
        }
    }
}
