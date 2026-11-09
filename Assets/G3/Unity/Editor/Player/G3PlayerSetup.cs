using System.Linq;
using G3.Unity.Characters;
using G3.Unity.Editor.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Puts a <see cref="G3Player"/> with the G3 hero model and the third-person camera rig into the open region scene.
    /// CLI: unity command eval "return G3.Unity.Editor.G3PlayerSetup.SetupScene();"
    ///      Tools/eval_job.sh 'return G3.Unity.Editor.G3PlayerSetup.ApplyHeroToOpenScenes();'  (no world rebuild)
    /// </summary>
    public static class G3PlayerSetup
    {
        public const string PlayerName = "G3Player";
        public const string HeroName = "Hero";

        /// <summary>
        /// Builds colliders for the scene's "G3World" root (idempotent), then creates or moves the player to the ground
        /// below <paramref name="spawn"/> (metres) or below the scene camera, and attaches hero + camera rig
        /// (<see cref="SetupHero"/>). The main camera becomes the player's camera; its FlyCamera is kept but disabled
        /// (F toggles fly mode in Play mode).
        /// </summary>
        public static string SetupScene(Vector3? spawn = null, bool buildColliders = true)
        {
            string collisionInfo = "colliders not built";
            var world = GameObject.Find("G3World");
            if (buildColliders && world != null)
                collisionInfo = G3CollisionBuilder.Build(world).ToString();

            var cam = Camera.main;
            var player = GameObject.Find(PlayerName);
            if (player == null)
            {
                player = new GameObject(PlayerName);
                player.AddComponent<CharacterController>();
                player.AddComponent<G3Player>();
            }
            var cc = player.GetComponent<CharacterController>();
            G3Player.ConfigureController(cc);

            // Spawn point: given point, else below the scene view / main camera.
            Vector3 from;
            if (spawn.HasValue) from = spawn.Value;
            else if (SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null && cam == null)
                from = SceneView.lastActiveSceneView.camera.transform.position;
            else if (cam != null) from = cam.transform.position;
            else from = Vector3.zero;

            var feet = FindGround(from, out bool hitGround);
            player.transform.position = feet + Vector3.up * 0.05f;

            if (cam != null)
            {
                var fly = cam.GetComponent<FlyCamera>();
                if (fly != null) fly.enabled = false;
                float yaw = cam.transform.eulerAngles.y;
                player.transform.rotation = Quaternion.Euler(0, yaw, 0);
                cam.transform.SetParent(player.transform, false);
                cam.transform.localRotation = Quaternion.identity;
                cam.nearClipPlane = 0.05f;
                player.GetComponent<G3Player>().playerCamera = cam;
            }
            string hero = SetupHero(player);
            EditorSceneManager.MarkSceneDirty(player.scene);
            return $"player at {player.transform.position} (ground {(hitGround ? "hit" : "NOT found")}); {hero}; {collisionInfo}";
        }

        /// <summary>
        /// Gives an existing player the hero model (prefab instance child "Hero", built from the game files when missing),
        /// the hero's G3 movement speeds (root speeds of its motions) and the <see cref="G3PlayerCamera"/> rig (third person).
        /// Idempotent; replaces the old capsule body.
        /// </summary>
        public static string SetupHero(GameObject player)
        {
            var gp = player.GetComponent<G3Player>();
            if (gp == null) return "no G3Player";

            var capsule = player.transform.Find("Body");
            if (capsule != null) Object.DestroyImmediate(capsule.gameObject);
            var old = player.transform.Find(HeroName);
            if (old != null) Object.DestroyImmediate(old.gameObject);

            string heroInfo;
            var prefab = G3HeroImporter.GetOrBuild();
            if (prefab != null)
            {
                var hero = (GameObject)PrefabUtility.InstantiatePrefab(prefab, player.transform);
                hero.name = HeroName;
                // The CharacterController hovers by its skin width; the model's feet go to the ground.
                hero.transform.localPosition = new Vector3(0, -player.GetComponent<CharacterController>().skinWidth, 0);
                hero.transform.localRotation = Quaternion.identity;
                ApplySpeeds(gp, hero.GetComponent<G3MotionPlayer>()?.library, hero.GetComponent<G3HeroAnimator>());
                heroInfo = $"hero {AssetDatabase.GetAssetPath(prefab)} (walk {gp.walkSpeed} run {gp.runSpeed}/{gp.strafeRunSpeed}/{gp.backRunSpeed} sprint {gp.sprintSpeed} m/s)";
            }
            else heroInfo = "hero prefab NOT built";

            var cam = gp.playerCamera != null ? gp.playerCamera : player.GetComponentInChildren<Camera>();
            if (cam != null)
            {
                var rig = cam.GetComponent<G3PlayerCamera>();
                if (rig == null) rig = cam.gameObject.AddComponent<G3PlayerCamera>();
                rig.player = gp;
                rig.thirdPerson = true;
                // Edit-mode framing like the runtime rig: behind and above the hero.
                cam.transform.localPosition = new Vector3(rig.shoulderOffset, rig.pivotHeight + 0.6f, -rig.distance);
                cam.transform.localRotation = Quaternion.Euler(10, 0, 0);
            }
            EditorUtility.SetDirty(player);
            return heroInfo;
        }

        /// <summary>Player speeds = root speeds of the hero motions (motion-name field, Docs/formats-animation.md §5).</summary>
        static void ApplySpeeds(G3Player gp, G3MotionLibrary lib, G3HeroAnimator anim)
        {
            if (lib == null || anim == null) return;
            var sel = new G3LocomotionSelector(lib, new G3MotionKey(anim.rig, "Move", "Stand", "Fwd", anim.stance, anim.leftItem, anim.rightItem, anim.pose));
            float Pick(float v, float fallback) => v > 0 ? v : fallback;
            gp.walkSpeed = Pick(sel.Speed("Walk", "Fwd"), gp.walkSpeed);
            gp.runSpeed = Pick(sel.Speed("Run", "Fwd"), gp.runSpeed);
            gp.strafeRunSpeed = Pick(sel.Speed("Run", "Left"), gp.strafeRunSpeed);
            gp.backRunSpeed = Pick(sel.Speed("Run", "Back"), gp.backRunSpeed);
            gp.sprintSpeed = Pick(sel.Speed("Sprint", "Fwd"), gp.sprintSpeed);
        }

        /// <summary>
        /// Applies the hero setup to the players of all open scenes (e.g. the master scene) without a world rebuild and
        /// saves those scenes.
        /// </summary>
        public static string ApplyHeroToOpenScenes()
        {
            var results = new System.Collections.Generic.List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                var player = scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<G3Player>() != null);
                if (player == null) continue;
                results.Add($"{scene.path}: {SetupHero(player)}");
                EditorSceneManager.MarkSceneDirty(scene);
                if (!string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
            }
            return results.Count == 0 ? "no G3Player in the open scenes" : string.Join("\n", results);
        }

        /// <summary>Highest collider surface below <paramref name="from"/> (searching from 500 m above it).</summary>
        public static Vector3 FindGround(Vector3 from, out bool hit)
        {
            Physics.SyncTransforms();
            var origin = new Vector3(from.x, from.y + 500f, from.z);
            // Prefer the first surface below the given height (inside buildings), else the top-most one.
            if (Physics.Raycast(new Vector3(from.x, from.y + 1f, from.z), Vector3.down, out var h, 1000f, ~0, QueryTriggerInteraction.Ignore))
            { hit = true; return h.point; }
            if (Physics.Raycast(origin, Vector3.down, out h, 2000f, ~0, QueryTriggerInteraction.Ignore))
            { hit = true; return h.point; }
            hit = false;
            return from;
        }

        [MenuItem("G3/Setup Player in Scene")]
        static void Menu() => Debug.Log(SetupScene());

        [MenuItem("G3/Player/Apply Hero to Open Scenes")]
        static void MenuApplyHero() => Debug.Log(ApplyHeroToOpenScenes());
    }
}
