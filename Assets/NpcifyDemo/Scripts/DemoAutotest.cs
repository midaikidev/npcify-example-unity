using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace NpcifyDemo
{
    /// <summary>
    /// Smoke test that runs only with the <c>-npcifyAutotest &lt;folder&gt;</c> command-line
    /// argument. It walks to each NPC, talks, picks the first reply, saves screenshots
    /// and quits with exit code 0 on success.
    /// </summary>
    public sealed class DemoAutotest : MonoBehaviour
    {
        private const string Argument = "-npcifyAutotest";

        private string _outputFolder;
        private NpcDialogueController _controller;
        private int _failures;
        private float _maxMouthOpenness;

        private void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, Argument);
            if (index < 0)
            {
                enabled = false;
                return;
            }
            _outputFolder = index + 1 < args.Length ? args[index + 1] : Path.Combine(Application.persistentDataPath, "autotest");
            Directory.CreateDirectory(_outputFolder);
        }

        private IEnumerator Start()
        {
            _controller = FindAnyObjectByType<NpcDialogueController>();
            var npcs = FindObjectsByType<NpcifyNpc>(FindObjectsSortMode.InstanceID)
                .OrderBy(npc => npc.Mode)
                .ToArray();

            yield return WaitFor(() => npcs.All(npc => npc.State != NpcLoadState.Loading), 180f, "characters loaded");
            yield return new WaitForSeconds(1f);
            foreach (var npc in npcs)
            {
                Log($"{npc.name}: {npc.State} {npc.StatusMessage}");
            }
            yield return Capture("01-scene");

            var step = 2;
            foreach (var npc in npcs.Where(npc => npc.IsReady))
            {
                var tag = npc.Mode == NpcConnectionMode.OfflineBaked ? "offline" : "online";
                MovePlayerNear(npc);
                yield return new WaitForSeconds(1.2f);
                yield return Capture($"{step++:00}-{tag}-prompt");

                _controller.TryOpenNearby();
                yield return new WaitForSeconds(2.2f);
                yield return Capture($"{step++:00}-{tag}-speaking");
                yield return WaitFor(() => _controller.IsChoosing, 90f, $"{tag} opening");
                yield return Capture($"{step++:00}-{tag}-choices");
                Log($"{tag} choices: {string.Join(" | ", _controller.ChoiceLabels)} (max mouth openness {_maxMouthOpenness:F2})");
                _maxMouthOpenness = 0f;

                _controller.Choose(0);
                yield return new WaitForSeconds(0.5f);
                yield return WaitFor(() => _controller.IsChoosing, 90f, $"{tag} reply");
                yield return Capture($"{step++:00}-{tag}-reply");
                Log($"{tag} reply choices: {string.Join(" | ", _controller.ChoiceLabels)}");
                if (_controller.ChoiceLabels.Contains("Try again"))
                {
                    _failures++;
                    Log($"{tag} reply failed");
                }
                _controller.Close();
                yield return new WaitForSeconds(1f);
            }

            if (npcs.Any(npc => !npc.IsReady))
            {
                _failures++;
            }
            Log(_failures == 0 ? "AUTOTEST PASSED" : $"AUTOTEST FAILED ({_failures})");
            Application.Quit(_failures == 0 ? 0 : 1);
        }

        private void Update()
        {
            var talking = _controller != null ? _controller.ActiveNpc : null;
            if (talking != null && talking.Character != null)
            {
                _maxMouthOpenness = Mathf.Max(_maxMouthOpenness, talking.Character.Expressions.MouthOpenness);
            }
        }

        private void MovePlayerNear(NpcifyNpc npc)
        {
            var player = FindAnyObjectByType<StarterAssets.ThirdPersonController>().transform;
            var controller = player.GetComponent<CharacterController>();
            var target = npc.transform.position + npc.transform.forward * 1.9f;
            if (controller != null)
            {
                controller.enabled = false;
            }
            player.position = target + Vector3.up * 0.05f;
            player.rotation = Quaternion.LookRotation(-npc.transform.forward, Vector3.up);
            if (controller != null)
            {
                controller.enabled = true;
            }
        }

        private IEnumerator WaitFor(Func<bool> condition, float timeoutSeconds, string label)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    _failures++;
                    Log($"Timeout: {label}");
                    yield break;
                }
                yield return null;
            }
        }

        private IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var path = Path.Combine(_outputFolder, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Log($"Screenshot {path}");
            yield return null;
        }

        private static void Log(string message)
        {
            Debug.Log("[Npcify autotest] " + message);
        }
    }
}
