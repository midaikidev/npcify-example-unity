using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace NpcifyDemo.Editor
{
    /// <summary>Opens the demo scene when the project opens with an empty, unsaved scene (a fresh clone).</summary>
    [InitializeOnLoad]
    internal static class DemoStartupScene
    {
        private const string OpenedKey = "NpcifyDemo.StartupSceneOpened";

        static DemoStartupScene()
        {
            EditorApplication.delayCall += OpenDemoScene;
        }

        private static void OpenDemoScene()
        {
            if (SessionState.GetBool(OpenedKey, false) || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }
            SessionState.SetBool(OpenedKey, true);
            var active = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(active.path) && !active.isDirty &&
                AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoSceneBuilder.DemoScenePath) != null)
            {
                EditorSceneManager.OpenScene(DemoSceneBuilder.DemoScenePath);
            }
        }
    }
}
