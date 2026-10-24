using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NpcifyDemo.Editor
{
    /// <summary>
    /// Player builds. A local <c>.env</c> file is copied next to the built game,
    /// so the online NPC works in the build too. Never share a build that contains your key.
    /// </summary>
    public static class DemoBuild
    {
        [MenuItem("Npcify/Build/macOS")]
        public static void BuildMac()
        {
            Build(BuildTarget.StandaloneOSX, "Builds/macOS/NpcifyUnityDemo.app", "Builds/macOS");
        }

        [MenuItem("Npcify/Build/Windows")]
        public static void BuildWindows()
        {
            Build(BuildTarget.StandaloneWindows64, "Builds/Windows/NpcifyUnityDemo.exe", "Builds/Windows");
        }

        private static void Build(BuildTarget target, string locationPath, string outputFolder)
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { DemoSceneBuilder.DemoScenePath },
                locationPathName = locationPath,
                target = target,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new Exception($"Build failed: {report.summary.result}, {report.summary.totalErrors} errors");
            }

            var envFile = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".env"));
            if (File.Exists(envFile))
            {
                File.Copy(envFile, Path.Combine(outputFolder, ".env"), true);
            }
            Debug.Log($"[Npcify demo] Built {locationPath} ({report.summary.totalSize / (1024 * 1024)} MB)");
        }
    }
}
