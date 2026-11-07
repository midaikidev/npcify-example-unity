using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    /// <summary>
    /// The audio of the blocks of streamed replies (docs/streaming_reply.md): a block can play when
    /// its audio is complete and BlockAudioPreparer finished; notes of the stream for logs.
    /// </summary>
    public sealed partial class NpcifyCharacterRuntime
    {
        // Blocks of streamed replies: false while their audio is prepared, true when they can play.
        private readonly Dictionary<StagingDialogueBlock, bool> _blockAudio = new Dictionary<StagingDialogueBlock, bool>();
        // The known lengths in seconds of the decoded audio of blocks of streamed replies.
        private readonly Dictionary<StagingDialogueBlock, double> _blockAudioLength = new Dictionary<StagingDialogueBlock, double>();
        private CancellationTokenSource _blockPreparation = new CancellationTokenSource();

        /// <summary>
        /// Optional hook that prepares the audio of one dialogue block of a streamed reply, for
        /// example to decode it. The block plays only after the hook finishes; a failure plays the
        /// block without audio. NpcifyCharacter sets it.
        /// </summary>
        public Func<StagingDialogueBlock, CancellationToken, Task> BlockAudioPreparer { get; set; }

        /// <summary>
        /// Optional hook that gives the length in seconds of the prepared audio of a dialogue block
        /// of a streamed reply, or 0 when it is not known (docs/streaming_reply.md). A block plays
        /// to the end of its audio: the clock of a pending block runs on while later revisions add
        /// its alignment, and the next block starts when the audio ended. Without a length, the
        /// clock waits at the estimated end of a pending block until its final version. The runtime
        /// calls it on the thread that calls Tick. NpcifyCharacter sets it (AudioClip.length).
        /// </summary>
        public Func<StagingDialogueBlock, double> BlockAudioLength { get; set; }

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

        // A pending block of the reply that plays starts: a note of the end that its clock runs to.
        private void NoteBlockStart(StagingDialogueBlock block)
        {
            var turn = _playingTurn;
            if (turn == null || block == null || !block.Pending || OnStreamLog == null)
            {
                return;
            }
            var audio = AudioLengthOf(block);
            var timed = block.EndTime - block.StartTime;
            Note(turn, audio > 0.0
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "the pending block {0} plays its {1:F2} s of audio (its timing ends at {2:F2} s)", block.Id, audio, timed)
                : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "the pending block {0} has no audio length: its clock waits at {1:F2} s for its final version", block.Id, timed));
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

        // The length of the audio of a block that can play, for the staging player; 0 when not known.
        private double AudioLengthOf(StagingDialogueBlock block)
        {
            if (block == null || !_blockAudio.TryGetValue(block, out var ready) || !ready)
            {
                return 0.0;
            }
            if (_blockAudioLength.TryGetValue(block, out var known))
            {
                return known;
            }
            var length = 0.0;
            var hook = BlockAudioLength;
            if (hook != null)
            {
                try
                {
                    length = hook(block);
                }
                catch (Exception error)
                {
                    // Without a length the block plays to the end of its timing, as before 0.6.1.
                    _blockAudioLength[block] = 0.0;
                    _mainThreadWork.Enqueue(() => OnError?.Invoke(error));
                    return 0.0;
                }
            }
            if (!(length > 0.0) && IsPcmAudio(block.Audio))
            {
                length = DecodeAudioBytes(block.Audio.Data).DurationSeconds;
            }
            if (!(length > 0.0) || double.IsInfinity(length))
            {
                return 0.0;
            }
            _blockAudioLength[block] = length;
            return length;
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
                var playing = _playingTurn;
                if (playing != null)
                {
                    PumpTurn(playing);
                }
                // Newest first: a newer reply that can start stops the older ones before they start.
                var turns = _turns.ToArray();
                for (var index = turns.Length - 1; index >= 0; index--)
                {
                    if (turns[index] != playing)
                    {
                        PumpTurn(turns[index]);
                    }
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
                _blockAudioLength.Remove(block);
            }
        }
    }
}
