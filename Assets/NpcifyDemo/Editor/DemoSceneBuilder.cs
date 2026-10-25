using System.Linq;
using StarterAssets;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NpcifyDemo.Editor
{
    /// <summary>
    /// Creates the demo scene from the Unity "Get Started" template level: removes the
    /// tutorial objects, sets an over-the-shoulder camera, and adds the two Npcify NPCs.
    /// </summary>
    public static class DemoSceneBuilder
    {
        public const string TemplateScenePath = "Assets/Scenes/GetStarted_Scene.unity";
        public const string DemoScenePath = "Assets/NpcifyDemo/Scenes/NpcifyDemo.unity";
        public const string PlayerPrefabPath = "Assets/Prefabs/PlayerRobot.prefab";

        // An open corner of the template floor (the floor top is at y = 5).
        private static readonly Vector3 PlayerStart = new Vector3(-8f, 5.1f, -1f);
        private static readonly Vector3 OfflineNpcPosition = new Vector3(-10.4f, 5.1f, 4.6f);
        private static readonly Vector3 OnlineNpcPosition = new Vector3(-5.6f, 5.1f, 4.6f);

        private static readonly string[] RemovedRootNames =
        {
            "Tutorial", "Collectibles", "Remaining_Collectibles_UI", "Secret_Area",
        };

        private static readonly string[] AlwaysIncludedShaders =
        {
            "VRM10/Universal Render Pipeline/MToon10",
            "UniGLTF/UniUnlit",
        };

        [MenuItem("Npcify/Rebuild Demo Scene")]
        public static void BuildFromMenu()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Build();
            }
        }

        /// <summary>Batch mode entry point: <c>-executeMethod NpcifyDemo.Editor.DemoSceneBuilder.Build</c>.</summary>
        public static void Build()
        {
            var scene = EditorSceneManager.OpenScene(TemplateScenePath, OpenSceneMode.Single);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (RemovedRootNames.Contains(root.name) ||
                    root.GetComponentInChildren<Pickup>(true) != null ||
                    root.GetComponentInChildren<UpdateCollectibleCount>(true) != null)
                {
                    Object.DestroyImmediate(root);
                }
            }

            var player = CreatePlayer();
            ToonStyleBuilder.ConfigureRenderers();
            ToonStyleBuilder.ApplyToScene();
            ConfigureShoulderCamera(player);
            ConfigureBrain();
            Physics.SyncTransforms();

            var npcRoot = new GameObject("Npcify NPCs");
            CreateNpc(npcRoot.transform, "Hinata (offline)", NpcConnectionMode.OfflineBaked,
                "221608de-65d0-49f0-97b4-800ce08c198d", OfflineNpcPosition, PlayerStart);
            CreateNpc(npcRoot.transform, "Vespera (online)", NpcConnectionMode.OnlineApi,
                "37d81b69-04a8-4720-9cda-4172d4317803", OnlineNpcPosition, PlayerStart);

            var demo = new GameObject("Npcify Demo");
            var view = demo.AddComponent<DialogueView>();
            var cameraObject = new GameObject("Dialogue Camera");
            cameraObject.transform.SetParent(demo.transform, false);
            var dialogueCamera = cameraObject.AddComponent<CinemachineCamera>();
            dialogueCamera.Lens = LensSettings.Default;
            dialogueCamera.Lens.FieldOfView = 38f;
            var rig = cameraObject.AddComponent<DialogueCameraRig>();
            var controller = demo.AddComponent<NpcDialogueController>();
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("player").objectReferenceValue = player.transform;
            serialized.FindProperty("playerLookTarget").objectReferenceValue = player.CinemachineCameraTarget.transform;
            serialized.FindProperty("view").objectReferenceValue = view;
            serialized.FindProperty("dialogueCamera").objectReferenceValue = rig;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            demo.AddComponent<DemoAutotest>();

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DemoScenePath));
            if (!EditorSceneManager.SaveScene(scene, DemoScenePath))
            {
                throw new System.InvalidOperationException($"Could not save {DemoScenePath}");
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(DemoScenePath, true) };
            ConfigurePlayerSettings();
            AssetDatabase.SaveAssets();
            Debug.Log($"[Npcify demo] Saved {DemoScenePath}");
        }

        private static ThirdPersonController CreatePlayer()
        {
            var existing = Object.FindAnyObjectByType<ThirdPersonController>();
            if (existing != null)
            {
                Object.DestroyImmediate(existing.transform.root.gameObject);
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            // The character is a child of the prefab root. It faces the NPCs (+Z) at the start.
            var player = instance.GetComponentInChildren<ThirdPersonController>();
            player.transform.SetPositionAndRotation(SnapToGround(PlayerStart), Quaternion.identity);
            player.CinemachineCameraTarget.transform.rotation = Quaternion.identity;
            return player;
        }

        private static void CreateNpc(Transform parent, string name, NpcConnectionMode mode, string characterId,
            Vector3 position, Vector3 lookAt)
        {
            var npc = new GameObject(name);
            npc.transform.SetParent(parent, false);
            npc.transform.position = SnapToGround(position);
            npc.transform.rotation = Quaternion.LookRotation(Flatten(lookAt - position), Vector3.up);
            var component = npc.AddComponent<NpcifyNpc>();
            var serialized = new SerializedObject(component);
            serialized.FindProperty("mode").enumValueIndex = (int)mode;
            serialized.FindProperty("defaultCharacterId").stringValue = characterId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureShoulderCamera(ThirdPersonController player)
        {
            var follow = Object.FindAnyObjectByType<CinemachineThirdPersonFollow>();
            if (follow == null)
            {
                Debug.LogWarning("[Npcify demo] The player camera has no CinemachineThirdPersonFollow.");
                return;
            }
            follow.ShoulderOffset = new Vector3(0.6f, 0.15f, 0f);
            follow.VerticalArmLength = 0.25f;
            follow.CameraSide = 1f;
            follow.CameraDistance = 3.4f;
            follow.Damping = new Vector3(0.1f, 0.3f, 0.3f);
            EditorUtility.SetDirty(follow);
            var camera = follow.GetComponent<CinemachineCamera>();
            if (camera != null)
            {
                camera.Lens.FieldOfView = 50f;
                EditorUtility.SetDirty(camera);
            }
            player.TopClamp = 60f;
            player.BottomClamp = -25f;
            EditorUtility.SetDirty(player);
        }

        private static void ConfigureBrain()
        {
            var brain = Object.FindAnyObjectByType<CinemachineBrain>();
            if (brain == null)
            {
                return;
            }
            brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, 0.7f);
            EditorUtility.SetDirty(brain);
        }

        private static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "Npcify";
            PlayerSettings.productName = "Npcify Unity Demo";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, "ai.npcify.example.unity");
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;

            var graphics = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            var serialized = new SerializedObject(graphics);
            var shaders = serialized.FindProperty("m_AlwaysIncludedShaders");
            foreach (var shaderName in AlwaysIncludedShaders)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"[Npcify demo] Shader not found: {shaderName}");
                    continue;
                }
                var present = false;
                for (var i = 0; i < shaders.arraySize; i++)
                {
                    present |= shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader;
                }
                if (!present)
                {
                    shaders.InsertArrayElementAtIndex(shaders.arraySize);
                    shaders.GetArrayElementAtIndex(shaders.arraySize - 1).objectReferenceValue = shader;
                }
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Vector3 SnapToGround(Vector3 position)
        {
            var from = position + Vector3.up * 10f;
            if (Physics.Raycast(from, Vector3.down, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }
            return position;
        }

        private static Vector3 Flatten(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }
    }
}
