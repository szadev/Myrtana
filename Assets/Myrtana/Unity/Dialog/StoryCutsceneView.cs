using System;
using System.Collections.Generic;
using System.Linq;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The presentation of the story's cutscene commands (Docs/story-coverage.md, story-2):
    /// <list type="bullet">
    /// <item>ShowPicture / ShowSubtitle → the G3 slideshow page (<c>IDD_PAGE_SLIDESHOW_2</c>, Docs/formats-gui.md §11):
    /// the full-screen 1024×768 picture <c>G3_Epilog_NN</c> and a 19 px bold centred subtitle at 12,660–1008,760; shown
    /// until the conversation ends [interp: G3 hides the page with the dialog].</item>
    /// <item>PlayMusic → the music situation (the scene's <see cref="G3MusicSituationOverride"/>), held until the
    /// conversation ends [interp].</item>
    /// <item>EndGame → <see cref="GameFlow.EndGame"/>: the credits videos, then the game quits.</item>
    /// <item>StartEffect → <see cref="G3EffectPlayer"/> at the named actor (shown NPC, hero), named place or interactive
    /// object; skipped (logged) when the entity is nowhere to be found.</item>
    /// </list>
    /// </summary>
    public sealed class StoryCutsceneView : MonoBehaviour, ISessionView
    {
        public const int SortingOrder = 900;
        const int Width = 1024, Height = 768;
        static readonly RectInt SubtitleRect = new RectInt(12, 660, 996, 100);

        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        GameStartFlow _flow;
        NpcViewManager _npcs;
        Transform _hero;
        GameSession _session;
        UIDocument _document;
        VisualElement _frame, _backdrop;
        G3Image _picture;
        Label _subtitle;
        string _musicSet;
        Dictionary<string, Vector3> _objectPositions;

        public void Init(GameStartFlow flow, NpcViewManager npcs, Transform hero)
        {
            _flow = flow;
            _npcs = npcs;
            _hero = hero;
        }

        public void Bind(GameSession session)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            Hide();
            _session = session;
            _objectPositions = null;
            if (session == null) return;
            var events = session.Events;
            _subscriptions.Add(events.Subscribe<StoryPictureShown>(e => ShowPicture(e.Image)));
            _subscriptions.Add(events.Subscribe<StorySubtitleShown>(e => ShowSubtitle(session.Localizer.Text(e.Text))));
            _subscriptions.Add(events.Subscribe<StoryMusicRequested>(e => SetMusic(e.Situation)));
            _subscriptions.Add(events.Subscribe<StoryEffectStarted>(e => StartEffect(e.Effect, e.At)));
            _subscriptions.Add(events.Subscribe<StorySoundRequested>(e => PlaySound(e.Sound, e.At)));
            _subscriptions.Add(events.Subscribe<GameEndRequested>(_ => EndGame()));
            _subscriptions.Add(events.Subscribe<DialogEnded>(_ => Hide()));
            BindSectors(session.WorldState?.Sectors);
        }

        void OnDestroy()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            BindSectors(null);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Story sectors: the world's sector objects (cells) and placed effects follow SetSectorStatus (story-4)

        WorldSectors _sectors;

        void BindSectors(WorldSectors sectors)
        {
            if (_sectors != null) _sectors.Changed -= OnSectorChanged;
            _sectors = sectors;
            if (sectors != null) sectors.Changed += OnSectorChanged;
            G3StorySectors.IsEnabled = sectors != null ? sectors.IsEnabled : (Func<string, bool>)null;
        }

        static void OnSectorChanged(string sector) => G3StorySectors.NotifyChanged();

        // ---------------------------------------------------------------------------------------------------------
        // Slideshow page

        void EnsurePage()
        {
            if (_document != null) return;
            _document = G3PanelSettings.CreateDocument("Story slideshow", GuiPanelKind.Screen, transform, SortingOrder);
            var root = _document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            var page = new VisualElement { pickingMode = PickingMode.Ignore };
            page.style.position = Position.Absolute;
            page.style.left = page.style.top = page.style.right = page.style.bottom = 0;
            page.style.backgroundColor = Color.black;
            root.Add(page);

            // Wider screens: the picture blurred and darkened at the sides, like the menu and the loading screen.
            _backdrop = new VisualElement { pickingMode = PickingMode.Ignore };
            _backdrop.style.position = Position.Absolute;
            _backdrop.style.left = _backdrop.style.top = _backdrop.style.right = _backdrop.style.bottom = 0;
            _backdrop.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            page.Add(_backdrop);

            _frame = new VisualElement { pickingMode = PickingMode.Ignore };
            _frame.style.position = Position.Absolute;
            _frame.style.width = Width;
            _frame.style.height = Height;
            _frame.style.left = Length.Percent(50);
            _frame.style.translate = new Translate(Length.Percent(-50), 0);
            _frame.style.transformOrigin = new TransformOrigin(Length.Percent(50), 0);
            page.Add(_frame);
            G3Backdrop.AddEdgeShadows(_frame, Width, Height);

            _picture = new G3Image();
            _picture.style.position = Position.Absolute;
            _picture.style.left = _picture.style.top = _picture.style.right = _picture.style.bottom = 0;
            _frame.Add(_picture);

            _subtitle = new Label { pickingMode = PickingMode.Ignore };
            _subtitle.style.position = Position.Absolute;
            _subtitle.style.left = SubtitleRect.x;
            _subtitle.style.top = SubtitleRect.y;
            _subtitle.style.width = SubtitleRect.width;
            _subtitle.style.height = SubtitleRect.height;
            G3Text.Font(_subtitle, 19, bold: true); // IDC_STATIC_SLIDESHOW_SUBTITLE_2: res -19 bold
            _subtitle.style.unityTextAlign = TextAnchor.UpperCenter;
            _subtitle.style.whiteSpace = WhiteSpace.Normal;
            _subtitle.style.color = new Color(0.878f, 0.769f, 0.584f); // static_text gold (formats-gui.md §12)
            _frame.Add(_subtitle);
            page.RegisterCallback<GeometryChangedEvent>(_ => Fit(page));
            root.style.display = DisplayStyle.None;
        }

        /// <summary>The 4:3 page fills the screen height (or width), centred, as the loading screen does (on its fitted
        /// Screen panel the scale is 1; the transform only covers rounding and the plain fallback).</summary>
        void Fit(VisualElement page)
        {
            float w = page.layout.width, h = page.layout.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0 || h <= 0) return;
            float s = Mathf.Min(w / Width, h / Height);
            _frame.style.scale = new Scale(new Vector3(s, s, 1f));
            _frame.style.top = (h - Height * s) / 2;
        }

        void ShowPicture(string image)
        {
            EnsurePage();
            _picture.Image = SkinImageName(image);
            SetBackdrop(_picture.Image);
            _document.rootVisualElement.style.display = DisplayStyle.Flex;
        }

        void SetBackdrop(string image)
        {
            var blurred = string.IsNullOrEmpty(image) ? null : G3Backdrop.Blurred(G3Skin.Load()?.Sprite(image), G3Backdrop.SideBrightness);
            _backdrop.style.backgroundImage = blurred != null ? new StyleBackground(blurred) : new StyleBackground(StyleKeyword.None);
        }

        void ShowSubtitle(string text)
        {
            EnsurePage();
            _subtitle.text = text;
            _document.rootVisualElement.style.display = DisplayStyle.Flex;
        }

        /// <summary>The infos name the .dds file ("G3_Epilog_00.dds"); the GUI index knows the packed image by its .tga name.</summary>
        static string SkinImageName(string image)
        {
            if (string.IsNullOrEmpty(image)) return null;
            int dot = image.LastIndexOf('.');
            string stem = dot > 0 ? image.Substring(0, dot) : image;
            var skin = G3Skin.Load();
            foreach (var candidate in new[] { stem + ".tga", image, stem })
                if (skin?.Sprite(candidate) != null) return candidate;
            Debug.LogWarning($"[Myrtana] story picture '{image}' is not in the imported GUI (G3/GUI/Import).");
            return stem + ".tga";
        }

        /// <summary>The slideshow page is on screen (G3 gCHUDPageSlideshow2 hides the cursor; fix-87).</summary>
        public bool SlideshowShown => _document != null && _document.rootVisualElement.style.display == DisplayStyle.Flex;

        void Hide()
        {
            if (_document != null)
            {
                _document.rootVisualElement.style.display = DisplayStyle.None;
                _subtitle.text = "";
                _picture.Image = null;
                SetBackdrop(null);
            }
            SetMusic(null);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Music, end of the game, effects

        void SetMusic(string situation)
        {
            var source = FindAnyObjectByType<G3MusicSituationOverride>();
            if (source == null) return;
            string wanted = string.IsNullOrEmpty(situation) ? null : situation.ToLowerInvariant();
            if (wanted == null)
            {
                if (_musicSet == null) return;
                if (source.situation == _musicSet) source.situation = "";
                _musicSet = null;
            }
            else
            {
                source.situation = wanted;
                _musicSet = wanted;
            }
            var context = FindAnyObjectByType<G3AudioContext>();
            if (context != null && context.isActiveAndEnabled) context.Refresh();
        }

        void EndGame()
        {
            Debug.Log("[Myrtana] EndGame: end credits, then the game ends");
            if (_flow?.Flow != null) _flow.Flow.EndGame();
        }

        void StartEffect(string effect, string at)
        {
            if (!TryFind(at, out var position, out var follow))
            {
                Debug.Log($"[Myrtana] StartEffect {effect}: '{at}' is not in the world here; skipped.");
                return;
            }
            G3EffectPlayer.Play(effect, position, follow);
        }

        /// <summary>SaySound: the G3 sample at the speaker (3D, 2–40 m falloff [interp]).</summary>
        void PlaySound(string sound, string at)
        {
            if (!TryFind(at, out var position, out var follow)) return;
            var context = FindAnyObjectByType<G3AudioContext>();
            var clip = context != null && context.library != null ? context.library.GetClip(sound) : null;
            if (clip == null)
            {
                Debug.Log($"[Myrtana] SaySound {sound}: sample not imported; skipped.");
                return;
            }
            G3SoundPool.Instance.Play(clip, position, follow, G3SoundPool.EffectsVolume, 2f, 40f);
        }

        /// <summary>The hero, a shown NPC, a named place or an interactive object of that name.</summary>
        bool TryFind(string entity, out Vector3 position, out Transform follow)
        {
            position = default;
            follow = null;
            if (string.IsNullOrEmpty(entity)) return false;
            var actor = new ActorId(entity);
            if (actor.IsPlayer && _hero != null)
            {
                follow = _hero;
                position = _hero.position;
                return true;
            }
            if (_npcs != null && _npcs.TryGetView(actor, out var view))
            {
                follow = view;
                position = view.position;
                return true;
            }
            if (_session?.Party?.Positions != null && _session.Party.Positions.TryGetPose(entity, out var pose))
            {
                position = new Vector3(pose.X, pose.Y, pose.Z);
                return true;
            }
            if (_session != null)
            {
                _objectPositions ??= ObjectPositions(_session);
                // "MoraSul_TempleDoor_Tester" names the door "MoraSul_TempleDoor_Tester_01" (G3 entity names are prefixes here).
                foreach (var kv in _objectPositions)
                    if (kv.Key.StartsWith(entity, StringComparison.OrdinalIgnoreCase))
                    {
                        position = kv.Value;
                        return true;
                    }
            }
            return false;
        }

        static Dictionary<string, Vector3> ObjectPositions(GameSession session)
        {
            var map = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in session.Content.Database.All<InteractiveObjectDef>())
                if (!string.IsNullOrEmpty(o.Name) && o.Position != null && o.Position.Length >= 3 && !map.ContainsKey(o.Name))
                    map[o.Name] = new Vector3(o.Position[0], o.Position[1], o.Position[2]);
            return map;
        }
    }
}
