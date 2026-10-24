using System;
using System.Collections.Generic;

namespace Npcify.Sdk.Models
{
    [Serializable]
    public sealed class NpcifyOptions
    {
        public string BaseUrl = "https://api.npcify.ai";
        public string CharacterId = string.Empty;
        public string ApiKey = string.Empty;
        public string ApiToken = string.Empty;
        public bool ServerRendering;
        public bool Authoring;
        public byte[] BakedConversationBytes;
        public BakedConversationAsset BakedConversationAsset;
        public float Volume = 1f;
        public bool Muted;

        public string ResolvedToken =>
            !string.IsNullOrWhiteSpace(ApiToken)
                ? ApiToken.Trim()
                : !string.IsNullOrWhiteSpace(ApiKey)
                    ? ApiKey.Trim()
                    : string.Empty;
    }

    [Serializable]
    public sealed class NpcifyAnimationEntry
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string ObjectKey = string.Empty;
    }

    [Serializable]
    public sealed class NpcifyAssetInfo
    {
        public List<NpcifyAnimationEntry> Animations = new List<NpcifyAnimationEntry>();
        public string Name;
        public string IdleAnimationId;
        public bool AllowCustomReply;
        public bool BakedConversationEnabled;
        public int BakedConversationRevision;
    }

    [Serializable]
    public sealed class StagingVisemeCue
    {
        public string Viseme = string.Empty;
        public double Time;
        public double Duration;
        public double Weight = 1.0;
    }

    [Serializable]
    public sealed class StagingWordCue
    {
        public string Word = string.Empty;
        public double StartTime;
        public double EndTime;
    }

    [Serializable]
    public sealed class StagingCharacterCue
    {
        public string Character = string.Empty;
        public double StartTime;
        public double EndTime;
    }

    [Serializable]
    public sealed class StagingAudioSource
    {
        public string Type = "silent";
        public string MimeType = "audio/mpeg";
        public byte[] Data;
        public string Base64Data;
        public string Url;
        public string StreamId;
        public double Duration;
    }

    [Serializable]
    public sealed class StagingDialogueBlock
    {
        public string Id = string.Empty;
        public string Type = "speech";
        public double StartTime;
        public double EndTime;
        public string Text = string.Empty;
        public double Duration;
        public StagingAudioSource Audio = new StagingAudioSource();
        public List<StagingVisemeCue> Visemes = new List<StagingVisemeCue>();
        public List<StagingWordCue> Words = new List<StagingWordCue>();
        public List<StagingCharacterCue> Characters = new List<StagingCharacterCue>();
    }

    [Serializable]
    public sealed class StagingExpressionClip
    {
        public string Id = string.Empty;
        public string Expression = string.Empty;
        public double StartTime;
        public double EndTime;
        public double Weight = 1.0;
    }

    [Serializable]
    public sealed class StagingAnimationClip
    {
        public string Id = string.Empty;
        public string Animation = string.Empty;
        public double StartTime;
        public double EndTime;
        public bool Loop;
    }

    [Serializable]
    public sealed class StagingTimeline
    {
        public double Duration;
        public List<StagingDialogueBlock> Dialogue = new List<StagingDialogueBlock>();
        public List<StagingExpressionClip> Expressions = new List<StagingExpressionClip>();
        public List<StagingAnimationClip> Animations = new List<StagingAnimationClip>();
    }

    [Serializable]
    public sealed class StagingReplyOption
    {
        public string Label = string.Empty;
        public bool End;
    }

    [Serializable]
    public sealed class StagingResultPayload
    {
        public int ProtocolVersion = 2;
        public string TaskId = string.Empty;
        public string StagingId = string.Empty;
        public string Text = string.Empty;
        public StagingTimeline Timeline = new StagingTimeline();
        public List<StagingReplyOption> Options = new List<StagingReplyOption>();
    }

    [Serializable]
    public sealed class ConversationCreationResult
    {
        public string ConversationId = string.Empty;
        public StagingResultPayload OpeningStaging = new StagingResultPayload();
        public int ChatPricePoints;
    }

    [Serializable]
    public sealed class ConversationWorldlineNode
    {
        public string Id = string.Empty;
        public string ParentId;
        public string TriggerText;
        public string Text = string.Empty;
        public List<StagingReplyOption> Options = new List<StagingReplyOption>();
        public string CreatedAt = "1970-01-01T00:00:00.000Z";
    }

    [Serializable]
    public sealed class ConversationWorldline
    {
        public string ConversationId = string.Empty;
        public string OpeningStagingId;
        public string ActiveStagingId;
        public List<ConversationWorldlineNode> Stagings = new List<ConversationWorldlineNode>();
    }

    [Serializable]
    public sealed class BakedConversationNode
    {
        public string Id = string.Empty;
        public string ParentId;
        public string TriggerText;
        public StagingResultPayload Payload = new StagingResultPayload();
    }

    [Serializable]
    public sealed class BakedConversationAsset
    {
        public const string ExpectedFormat = "npcify.baked-conversation";
        public const int ExpectedVersion = 1;

        public string Format = ExpectedFormat;
        public int Version = ExpectedVersion;
        public string AvatarId = string.Empty;
        public int Revision;
        public string RootStagingId;
        public List<BakedConversationNode> Stagings = new List<BakedConversationNode>();
    }

    [Serializable]
    public sealed class NpcifyPlaybackCursor
    {
        public string TaskId = string.Empty;
        public string StagingId = string.Empty;
        public string BlockId;
        public string BlockType;
        public double BlockTime;
        public double TimelineTime;
    }

    [Serializable]
    public sealed class NpcifySpeechState
    {
        public bool Speaking;
        public bool Thinking;
        public StagingDialogueBlock CurrentSpeech;
        public string CurrentText = string.Empty;
        public string RevealedText = string.Empty;
        public IReadOnlyList<StagingReplyOption> Options = Array.Empty<StagingReplyOption>();
        public NpcifyPlaybackCursor Cursor;
    }
}
