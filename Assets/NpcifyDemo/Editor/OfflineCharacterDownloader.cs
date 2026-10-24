using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace NpcifyDemo.Editor
{
    /// <summary>
    /// Downloads the offline character (VRM, VRMA animations and baked conversation)
    /// into StreamingAssets. The API key and character ID come from the .env file.
    /// </summary>
    public static class OfflineCharacterDownloader
    {
        public const string DefaultFolder = "hinata";

        [MenuItem("Npcify/Download Offline Character")]
        public static void DownloadFromMenu()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Npcify", "Downloading the offline character…", 0.2f);
                var folder = Download(DefaultFolder);
                EditorUtility.DisplayDialog("Npcify", $"The offline character is in {folder}.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Npcify", "Download failed: " + ex.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>Batch mode entry point: <c>-executeMethod NpcifyDemo.Editor.OfflineCharacterDownloader.DownloadFromCommandLine</c>.</summary>
        public static void DownloadFromCommandLine()
        {
            var folder = Download(DefaultFolder);
            Debug.Log($"[Npcify demo] Offline character saved to {folder}");
        }

        /// <summary>Downloads on a worker thread and blocks until the files are saved.</summary>
        public static string Download(string folderName)
        {
            // Unity paths and settings are read on the main thread. The network work runs on a
            // worker thread, so it cannot deadlock on the blocked main thread.
            var request = CreateRequest(folderName);
            Task.Run(() => DownloadAsync(request)).GetAwaiter().GetResult();
            AssetDatabase.Refresh();
            return request.Root;
        }

        private static DownloadRequest CreateRequest(string folderName)
        {
            DemoEnv.Reload();
            if (!DemoEnv.HasApiKey)
            {
                throw new InvalidOperationException($"Set {DemoEnv.ApiKeyKey} in the .env file first.");
            }
            var characterId = DemoEnv.Get(DemoEnv.OfflineCharacterIdKey);
            if (string.IsNullOrWhiteSpace(characterId))
            {
                throw new InvalidOperationException($"Set {DemoEnv.OfflineCharacterIdKey} in the .env file first.");
            }

            return new DownloadRequest
            {
                ApiKey = DemoEnv.ApiKey,
                CharacterId = characterId,
                AvatarUrl = $"{DemoEnv.ApiBaseUrl.TrimEnd('/')}/avatars/{Uri.EscapeDataString(characterId)}",
                Root = Path.Combine(Application.streamingAssetsPath, "Npcify", folderName),
            };
        }

        private static async Task DownloadAsync(DownloadRequest request)
        {
            var characterId = request.CharacterId;
            var avatarUrl = request.AvatarUrl;
            var root = request.Root;
            var animationsRoot = Path.Combine(root, OfflineCharacterSource.AnimationsFolder);
            Directory.CreateDirectory(animationsRoot);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

            var info = JsonUtility.FromJson<CharacterInfo>(await http.GetStringAsync(avatarUrl + "/info"));
            if (!info.bakedConversationEnabled)
            {
                throw new InvalidOperationException($"{info.name} has no baked conversation. Enable it in the Npcify dashboard.");
            }

            var conversation = await http.GetByteArrayAsync(avatarUrl + "/baked-conversation/asset");
            // Validates the asset before it replaces the shipped file.
            Npcify.Sdk.Conversation.NpcifyBakedConversation.FromBytes(conversation, characterId);
            File.WriteAllBytes(Path.Combine(root, OfflineCharacterSource.ConversationFile), conversation);

            var vrmUrl = JsonUtility.FromJson<DownloadUrl>(await http.GetStringAsync(avatarUrl + "/download-url")).url;
            File.WriteAllBytes(Path.Combine(root, OfflineCharacterSource.VrmFile), await DownloadUnsignedAsync(vrmUrl));

            var animationIds = new List<string>();
            foreach (var animation in info.animations)
            {
                var url = JsonUtility.FromJson<DownloadUrl>(
                    await http.GetStringAsync($"{avatarUrl}/animations/{Uri.EscapeDataString(animation.id)}/download-url")).url;
                File.WriteAllBytes(Path.Combine(animationsRoot, animation.id + ".vrma"), await DownloadUnsignedAsync(url));
                animationIds.Add(animation.id);
            }

            var manifest = new OfflineManifest
            {
                characterId = characterId,
                name = info.name,
                idleAnimationId = info.idleAnimationId,
                bakedConversationRevision = info.bakedConversationRevision,
                animations = animationIds.ToArray(),
            };
            File.WriteAllText(Path.Combine(root, OfflineCharacterSource.ManifestFile), JsonUtility.ToJson(manifest, true) + "\n");
        }

        private static async Task<byte[]> DownloadUnsignedAsync(string signedUrl)
        {
            // Signed storage URLs must not receive the API key.
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            return await http.GetByteArrayAsync(signedUrl);
        }

        private sealed class DownloadRequest
        {
            public string ApiKey;
            public string CharacterId;
            public string AvatarUrl;
            public string Root;
        }

        [Serializable]
        private sealed class CharacterInfo
        {
            public string name;
            public string idleAnimationId;
            public bool bakedConversationEnabled;
            public int bakedConversationRevision;
            public AnimationEntry[] animations = Array.Empty<AnimationEntry>();
        }

        [Serializable]
        private sealed class AnimationEntry
        {
            public string id;
            public string name;
        }

        [Serializable]
        private sealed class DownloadUrl
        {
            public string url;
        }

        [Serializable]
        private sealed class OfflineManifest
        {
            public string characterId;
            public string name;
            public string idleAnimationId;
            public int bakedConversationRevision;
            public string[] animations;
        }
    }
}
