using System;
using System.Collections.Generic;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero's other bodies (Docs/combat.md §12.5; the rules are <see cref="HeroForms"/>), on the player object:
    /// <list type="bullet">
    /// <item>Animal form: the hero model is hidden and the form's NPC model (its prefab, motion library, locomotion and
    /// sounds) takes its place under the player controller; the controller runs at the form's gait speeds, the camera
    /// pivot sits at the form's height, and <see cref="PlayerCombat.SwapBody"/> lets the hero fight with the form's
    /// attacks. Back with the Confirm key (G3 default Enter), the timer, or on entering water (ZS_DiveIn / PS_Swim: TransformBack).</item>
    /// <item>Soul travel: the body stays where it was as a still copy; the hero model becomes a pale ghost (alpha 0.2)
    /// moving at 0.55 speed, which no NPC sees, targets or hits. The Confirm key returns the soul: the hero is back at the body.</item>
    /// <item>Dusk to Dawn: the hero stands still while the clock races to the morning.</item>
    /// </list>
    /// </summary>
    public sealed class HeroFormView : MonoBehaviour, ISessionControlHolder
    {
        const float Cm = 100f;

        PlayerCombat _combat;
        HeroControlLock _controls;
        CombatPresenter _presenter;
        G3Player _player;
        G3PlayerCamera _camera;
        GameSession _session;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        Transform _heroVisual;
        GameObject _form, _body;
        Vector3 _bodyFeet;
        float _bodyYaw;
        int _ghostFx;
        readonly Dictionary<Renderer, Material[]> _savedMaterials = new Dictionary<Renderer, Material[]>();
        Material _ghostMaterial;
        float _walk, _run, _sprint, _strafe, _back, _pivot;
        bool _saved, _dusk;

        public void Init(PlayerCombat combat, HeroControlLock controls, CombatPresenter presenter)
        {
            _combat = combat;
            _controls = controls;
            _presenter = presenter;
            _player = GetComponent<G3Player>();
            _camera = _player != null && _player.playerCamera != null ? _player.playerCamera.GetComponent<G3PlayerCamera>() : null;
            var hero = GetComponentInChildren<G3HeroAnimator>(true);
            _heroVisual = hero != null ? hero.transform : null;
        }

        public void Bind(GameSession session)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            RestoreHero(); // a loaded game starts in the hero's own body
            _session = session;
            _subscriptions.Add(session.Events.Subscribe<HeroFormChanged>(OnFormChanged));
            _subscriptions.Add(session.Events.Subscribe<DuskToDawnChanged>(OnDuskToDawn));
        }

        void OnDestroy()
        {
            foreach (var s in _subscriptions) s.Dispose();
            if (_ghostMaterial != null) Destroy(_ghostMaterial);
        }

        void OnFormChanged(HeroFormChanged e)
        {
            Debug.Log($"[Myrtana] hero form {e.From} → {e.Kind}{(e.Form != null ? " " + e.Form.Id : "")}{(e.Seconds != 0 ? $" ({e.Seconds:0} s)" : "")}");
            if (e.From == HeroFormKind.Animal) EndAnimal();
            if (e.From == HeroFormKind.Ghost) EndGhost();
            if (e.Kind == HeroFormKind.Animal) BeginAnimal(e.Form);
            else if (e.Kind == HeroFormKind.Ghost) BeginGhost();
        }

        void Update()
        {
            if (_session == null) return;
            var forms = _session.SpellEffects.Hero;
            if (forms.Kind == HeroFormKind.Hero) return;
            // ZS_DiveIn / PS_Swim: a transformed hero entering water turns back.
            if (forms.Transformed && _player != null && _player.Swimming)
            {
                forms.TransformBack();
                return;
            }
            // OnPlayerConfirm: session key Confirm ("Reconverting", G3 default Enter; E is camera rotation in G3)
            if (G3Input.WasPressed(G3.Player.SessionKey.Confirm) && !_controls.IsLocked && !_session.Dialog.IsActive) forms.Confirm();
        }

        // ------------------------------------------------------------------------------------------- animal form

        void BeginAnimal(NpcDef form)
        {
            var prefab = form != null ? G3NpcPrefabs.Load(form.Visual) : null;
            if (prefab == null || _heroVisual == null)
            {
                Debug.LogWarning($"[Myrtana] animal form {form?.Visual}: no prefab (G3Npc/{form?.Visual}); the hero keeps his model");
                return;
            }
            var holder = new GameObject("Form holder");
            holder.SetActive(false); // set the copy up before it wakes
            _form = Instantiate(prefab, holder.transform);
            _form.name = "Form " + form.Visual;
            var info = _form.GetComponent<G3Character>();
            var library = G3MotionLibraries.Load(info != null ? info.Rig : null);
            if (library != null)
            {
                var legacy = _form.GetComponent<Animation>();
                if (legacy != null) DestroyImmediate(legacy);
                if (info != null) info.AutoPlay = null;
                var mecanim = _form.AddComponent<Animator>();
                mecanim.applyRootMotion = false;
                _form.AddComponent<G3MotionPlayer>().library = library;
                _form.AddComponent<G3NpcAnimator>();
                _form.AddComponent<G3MotionSounds>().AsHero();
            }
            foreach (var c in _form.GetComponentsInChildren<Collider>(true)) if (!c.isTrigger) c.enabled = false; // the controller is the body
            G3ReliefFooting.Attach(_form, hero: true);
            _form.transform.SetParent(_heroVisual.parent, false);
            _form.transform.localPosition = _heroVisual.localPosition;
            _form.transform.localRotation = _heroVisual.localRotation;
            Destroy(holder);
            _form.SetActive(true);
            _heroVisual.gameObject.SetActive(false);

            var def = CombatWiring.CombatDefOf(form);
            float reach = _session.NpcCombat.Rules.Reach.ReachOf(def.Species, true, FighterAction.Attack, _session.Settings.Rules.ExtendedAI) / Cm;
            _combat.SwapBody(_form.transform, def.Humanoid, reach);
            SetSpeeds(_form.GetComponent<G3NpcAnimator>(), Height(_form.transform));
            // The boom counts from the form's rear (a wolf is ~1.5 m long, the hero's capsule 0.6 m wide).
            if (_camera != null) _camera.bodyExtraDistance = Mathf.Clamp(HalfLength(_form.transform) - HeroHalfLength, 0f, 4f);
            G3EffectPlayer.Play("eff_magic_animalform_target_01", transform.position + Vector3.up, transform);
        }

        void EndAnimal()
        {
            if (_form == null) return;
            G3EffectPlayer.Play("eff_magic_animalform_target_01", transform.position + Vector3.up, transform);
            Destroy(_form);
            _form = null;
            if (_heroVisual != null)
            {
                _heroVisual.gameObject.SetActive(true);
                _combat.SwapBody(_heroVisual, true, 0.6f);
            }
            RestoreSpeeds();
        }

        /// <summary>The controller at the form's gait speeds (its walk/run/sprint clips) [interp: G3 moves the form by its motions].</summary>
        void SetSpeeds(G3NpcAnimator animator, float height)
        {
            if (_player == null) return;
            SaveSpeeds();
            if (animator != null)
            {
                _player.walkSpeed = animator.GaitSpeed("Walk", _walk);
                _player.runSpeed = animator.GaitSpeed("Run", _run);
                _player.sprintSpeed = animator.GaitSpeed("Sprint", _player.runSpeed * 1.5f);
                _player.strafeRunSpeed = _player.runSpeed * (_strafe / Mathf.Max(0.1f, _run));
                _player.backRunSpeed = _player.runSpeed * (_back / Mathf.Max(0.1f, _run));
            }
            if (_camera != null && height > 0.1f) _camera.pivotHeight = Mathf.Clamp(height * 1.1f, 0.4f, 3.5f);
        }

        void SaveSpeeds()
        {
            if (_saved || _player == null) return;
            _walk = _player.walkSpeed;
            _run = _player.runSpeed;
            _sprint = _player.sprintSpeed;
            _strafe = _player.strafeRunSpeed;
            _back = _player.backRunSpeed;
            _pivot = _camera != null ? _camera.pivotHeight : 0;
            _saved = true;
        }

        void RestoreSpeeds()
        {
            if (!_saved || _player == null) return;
            _player.walkSpeed = _walk;
            _player.runSpeed = _run;
            _player.sprintSpeed = _sprint;
            _player.strafeRunSpeed = _strafe;
            _player.backRunSpeed = _back;
            if (_camera != null)
            {
                _camera.pivotHeight = _pivot;
                _camera.bodyExtraDistance = 0f;
            }
            _saved = false;
        }

        const float HeroHalfLength = 0.3f;

        /// <summary>How far the form's skeleton reaches from its centre on the ground plane, m (snout or tail).</summary>
        static float HalfLength(Transform visual)
        {
            float reach = 0;
            var c = visual.position;
            foreach (var r in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var bone in r.bones)
                    if (bone != null)
                    {
                        var d = bone.position - c;
                        reach = Mathf.Max(reach, new Vector2(d.x, d.z).magnitude);
                    }
            return reach;
        }

        static float Height(Transform visual)
        {
            float top = 0;
            foreach (var r in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var bone in r.bones)
                    if (bone != null) top = Mathf.Max(top, bone.position.y - visual.position.y);
            return top;
        }

        // ------------------------------------------------------------------------------------------- soul travel

        /// <summary>Spl_SoulTravel EffectTargetCast: the ghost form's effect.</summary>
        public const string SoulTravelGhostEffect = "eff_magic_soultravel_ghost_01";

        void BeginGhost()
        {
            if (_heroVisual == null) return;
            _bodyFeet = transform.position;
            _bodyYaw = transform.eulerAngles.y;
            // The body stays where it stood: a still copy of the hero in his current pose (PS_GhostSummoner).
            var holder = new GameObject("Body holder");
            holder.SetActive(false);
            _body = Instantiate(_heroVisual.gameObject, holder.transform);
            foreach (var b in _body.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(b);
            foreach (var a in _body.GetComponentsInChildren<Animator>(true)) a.enabled = false;
            global::G3.Unity.Characters.G3HeroView.MakeWhole(_body); // made in first person: its head and the full body again (fix-208)
            _body.name = "Hero body (soul travel)";
            _body.transform.SetParent(null, false);
            _body.transform.SetPositionAndRotation(_heroVisual.position, _heroVisual.rotation);
            Destroy(holder);
            _body.SetActive(true);
            // The soul: the hero model, pale and slow (PC_Hero_Ghost: alpha 0.2, time scale 0.55); unseen and unhurt.
            MakeGhost(true);
            SaveSpeeds();
            if (_player != null)
            {
                _player.walkSpeed = _walk * SpellEffectRules.GhostTimeScale;
                _player.runSpeed = _run * SpellEffectRules.GhostTimeScale;
                _player.sprintSpeed = _sprint * SpellEffectRules.GhostTimeScale;
                _player.strafeRunSpeed = _strafe * SpellEffectRules.GhostTimeScale;
                _player.backRunSpeed = _back * SpellEffectRules.GhostTimeScale;
            }
            if (_heroVisual.TryGetComponent<G3MotionPlayer>(out var motion)) motion.TimeScale = SpellEffectRules.GhostTimeScale;
            _presenter.Unregister(_combat.Fighter); // nobody targets or hits the ghost (DoLogicalDamage skips ghosts)
            // The spell's target effect for the whole travel: the ghost glow and the screen parts (ScreenLoop, WhiteBlend),
            // which G3ScreenOverlay draws over the view as G3 does — a faint pale-blue/white tint (WorldSpellView leaves the
            // target effect of soul travel to this view, so it is not started twice).
            _ghostFx = G3EffectPlayer.Start(SoulTravelGhostEffect, transform, withSound: true);
        }

        void EndGhost()
        {
            if (_body == null && _savedMaterials.Count == 0) return;
            G3EffectPlayer.End(_ghostFx); // the screen parts go with the ghost
            _ghostFx = 0;
            MakeGhost(false);
            RestoreSpeeds();
            if (_heroVisual != null && _heroVisual.TryGetComponent<G3MotionPlayer>(out var motion)) motion.TimeScale = 1f;
            if (_body != null)
            {
                Destroy(_body);
                _body = null;
                if (_player != null)
                {
                    _player.Teleport(_bodyFeet);
                    _player.TurnTo(_bodyYaw, _player.Pitch); // fix-76: a near body blends, a far one cuts
                }
            }
            _presenter.Register(_combat.Fighter);
            G3EffectPlayer.Play("eff_magic_soultravel_end_01", transform.position + Vector3.up, transform);
        }

        void MakeGhost(bool on)
        {
            if (_heroVisual == null) return;
            if (!on)
            {
                foreach (var kv in _savedMaterials) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
                _savedMaterials.Clear();
                return;
            }
            var ghost = GhostMaterial();
            if (ghost == null) return;
            foreach (var r in _heroVisual.GetComponentsInChildren<Renderer>(true))
            {
                if (_savedMaterials.ContainsKey(r)) continue;
                _savedMaterials[r] = r.sharedMaterials;
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = ghost;
                r.sharedMaterials = mats;
            }
        }

        /// <summary>A pale, see-through unlit material (URP Unlit set to transparent; Sprites/Default as fallback).</summary>
        Material GhostMaterial()
        {
            if (_ghostMaterial != null) return _ghostMaterial;
            var color = new Color(0.75f, 0.85f, 1f, SpellEffectRules.GhostAlpha);
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null)
            {
                _ghostMaterial = new Material(shader) { name = "Hero ghost" };
                _ghostMaterial.SetFloat("_Surface", 1f);
                _ghostMaterial.SetFloat("_Blend", 0f);
                _ghostMaterial.SetFloat("_ZWrite", 0f);
                _ghostMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _ghostMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _ghostMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _ghostMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                _ghostMaterial.SetColor("_BaseColor", color);
            }
            else if ((shader = Shader.Find("Sprites/Default")) != null)
                _ghostMaterial = new Material(shader) { name = "Hero ghost", color = color };
            return _ghostMaterial;
        }

        // ------------------------------------------------------------------------------------------- dusk to dawn, reset

        /// <summary>PS_DuskToDawn: the hero stands (AIMode Talk) until 08:00.</summary>
        void OnDuskToDawn(DuskToDawnChanged e)
        {
            if (e.Running == _dusk) return;
            _dusk = e.Running;
            if (_dusk) _controls.Acquire(this);
            else _controls.Release(this);
        }

        void RestoreHero()
        {
            EndAnimal();
            EndGhost();
            if (_dusk)
            {
                _dusk = false;
                _controls?.Release(this);
            }
        }
    }
}
