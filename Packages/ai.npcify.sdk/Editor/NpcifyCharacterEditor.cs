#if UNITY_EDITOR
using System;
using System.IO;
using Npcify.Sdk.Models;
using Npcify.Sdk.Unity;
using UnityEditor;
using UnityEngine;

namespace Npcify.Sdk.Editor
{
    [CustomEditor(typeof(NpcifyCharacter))]
    public sealed class NpcifyCharacterEditor : UnityEditor.Editor
    {
        [Serializable]
        private sealed class NpcifySdkConfigFile
        {
            public string baseUrl;
            public string endpoint;
            public string characterId;
            public string apiKey;
            public bool bakedConversationEnabled;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var character = (NpcifyCharacter)target;
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Npcify SDK Utilities", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Import npcify-unity-sdk.json"))
                {
                    var path = EditorUtility.OpenFilePanel(
                        "Import Npcify Unity SDK Config",
                        Application.dataPath,
                        "json"
                    );
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        var parsed = JsonUtility.FromJson<NpcifySdkConfigFile>(json);
                        if (parsed != null)
                        {
                            var resolvedBaseUrl = !string.IsNullOrWhiteSpace(parsed.baseUrl)
                                ? parsed.baseUrl
                                : !string.IsNullOrWhiteSpace(parsed.endpoint)
                                    ? parsed.endpoint
                                    : "https://api.npcify.ai";
                            Undo.RecordObject(character, "Import Npcify Config");
                            character.Configure(new NpcifyOptions
                            {
                                CharacterId = parsed.characterId,
                                BaseUrl = resolvedBaseUrl,
                                ApiKey = parsed.apiKey,
                            });
                            EditorUtility.SetDirty(character);
                        }
                    }
                }

                if (GUILayout.Button("Auto-Bind Rig & Meshes"))
                {
                    Undo.RecordObject(character, "Bind Npcify Avatar Rig");
                    character.RefreshAvatarBindings();
                    EditorUtility.SetDirty(character);
                }
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(6f);
                var speech = character.Speech;
                EditorGUILayout.HelpBox(
                    $"Speaking: {speech.Speaking} | Thinking: {speech.Thinking}\n" +
                    $"Mouth Openness: {character.Expressions.MouthOpenness:F2}\n" +
                    $"Revealed: {speech.RevealedText}",
                    MessageType.Info
                );
            }
        }
    }
}
#endif
