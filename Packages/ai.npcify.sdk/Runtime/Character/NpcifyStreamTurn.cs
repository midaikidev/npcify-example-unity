using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    /// <summary>What a stream event changed in a <see cref="NpcifyStreamTurn"/>.</summary>
    internal enum NpcifyStreamChange
    {
        None,
        Timeline,
        Audio,
        Final,
        Failed,
    }

    /// <summary>
    /// One reply that plays while the server makes it (docs/streaming_reply.md): the numbered
    /// timeline revisions of conversations:stream, the MP3 chunks of each speech block, the reply
    /// options, and the reconciliation with the HTTP result. A revision only adds dialogue, and
    /// only the last (pending) speech block can grow; a revision that changes published dialogue
    /// fails the turn. The runtime calls it on the thread that calls Tick.
    /// </summary>
    internal sealed class NpcifyStreamTurn
    {
        public const string ChangedDialogueMessage = "A staging revision changed published dialogue.";

        private sealed class AudioStream
        {
            public readonly List<byte[]> Chunks = new List<byte[]>();
            public long NextIndex;
            public bool Broken;
            public byte[] Bytes;
        }

        private static long _nextSequence;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Dictionary<string, AudioStream> _audio = new Dictionary<string, AudioStream>(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _revision;

        public NpcifyStreamTurn(string taskId)
        {
            TaskId = (taskId ?? string.Empty).Trim().ToLowerInvariant();
            Sequence = Interlocked.Increment(ref _nextSequence);
            Payload = new StagingResultPayload { ProtocolVersion = 1, TaskId = TaskId };
        }

        /// <summary>The Idempotency-Key of the turn request; the server uses it as the task ID.</summary>
        public string TaskId { get; }

        /// <summary>Later turns have higher numbers.</summary>
        public long Sequence { get; }

        /// <summary>Seconds since the turn request, for the notes of the stream.</summary>
        public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

        /// <summary>The staging that plays: revisions add to it, and the HTTP result completes it.</summary>
        public StagingResultPayload Payload { get; }

        /// <summary>A timeline revision arrived: the stream plays this reply, not the HTTP result.</summary>
        public bool HasTimeline { get; private set; }

        /// <summary>The dialogue is complete (the staging event or the HTTP result).</summary>
        public bool Complete { get; private set; }

        /// <summary>The options and the text are known (the options event or the HTTP result).</summary>
        public bool HasFinal { get; private set; }

        public bool Failed { get; private set; }

        public bool PlaybackStarted { get; private set; }

        /// <summary>The turn plays no more: stopped, replaced by a newer reply, or failed.</summary>
        public bool Stopped { get; private set; }

        /// <summary>The playback ended before the options were known; OnSpeechEnd waits for them.</summary>
        public bool EndPending { get; set; }

        /// <summary>Milestones already in the notes of the stream: 1 the first revision, 2 the first audio.</summary>
        public int Noted { get; set; }

        /// <summary>True when the playback starts, false when it never will.</summary>
        public Task<bool> Started => _started.Task;

        public NpcifyStreamChange Apply(Dictionary<string, object> streamEvent)
        {
            if (Stopped || streamEvent == null)
            {
                return NpcifyStreamChange.None;
            }
            switch (GetString(streamEvent, "type"))
            {
                case "start":
                    AdoptStagingId(streamEvent);
                    return NpcifyStreamChange.None;
                case "speech_chunk":
                    AppendChunk(streamEvent);
                    return NpcifyStreamChange.None;
                case "speech_complete":
                    return CompleteAudio(GetString(streamEvent, "streamId"));
                case "speech_error":
                    BreakAudio(GetString(streamEvent, "streamId"));
                    return NpcifyStreamChange.None;
                case "staging_live":
                case "staging_prefix":
                    return ApplyRevision(streamEvent, false);
                case "staging":
                    // Servers before the complete flag sent only complete staging events.
                    return ApplyRevision(streamEvent, !(streamEvent.TryGetValue("complete", out var flag) && flag is bool done && !done));
                case "options":
                    return ApplyOptions(streamEvent);
                case "error":
                    Fail();
                    return NpcifyStreamChange.Failed;
                default:
                    return NpcifyStreamChange.None;
            }
        }

        /// <summary>
        /// Applies the HTTP result. Returns false when no revision arrived: the result then plays
        /// as a whole. Speech whose stream did not complete takes the stored audio of the result.
        /// Throws InvalidDataException when the result changes published dialogue.
        /// </summary>
        public bool ApplyResult(StagingResultPayload result)
        {
            if (result == null || !HasTimeline)
            {
                return false;
            }
            Payload.ProtocolVersion = result.ProtocolVersion;
            Payload.TaskId = string.IsNullOrEmpty(result.TaskId) ? TaskId : result.TaskId;
            Payload.StagingId = result.StagingId;
            Payload.Text = result.Text ?? string.Empty;
            Payload.Options = result.Options ?? new List<StagingReplyOption>();
            HasFinal = true;
            if (Stopped)
            {
                return true;
            }
            var dialogue = Payload.Timeline.Dialogue;
            var final = result.Timeline ?? new StagingTimeline();
            var published = dialogue.Count;
            Merge(final);
            for (var index = 0; index < published; index++)
            {
                if (!HasPlayableAudio(dialogue[index]))
                {
                    // Missed or failed live chunks: play the stored audio of the result.
                    dialogue[index].Audio = final.Dialogue[index].Audio;
                }
            }
            Complete = true;
            return true;
        }

        /// <summary>The block can play: a pause, silent speech, or speech whose audio arrived completely.</summary>
        public static bool HasPlayableAudio(StagingDialogueBlock block)
        {
            if (block == null || block.Type != "speech")
            {
                return true;
            }
            var audio = block.Audio;
            if (audio == null || audio.Type == "silent")
            {
                return true;
            }
            if (audio.Type == "stream")
            {
                return false;
            }
            return (audio.Data != null && audio.Data.Length > 0) || !string.IsNullOrWhiteSpace(audio.Url);
        }

        public void MarkStarted()
        {
            PlaybackStarted = true;
            _started.TrySetResult(true);
        }

        public void Stop()
        {
            Stopped = true;
            EndPending = false;
            _started.TrySetResult(PlaybackStarted);
        }

        public void Fail()
        {
            Failed = true;
            Stop();
        }

        private void AdoptStagingId(Dictionary<string, object> streamEvent)
        {
            var stagingId = GetString(streamEvent, "stagingId");
            if (string.IsNullOrEmpty(Payload.StagingId) && !string.IsNullOrEmpty(stagingId))
            {
                Payload.StagingId = stagingId;
            }
        }

        private void AppendChunk(Dictionary<string, object> streamEvent)
        {
            var streamId = GetString(streamEvent, "streamId");
            if (string.IsNullOrEmpty(streamId))
            {
                return;
            }
            var audio = GetAudio(streamId);
            if (audio.Bytes != null || audio.Broken)
            {
                return;
            }
            var index = GetLong(streamEvent, "chunkIndex", -1);
            var data = GetBytes(streamEvent, "data");
            if (index != audio.NextIndex || data == null)
            {
                // A missed chunk: this speech plays the stored audio of the HTTP result.
                BreakAudio(streamId);
                return;
            }
            audio.Chunks.Add(data);
            audio.NextIndex++;
        }

        private NpcifyStreamChange CompleteAudio(string streamId)
        {
            if (string.IsNullOrEmpty(streamId))
            {
                return NpcifyStreamChange.None;
            }
            var audio = GetAudio(streamId);
            if (audio.Bytes != null || audio.Broken || audio.Chunks.Count == 0)
            {
                return NpcifyStreamChange.None;
            }
            var length = 0;
            foreach (var chunk in audio.Chunks)
            {
                length += chunk.Length;
            }
            var bytes = new byte[length];
            var offset = 0;
            foreach (var chunk in audio.Chunks)
            {
                Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length);
                offset += chunk.Length;
            }
            audio.Chunks.Clear();
            audio.Bytes = bytes;
            var changed = false;
            foreach (var block in Payload.Timeline.Dialogue)
            {
                changed |= AttachAudio(block);
            }
            return changed ? NpcifyStreamChange.Audio : NpcifyStreamChange.None;
        }

        private void BreakAudio(string streamId)
        {
            if (string.IsNullOrEmpty(streamId))
            {
                return;
            }
            var audio = GetAudio(streamId);
            if (audio.Bytes == null)
            {
                audio.Broken = true;
                audio.Chunks.Clear();
            }
        }

        private AudioStream GetAudio(string streamId)
        {
            if (!_audio.TryGetValue(streamId, out var audio))
            {
                audio = new AudioStream();
                _audio[streamId] = audio;
            }
            return audio;
        }

        // A stream block whose chunks are complete gets them as MP3 bytes.
        private bool AttachAudio(StagingDialogueBlock block)
        {
            var audio = block?.Audio;
            if (audio == null || audio.Type != "stream" || string.IsNullOrEmpty(audio.StreamId) ||
                !_audio.TryGetValue(audio.StreamId, out var stream) || stream.Bytes == null)
            {
                return false;
            }
            audio.Type = "bytes";
            audio.Data = stream.Bytes;
            audio.MimeType = string.IsNullOrEmpty(audio.MimeType) ? "audio/mpeg" : audio.MimeType;
            return true;
        }

        private NpcifyStreamChange ApplyRevision(Dictionary<string, object> streamEvent, bool complete)
        {
            if (Complete)
            {
                return NpcifyStreamChange.None;
            }
            var revision = GetLong(streamEvent, "revision", 0);
            if (revision <= _revision ||
                !(streamEvent.TryGetValue("timeline", out var value) && value is Dictionary<string, object> timelineMap))
            {
                return NpcifyStreamChange.None;
            }
            Merge(NpcifySchemaMapper.MapTimeline(timelineMap));
            var dialogue = Payload.Timeline.Dialogue;
            if (!HasFinal)
            {
                Payload.Text = JoinSpeech(dialogue);
            }
            _revision = revision;
            AdoptStagingId(streamEvent);
            HasTimeline = true;
            Complete = complete;
            return NpcifyStreamChange.Timeline;
        }

        private NpcifyStreamChange ApplyOptions(Dictionary<string, object> streamEvent)
        {
            if (HasFinal)
            {
                return NpcifyStreamChange.None;
            }
            var options = new List<StagingReplyOption>();
            if (streamEvent.TryGetValue("options", out var value) && value is IList list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object> option)
                    {
                        options.Add(new StagingReplyOption
                        {
                            Label = GetString(option, "label") ?? string.Empty,
                            End = option.TryGetValue("end", out var end) && end is bool ended && ended,
                        });
                    }
                }
            }
            Payload.Options = options;
            var text = GetString(streamEvent, "text");
            if (text != null)
            {
                Payload.Text = text;
            }
            HasFinal = true;
            return NpcifyStreamChange.Final;
        }

        private void ApplyTracks(StagingTimeline timeline)
        {
            var target = Payload.Timeline;
            target.Duration = timeline.Duration;
            target.Expressions = timeline.Expressions ?? new List<StagingExpressionClip>();
            target.Animations = timeline.Animations ?? new List<StagingAnimationClip>();
            target.Motions = timeline.Motions ?? new List<Npcify.Sdk.Motion.StagingMotionPrompt>();
            if (timeline.Motion != null)
            {
                target.Motion = timeline.Motion;
            }
        }

        /// <summary>
        /// Adds the new blocks of a revision or of the result. A published block stays as it is,
        /// except a pending speech block: it keeps its ID, text and start, and takes the end,
        /// timing and lip-sync of its next version (its end grows while it stays pending).
        /// </summary>
        private void Merge(StagingTimeline next)
        {
            var dialogue = Payload.Timeline.Dialogue;
            var blocks = next.Dialogue ?? new List<StagingDialogueBlock>();
            var valid = blocks.Count >= dialogue.Count;
            for (var index = 0; valid && index < dialogue.Count; index++)
            {
                var a = dialogue[index];
                var b = blocks[index];
                if (b == null || a.Id != b.Id || a.Type != b.Type || a.Text != b.Text || Math.Abs(a.StartTime - b.StartTime) > 1e-6)
                {
                    valid = false;
                }
                else if (a.Pending && a.Type == "speech")
                {
                    valid = !b.Pending || b.EndTime >= a.EndTime - 1e-6;
                }
                else
                {
                    valid = Math.Abs(a.EndTime - b.EndTime) <= 1e-6;
                }
            }
            if (!valid)
            {
                Fail();
                throw new InvalidDataException(ChangedDialogueMessage);
            }
            for (var index = 0; index < dialogue.Count; index++)
            {
                var block = dialogue[index];
                if (block.Pending)
                {
                    var grown = blocks[index];
                    block.EndTime = grown.EndTime;
                    block.Duration = grown.Duration;
                    block.Visemes = grown.Visemes;
                    block.Words = grown.Words;
                    block.Characters = grown.Characters;
                    block.Pending = grown.Pending;
                }
            }
            for (var index = dialogue.Count; index < blocks.Count; index++)
            {
                var block = blocks[index];
                AttachAudio(block);
                dialogue.Add(block);
            }
            ApplyTracks(next);
        }

        private static string JoinSpeech(List<StagingDialogueBlock> dialogue)
        {
            var text = new StringBuilder();
            foreach (var block in dialogue)
            {
                if (block.Type != "speech" || string.IsNullOrEmpty(block.Text))
                {
                    continue;
                }
                if (text.Length > 0)
                {
                    text.Append('\n');
                }
                text.Append(block.Text);
            }
            return text.ToString();
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var value) ? value as string : null;
        }

        private static long GetLong(Dictionary<string, object> map, string key, long fallback)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return fallback;
            }
            if (value is long integer)
            {
                return integer;
            }
            return value is double number ? (long)number : fallback;
        }

        private static byte[] GetBytes(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value))
            {
                return null;
            }
            if (value is byte[] bytes)
            {
                return bytes;
            }
            if (value is string base64)
            {
                try
                {
                    return Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    return null;
                }
            }
            return null;
        }
    }
}
