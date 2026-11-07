using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    /// <summary>
    /// Replies that play while the server makes them (docs/streaming_reply.md): each published
    /// dialogue prefix of conversations:stream plays when the audio of its blocks is ready; the
    /// HTTP result completes the reply and plays only when no revision arrived.
    /// </summary>
    public sealed partial class NpcifyCharacterRuntime
    {
        // Tick, stream events, block audio and HTTP results change the playback under this lock.
        private readonly object _playbackGate = new object();
        // Turns that can still start to play, oldest first; the playing turn is _playingTurn.
        private readonly List<NpcifyStreamTurn> _turns = new List<NpcifyStreamTurn>();
        // Blocks of streamed replies: false while their audio is prepared, true when they can play.
        private readonly Dictionary<StagingDialogueBlock, bool> _blockAudio = new Dictionary<StagingDialogueBlock, bool>();
        private NpcifyStreamTurn _playingTurn;
        private CancellationTokenSource _blockPreparation = new CancellationTokenSource();

        /// <summary>
        /// Optional hook that prepares the audio of one dialogue block of a streamed reply, for
        /// example to decode it. The block plays only after the hook finishes; a failure plays the
        /// block without audio. NpcifyCharacter sets it.
        /// </summary>
        public Func<StagingDialogueBlock, CancellationToken, Task> BlockAudioPreparer { get; set; }

        /// <summary>
        /// True when replies play while they stream: NpcifyOptions.StreamPlayback is on, the
        /// conversation is not baked, and a StagingPreparer comes with a BlockAudioPreparer.
        /// Otherwise a reply plays after its HTTP result, as before 0.6.0.
        /// </summary>
        public bool StreamPlaybackEnabled =>
#if UNITY_WEBGL && !UNITY_EDITOR
            false;
#else
            _options.StreamPlayback && _bakedConversation == null &&
            (StagingPreparer == null || BlockAudioPreparer != null);
#endif

        private static bool IsTurnEvent(string type)
        {
            switch (type)
            {
                case "start":
                case "speech_chunk":
                case "speech_complete":
                case "speech_error":
                case "staging_live":
                case "staging_prefix":
                case "staging":
                case "options":
                case "error":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Requests a turn; the reply plays from the stream, or from the HTTP result.</summary>
        private async Task<StagingResultPayload> RequestTurnAsync(string message, CancellationToken cancellationToken)
        {
            var conversationId = _conversationId;
            // The server uses the Idempotency-Key as the task ID of the stream events.
            var requestId = Guid.NewGuid().ToString("D");
            var stream = await ResolveTurnStreamAsync(conversationId, cancellationToken);
            var turn = stream.Joined && StreamPlaybackEnabled ? OpenTurn(requestId) : null;
            StagingResultPayload staging;
            try
            {
                staging = await _api.CreateStagingAsync(
                    conversationId,
                    message,
                    _activeStagingId,
                    false,
                    requestId,
                    null,
                    6,
                    stream.Capabilities,
                    cancellationToken);
            }
            catch (Exception) when (turn != null && !cancellationToken.IsCancellationRequested && IsCompletedByStream(turn))
            {
                // The stream delivered the whole reply and its options: a failed answer (for
                // example a timeout) does not fail it.
                lock (_playbackGate)
                {
                    _activeStagingId = turn.Payload.StagingId;
                    PumpTurn(turn);
                }
                await WaitForPlaybackAsync(turn, cancellationToken);
                return turn.Payload;
            }
            catch (Exception)
            {
                CloseTurn(turn, null);
                throw;
            }
            _activeStagingId = staging.StagingId;
            var streamed = CloseTurn(turn, staging);
            if (streamed == null)
            {
                await PrepareAndPlayAsync(staging, cancellationToken);
                return staging;
            }
            // As with a reply of the HTTP path, the task completes when the reply plays.
            await WaitForPlaybackAsync(streamed, cancellationToken);
            return streamed.Payload;
        }

        private bool IsCompletedByStream(NpcifyStreamTurn turn)
        {
            lock (_playbackGate)
            {
                return turn.HasTimeline && turn.Complete && turn.HasFinal && !turn.Failed;
            }
        }

        private NpcifyStreamTurn OpenTurn(string requestId)
        {
            var turn = new NpcifyStreamTurn(requestId);
            lock (_playbackGate)
            {
                _turns.Add(turn);
            }
            return turn;
        }

        /// <summary>
        /// Ends the request of a turn. With a result, returns the turn when the stream plays the
        /// reply, or null when the result plays as a whole. Without a result, stops the turn.
        /// </summary>
        private NpcifyStreamTurn CloseTurn(NpcifyStreamTurn turn, StagingResultPayload result)
        {
            if (turn == null)
            {
                return null;
            }
            lock (_playbackGate)
            {
                if (result == null)
                {
                    EndTurn(turn);
                    return null;
                }
                bool owned;
                try
                {
                    owned = turn.ApplyResult(result);
                }
                catch (Exception error)
                {
                    EndTurn(turn);
                    OnError?.Invoke(error);
                    return turn;
                }
                Note(turn, owned ? "the HTTP result completes the streamed reply" : "the HTTP result plays: no revision arrived");
                if (!owned)
                {
                    // Late events of this turn are ignored: the result plays as a whole.
                    EndTurn(turn);
                    return null;
                }
                PumpTurn(turn);
                return turn;
            }
        }

        private async Task WaitForPlaybackAsync(NpcifyStreamTurn turn, CancellationToken cancellationToken)
        {
            if (turn.Started.IsCompleted)
            {
                return;
            }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                await Task.WhenAny(turn.Started, cancelled.Task);
            }
            if (!turn.Started.IsCompleted)
            {
                lock (_playbackGate)
                {
                    EndTurn(turn);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Socket thread: the event is applied on the thread that calls Tick.
        private void EnqueueTurnEvent(Dictionary<string, object> streamEvent)
        {
            _mainThreadWork.Enqueue(() => HandleTurnEvent(streamEvent));
        }

        private void HandleTurnEvent(Dictionary<string, object> streamEvent)
        {
            var taskId = streamEvent.TryGetValue("taskId", out var value) && value is string text
                ? text.Trim().ToLowerInvariant()
                : null;
            if (string.IsNullOrEmpty(taskId))
            {
                return;
            }
            lock (_playbackGate)
            {
                var turn = FindTurn(taskId);
                if (turn == null)
                {
                    return;
                }
                NpcifyStreamChange change;
                try
                {
                    change = turn.Apply(streamEvent);
                }
                catch (Exception error)
                {
                    // A revision that changes published dialogue: the turn stops, nothing replays.
                    EndTurn(turn);
                    OnError?.Invoke(error);
                    return;
                }
                if (change == NpcifyStreamChange.Failed)
                {
                    // The HTTP request fails too and reports the error.
                    Note(turn, "the server stopped the turn");
                    EndTurn(turn);
                }
                else if (change != NpcifyStreamChange.None)
                {
                    NoteMilestones(turn);
                    PumpTurn(turn);
                }
            }
        }

        private NpcifyStreamTurn FindTurn(string taskId)
        {
            if (_playingTurn != null && _playingTurn.TaskId == taskId)
            {
                return _playingTurn;
            }
            foreach (var turn in _turns)
            {
                if (turn.TaskId == taskId)
                {
                    return turn;
                }
            }
            return null;
        }

        // Prepares the audio of ready blocks, starts the playback once the first block can play,
        // completes it, and raises a pending OnSpeechEnd once the options are known.
        private void PumpTurn(NpcifyStreamTurn turn)
        {
            if (turn.Stopped)
            {
                return;
            }
            var dialogue = turn.Payload.Timeline.Dialogue;
            foreach (var block in dialogue)
            {
                PrepareBlock(block);
            }
            if (!turn.PlaybackStarted)
            {
                if (IsBlockReady(dialogue, 0) && (_playingTurn == null || _playingTurn.Sequence < turn.Sequence))
                {
                    StartStreamPlayback(turn);
                }
                return;
            }
            if (turn != _playingTurn)
            {
                return;
            }
            if (turn.Complete)
            {
                _stagingPlayer.CompleteStream();
            }
            _stagingPlayer.SkipElapsedClips();
            _stagingPlayer.RefreshCurrentBlock();
            if (turn.EndPending && turn.HasFinal)
            {
                turn.EndPending = false;
                _playingTurn = null;
                OnSpeechEnd?.Invoke(turn.Payload);
            }
        }

        private void StartStreamPlayback(NpcifyStreamTurn turn)
        {
            _playingTurn?.Stop();
            foreach (var waiting in _turns)
            {
                if (waiting.Sequence < turn.Sequence)
                {
                    // An older reply that did not play yet never plays over this one.
                    waiting.Stop();
                }
            }
            _turns.RemoveAll(waiting => waiting == turn || waiting.Stopped);
            _playingTurn = turn;
            turn.MarkStarted();
            ForgetBlocksOutside(turn.Payload.Timeline.Dialogue);
            _thinking = false;
            _activeStagingId = turn.Payload.StagingId;
            Note(turn, "the streamed reply plays (" + turn.Payload.Timeline.Dialogue.Count + " block(s) published)");
            LoadStoredMotion(turn.Payload);
            OnSpeechStart?.Invoke(turn.Payload);
            var dialogue = turn.Payload.Timeline.Dialogue;
            _stagingPlayer.PlayStream(turn.Payload, index => IsBlockReady(dialogue, index));
            if (turn.Complete)
            {
                _stagingPlayer.CompleteStream();
            }
        }

        // The turn plays no more: its playback stops without OnSpeechEnd.
        private void EndTurn(NpcifyStreamTurn turn)
        {
            turn.Stop();
            _turns.Remove(turn);
            if (_playingTurn == turn)
            {
                _playingTurn = null;
                _thinking = false;
                _stagingPlayer.Stop();
            }
        }

        private void StopStreamTurns()
        {
            lock (_playbackGate)
            {
                foreach (var turn in _turns)
                {
                    turn.Stop();
                }
                _turns.Clear();
                _playingTurn?.Stop();
                _playingTurn = null;
                _blockPreparation.Cancel();
                _blockPreparation.Dispose();
                _blockPreparation = new CancellationTokenSource();
                _blockAudio.Clear();
            }
        }

        private void HandleStagingCompleted(StagingResultPayload staging)
        {
            var turn = _playingTurn;
            if (turn != null && ReferenceEquals(turn.Payload, staging))
            {
                if (!turn.HasFinal)
                {
                    // The options are not known yet: the end comes with them.
                    turn.EndPending = true;
                    return;
                }
                _playingTurn = null;
            }
            OnSpeechEnd?.Invoke(staging);
        }

        private void NoteMilestones(NpcifyStreamTurn turn)
        {
            var dialogue = turn.Payload.Timeline.Dialogue;
            if (turn.Noted < 1 && turn.HasTimeline)
            {
                turn.Noted = 1;
                Note(turn, "the first revision arrived");
            }
            if (turn.Noted < 2 && dialogue.Count > 0 && NpcifyStreamTurn.HasPlayableAudio(dialogue[0]))
            {
                turn.Noted = 2;
                Note(turn, "the audio of the first block is complete");
            }
        }

        // A note of the stream with the time since the turn request (OnStreamLog).
        private void Note(NpcifyStreamTurn turn, string message)
        {
            var handler = OnStreamLog;
            if (handler != null)
            {
                handler(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Turn {0}: {1} after {2:F2} s.",
                    turn.TaskId.Length > 8 ? turn.TaskId.Substring(0, 8) : turn.TaskId, message, turn.ElapsedSeconds));
            }
        }

        private bool IsBlockReady(List<StagingDialogueBlock> dialogue, int index)
        {
            return index >= 0 && index < dialogue.Count &&
                   _blockAudio.TryGetValue(dialogue[index], out var ready) && ready;
        }

        private void PrepareBlock(StagingDialogueBlock block)
        {
            if (block == null || _blockAudio.ContainsKey(block) || !NpcifyStreamTurn.HasPlayableAudio(block))
            {
                return;
            }
            var preparer = BlockAudioPreparer;
            var audio = block.Audio;
            var encoded = block.Type == "speech" && audio != null && audio.Type != "silent" &&
                          ((audio.Data != null && audio.Data.Length > 0) || !string.IsNullOrWhiteSpace(audio.Url));
            if (preparer == null || !encoded)
            {
                _blockAudio[block] = true;
                return;
            }
            var token = _blockPreparation.Token;
            Task preparation;
            try
            {
                preparation = preparer(block, token) ?? Task.CompletedTask;
            }
            catch (Exception error)
            {
                preparation = Task.FromException(error);
            }
            if (preparation.IsCompleted)
            {
                // Ready now; the caller starts or continues the playback.
                ReportPreparationFailure(preparation);
                _blockAudio[block] = true;
                return;
            }
            _blockAudio[block] = false;
            _ = FinishPreparationAsync(preparation, block, token);
        }

        private void ReportPreparationFailure(Task preparation)
        {
            if (preparation.IsFaulted && preparation.Exception != null)
            {
                // Missing audio must not stop the reply: the block plays its text and timeline.
                var error = preparation.Exception.GetBaseException();
                _mainThreadWork.Enqueue(() => OnError?.Invoke(error));
            }
        }

        private async Task FinishPreparationAsync(Task preparation, StagingDialogueBlock block, CancellationToken cancellationToken)
        {
            try
            {
                await preparation;
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                ReportPreparationFailure(preparation);
            }
            lock (_playbackGate)
            {
                if (cancellationToken.IsCancellationRequested || !_blockAudio.ContainsKey(block))
                {
                    return;
                }
                _blockAudio[block] = true;
                if (_playingTurn != null)
                {
                    PumpTurn(_playingTurn);
                }
                foreach (var turn in _turns.ToArray())
                {
                    PumpTurn(turn);
                }
            }
        }

        // Readiness of blocks that no reply in progress plays.
        private void ForgetBlocksOutside(List<StagingDialogueBlock> dialogue)
        {
            var keep = new HashSet<StagingDialogueBlock>(dialogue);
            foreach (var turn in _turns)
            {
                keep.UnionWith(turn.Payload.Timeline.Dialogue);
            }
            var stale = new List<StagingDialogueBlock>();
            foreach (var block in _blockAudio.Keys)
            {
                if (!keep.Contains(block))
                {
                    stale.Add(block);
                }
            }
            foreach (var block in stale)
            {
                _blockAudio.Remove(block);
            }
        }
    }
}
