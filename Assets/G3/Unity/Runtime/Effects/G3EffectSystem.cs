using System;
using System.Collections.Generic;
using UnityEngine;
using G3.Effects;

namespace G3.Unity
{
    /// <summary>
    /// Plays G3 effects (g3.efm, e.g. <c>eff_magic_fireball_hand_01</c>) by name: the visuals of every spawned template
    /// (pooled <see cref="G3Vfx"/> instances: particles, lights) and, when asked, the sound through <see cref="G3EffectSounds"/>.
    /// Every effect started through <see cref="G3EffectSounds"/> (hits, impacts, casts, footsteps …) also shows its visuals,
    /// so existing callers need no change; callers that must stop a looping effect (the spell in the hand) use
    /// <see cref="Start"/> / <see cref="Stop"/>. Docs/formats-effects.md §6.
    /// </summary>
    public static class G3EffectPlayer
    {
        /// <summary>Fire-and-forget effects without a target: looping parts stop emitting after this many seconds [interp].</summary>
        public const float LooseLoopSeconds = 2.5f;
        /// <summary>… with a target (a missile, a hit body): until the target goes away, at most this long [interp].</summary>
        public const float FollowLoopSeconds = 12f;
        /// <summary>Effects are not started farther than this from the camera (sounds stop at 40 m).</summary>
        public const float SpawnDistance = 70f;

        public static bool LogPlays;
        static bool s_fromPlayer;

        /// <summary>Whether visuals for <paramref name="effect"/> were imported.</summary>
        public static bool Has(string effect) => G3EffectSystem.Library?.Get(effect) is { } e && e.spawns.Count > 0;

        /// <summary>
        /// Starts an effect and returns a handle for <see cref="Stop"/> (0 = nothing visual started). Attached parts (link
        /// TargetEntity/TargetBone) follow <paramref name="follow"/> and stop when it is destroyed or disabled.
        /// </summary>
        public static int Start(string effect, Vector3 position, Quaternion rotation, Transform follow = null, bool withSound = true)
            => Start(effect, position, rotation, follow, withSound, null);

        /// <summary>
        /// As <see cref="Start(string, Vector3, Quaternion, Transform, bool)"/>, each part drawn with the look
        /// <paramref name="look"/> gives for its template name (fix-196: Spl_Light's orb, <see cref="G3.Effects.LightOrbRules"/>);
        /// null = the imported look.
        /// </summary>
        public static int Start(string effect, Vector3 position, Quaternion rotation, Transform follow, bool withSound,
                                Func<string, G3.Effects.EffectPartLook> look)
        {
            if (withSound) SoundWithoutVisuals(effect, position, follow);
            var sys = G3EffectSystem.Instance;
            return sys != null ? sys.Spawn(effect, position, rotation, follow, 0f, look) : 0;
        }

        /// <summary>Start on a transform (its position and rotation).</summary>
        public static int Start(string effect, Transform on, bool withSound = true) =>
            on == null ? 0 : Start(effect, on.position, on.rotation, on, withSound);

        /// <summary>
        /// A screen part of a G3 effect (VFX_*_Screen*, VFX_*_WhiteBlend*): drawn over the view by
        /// <see cref="G3ScreenOverlay"/>, never as world particles (G3.Effects.ScreenSpriteRules).
        /// </summary>
        public static bool IsScreenPart(UnityEngine.Object prefab) =>
            prefab != null && G3.Effects.ScreenSpriteRules.IsScreenTemplate(prefab.name);

        /// <summary>
        /// Plays an effect once; looping parts end with the target or after <see cref="LooseLoopSeconds"/>. Returns the
        /// handle (0 = nothing visual started), for probes only (<see cref="PartsOf"/>).
        /// </summary>
        public static int Play(string effect, Vector3 position, Transform follow = null, bool withSound = true)
        {
            if (withSound) SoundWithoutVisuals(effect, position, follow);
            var sys = G3EffectSystem.Instance;
            return sys != null ? sys.Spawn(effect, position, follow != null ? follow.rotation : Quaternion.identity, follow, follow != null ? FollowLoopSeconds : LooseLoopSeconds) : 0;
        }

        /// <summary>Live parts of a started effect (0 when it is gone or never started; fix-176 probe).</summary>
        public static int PartsOf(int handle) => G3EffectSystem.InstanceIfAny is { } s ? s.PartsOf(handle) : 0;

        /// <summary>Stops a started effect: no new particles; it is gone when the last particle died.</summary>
        public static void Stop(int handle) => G3EffectSystem.InstanceIfAny?.Stop(handle);

        /// <summary>
        /// Stops a started effect; one that has screen parts (<see cref="IsScreenPart"/>) is removed at once, particles and
        /// all: G3 ends the screen tint with the effect (the soul travel load at the release, the ghost form at its end)
        /// instead of letting its sprites live out their lifetime over the view.
        /// </summary>
        public static void End(int handle) => G3EffectSystem.InstanceIfAny?.Stop(handle, killWithScreenParts: true);

        /// <summary>
        /// Removes a started effect at once, particles and all (fix-124): an item's effect when the item entity leaves the
        /// world (the torch put away, a weapon picked up). G3 deletes the item entity and the effect entities it spawned with
        /// it [interp: SpawnEntity AutoKill]; letting the torch's BigGlow halo (2-3 s sprites) live out looked like a slow fade.
        /// </summary>
        public static void Kill(int handle) => G3EffectSystem.InstanceIfAny?.Stop(handle, killAll: true);

        /// <summary>
        /// fix-182: the torch halo part (<see cref="EmitterRules.IsTorchGlow"/>, VFX_Env_Fire_Torch_Glow_01 of eff_item_torch_02)
        /// is shown. true = G3; the port option "Torch glow" sets it ([owner] default off). Off: the part is not spawned and
        /// live ones are cleared at once; the flame, sparks, smoke and the torch's light stay. Turned on, a torch already
        /// burning gets its halo when it is lit again.
        /// </summary>
        public static bool TorchGlow { get; set; } = true;

        /// <summary>The started effect still has parts (or parts to come).</summary>
        public static bool IsLive(int handle) => handle != 0 && G3EffectSystem.InstanceIfAny is { } s && s.IsLive(handle);

        static void SoundWithoutVisuals(string effect, Vector3 position, Transform follow)
        {
            s_fromPlayer = true;
            try { G3EffectSounds.Play(effect, position, follow); }
            finally { s_fromPlayer = false; }
        }

        /// <summary>Every sound effect started elsewhere shows its visuals too (hits: blood and sparks; impacts; casts).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Hook()
        {
            G3EffectSounds.Requested -= OnSoundRequested;
            G3EffectSounds.Requested += OnSoundRequested;
            Characters.G3MotionPlayer.AnyEffectEvent -= OnMotionEffect;
            Characters.G3MotionPlayer.AnyEffectEvent += OnMotionEffect;
        }

        /// <summary>Effect tracks of motions (formats-animation.md §8.7): the sound is G3MotionSounds', the visuals are ours.</summary>
        static void OnMotionEffect(Characters.G3MotionPlayer player, string effect)
        {
            if (player == null || !Has(effect)) return;
            var t = player.transform;
            Play(effect, t.position, t, withSound: false);
        }

        static void OnSoundRequested(string effect, Vector3 position, Transform follow)
        {
            if (s_fromPlayer) return;
            Play(effect, position, follow, withSound: false);
        }
    }

    /// <summary>
    /// The pooled effect runtime behind <see cref="G3EffectPlayer"/>: one object, one Update for every live effect part,
    /// distance culling and a cap on live parts. Created on first use when the imported <see cref="G3EffectLibrary"/> exists.
    /// </summary>
    [DefaultExecutionOrder(500)]
    public sealed class G3EffectSystem : MonoBehaviour
    {
        [Tooltip("Live effect parts at most; new effects beyond are skipped (logged once).")] public int maxParts = 384;
        [Tooltip("Parts farther than this from the camera emit nothing and their lights are off.")] public float cullDistance = 90f;
        [Tooltip("fix-58: parts kept free for gameplay effects (hits, sparks, blood, casts): world-placed ambient effects start only below maxParts minus this [interp].")]
        public int gameplayReserve = 64;

        /// <summary>A world-placed ambient effect may start (the gameplay reserve stays free).</summary>
        public bool HasAmbientRoom => _partCount < maxParts - Mathf.Clamp(gameplayReserve, 0, maxParts);

        static G3EffectSystem s_instance;
        static G3EffectLibrary s_library;
        static bool s_libraryLoaded;

        // Domain reload is off in the editor: a library missing in an earlier Play (or before the import) is looked up again.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetLibrary()
        {
            s_libraryLoaded = false;
            s_library = null;
        }

        public static G3EffectLibrary Library
        {
            get
            {
                if (!s_libraryLoaded)
                {
                    s_libraryLoaded = true;
                    s_library = Resources.Load<G3EffectLibrary>(G3EffectLibrary.ResourcePath);
                    if (s_library == null) Debug.Log("[G3Effects] no effect library (run G3/Effects/Import Effects); visual effects are off");
                }
                return s_library;
            }
        }

        public static G3EffectSystem InstanceIfAny => s_instance;

        /// <summary>The runtime (created on first use; null without an imported library or outside Play mode).</summary>
        public static G3EffectSystem Instance
        {
            get
            {
                if (s_instance != null) return s_instance;
                if (!Application.isPlaying || Library == null) return null;
                s_instance = new GameObject("G3 Effects").AddComponent<G3EffectSystem>();
                return s_instance;
            }
        }

        sealed class Live
        {
            public int handle;
            public readonly List<G3Vfx> parts = new List<G3Vfx>();
            public readonly List<(float at, G3EffectSpawnData spawn)> pending = new List<(float, G3EffectSpawnData)>();
            public Vector3 position;
            public Quaternion rotation;
            public Transform follow;
            public bool followSet, stopped;
            public float age, maxSeconds;
            public string effect;
            public Func<string, G3.Effects.EffectPartLook> look;
        }

        readonly Dictionary<G3Vfx, Stack<G3Vfx>> _pool = new Dictionary<G3Vfx, Stack<G3Vfx>>();
        readonly Dictionary<int, Live> _live = new Dictionary<int, Live>();
        readonly List<Live> _liveList = new List<Live>();
        readonly Dictionary<(Transform, string), Transform> _bones = new Dictionary<(Transform, string), Transform>();
        int _nextHandle = 1, _partCount;
        bool _capLogged;
        Transform _camera;

        public int LiveEffects => _liveList.Count;
        public int LiveParts => _partCount;

        void OnDestroy()
        {
            _overlay.Dispose();
            if (s_instance == this) s_instance = null;
        }

        internal int Spawn(string effect, Vector3 position, Quaternion rotation, Transform follow, float maxSeconds,
                           Func<string, G3.Effects.EffectPartLook> look = null)
        {
            var entry = Library?.Get(effect);
            if (entry == null || entry.spawns.Count == 0) return 0;
            var cam = Cam();
            if (cam != null && (cam.position - position).sqrMagnitude > G3EffectPlayer.SpawnDistance * G3EffectPlayer.SpawnDistance) return 0;
            if (_partCount >= maxParts)
            {
                // fix-58: who holds the budget, once per episode (logged again after the parts fell below 3/4)
                if (!_capLogged) Debug.LogWarning($"[G3Effects] {maxParts} live effect parts: skipping new effects ({effect})\n{Report(15)}");
                _capLogged = true;
                return 0;
            }
            var live = new Live
            {
                handle = _nextHandle++, position = position, rotation = rotation, follow = follow, followSet = follow != null,
                maxSeconds = maxSeconds, effect = effect, look = look,
            };
            foreach (var s in entry.spawns)
            {
                if (s.prefab == null || Hidden(s.prefab)) continue;
                if (s.timeOffset > 0) live.pending.Add((s.timeOffset, s));
                else SpawnPart(live, s);
            }
            if (G3EffectPlayer.LogPlays) Debug.Log($"[G3Effects] {effect} #{live.handle}: {live.parts.Count} parts{(follow != null ? " on " + follow.name : "")}");
            if (live.parts.Count == 0 && live.pending.Count == 0) return 0;
            _live[live.handle] = live;
            _liveList.Add(live);
            return live.handle;
        }

        /// <summary>One imported template (a world-placed particle entity) until stopped; 0 when unknown or over the cap.</summary>
        public int SpawnTemplate(string template, Vector3 position, Quaternion rotation)
        {
            var prefab = Library?.Template(template);
            if (prefab == null || Hidden(prefab) || !HasAmbientRoom) return 0;
            var live = new Live { handle = _nextHandle++, position = position, rotation = rotation, effect = template };
            SpawnPart(live, new G3EffectSpawnData { prefab = prefab });
            _live[live.handle] = live;
            _liveList.Add(live);
            return live.handle;
        }

        internal bool IsLive(int handle) => _live.ContainsKey(handle);
        internal int PartsOf(int handle) => _live.TryGetValue(handle, out var l) ? l.parts.Count : 0;

        /// <summary>
        /// fix-58: the live effects grouped by name (dev command <c>fx</c>; logged when the budget is full): effects, parts,
        /// stopped, endless (no time limit, no target: only their caller's Stop ends them), target gone, oldest age.
        /// </summary>
        public string Report(int top = 30)
        {
            var infos = new List<G3.Effects.LiveEffectInfo>(_liveList.Count);
            foreach (var l in _liveList)
                infos.Add(new G3.Effects.LiveEffectInfo(l.effect, l.parts.Count, l.pending.Count, l.stopped, l.followSet,
                                                        l.followSet && (l.follow == null || !l.follow.gameObject.activeInHierarchy), l.age, l.maxSeconds));
            return G3.Effects.EffectBudgetReport.Build(infos, maxParts, top);
        }

        /// <summary>The report of the running effect system, or a note when there is none.</summary>
        public static string ReportOrNone(int top = 30) => s_instance != null ? s_instance.Report(top) : "effects: no effect system running";

        internal void Stop(int handle, bool killWithScreenParts = false, bool killAll = false)
        {
            if (!_live.TryGetValue(handle, out var live)) return;
            live.stopped = true;
            live.pending.Clear();
            bool kill = killAll;
            if (killWithScreenParts)
                foreach (var p in live.parts) kill |= G3EffectPlayer.IsScreenPart(p.prefab);
            foreach (var p in live.parts)
            {
                p.StopEmitting();
                if (kill) p.ClearParticles();
            }
        }

        /// <summary>fix-182: a part the options hide (the torch halo without <see cref="G3EffectPlayer.TorchGlow"/>).</summary>
        static bool Hidden(G3Vfx prefab) => !G3EffectPlayer.TorchGlow && prefab != null && EmitterRules.IsTorchGlow(prefab.templateName);

        void SpawnPart(Live live, G3EffectSpawnData s)
        {
            var vfx = Acquire(s.prefab);
            vfx.ApplyLook(live.look != null ? live.look(vfx.templateName) : G3.Effects.EffectPartLook.Default);
            var offset = new Vector3(Mathf.Lerp(s.offsetMin.x, s.offsetMax.x, UnityEngine.Random.value),
                                     Mathf.Lerp(s.offsetMin.y, s.offsetMax.y, UnityEngine.Random.value),
                                     Mathf.Lerp(s.offsetMin.z, s.offsetMax.z, UnityEngine.Random.value));
            Transform anchor = null;
            if (live.follow != null && s.link != 0)
                anchor = s.link == 2 && !string.IsNullOrEmpty(s.bone) ? Bone(live.follow, s.bone) : live.follow;
            vfx.follow = anchor;
            vfx.followSet = anchor != null;
            if (anchor != null)
            {
                // keep where the effect was asked to play relative to the anchor (a hit point on a body)
                var local = Quaternion.Inverse(anchor.rotation) * (live.position - anchor.position);
                vfx.followOffset = (s.link == 2 ? Vector3.zero : local) + offset;
                vfx.position = anchor.TransformPoint(vfx.followOffset);
                vfx.transform.SetPositionAndRotation(vfx.position, anchor.rotation);
            }
            else
            {
                vfx.position = live.position + live.rotation * offset;
                vfx.transform.SetPositionAndRotation(vfx.position, live.rotation);
            }
            // Fire-and-forget effects: looping parts end after maxSeconds (with a target: when it goes away).
            vfx.maxSeconds = live.maxSeconds;
            vfx.tickDebt = 0f;
            vfx.tickPhase = _nextTickPhase++ & 1;
            vfx.Begin();
            if (live.stopped) vfx.StopEmitting();
            live.parts.Add(vfx);
        }

        G3Vfx Acquire(G3Vfx prefab)
        {
            if (!_pool.TryGetValue(prefab, out var stack)) _pool[prefab] = stack = new Stack<G3Vfx>();
            G3Vfx v;
            if (stack.Count > 0) v = stack.Pop();
            else
            {
                v = Instantiate(prefab, transform);
                v.prefab = prefab;
                v.screen = G3EffectPlayer.IsScreenPart(prefab);
                v.torchGlow = EmitterRules.IsTorchGlow(prefab.templateName);
                v.heatHaze = v.DrawsHeatHaze();
                KillStoppedStretchedDrops(v);
            }
            v.gameObject.SetActive(true);
            _partCount++;
            return v;
        }

        /// <summary>
        /// fix-132: stretched drops that stop on the ground die there (<see cref="EmitterRules.DiesAtHit"/>); also for
        /// effect prefabs imported before the importer set it.
        /// </summary>
        static void KillStoppedStretchedDrops(G3Vfx v)
        {
            var ps = v.system;
            if (ps == null) return;
            var col = ps.collision;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            if (r == null) return;
            if (EmitterRules.DiesAtHit(r.renderMode == ParticleSystemRenderMode.Stretch, col.enabled, col.bounce.constant,
                                       col.dampen.constant))
                col.lifetimeLoss = 1f;
        }

        void Release(G3Vfx v)
        {
            _partCount--;
            v.follow = null;
            v.followSet = false;
            v.ClearParticles();
            v.ApplyLook(G3.Effects.EffectPartLook.Default); // fix-196: a pooled part shared by other effects comes back as imported
            v.gameObject.SetActive(false);
            if (v.prefab != null) _pool[v.prefab].Push(v);
            else Destroy(v.gameObject);
        }

        /// <summary>
        /// perf-3: parts farther than this from the camera (m) tick every 2nd frame with the time of both (emission counts and
        /// ages stay the same; a far fire's particles come in pairs of frames, its light flickers at half the frame rate).
        /// At Faring 260 parts of 111 torches / fires ticked every frame: 0.46 ms in the editor.
        /// </summary>
        public const float HalfRateDistance = 25f;
        int _nextTickPhase;

        void Update()
        {
            float dt = Time.deltaTime;
            var cam = Cam();
            Vector3 camPos = cam != null ? cam.position : Vector3.zero;
            float cull2 = cullDistance * cullDistance;
            float hazeCull = EmitterRules.PartCullDistance(true, cullDistance), hazeCull2 = hazeCull * hazeCull;
            int parity = Time.frameCount & 1;
            if (_capLogged && _partCount < maxParts * 3 / 4) _capLogged = false;
            for (int i = _liveList.Count - 1; i >= 0; i--)
            {
                var live = _liveList[i];
                live.age += dt;
                bool targetGone = live.followSet && (live.follow == null || !live.follow.gameObject.activeInHierarchy);
                if (targetGone && !live.stopped) Stop(live.handle);
                if (live.maxSeconds > 0 && live.age > live.maxSeconds && !live.stopped)
                    foreach (var p in live.parts) if (p.maxSeconds > 0) p.StopEmitting();
                for (int k = live.pending.Count - 1; k >= 0; k--)
                    if (live.age >= live.pending[k].at)
                    {
                        var s = live.pending[k].spawn;
                        live.pending.RemoveAt(k);
                        if (!live.stopped && !Hidden(s.prefab)) SpawnPart(live, s);
                    }
                for (int k = live.parts.Count - 1; k >= 0; k--)
                {
                    var p = live.parts[k];
                    if (p.torchGlow && !G3EffectPlayer.TorchGlow)
                    {
                        // fix-182: the option turned off under a burning torch: the halo goes at once
                        live.parts.RemoveAt(k);
                        Release(p);
                        continue;
                    }
                    if (p.followSet)
                    {
                        if (p.follow != null)
                        {
                            p.position = p.follow.TransformPoint(p.followOffset);
                            p.transform.SetPositionAndRotation(p.position, p.follow.rotation);
                        }
                        else p.StopEmitting();
                    }
                    // Unattached parts never move: their spawn position is kept (no transform read per part per frame, perf-8).
                    float d2 = cam == null ? 0f : (p.position - camPos).sqrMagnitude;
                    bool near = cam == null || d2 < (p.heatHaze ? hazeCull2 : cull2);
                    float step = dt + p.tickDebt;
                    if (d2 >= HalfRateDistance * HalfRateDistance && p.tickPhase != parity && !p.screen) p.tickDebt = step; // the other frame
                    else
                    {
                        p.tickDebt = 0f;
                        p.Tick(step, near);
                    }
                    if (p.Finished && p.Age > 0.05f)
                    {
                        live.parts.RemoveAt(k);
                        Release(p);
                    }
                }
                if (live.parts.Count == 0 && live.pending.Count == 0)
                {
                    _live.Remove(live.handle);
                    _liveList.RemoveAt(i);
                }
            }
        }

        // Screen parts are drawn over the view after every part moved this frame.
        void LateUpdate()
        {
            var cam = Cam();
            _overlay.Draw(cam != null ? cam.GetComponent<Camera>() : null, _liveList.Count == 0 ? null : EnumerateParts());
        }

        readonly G3ScreenOverlay _overlay = new G3ScreenOverlay();
        readonly List<G3Vfx> _screenParts = new List<G3Vfx>();

        List<G3Vfx> EnumerateParts()
        {
            _screenParts.Clear();
            foreach (var live in _liveList)
                foreach (var p in live.parts)
                    if (p.screen && p.ScreenSprites.Count > 0) _screenParts.Add(p);
            return _screenParts;
        }

        Transform Bone(Transform root, string bone)
        {
            var key = (root, bone);
            if (_bones.TryGetValue(key, out var t) && t != null) return t;
            t = FindDeep(root, bone) ?? FindBySuffix(root, BoneSuffix(bone)) ?? root;
            _bones[key] = t;
            return t;
        }

        /// <summary>
        /// g3.efm names Hero bones ("Hero_Right_Leg_Leg_2"); other rigs share the part after the rig prefix
        /// ("Wolf_Right_Leg_Leg_2" …) [interp: G3 resolves the bone on the entity's own skeleton].
        /// </summary>
        static string BoneSuffix(string bone)
        {
            int i = bone.IndexOf('_');
            return i > 0 && i < bone.Length - 1 ? bone.Substring(i) : null;
        }

        static Transform FindBySuffix(Transform t, string suffix)
        {
            if (suffix == null) return null;
            if (t.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && !t.name.StartsWith("Slot_", StringComparison.Ordinal)) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindBySuffix(t.GetChild(i), suffix);
                if (r != null) return r;
            }
            return null;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase)) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        Transform Cam()
        {
            if (_camera == null && Camera.main != null) _camera = Camera.main.transform;
            return _camera;
        }
    }
}
