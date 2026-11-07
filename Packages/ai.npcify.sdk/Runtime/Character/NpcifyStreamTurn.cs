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
        /// <summary>
        /// The server runs the turn again (a retried request): a second start event, or after a
        /// retry a revision that starts the dialogue again. The event belongs to the new run.
        /// </summary>
        Restarted,
    }

    /// <summary>
    /// Why a turn plays no more. Game and Superseded are final; after Rejected or Failed, a rerun
    /// of the turn (a retried request) can start it again.
    /// </summary>
    internal enum NpcifyTurnStop
    {
        None,
        /// <summary>The game stopped the speech (StopSpeaking, a new conversation, a cancelled task).</summary>
        Game,
        /// <summary>A newer reply plays, or the HTTP result plays as a whole.</summary>
        Superseded,
        /// <summary>The SDK refused a revision that changed published dialogue.</summary>
        Rejected,
        /// <summary>The server sent an error event, or the request failed.</summary>
        Failed,
    }

    /// <summary>
    /// One reply that plays while the server makes it (docs/streaming_reply.md): the numbered
    /// timeline revisions of conversations:stream, the MP3 chunks of each speech block, the reply
    /// options, and the reconciliation with the HTTP result. A revision only adds dialogue, and
    /// only the last (pending) speech block can grow; a revision that changes published dialogue
    /// fails the turn, unless the request was sent again (then it starts a rerun). The runtime
    /// calls it under its playback lock.
    /// </summary>
    internal sealed partial class NpcifyStreamTurn
    {
        public const string ChangedDialogueMessage = "A staging revision changed published dialogue.";

        private static long _nextSequence;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private TaskCompletionSource<bool> _started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _revision;
        // An event of the turn arrived: a later start event means that the server runs it again.
        private bool _runStarted;
        // The request was sent again: the server can run the turn again.
        private bool _rerunExpected;

        public NpcifyStreamTurn(string taskId)
        {
            TaskId = (taskId ?? string.Empty).Trim().ToLowerInvariant();
            Sequence = NextSequence();
            Payload = new StagingResultPayload { ProtocolVersion = 1, TaskId = TaskId };
        }

        /// <summary>The order of turn requests: later requests have higher numbers.</summary>
        public static long NextSequence()
        {
            return Interlocked.Increment(ref _nextSequence);
        }

        /// <summary>The Idempotency-Key of the turn request; the server uses it as the task ID.</summary>
        public string TaskId { get; }

        /// <summary>Later turns have higher numbers.</summary>
        public long Sequence { get; }

        /// <summary>Seconds since the turn request, for the notes of the stream.</summary>
        public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

        /// <summary>
        /// The staging that plays: revisions add to it, and the HTTP result completes it. A rerun
        /// gets a new object.
        /// </summary>
        public StagingResultPayload Payload { get; private set; }

        /// <summary>A timeline revision arrived: the stream plays this reply, not the HTTP result.</summary>
        public bool HasTimeline { get; private set; }

        /// <summary>The dialogue is complete (the staging event or the HTTP result).</summary>
        public bool Complete { get; private set; }

        /// <summary>The options and the text are known (the options event or the HTTP result).</summary>
        public bool HasFinal { get; private set; }

        /// <summary>Why the turn plays no more (None: it can still play).</summary>
        public NpcifyTurnStop StopReason { get; private set; }

        /// <summary>The turn plays no more: stopped, replaced by a newer reply, refused or failed.</summary>
        public bool Stopped => StopReason != NpcifyTurnStop.None;

        public bool Failed => StopReason == NpcifyTurnStop.Failed || StopReason == NpcifyTurnStop.Rejected;

        public bool PlaybackStarted { get; private set; }

        /// <summary>The HTTP request of the turn finished (a result or a failure).</summary>
        public bool RequestClosed { get; set; }

        /// <summary>
        /// The playback ended before the options were known; OnSpeechEnd waits for them. A stop
        /// keeps it, so that the runtime can raise the end.
        /// </summary>
        public bool EndPending { get; set; }

        /// <summary>Milestones already in the notes of the stream: 1 the first revision, 2 the first audio.</summary>
        public int Noted { get; set; }

        /// <summary>True when the playback starts, false when it never will.</summary>
        public Task<bool> Started => _started.Task;

        // A rerun can still come: the request was sent again and did not finish.
        private bool RerunPossible => _rerunExpected && !RequestClosed;

        /// <summary>
        /// The request of the turn is sent again: the server can run the turn again, and the start
        /// event of the rerun can be lost while the stream connects again. Until the request
        /// finishes, a revision whose number is not higher than the last one, or one that does not
        /// continue the published dialogue, starts the rerun too.
        /// </summary>
        public void ExpectRerun()
        {
            _rerunExpected = true;
        }

        public NpcifyStreamChange Apply(Dictionary<string, object> streamEvent)
        {
            if (streamEvent == null)
            {
                return NpcifyStreamChange.None;
            }
            var type = GetString(streamEvent, "type");
            var started = _runStarted;
            // Each run of the turn sends its start event first.
            _runStarted = true;
            if (IsFinalStop(StopReason))
            {
                return NpcifyStreamChange.None;
            }
            if (type == "start")
            {
                if (started)
                {
                    // A retried request runs the turn again under the same task ID.
                    return NpcifyStreamChange.Restarted;
                }
                if (!Stopped)
                {
                    AdoptStagingId(streamEvent);
                }
                return NpcifyStreamChange.None;
            }
            if (Stopped)
            {
                // A failed or refused turn: only a rerun of a retried request can start it again.
                return RerunPossible ? ApplyWhileFailed(type, streamEvent) : NpcifyStreamChange.None;
            }
            switch (type)
            {
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
                    Stop(NpcifyTurnStop.Failed);
                    return NpcifyStreamChange.Failed;
                default:
                    return NpcifyStreamChange.None;
            }
        }

        /// <summary>
        /// Applies the HTTP result. Returns false when the result plays as a whole: no revision
        /// arrived, or the server failed the stream (a rerun whose events did not arrive). A turn
        /// that the game stopped or that a newer reply replaced does not play again: it takes the
        /// complete staging of the result. Speech whose stream did not complete takes the stored
        /// audio of the result. Throws InvalidDataException when the result changes published
        /// dialogue, or when the SDK refused a revision of this turn.
        /// </summary>
        public bool ApplyResult(StagingResultPayload result)
        {
            if (result == null)
            {
                return false;
            }
            if (StopReason == NpcifyTurnStop.Rejected)
            {
                throw new InvalidDataException(ChangedDialogueMessage);
            }
            if (StopReason == NpcifyTurnStop.Failed || (!HasTimeline && !Stopped))
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
                Payload.Timeline = result.Timeline ?? new StagingTimeline();
                Complete = true;
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

        /// <summary>All blocks are known and can play: the stream delivered the whole reply.</summary>
        public bool AllBlocksPlayable
        {
            get
            {
                foreach (var block in Payload.Timeline.Dialogue)
                {
                    if (!HasPlayableAudio(block))
                    {
                        return false;
                    }
                }
                return Complete;
            }
        }

        public void MarkStarted()
        {
            PlaybackStarted = true;
            _started.TrySetResult(true);
        }

        public void Stop(NpcifyTurnStop reason)
        {
            if (reason != NpcifyTurnStop.None && (!IsFinalStop(StopReason) || reason == NpcifyTurnStop.Game))
            {
                StopReason = reason;
            }
            _started.TrySetResult(PlaybackStarted);
        }

        private static bool IsFinalStop(NpcifyTurnStop reason)
        {
            return reason == NpcifyTurnStop.Game || reason == NpcifyTurnStop.Superseded;
        }

        /// <summary>
        /// The server runs the turn again (a retried request): the turn forgets the first run and
        /// applies the new run from <paramref name="firstEvent"/> on, in a new staging object (the
        /// game got the first one with OnSpeechStart).
        /// </summary>
        public void Restart(Dictionary<string, object> firstEvent)
        {
            ForgetAudioOf(Payload.Timeline.Dialogue);
            _revision = 0;
            _runStarted = true;
            _rerunExpected = false;
            HasTimeline = false;
            Complete = false;
            HasFinal = false;
            PlaybackStarted = false;
            EndPending = false;
            Noted = 0;
            StopReason = NpcifyTurnStop.None;
            if (_started.Task.IsCompleted)
            {
                _started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Payload = new StagingResultPayload { ProtocolVersion = 1, TaskId = TaskId };
            if (firstEvent != null)
            {
                AdoptStagingId(firstEvent);
            }
        }

        private void AdoptStagingId(Dictionary<string, object> streamEvent)
        {
            var stagingId = GetString(streamEvent, "stagingId");
            if (string.IsNullOrEmpty(Payload.StagingId) && !string.IsNullOrEmpty(stagingId))
            {
                Payload.StagingId = stagingId;
            }
        }

        // A failed or refused turn while its request is sent again: the audio of the rerun can
        // come before its first revision, and a revision that starts over shows the rerun.
        private NpcifyStreamChange ApplyWhileFailed(string type, Dictionary<string, object> streamEvent)
        {
            switch (type)
            {
                case "speech_chunk":
                    AppendChunk(streamEvent);
                    return NpcifyStreamChange.None;
                case "speech_complete":
                    CompleteAudio(GetString(streamEvent, "streamId"));
                    return NpcifyStreamChange.None;
                case "speech_error":
                    BreakAudio(GetString(streamEvent, "streamId"));
                    return NpcifyStreamChange.None;
                case "staging_live":
                case "staging_prefix":
                case "staging":
                    var timeline = ReadTimeline(streamEvent);
                    return timeline != null && StartsRerun(GetLong(streamEvent, "revision", 0), timeline)
                        ? NpcifyStreamChange.Restarted
                        : NpcifyStreamChange.None;
                default:
                    return NpcifyStreamChange.None;
            }
        }

        private static StagingTimeline ReadTimeline(Dictionary<string, object> streamEvent)
        {
            return streamEvent.TryGetValue("timeline", out var value) && value is Dictionary<string, object> map
                ? NpcifySchemaMapper.MapTimeline(map)
                : null;
        }

        // A revision of a rerun: its numbers start at 1 again, and its dialogue starts over.
        private bool StartsRerun(long revision, StagingTimeline timeline)
        {
            return revision <= _revision || !Continues(timeline);
        }

        private NpcifyStreamChange ApplyRevision(Dictionary<string, object> streamEvent, bool complete)
        {
            var timeline = ReadTimeline(streamEvent);
            if (timeline == null)
            {
                return NpcifyStreamChange.None;
            }
            var revision = GetLong(streamEvent, "revision", 0);
            if (RerunPossible && StartsRerun(revision, timeline))
            {
                return NpcifyStreamChange.Restarted;
            }
            if (Complete || revision <= _revision)
            {
                return NpcifyStreamChange.None;
            }
            Merge(timeline);
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
        /// True when the timeline keeps the published dialogue: the same blocks, of which only a
        /// pending speech block can grow.
        /// </summary>
        private bool Continues(StagingTimeline next)
        {
            var dialogue = Payload.Timeline.Dialogue;
            var blocks = next.Dialogue ?? new List<StagingDialogueBlock>();
            if (blocks.Count < dialogue.Count)
            {
                return false;
            }
            for (var index = 0; index < dialogue.Count; index++)
            {
                var a = dialogue[index];
                var b = blocks[index];
                if (b == null || a.Id != b.Id || a.Type != b.Type || a.Text != b.Text || Math.Abs(a.StartTime - b.StartTime) > 1e-6)
                {
                    return false;
                }
                var valid = a.Pending && a.Type == "speech"
                    ? !b.Pending || b.EndTime >= a.EndTime - 1e-6
                    : Math.Abs(a.EndTime - b.EndTime) <= 1e-6;
                if (!valid)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Adds the new blocks of a revision or of the result. A published block stays as it is,
        /// except a pending speech block: it keeps its ID, text and start, and takes the end,
        /// timing and lip-sync of its next version (its end grows while it stays pending).
        /// </summary>
        private void Merge(StagingTimeline next)
        {
            if (!Continues(next))
            {
                Stop(NpcifyTurnStop.Rejected);
                throw new InvalidDataException(ChangedDialogueMessage);
            }
            var dialogue = Payload.Timeline.Dialogue;
            var blocks = next.Dialogue ?? new List<StagingDialogueBlock>();
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
    }
}
