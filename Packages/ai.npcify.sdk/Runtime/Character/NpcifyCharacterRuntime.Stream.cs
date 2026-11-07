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
        // Turns in progress, oldest first: an open request, or a reply that waits for its first block.
        private readonly List<NpcifyStreamTurn> _turns = new List<NpcifyStreamTurn>();
        private NpcifyStreamTurn _playingTurn;
        // The request order of the newest reply that started to play: an older reply never plays over it.
        private long _playedSequence;

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
            var sequence = turn?.Sequence ?? NpcifyStreamTurn.NextSequence();
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
                // The stream delivered the whole reply, its audio and its options: a failed answer
                // (for example a timeout) does not fail it.
                lock (_playbackGate)
                {
                    turn.RequestClosed = true;
                    AdoptActiveStaging(turn.Sequence, turn.Payload.StagingId);
                    PumpTurn(turn);
                    ForgetClosedTurns();
                }
                await WaitForPlaybackAsync(turn, cancellationToken);
                return turn.Payload;
            }
            catch (Exception)
            {
                CloseTurn(turn, null);
                throw;
            }
            AdoptActiveStaging(sequence, staging.StagingId);
            // Throws when the result changes dialogue that played: the request fails.
            var streamed = CloseTurn(turn, staging);
            if (streamed == null)
            {
                await PlayTurnResultAsync(staging, sequence, cancellationToken);
                return staging;
            }
            // As with a reply of the HTTP path, the task completes when the reply plays.
            await WaitForPlaybackAsync(streamed, cancellationToken);
            return streamed.Payload;
        }

        // The next turn continues from this staging, unless a newer reply played already.
        private void AdoptActiveStaging(long sequence, string stagingId)
        {
            if (sequence >= Volatile.Read(ref _playedSequence))
            {
                _activeStagingId = stagingId;
            }
        }

        private bool IsCompletedByStream(NpcifyStreamTurn turn)
        {
            lock (_playbackGate)
            {
                return turn.HasTimeline && turn.HasFinal && !turn.Failed && turn.AllBlocksPlayable;
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
        /// reply (or played it, or the game stopped it), or null when the result plays as a whole.
        /// Without a result, stops the turn. Throws when the result changes published dialogue.
        /// </summary>
        private NpcifyStreamTurn CloseTurn(NpcifyStreamTurn turn, StagingResultPayload result)
        {
            if (turn == null)
            {
                return null;
            }
            lock (_playbackGate)
            {
                turn.RequestClosed = true;
                if (result == null)
                {
                    EndTurn(turn, NpcifyTurnStop.Failed);
                    return null;
                }
                bool owned;
                try
                {
                    owned = turn.ApplyResult(result);
                }
                catch (Exception)
                {
                    EndTurn(turn, NpcifyTurnStop.Rejected);
                    throw;
                }
                Note(turn, owned ? "the HTTP result completes the streamed reply" : "the HTTP result plays as a whole");
                if (!owned)
                {
                    // Late events of this turn are ignored: the result plays as a whole.
                    EndTurn(turn, NpcifyTurnStop.Superseded);
                    return null;
                }
                PumpTurn(turn);
                ForgetClosedTurns();
                return turn;
            }
        }

        // The HTTP path: the result plays as a whole, unless a newer reply plays already.
        private async Task PlayTurnResultAsync(StagingResultPayload staging, long sequence, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _playedSequence) > sequence)
            {
                return;
            }
            var preparer = StagingPreparer;
            if (preparer != null)
            {
                await preparer(staging, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_playbackGate)
            {
                if (_playedSequence > sequence)
                {
                    return;
                }
                _playedSequence = sequence;
                SupersedeOlderTurns(sequence);
                PlayStaging(staging);
            }
        }

        private async Task WaitForPlaybackAsync(NpcifyStreamTurn turn, CancellationToken cancellationToken)
        {
            var started = turn.Started;
            if (started.IsCompleted)
            {
                return;
            }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                await Task.WhenAny(started, cancelled.Task);
            }
            if (!started.IsCompleted)
            {
                lock (_playbackGate)
                {
                    EndTurn(turn, NpcifyTurnStop.Game);
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
                catch (Exception)
                {
                    // A revision that changes published dialogue: the reply stops, nothing replays.
                    // The request reports the failure (or its result does not match either).
                    Note(turn, "a revision changed published dialogue");
                    EndTurn(turn, NpcifyTurnStop.Rejected);
                    return;
                }
                switch (change)
                {
                    case NpcifyStreamChange.None:
                        return;
                    case NpcifyStreamChange.Restarted:
                        RestartTurn(turn, streamEvent);
                        return;
                    case NpcifyStreamChange.Failed:
                        // The HTTP request fails too and reports the error.
                        Note(turn, "the server stopped the turn");
                        EndTurn(turn, NpcifyTurnStop.Failed);
                        return;
                    default:
                        NoteMilestones(turn);
                        PumpTurn(turn);
                        return;
                }
            }
        }

        private NpcifyStreamTurn FindTurn(string taskId)
        {
            foreach (var turn in _turns)
            {
                if (turn.TaskId == taskId)
                {
                    return turn;
                }
            }
            return _playingTurn != null && _playingTurn.TaskId == taskId ? _playingTurn : null;
        }

        // A retried request: the server runs the turn again under the same task ID.
        private void RestartTurn(NpcifyStreamTurn turn, Dictionary<string, object> startEvent)
        {
            if (turn.Sequence < _playedSequence)
            {
                // A newer reply plays already.
                turn.Stop(NpcifyTurnStop.Superseded);
                return;
            }
            if (_playingTurn == turn)
            {
                // The first run stops without OnSpeechEnd; the new run plays from its start.
                _playingTurn = null;
                _stagingPlayer.Stop();
            }
            turn.Restart(startEvent);
            _thinking = true;
            Note(turn, "the server runs the turn again");
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
                // A rerun of the reply that played last can play again.
                if (IsBlockReady(dialogue, 0) && turn.Sequence >= _playedSequence)
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
            StopPlayingTurn(NpcifyTurnStop.Superseded);
            SupersedeOlderTurns(turn.Sequence);
            _playingTurn = turn;
            _playedSequence = turn.Sequence;
            turn.MarkStarted();
            ForgetBlocksOutside(turn.Payload.Timeline.Dialogue);
            _thinking = false;
            _activeStagingId = turn.Payload.StagingId;
            Note(turn, "the streamed reply plays (" + turn.Payload.Timeline.Dialogue.Count + " block(s) published)");
            LoadStoredMotion(turn.Payload);
            OnSpeechStart?.Invoke(turn.Payload);
            if (_playingTurn != turn)
            {
                return;
            }
            _stagingPlayer.PlayStream(turn.Payload, index => IsBlockReady(turn.Payload.Timeline.Dialogue, index));
            if (turn.Complete)
            {
                _stagingPlayer.CompleteStream();
            }
            ForgetClosedTurns();
        }

        // Older replies never play (or play again) over a newer one.
        private void SupersedeOlderTurns(long sequence)
        {
            foreach (var older in _turns)
            {
                if (older.Sequence < sequence && older != _playingTurn)
                {
                    older.Stop(NpcifyTurnStop.Superseded);
                }
            }
            ForgetClosedTurns();
        }

        // The playing reply stops. If it already played to its end and only waited for its
        // options, its OnSpeechEnd fires now: the game heard the whole reply.
        private void StopPlayingTurn(NpcifyTurnStop reason)
        {
            var turn = _playingTurn;
            if (turn == null)
            {
                return;
            }
            _playingTurn = null;
            var ended = turn.EndPending;
            turn.Stop(reason);
            _stagingPlayer.Stop();
            ForgetClosedTurns();
            if (ended)
            {
                OnSpeechEnd?.Invoke(turn.Payload);
            }
        }

        // The turn plays no more; a reply that played to its end still gets its OnSpeechEnd.
        private void EndTurn(NpcifyStreamTurn turn, NpcifyTurnStop reason)
        {
            if (_playingTurn == turn)
            {
                StopPlayingTurn(reason);
                _thinking = false;
            }
            else
            {
                turn.Stop(reason);
            }
            ForgetClosedTurns();
        }

        // A turn stays findable while its request is open: a retry can run it again, and its result
        // never plays again over its stream.
        private void ForgetClosedTurns()
        {
            _turns.RemoveAll(turn => turn.RequestClosed && (turn.Stopped || turn.PlaybackStarted));
        }

        private void StopStreamTurns()
        {
            lock (_playbackGate)
            {
                foreach (var turn in _turns)
                {
                    turn.Stop(NpcifyTurnStop.Game);
                }
                _turns.Clear();
                _playingTurn?.Stop(NpcifyTurnStop.Game);
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
    }
}
