using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;
using Npcify.Sdk.Protocol;

namespace Npcify.Sdk.Api
{
    public sealed partial class NpcifyApiClient
    {
        public Task<string> GetVariantVrmDownloadUrlAsync(
            string variantKey,
            CancellationToken cancellationToken = default
        )
        {
            return GetVariantVrmDownloadUrlAsync(_options.CharacterId, variantKey, null, cancellationToken);
        }

        public async Task<string> GetVariantVrmDownloadUrlAsync(
            string avatarId,
            string variantKey,
            string assetProfile = null,
            CancellationToken cancellationToken = default
        )
        {
            var targetAvatarId = string.IsNullOrWhiteSpace(avatarId) ? _options.CharacterId : avatarId.Trim();
            if (string.IsNullOrWhiteSpace(targetAvatarId))
            {
                throw new ArgumentException("Avatar ID is required.", nameof(avatarId));
            }
            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException("Variant key is required.", nameof(variantKey));
            }

            var trimmedVariant = variantKey.Trim();
            string url;
            if (string.Equals(trimmedVariant, NpcifyVariantSummary.DefaultKey, StringComparison.Ordinal))
            {
                url = $"{BaseUrl}/avatars/{Uri.EscapeDataString(targetAvatarId)}/download-url";
            }
            else
            {
                url =
                    $"{BaseUrl}/avatars/{Uri.EscapeDataString(targetAvatarId)}/variants/{Uri.EscapeDataString(trimmedVariant)}/download-url";
            }

            if (!string.IsNullOrWhiteSpace(assetProfile) &&
                !string.Equals(assetProfile, "original", StringComparison.Ordinal))
            {
                url += "?assetProfile=" + Uri.EscapeDataString(assetProfile);
            }

            using (var request = CreateRequest(HttpMethod.Get, url, "application/json"))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ExtractJsonStringField(json, "url");
            }
        }

        public async Task<ConversationWorldline> GetConversationWorldlineAsync(
            string conversationId,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                throw new ArgumentException("Conversation ID is required.", nameof(conversationId));
            }

            var url = $"{BaseUrl}/conversations/{Uri.EscapeDataString(conversationId)}/worldline";
            using (var request = CreateRequest(HttpMethod.Get, url, "application/json"))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseWorldlineJson(json);
            }
        }

        internal static ConversationWorldline ParseWorldlineJson(string json)
        {
            if (!(NpcifyJson.TryParse(json) is Dictionary<string, object> map))
            {
                throw new InvalidDataException("The conversation worldline is not a JSON object.");
            }

            var worldline = new ConversationWorldline
            {
                ConversationId = (InfoField(map, "conversationId") as string) ?? string.Empty,
                OpeningStagingId = InfoField(map, "openingStagingId") as string,
                ActiveStagingId = InfoField(map, "activeStagingId") as string,
            };

            if (InfoField(map, "stagings") is IList stagingsList)
            {
                foreach (var item in stagingsList)
                {
                    if (!(item is Dictionary<string, object> nodeMap))
                    {
                        continue;
                    }

                    var options = new List<StagingReplyOption>();
                    if (InfoField(nodeMap, "options") is IList optList)
                    {
                        foreach (var optItem in optList)
                        {
                            if (optItem is Dictionary<string, object> optMap)
                            {
                                options.Add(new StagingReplyOption
                                {
                                    Label = (InfoField(optMap, "label") as string) ?? string.Empty,
                                    End = InfoField(optMap, "end") is bool endFlag && endFlag,
                                });
                            }
                        }
                    }

                    worldline.Stagings.Add(new ConversationWorldlineNode
                    {
                        Id = (InfoField(nodeMap, "id") as string) ?? string.Empty,
                        ParentId = InfoField(nodeMap, "parentId") as string,
                        TriggerText = InfoField(nodeMap, "triggerText") as string,
                        Text = (InfoField(nodeMap, "text") as string) ?? string.Empty,
                        Options = options,
                        CreatedAt = (InfoField(nodeMap, "createdAt") as string) ?? "1970-01-01T00:00:00.000Z",
                        InitialVariant = InfoField(nodeMap, "initialVariant") as string,
                        ActiveVariant = InfoField(nodeMap, "activeVariant") as string,
                        ToolCalls = NpcifySchemaMapper.MapToolCalls(InfoField(nodeMap, "toolCalls") as IList),
                    });
                }
            }

            return worldline;
        }

        private static void PopulateInfoCollections(Dictionary<string, object> info, NpcifyAssetInfo result)
        {
            if (InfoField(info, "animations") is IList animList)
            {
                foreach (var entry in animList)
                {
                    if (entry is Dictionary<string, object> animMap)
                    {
                        result.Animations.Add(new NpcifyAnimationEntry
                        {
                            Id = (InfoField(animMap, "id") as string) ?? string.Empty,
                            Name = (InfoField(animMap, "name") as string) ?? string.Empty,
                            ObjectKey = (InfoField(animMap, "objectKey") as string) ?? string.Empty,
                        });
                    }
                }
            }

            if (InfoField(info, "variants") is IList variantList)
            {
                foreach (var entry in variantList)
                {
                    if (entry is Dictionary<string, object> varMap)
                    {
                        result.Variants.Add(new NpcifyVariantSummary
                        {
                            Key = (InfoField(varMap, "key") as string) ?? NpcifyVariantSummary.DefaultKey,
                            Label = (InfoField(varMap, "label") as string) ?? string.Empty,
                            Description = (InfoField(varMap, "description") as string) ?? string.Empty,
                            R18 = InfoField(varMap, "r18") is bool r18 && r18,
                            PortraitUrl = InfoField(varMap, "portraitUrl") as string,
                            CreditText = InfoField(varMap, "creditText") as string,
                        });
                    }
                }
            }
        }
    }
}
