using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Api;
using UnityEngine;

namespace NpcifyDemo
{
    /// <summary>Character data that an NPC needs before it can appear in the scene.</summary>
    public sealed class NpcCharacterManifest
    {
        public string CharacterId;
        public string Name;
        public string IdleAnimationId = "idle";
        public bool AllowCustomReply;
    }

    public interface INpcCharacterSource
    {
        Task<NpcCharacterManifest> LoadManifestAsync(CancellationToken cancellationToken);
        Task<byte[]> LoadVrmAsync(CancellationToken cancellationToken);
        Task<byte[]> LoadAnimationAsync(string animationId);
    }

    /// <summary>
    /// Offline character: every file is in <c>StreamingAssets/Npcify/&lt;folder&gt;</c>.
    /// Use the <c>Npcify &gt; Download Offline Character</c> menu to refresh the files.
    /// </summary>
    public sealed class OfflineCharacterSource : INpcCharacterSource
    {
        public const string ManifestFile = "character.json";
        public const string VrmFile = "character.vrm";
        public const string ConversationFile = "conversation.npcp";
        public const string AnimationsFolder = "animations";

        private readonly string _root;

        public OfflineCharacterSource(string folder)
        {
            _root = Path.Combine(Application.streamingAssetsPath, "Npcify", folder);
        }

        public string RootPath => _root;

        public bool Exists => File.Exists(Path.Combine(_root, ManifestFile));

        public async Task<NpcCharacterManifest> LoadManifestAsync(CancellationToken cancellationToken)
        {
            var json = await ReadTextAsync(Path.Combine(_root, ManifestFile), cancellationToken);
            var file = JsonUtility.FromJson<OfflineManifestFile>(json);
            return new NpcCharacterManifest
            {
                CharacterId = file.characterId,
                Name = file.name,
                IdleAnimationId = string.IsNullOrEmpty(file.idleAnimationId) ? "idle" : file.idleAnimationId,
                AllowCustomReply = false,
            };
        }

        public Task<byte[]> LoadVrmAsync(CancellationToken cancellationToken)
        {
            return ReadBytesAsync(Path.Combine(_root, VrmFile), cancellationToken);
        }

        public Task<byte[]> LoadConversationAsync(CancellationToken cancellationToken)
        {
            return ReadBytesAsync(Path.Combine(_root, ConversationFile), cancellationToken);
        }

        public Task<byte[]> LoadAnimationAsync(string animationId)
        {
            return ReadBytesAsync(Path.Combine(_root, AnimationsFolder, animationId + ".vrma"), CancellationToken.None);
        }

        private static Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
        {
            return Task.Run(() => File.ReadAllBytes(path), cancellationToken);
        }

        private static Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
        {
            return Task.Run(() => File.ReadAllText(path), cancellationToken);
        }

        [Serializable]
        private sealed class OfflineManifestFile
        {
            public string characterId;
            public string name;
            public string idleAnimationId;
        }
    }

    /// <summary>Online character: the model, animations and every reply come from the Npcify API.</summary>
    public sealed class OnlineCharacterSource : INpcCharacterSource
    {
        private readonly NpcifyApiClient _api;

        public OnlineCharacterSource(NpcifyApiClient api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public async Task<NpcCharacterManifest> LoadManifestAsync(CancellationToken cancellationToken)
        {
            var info = await _api.GetInfoAsync(cancellationToken);
            return new NpcCharacterManifest
            {
                CharacterId = _api.Options.CharacterId,
                Name = info.Name,
                IdleAnimationId = string.IsNullOrEmpty(info.IdleAnimationId) ? "idle" : info.IdleAnimationId,
                AllowCustomReply = info.AllowCustomReply,
            };
        }

        public async Task<byte[]> LoadVrmAsync(CancellationToken cancellationToken)
        {
            var url = await _api.GetVrmDownloadUrlAsync(cancellationToken: cancellationToken);
            return await _api.DownloadBytesAsync(url, cancellationToken);
        }

        public async Task<byte[]> LoadAnimationAsync(string animationId)
        {
            var url = await _api.GetVrmaDownloadUrlAsync(animationId);
            return await _api.DownloadBytesAsync(url);
        }
    }
}
