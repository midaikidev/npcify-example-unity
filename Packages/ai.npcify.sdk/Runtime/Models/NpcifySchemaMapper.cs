using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Npcify.Sdk.Protocol;

namespace Npcify.Sdk.Models
{
    /// <summary>
    /// Converts decoded MessagePack dictionaries and lists into strongly typed Npcify SDK models.
    /// </summary>
    public static class NpcifySchemaMapper
    {
        public static StagingResultPayload DecodeStaging(byte[] npcpBytes)
        {
            var raw = NpcifyPerformanceCodec.Decode(npcpBytes, NpcifyPerformanceKind.Staging);
            return MapStagingResultPayload(RequireMap(raw, "staging"));
        }

        public static ConversationCreationResult DecodeConversation(byte[] npcpBytes)
        {
            var raw = NpcifyPerformanceCodec.Decode(npcpBytes, NpcifyPerformanceKind.Conversation);
            var map = RequireMap(raw, "conversation");
            var conversationMap = GetMap(map, "conversation");
            var id = conversationMap != null ? GetString(conversationMap, "id") : GetString(map, "conversationId");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidDataException("Missing conversation ID in conversation response.");
            }

            var openingRaw = GetMap(map, "openingStaging") ?? throw new InvalidDataException("Missing openingStaging.");
            return new ConversationCreationResult
            {
                ConversationId = id,
                OpeningStaging = MapStagingResultPayload(openingRaw),
                ChatPricePoints = (int)GetLong(map, "chatPricePoints", 0),
            };
        }

        public static BakedConversationAsset DecodeBakedConversation(byte[] npcpBytes)
        {
            var raw = NpcifyPerformanceCodec.Decode(npcpBytes, NpcifyPerformanceKind.Baked);
            return MapBakedConversationAsset(RequireMap(raw, "baked"));
        }

        public static byte[] EncodeBakedConversation(BakedConversationAsset asset, bool compress = true)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var stagings = new List<object>(asset.Stagings.Count);
            foreach (var node in asset.Stagings)
            {
                stagings.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = node.Id,
                    ["parentId"] = node.ParentId,
                    ["triggerText"] = node.TriggerText,
                    ["payload"] = ToMap(node.Payload),
                });
            }

            var root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["format"] = asset.Format,
                ["version"] = (long)asset.Version,
                ["avatarId"] = asset.AvatarId,
                ["revision"] = (long)asset.Revision,
                ["rootStagingId"] = asset.RootStagingId,
                ["stagings"] = stagings,
            };
            return NpcifyPerformanceCodec.Encode(NpcifyPerformanceKind.Baked, root, compress);
        }

        public static StagingResultPayload MapStagingResultPayload(Dictionary<string, object> map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            var stagingId = GetString(map, "stagingId");
            if (string.IsNullOrWhiteSpace(stagingId))
            {
                throw new InvalidDataException("Staging payload is missing stagingId.");
            }

            var timelineMap = GetMap(map, "timeline") ?? new Dictionary<string, object>(StringComparer.Ordinal);
            var optionsList = GetList(map, "options");
            var options = new List<StagingReplyOption>();
            if (optionsList != null)
            {
                foreach (var item in optionsList)
                {
                    if (item is Dictionary<string, object> optMap)
                    {
                        options.Add(new StagingReplyOption
                        {
                            Label = GetString(optMap, "label") ?? string.Empty,
                            End = GetBool(optMap, "end", false),
                        });
                    }
                }
            }

            return new StagingResultPayload
            {
                ProtocolVersion = (int)GetLong(map, "protocolVersion", 2),
                TaskId = GetString(map, "taskId") ?? string.Empty,
                StagingId = stagingId,
                Text = GetString(map, "text") ?? string.Empty,
                Timeline = MapTimeline(timelineMap),
                Options = options,
            };
        }

        public static StagingTimeline MapTimeline(Dictionary<string, object> map)
        {
            var timeline = new StagingTimeline
            {
                Duration = Math.Max(0.0, GetDouble(map, "duration", 0.0)),
            };

            var dialogueList = GetList(map, "dialogue");
            if (dialogueList != null)
            {
                foreach (var item in dialogueList)
                {
                    if (item is Dictionary<string, object> blockMap)
                    {
                        timeline.Dialogue.Add(MapDialogueBlock(blockMap));
                    }
                }
            }

            var expressionList = GetList(map, "expressions");
            if (expressionList != null)
            {
                foreach (var item in expressionList)
                {
                    if (item is Dictionary<string, object> clipMap)
                    {
                        // The server sends { expression: { name, intensity, weight } }.
                        var expressionMap = GetMap(clipMap, "expression");
                        timeline.Expressions.Add(new StagingExpressionClip
                        {
                            Id = GetString(clipMap, "id") ?? string.Empty,
                            Expression = (expressionMap != null ? GetString(expressionMap, "name") : GetString(clipMap, "expression"))
                                ?? string.Empty,
                            StartTime = GetDouble(clipMap, "startTime", 0.0),
                            EndTime = GetDouble(clipMap, "endTime", 0.0),
                            Weight = expressionMap != null
                                ? GetDouble(expressionMap, "weight", GetDouble(clipMap, "weight", 1.0))
                                : GetDouble(clipMap, "weight", 1.0),
                        });
                    }
                }
            }

            var animationList = GetList(map, "animations");
            if (animationList != null)
            {
                foreach (var item in animationList)
                {
                    if (item is Dictionary<string, object> clipMap)
                    {
                        timeline.Animations.Add(new StagingAnimationClip
                        {
                            Id = GetString(clipMap, "id") ?? string.Empty,
                            Animation = GetString(clipMap, "animation") ?? string.Empty,
                            StartTime = GetDouble(clipMap, "startTime", 0.0),
                            EndTime = GetDouble(clipMap, "endTime", 0.0),
                            Loop = GetBool(clipMap, "loop", false),
                        });
                    }
                }
            }

            return timeline;
        }

        public static StagingDialogueBlock MapDialogueBlock(Dictionary<string, object> map)
        {
            var type = GetString(map, "type") ?? "speech";
            var startTime = GetDouble(map, "startTime", 0.0);
            var endTime = GetDouble(map, "endTime", startTime);
            var block = new StagingDialogueBlock
            {
                Id = GetString(map, "id") ?? string.Empty,
                Type = type,
                StartTime = startTime,
                EndTime = endTime,
                Text = GetString(map, "text") ?? string.Empty,
                Duration = GetDouble(map, "duration", Math.Max(0.0, endTime - startTime)),
            };

            var audioMap = GetMap(map, "audio");
            if (audioMap != null)
            {
                var audioType = GetString(audioMap, "type") ?? "silent";
                byte[] audioBytes = null;
                if (audioMap.TryGetValue("data", out var dataVal))
                {
                    if (dataVal is byte[] rawBytes)
                    {
                        audioBytes = rawBytes;
                    }
                    else if (dataVal is string b64)
                    {
                        audioBytes = Convert.FromBase64String(b64);
                        if (audioType == "base64")
                        {
                            audioType = "bytes";
                        }
                    }
                }

                block.Audio = new StagingAudioSource
                {
                    Type = audioType,
                    MimeType = GetString(audioMap, "mimeType") ?? "audio/mpeg",
                    Data = audioBytes,
                    Url = GetString(audioMap, "url"),
                    StreamId = GetString(audioMap, "streamId"),
                    Duration = GetDouble(audioMap, "duration", block.Duration),
                };
            }

            var visemeList = GetList(map, "visemes");
            if (visemeList != null)
            {
                foreach (var item in visemeList)
                {
                    if (item is Dictionary<string, object> cueMap)
                    {
                        // The server sends { name, timestamp, endTime?, weight? }.
                        var cueTime = GetDouble(cueMap, "timestamp", GetDouble(cueMap, "time", 0.0));
                        var cueEnd = GetDouble(cueMap, "endTime", -1.0);
                        block.Visemes.Add(new StagingVisemeCue
                        {
                            Viseme = GetString(cueMap, "name") ?? GetString(cueMap, "viseme") ?? string.Empty,
                            Time = cueTime,
                            Duration = cueEnd > cueTime ? cueEnd - cueTime : GetDouble(cueMap, "duration", 0.08),
                            Weight = GetDouble(cueMap, "weight", 1.0),
                        });
                    }
                }
            }

            var wordList = GetList(map, "wordTimings") ?? GetList(map, "words");
            if (wordList != null)
            {
                foreach (var item in wordList)
                {
                    if (item is Dictionary<string, object> cueMap)
                    {
                        block.Words.Add(new StagingWordCue
                        {
                            Word = GetString(cueMap, "word") ?? string.Empty,
                            StartTime = GetDouble(cueMap, "startTime", 0.0),
                            EndTime = GetDouble(cueMap, "endTime", 0.0),
                        });
                    }
                }
            }

            var charList = GetList(map, "characterTimings") ?? GetList(map, "characters");
            if (charList != null)
            {
                foreach (var item in charList)
                {
                    if (item is Dictionary<string, object> cueMap)
                    {
                        block.Characters.Add(new StagingCharacterCue
                        {
                            Character = GetString(cueMap, "character") ?? string.Empty,
                            StartTime = GetDouble(cueMap, "startTime", 0.0),
                            EndTime = GetDouble(cueMap, "endTime", 0.0),
                        });
                    }
                }
            }

            return block;
        }

        public static BakedConversationAsset MapBakedConversationAsset(Dictionary<string, object> map)
        {
            var asset = new BakedConversationAsset
            {
                Format = GetString(map, "format") ?? string.Empty,
                Version = (int)GetLong(map, "version", 0),
                AvatarId = GetString(map, "avatarId") ?? string.Empty,
                Revision = (int)GetLong(map, "revision", 0),
                RootStagingId = GetString(map, "rootStagingId"),
            };

            var stagingsList = GetList(map, "stagings");
            if (stagingsList == null)
            {
                throw new InvalidDataException("Unsupported baked conversation asset.");
            }

            foreach (var item in stagingsList)
            {
                if (!(item is Dictionary<string, object> nodeMap))
                {
                    throw new InvalidDataException("Invalid baked performance node.");
                }

                var payloadMap = GetMap(nodeMap, "payload") ?? throw new InvalidDataException("Missing baked node payload.");
                asset.Stagings.Add(new BakedConversationNode
                {
                    Id = GetString(nodeMap, "id") ?? string.Empty,
                    ParentId = GetString(nodeMap, "parentId"),
                    TriggerText = GetString(nodeMap, "triggerText"),
                    Payload = MapStagingResultPayload(payloadMap),
                });
            }

            return asset;
        }

        public static Dictionary<string, object> ToMap(StagingResultPayload payload)
        {
            var dialogue = new List<object>(payload.Timeline.Dialogue.Count);
            foreach (var block in payload.Timeline.Dialogue)
            {
                var blockMap = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = block.Id,
                    ["type"] = block.Type,
                    ["startTime"] = block.StartTime,
                    ["endTime"] = block.EndTime,
                };
                if (block.Type == "pause")
                {
                    blockMap["duration"] = block.Duration;
                }
                else
                {
                    blockMap["text"] = block.Text;
                    var audioMap = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["type"] = block.Audio.Type,
                    };
                    if (block.Audio.Type == "bytes" && block.Audio.Data != null)
                    {
                        audioMap["mimeType"] = block.Audio.MimeType ?? "audio/mpeg";
                        audioMap["data"] = block.Audio.Data;
                    }
                    else if (block.Audio.Type == "silent")
                    {
                        audioMap["duration"] = block.Audio.Duration;
                    }
                    else if (!string.IsNullOrEmpty(block.Audio.Url))
                    {
                        audioMap["url"] = block.Audio.Url;
                    }

                    blockMap["audio"] = audioMap;
                }

                dialogue.Add(blockMap);
            }

            var options = new List<object>(payload.Options.Count);
            foreach (var option in payload.Options)
            {
                options.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["label"] = option.Label,
                    ["end"] = option.End,
                });
            }

            var animations = new List<object>(payload.Timeline.Animations.Count);
            foreach (var clip in payload.Timeline.Animations)
            {
                animations.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = clip.Id,
                    ["animation"] = clip.Animation,
                    ["startTime"] = clip.StartTime,
                    ["endTime"] = clip.EndTime,
                    ["loop"] = clip.Loop,
                });
            }

            var expressions = new List<object>(payload.Timeline.Expressions.Count);
            foreach (var clip in payload.Timeline.Expressions)
            {
                expressions.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = clip.Id,
                    ["expression"] = clip.Expression,
                    ["startTime"] = clip.StartTime,
                    ["endTime"] = clip.EndTime,
                    ["weight"] = clip.Weight,
                });
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["protocolVersion"] = (long)payload.ProtocolVersion,
                ["taskId"] = payload.TaskId ?? string.Empty,
                ["stagingId"] = payload.StagingId,
                ["text"] = payload.Text ?? string.Empty,
                ["timeline"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["duration"] = payload.Timeline.Duration,
                    ["dialogue"] = dialogue,
                    ["expressions"] = expressions,
                    ["animations"] = animations,
                },
                ["options"] = options,
            };
        }

        private static Dictionary<string, object> RequireMap(object value, string context)
        {
            if (value is Dictionary<string, object> map)
            {
                return map;
            }

            throw new InvalidDataException($"Expected MessagePack map for {context}.");
        }

        private static Dictionary<string, object> GetMap(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var val) ? val as Dictionary<string, object> : null;
        }

        private static IList GetList(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var val) ? val as IList : null;
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var val) ? val as string : null;
        }

        private static bool GetBool(Dictionary<string, object> map, string key, bool fallback)
        {
            return map.TryGetValue(key, out var val) && val is bool b ? b : fallback;
        }

        private static long GetLong(Dictionary<string, object> map, string key, long fallback)
        {
            if (!map.TryGetValue(key, out var val) || val == null)
            {
                return fallback;
            }

            if (val is long l)
            {
                return l;
            }

            if (val is double d)
            {
                return (long)d;
            }

            return fallback;
        }

        private static double GetDouble(Dictionary<string, object> map, string key, double fallback)
        {
            if (!map.TryGetValue(key, out var val) || val == null)
            {
                return fallback;
            }

            if (val is double d)
            {
                return d;
            }

            if (val is long l)
            {
                return l;
            }

            return fallback;
        }
    }
}
