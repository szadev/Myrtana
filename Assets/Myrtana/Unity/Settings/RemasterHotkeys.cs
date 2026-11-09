using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The world remaster master switch in the game (Docs/build.md §3): F7 turns every world part of
    /// <see cref="G3WorldRemaster"/> on or off at once (<see cref="RemasterOptions.MasterTarget"/>: all on → all off, else all
    /// on; live, the loaded cells are re-walked) and shows "Original" / "Remastered" for
    /// <see cref="WorldRemasterToggle.LabelSeconds"/>. Each part is stored on its own (PlayerPrefs <c>G3.Remaster.&lt;Part&gt;</c>,
    /// default off = the faithful port), so the Options → Remaster page shows the per-part states afterwards. F9 stays
    /// G3's quickload. The remaster pair list (materials, textures) is loaded in the background at start so the first flip
    /// does not stall. Demo and development builds: Ctrl+F7 gives the
    /// <see cref="DemoKit"/> teleporter stones. The single parts and the characters switch live on the Options →
    /// Remaster page (<see cref="G3MenuRemasterPage"/>), next to the six world part rows.
    /// </summary>
    public sealed class RemasterHotkeys : MonoBehaviour
    {
        static RemasterHotkeys _instance;

        LanguageSettings _languages;
        GameBootstrap _game;
        float _shownAt = -1f, _showFor;
        string _text;
        GUIStyle _style, _shadow;

        public void Bind(LanguageSettings languages, GameBootstrap game)
        {
            _languages = languages;
            _game = game;
        }

        void Awake()
        {
            _instance = this;
            useGUILayout = false;
        }

        void Start() => Resources.LoadAsync<G3WorldRemasterSet>(G3WorldRemasterSet.ResourceName);   // warm-up only

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>Switches every world part (stored) and shows the state label.</summary>
        public static void Set(bool on)
        {
            int slots = G3WorldRemaster.SetAll(on, persist: true);
            Debug.Log($"[Remaster] world remaster {(on ? "on" : "off")} (F7, all world parts: {WorldRemasterParts.Describe(G3WorldRemaster.Parts)}): {slots} material slots queued (G3RendererChanges)");
            if (_instance != null) _instance.Show(WorldRemasterToggle.Label(on, _instance.Language), WorldRemasterToggle.LabelSeconds);
        }

        string Language => _languages?.TextLanguage ?? "English";

        void Update()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb == null || !kb.f7Key.wasPressedThisFrame) return;
            bool ctrl = kb.ctrlKey.isPressed;
#if G3_DEMO || UNITY_EDITOR || DEVELOPMENT_BUILD
            if (ctrl) { GiveDemoKit(); return; }
#endif
            if (!ctrl) Set(RemasterOptions.MasterTarget(G3WorldRemaster.Parts));
        }

#if G3_DEMO || UNITY_EDITOR || DEVELOPMENT_BUILD
        void GiveDemoKit()
        {
            var session = _game != null ? _game.Session : null;
            var content = _game != null ? _game.Content : null;
            if (session == null || content == null) return;
            var given = DemoKit.Give(session.Items.Inventories, id => content.Database.TryGet<ItemDef>(id, out _));
            bool ru = Language == "Russian";
            string text = given.Count == 0
                ? (ru ? "Камни телепортации уже в инвентаре" : "Teleporter stones already in the inventory")
                : (ru ? "Камни телепортации: Ардея, Фаринг, Нордмар, Варант (инвентарь)" : "Teleporter stones: Ardea, Faring, Nordmar, Varant (inventory)");
            Debug.Log("[Demo] kit: " + (given.Count == 0 ? "nothing new" : string.Join(", ", given)));
            Show(text, 4f);
        }
#endif

        void Show(string text, float seconds)
        {
            _text = text;
            _showFor = seconds;
            _shownAt = Time.unscaledTime;
        }

        void OnGUI()
        {
            if (_text == null || _shownAt < 0 || Time.unscaledTime - _shownAt >= _showFor || Event.current.type != EventType.Repaint) return;
            int size = Mathf.Max(18, Mathf.RoundToInt(Screen.height / 30f));
            if (_style == null || _style.fontSize != size)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = size, fontStyle = FontStyle.Bold };
                _style.normal.textColor = new Color(0.95f, 0.88f, 0.70f);
                _shadow = new GUIStyle(_style);
                _shadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            }
            float age = Time.unscaledTime - _shownAt;
            float alpha = Mathf.Clamp01((_showFor - age) / 0.4f);
            var prev = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);
            var r = new Rect(0, Screen.height * 0.12f, Screen.width, size * 1.6f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), _text, _shadow);
            GUI.Label(r, _text, _style);
            GUI.color = prev;
        }
    }
}
