using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Conversation
{
    public sealed class BakedReplyResult
    {
        public StagingResultPayload Payload { get; set; }
        public bool Ended { get; set; }
    }

    /// <summary>
    /// Offline in-memory conversation runtime for baked NPCP assets (`npcify.baked-conversation`).
    /// </summary>
    public sealed class NpcifyBakedConversation
    {
        private readonly BakedConversationAsset _asset;
        private readonly Dictionary<string, BakedConversationNode> _nodes =
            new Dictionary<string, BakedConversationNode>(StringComparer.Ordinal);
        private string _activeId;

        public NpcifyBakedConversation(BakedConversationAsset asset, string expectedCharacterId = null)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            if (!string.Equals(asset.Format, BakedConversationAsset.ExpectedFormat, StringComparison.Ordinal) ||
                asset.Version != BakedConversationAsset.ExpectedVersion ||
                (!string.IsNullOrWhiteSpace(expectedCharacterId) &&
                 !string.Equals(asset.AvatarId, expectedCharacterId, StringComparison.Ordinal)) ||
                asset.Stagings == null)
            {
                throw new InvalidDataException("Unsupported baked conversation asset.");
            }

            _asset = asset;

            for (var i = 0; i < asset.Stagings.Count; i++)
            {
                var node = asset.Stagings[i];
                if (node == null ||
                    string.IsNullOrWhiteSpace(node.Id) ||
                    _nodes.ContainsKey(node.Id) ||
                    node.Payload == null ||
                    !string.Equals(node.Payload.StagingId, node.Id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Invalid baked performance ID.");
                }

                var dialogue = node.Payload.Timeline?.Dialogue;
                if (dialogue != null)
                {
                    for (var d = 0; d < dialogue.Count; d++)
                    {
                        var block = dialogue[d];
                        if (block != null &&
                            string.Equals(block.Type, "speech", StringComparison.Ordinal))
                        {
                            var audioType = block.Audio?.Type;
                            var hasEmbeddedAudio =
                                (string.Equals(audioType, "bytes", StringComparison.Ordinal) ||
                                 string.Equals(audioType, "base64", StringComparison.Ordinal)) &&
                                block.Audio.Data != null &&
                                block.Audio.Data.Length > 0;
                            var isSilent = string.Equals(audioType, "silent", StringComparison.Ordinal);

                            if (!hasEmbeddedAudio && !isSilent)
                            {
                                throw new InvalidDataException("Baked performances must embed all audio.");
                            }
                        }
                    }
                }

                _nodes[node.Id] = node;
            }

            var roots = _nodes.Values.Where(n => string.IsNullOrEmpty(n.ParentId)).ToList();
            if ((_nodes.Count > 0 &&
                 (roots.Count != 1 || !string.Equals(roots[0].Id, asset.RootStagingId, StringComparison.Ordinal))) ||
                (_nodes.Count == 0 && !string.IsNullOrEmpty(asset.RootStagingId)))
            {
                throw new InvalidDataException("Invalid baked conversation opening.");
            }

            foreach (var node in _nodes.Values)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var current = node;
                while (current != null)
                {
                    if (!seen.Add(current.Id))
                    {
                        throw new InvalidDataException("Baked conversation contains a cycle.");
                    }

                    if (!string.IsNullOrEmpty(current.ParentId) && !_nodes.ContainsKey(current.ParentId))
                    {
                        throw new InvalidDataException("Baked conversation has a missing parent.");
                    }

                    current = !string.IsNullOrEmpty(current.ParentId) && _nodes.TryGetValue(current.ParentId, out var parent)
                        ? parent
                        : null;
                }

                var options = node.Payload.Options;
                if (options != null)
                {
                    for (var o = 0; o < options.Count; o++)
                    {
                        var option = options[o];
                        if (option != null && !option.End && FindChild(node.Id, option.Label) == null)
                        {
                            throw new InvalidDataException("Baked conversation has an unrecorded option.");
                        }
                    }
                }
            }
        }

        public static NpcifyBakedConversation FromBytes(byte[] npcpBytes, string expectedCharacterId = null)
        {
            var asset = NpcifySchemaMapper.DecodeBakedConversation(npcpBytes);
            return new NpcifyBakedConversation(asset, expectedCharacterId);
        }

        public BakedConversationAsset Asset => _asset;

        public string ActiveStagingId => _activeId;

        public BakedConversationNode ActiveNode =>
            !string.IsNullOrEmpty(_activeId) && _nodes.TryGetValue(_activeId, out var node)
                ? node
                : null;

        public IReadOnlyList<StagingReplyOption> CurrentOptions =>
            ActiveNode?.Payload?.Options ?? (IReadOnlyList<StagingReplyOption>)Array.Empty<StagingReplyOption>();

        public void AssertAnimationsAvailable(IEnumerable<string> availableAnimationIds)
        {
            var available = new HashSet<string>(
                availableAnimationIds ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal
            );

            for (var i = 0; i < _asset.Stagings.Count; i++)
            {
                var animations = _asset.Stagings[i]?.Payload?.Timeline?.Animations;
                if (animations == null)
                {
                    continue;
                }

                for (var a = 0; a < animations.Count; a++)
                {
                    var clip = animations[a];
                    if (clip != null &&
                        !string.IsNullOrEmpty(clip.Animation) &&
                        !available.Contains(clip.Animation))
                    {
                        throw new InvalidOperationException(
                            $"Recorded animation {clip.Animation} is unavailable. Restore it or regenerate the performance."
                        );
                    }
                }
            }
        }

        public StagingResultPayload Start()
        {
            if (string.IsNullOrEmpty(_asset.RootStagingId))
            {
                throw new InvalidOperationException("This character has no recorded conversation yet.");
            }
            return Select(_asset.RootStagingId);
        }

        public StagingResultPayload Select(string stagingId)
        {
            if (string.IsNullOrEmpty(stagingId) || !_nodes.TryGetValue(stagingId, out var node))
            {
                throw new InvalidOperationException("Baked performance not found.");
            }

            _activeId = node.Id;
            return node.Payload;
        }

        public BakedReplyResult Reply(string message, string parentStagingId = null)
        {
            var targetParentId = !string.IsNullOrEmpty(parentStagingId) ? parentStagingId : (_activeId ?? string.Empty);
            if (!_nodes.TryGetValue(targetParentId, out var parent))
            {
                throw new InvalidOperationException("Choose one of the recorded reply options.");
            }

            var trimmed = (message ?? string.Empty).Trim();
            StagingReplyOption matchedOption = null;
            if (parent.Payload.Options != null)
            {
                for (var i = 0; i < parent.Payload.Options.Count; i++)
                {
                    var opt = parent.Payload.Options[i];
                    if (opt != null && string.Equals(opt.Label, trimmed, StringComparison.Ordinal))
                    {
                        matchedOption = opt;
                        break;
                    }
                }
            }

            if (matchedOption == null)
            {
                throw new InvalidOperationException("Choose one of the recorded reply options.");
            }

            if (matchedOption.End)
            {
                _activeId = null;
                return new BakedReplyResult
                {
                    Payload = new StagingResultPayload
                    {
                        ProtocolVersion = parent.Payload.ProtocolVersion,
                        TaskId = parent.Payload.TaskId,
                        StagingId = parent.Payload.StagingId,
                        Text = parent.Payload.Text,
                        Timeline = parent.Payload.Timeline,
                        Options = new List<StagingReplyOption>(),
                    },
                    Ended = true,
                };
            }

            var child = FindChild(parent.Id, matchedOption.Label);
            if (child == null)
            {
                throw new InvalidOperationException("This reply has not been recorded.");
            }

            return new BakedReplyResult
            {
                Payload = Select(child.Id),
                Ended = false,
            };
        }

        public ConversationWorldline Worldline(string conversationId = null)
        {
            var nodes = new List<ConversationWorldlineNode>(_asset.Stagings.Count);
            for (var i = 0; i < _asset.Stagings.Count; i++)
            {
                var node = _asset.Stagings[i];
                nodes.Add(new ConversationWorldlineNode
                {
                    Id = node.Id,
                    ParentId = node.ParentId,
                    TriggerText = node.TriggerText,
                    Text = node.Payload?.Text ?? string.Empty,
                    Options = node.Payload?.Options != null
                        ? new List<StagingReplyOption>(node.Payload.Options)
                        : new List<StagingReplyOption>(),
                    CreatedAt = "1970-01-01T00:00:00.000Z",
                });
            }

            return new ConversationWorldline
            {
                ConversationId = string.IsNullOrEmpty(conversationId) ? "baked:" + _asset.AvatarId : conversationId,
                OpeningStagingId = _asset.RootStagingId,
                ActiveStagingId = _activeId,
                Stagings = nodes,
            };
        }

        private BakedConversationNode FindChild(string parentId, string label)
        {
            for (var i = 0; i < _asset.Stagings.Count; i++)
            {
                var node = _asset.Stagings[i];
                if (node != null &&
                    string.Equals(node.ParentId, parentId, StringComparison.Ordinal) &&
                    string.Equals(node.TriggerText, label, StringComparison.Ordinal))
                {
                    return node;
                }
            }
            return null;
        }
    }
}
