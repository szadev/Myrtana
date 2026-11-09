using System.Collections.Generic;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The game's music situation for the G3 music director (<see cref="G3MusicDirector.GameSituation"/>): once per real second it feeds the session (player
    /// vitals, NPC poses, combat brains, enclave states, party) into <see cref="MusicSituationRule"/> (G3's
    /// HandleMusicSituation) and answers fight / showdown / revolution / revolutionwon / dead / explore. Asked after
    /// the scene's <see cref="G3MusicSituationOverride"/>, so story music (PlayMusic) and the title music win.
    /// A change refreshes the audio context at once (fight music starts immediately in G3).
    /// <para>Installs itself after the first scene load and finds the session through <see cref="GameBootstrap"/>
    /// until the bootstrap binds it (<see cref="Bind"/>).</para>
    /// </summary>
    [AddComponentMenu("Myrtana/Session Music Situation")]
    public sealed class SessionMusicSituation : MonoBehaviour, ISessionView
    {
        const float IntervalSeconds = 1f;

        [Tooltip("Log each situation change (Console: [Music]).")]
        public bool logChanges = true;
        [SerializeField] string current = "";

        readonly MusicSituationRule _idle = new MusicSituationRule();
        readonly List<MusicNpc> _npcs = new List<MusicNpc>();
        GameSession _session;
        bool _bound;
        GameBootstrap _bootstrap;
        G3Player _player;
        float _next;

        /// <summary>The answer for <see cref="G3MusicDirector.GameSituation"/> (null without a session).</summary>
        public string MusicSituation => isActiveAndEnabled && _session != null ? Rule.Current : null;

        /// <summary>The session's rule (its victory-played enclaves are saved with the game, section "world.music").</summary>
        public MusicSituationRule Rule => _session?.Music ?? _idle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindAnyObjectByType<SessionMusicSituation>() != null) return;
            var go = new GameObject("Music situation");
            DontDestroyOnLoad(go);
            go.AddComponent<SessionMusicSituation>();
            go.AddComponent<WorldSoundMute>();
        }

        public void Bind(GameSession session)
        {
            _session = session;
            _bound = true;
        }

        void OnDisable()
        {
            if (G3MusicDirector.GameSituation != null) G3MusicDirector.GameSituation = null;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + IntervalSeconds;

            if (!_bound)
            {
                if (_bootstrap == null) _bootstrap = FindAnyObjectByType<GameBootstrap>();
                _session = _bootstrap != null ? _bootstrap.Session : null;
            }
            if (_session == null) return;
            G3MusicDirector.GameSituation = () => MusicSituation;   // a static slot: cheap to re-assign, survives domain reloads
            if (_player == null) _player = FindAnyObjectByType<G3Player>();
            if (_player == null) return;

            string before = Rule.Current;
            bool menuOpen = GamePause.IsPaused || LoadingHold.IsHeld;
            bool dead = _session.Combat.Vitals.IsDead(ActorId.Player);
            Rule.HeroLevel = _session.Hero.Level; // the port's threat option (PortOptions.CombatMusicThreat)
            string situation = Rule.Evaluate(menuOpen, dead, false, Collect(_player.transform.position), IsPartyMember);
            current = situation;
            if (situation == before) return;
            if (logChanges) Debug.Log($"[Music] situation {before} -> {situation}");
            var context = FindAnyObjectByType<G3AudioContext>();
            if (context != null && context.isActiveAndEnabled) context.Refresh();
        }

        /// <summary>An attacker this close (m) always perceives the hero; farther it needs a free line of sight (fix-204).</summary>
        const float PerceiveCloseMetres = 10f;
        readonly RaycastLineOfSight _sight = new RaycastLineOfSight();

        /// <summary>
        /// Whether an attacker of the hero perceives him (fix-204, <see cref="MusicNpc.Perceives"/>): close, or a free line of
        /// sight. Only asked while no fight music plays (the rule ignores the answer once it does).
        /// </summary>
        bool Perceives(WorldPose npc, Vector3 hero, float sqrDistance)
        {
            var current = Rule.Current;
            if (current == MusicSituationRule.Fight || current == MusicSituationRule.Showdown) return true;
            if (sqrDistance <= PerceiveCloseMetres * PerceiveCloseMetres) return true;
            return _sight.IsClear(npc, new WorldPose(hero.x, hero.y, hero.z, 0));
        }

        List<MusicNpc> Collect(Vector3 player)
        {
            _npcs.Clear();
            var sim = _session.Npcs;
            foreach (var agent in sim.Agents)
            {
                if (!sim.IsInWorld(agent)) continue;
                var pose = sim.PoseOf(agent);
                float dx = pose.X - player.x, dy = pose.Y - player.y, dz = pose.Z - player.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > MusicSituationRule.RadiusMetres * MusicSituationRule.RadiusMetres) continue;

                var brain = _session.NpcCombat.Get(agent.Id);
                bool inCombat = brain != null && brain.IsFighting && brain.State.Phase != CombatPhase.Flee;
                string enclave = agent.Definition.Combat != null && agent.Definition.Combat.HasEnclave ? agent.Definition.Combat.Enclave : null;
                var status = EnclaveMusicStatus.None;
                if (enclave != null)
                {
                    var s = _session.Enclaves.Of(enclave).Status;
                    status = s == EnclaveStatus.Attack ? EnclaveMusicStatus.Attack
                        : s == EnclaveStatus.Flee ? EnclaveMusicStatus.Liberated : EnclaveMusicStatus.None;
                }
                bool perceives = !inCombat || !brain.State.Target.IsPlayer || Perceives(pose, player, d2);
                _npcs.Add(new MusicNpc(agent.Id, Mathf.Sqrt(d2), inCombat, inCombat ? brain.State.Target : default, enclave, status,
                                       inCombat ? LevelOf(agent.Definition.Combat) : MusicNpc.UnknownLevel, perceives));
            }
            return _npcs;
        }

        /// <summary>G3 CurrentLevel of an NPC (ambient creatures 0), for the threat option.</summary>
        int LevelOf(NpcCombatDef combat)
        {
            if (combat == null) return MusicNpc.UnknownLevel;
            if (combat.AmbientCreature) return 0;
            return CombatStats.NpcCurrentLevel(combat.Level, combat.LevelMax, _session.Hero.Level, _session.Settings.Rules.Difficulty);
        }

        bool IsPartyMember(ActorId actor)
        {
            foreach (var m in _session.Party.Members.Members)
                if (m.Id.Equals(actor)) return true;
            return false;
        }
    }
}
