using System;
using System.Collections.Generic;
using System.Text;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    public sealed class NpcifyStagingPlayer
    {
        private readonly NpcifyFacialMixer _facialMixer;
        private readonly NpcifyVisemePlayer _visemePlayer;
        private readonly NpcifyPlayedClips _playedExpressions = new NpcifyPlayedClips();
        private readonly NpcifyPlayedClips _playedAnimations = new NpcifyPlayedClips();
        private readonly StringBuilder _revealedBuilder = new StringBuilder();
        // The block that plays had no character timing when it started: all of its text shows.
        private bool _wholeTextRevealed;

        private StagingResultPayload _staging;
        private int _currentBlockIndex = -1;
        private double _blockElapsedSeconds;
        private double _timelineElapsedSeconds;
        private int _nextCharCueIndex;
        private bool _isPlaying;
        // A streamed staging (PlayStream): its dialogue grows, and a block starts only when ready.
        private Func<int, bool> _isBlockReady;
        // A streamed staging: the length in seconds of the decoded audio of a block (0: not known).
        private Func<StagingDialogueBlock, double> _audioLength;
        private bool _dialogueComplete = true;
        // The block that the playback waits for (-1: none); the clock does not run meanwhile.
        private int _waitingBlockIndex = -1;

        public NpcifyStagingPlayer(NpcifyFacialMixer facialMixer, NpcifyVisemePlayer visemePlayer)
        {
            _facialMixer = facialMixer ?? throw new ArgumentNullException(nameof(facialMixer));
            _visemePlayer = visemePlayer ?? throw new ArgumentNullException(nameof(visemePlayer));
        }

        public event Action<StagingDialogueBlock> OnDialogueBlockStarted;
        public event Action<StagingDialogueBlock> OnDialogueBlockEnded;
        public event Action<string, StagingCharacterCue> OnCharacterRevealed;
        public event Action<StagingExpressionClip> OnExpressionClipTriggered;
        public event Action<StagingAnimationClip> OnAnimationClipTriggered;
        public event Action<StagingResultPayload> OnStagingCompleted;

        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// A streamed staging waits for its next dialogue block (docs/streaming_reply.md). The
        /// staging clock stops meanwhile; the playback stays on.
        /// </summary>
        public bool IsWaitingForBlock => _isPlaying && _waitingBlockIndex >= 0;

        public StagingResultPayload CurrentStaging => _staging;

        public int CurrentBlockIndex => _currentBlockIndex;

        public StagingDialogueBlock CurrentBlock
        {
            get
            {
                var dialogue = _staging?.Timeline?.Dialogue;
                if (dialogue == null || _currentBlockIndex < 0 || _currentBlockIndex >= dialogue.Count)
                {
                    return null;
                }
                return dialogue[_currentBlockIndex];
            }
        }

        public double BlockElapsedSeconds => _blockElapsedSeconds;

        public double TimelineElapsedSeconds => _timelineElapsedSeconds;

        public string CurrentText => CurrentBlock?.Text ?? (_staging?.Text ?? string.Empty);

        public string RevealedText => _revealedBuilder.ToString();

        public NpcifyPlaybackCursor Cursor =>
            new NpcifyPlaybackCursor
            {
                TaskId = _staging?.TaskId ?? string.Empty,
                StagingId = _staging?.StagingId ?? string.Empty,
                BlockId = CurrentBlock?.Id,
                BlockType = CurrentBlock?.Type,
                BlockTime = _blockElapsedSeconds,
                TimelineTime = _timelineElapsedSeconds,
            };

        public void Play(StagingResultPayload staging)
        {
            Start(staging, null, true, null);
        }

        /// <summary>
        /// Plays a staging whose dialogue still grows (a streamed reply, docs/streaming_reply.md).
        /// Block i starts only when <paramref name="isBlockReady"/>(i) is true. At the end of the
        /// ready blocks the playback waits, with a stopped clock, until the next block is ready or
        /// <see cref="CompleteStream"/> says that no block follows.
        /// </summary>
        public void PlayStream(StagingResultPayload staging, Func<int, bool> isBlockReady)
        {
            PlayStream(staging, isBlockReady, null);
        }

        /// <summary>
        /// Plays a streamed staging as <see cref="PlayStream(StagingResultPayload, Func{int, bool})"/>.
        /// <paramref name="audioLength"/>(block) gives the length in seconds of the decoded audio of
        /// a block, or 0 when it is not known. A block plays to the end of its audio: the clock of a
        /// pending block runs on while later revisions add its alignment, and the next block starts
        /// when the audio ended. Without a length, the clock waits at the estimated end of a pending
        /// block until its final version.
        /// </summary>
        public void PlayStream(
            StagingResultPayload staging,
            Func<int, bool> isBlockReady,
            Func<StagingDialogueBlock, double> audioLength)
        {
            Start(staging, isBlockReady ?? (_ => true), false, audioLength);
        }

        /// <summary>The streamed staging that plays has its complete dialogue now.</summary>
        public void CompleteStream()
        {
            _dialogueComplete = true;
        }

        /// <summary>
        /// A revision changed the block that plays (a pending block grows): its lip-sync cues load
        /// again, at the same block time.
        /// </summary>
        public void RefreshCurrentBlock()
        {
            var block = CurrentBlock;
            if (_isPlaying && _waitingBlockIndex < 0 && block != null)
            {
                _visemePlayer.LoadCues(block.Visemes);
            }
        }

        /// <summary>
        /// Clips of a new revision that ended before the current staging time do not play; clips
        /// that started earlier and still run play for the rest of their time.
        /// </summary>
        public void SkipElapsedClips()
        {
            var timeline = _staging?.Timeline;
            if (!_isPlaying || timeline == null)
            {
                return;
            }
            foreach (var clip in timeline.Expressions ?? new List<StagingExpressionClip>())
            {
                if (clip != null && clip.EndTime <= _timelineElapsedSeconds)
                {
                    _playedExpressions.Add(clip.Id, clip.Expression, clip.StartTime, clip.EndTime);
                }
            }
            foreach (var clip in timeline.Animations ?? new List<StagingAnimationClip>())
            {
                if (clip != null && clip.EndTime <= _timelineElapsedSeconds)
                {
                    _playedAnimations.Add(clip.Id, clip.Animation, clip.StartTime, clip.EndTime);
                }
            }
        }

        private void Start(
            StagingResultPayload staging,
            Func<int, bool> isBlockReady,
            bool dialogueComplete,
            Func<StagingDialogueBlock, double> audioLength)
        {
            if (staging == null)
            {
                throw new ArgumentNullException(nameof(staging));
            }

            _staging = staging;
            _isBlockReady = isBlockReady;
            _audioLength = audioLength;
            _dialogueComplete = dialogueComplete;
            _waitingBlockIndex = -1;
            _playedExpressions.Clear();
            _playedAnimations.Clear();
            _revealedBuilder.Clear();
            _wholeTextRevealed = false;
            _timelineElapsedSeconds = 0.0;
            _blockElapsedSeconds = 0.0;
            _currentBlockIndex = -1;
            _nextCharCueIndex = 0;

            var dialogue = staging.Timeline?.Dialogue;
            if (dialogueComplete && (dialogue == null || dialogue.Count == 0))
            {
                _isPlaying = false;
                EvaluateGlobalTracks(0.0);
                OnStagingCompleted?.Invoke(staging);
                return;
            }

            _isPlaying = true;
            if (IsBlockReady(0))
            {
                AdvanceToBlock(0);
            }
            else
            {
                WaitForBlock(0);
            }
            EvaluateGlobalTracks(0.0);
        }

        public void UpdateTimeline(StagingResultPayload updatedStaging)
        {
            if (updatedStaging == null)
            {
                return;
            }

            if (_staging == null || !_isPlaying)
            {
                Play(updatedStaging);
                return;
            }

            _staging = updatedStaging;
        }

        public void Stop()
        {
            _isPlaying = false;
            _waitingBlockIndex = -1;
            _visemePlayer.Clear();
            _facialMixer.ClearVisemes();
        }

        public void Tick(double deltaTimeSeconds, float fallbackAudioRms = 0f)
        {
            var dt = Math.Max(0.0, deltaTimeSeconds);
            if (!_isPlaying || _staging == null)
            {
                _facialMixer.Update((float)dt);
                return;
            }

            if (_waitingBlockIndex >= 0)
            {
                // A generation gap does not advance the staging clock.
                if (ResumeWaitingBlock())
                {
                    EvaluateGlobalTracks(_timelineElapsedSeconds);
                    EvaluateCurrentBlock(fallbackAudioRms);
                }
                _facialMixer.Update((float)dt);
                return;
            }

            var current = CurrentBlock;
            var step = dt;
            if (current != null && current.Pending)
            {
                // A pending block ends with its final version. At the end of its audio (or, without
                // an audio length, at its estimated end) the clock waits.
                var end = BlockEnd(current);
                if (_blockElapsedSeconds >= end)
                {
                    _visemePlayer.Clear();
                    _facialMixer.ClearVisemes();
                    _facialMixer.Update((float)dt);
                    return;
                }
                step = Math.Min(dt, end - _blockElapsedSeconds);
            }

            _timelineElapsedSeconds += step;
            _blockElapsedSeconds += step;

            EvaluateGlobalTracks(_timelineElapsedSeconds);
            EvaluateCurrentBlock(fallbackAudioRms);
            _facialMixer.Update((float)dt);
        }

        private void AdvanceToBlock(int index)
        {
            var dialogue = _staging?.Timeline?.Dialogue;
            if (dialogue == null || index < 0 || index >= dialogue.Count)
            {
                CompletePlayback();
                return;
            }

            _currentBlockIndex = index;
            _blockElapsedSeconds = 0.0;
            _nextCharCueIndex = 0;
            _revealedBuilder.Clear();
            _wholeTextRevealed = false;

            var block = dialogue[index];
            _visemePlayer.LoadCues(block.Visemes);

            if (block.Characters == null || block.Characters.Count == 0)
            {
                if (!string.IsNullOrEmpty(block.Text))
                {
                    // Character timing that a later revision adds does not show the text again.
                    _revealedBuilder.Append(block.Text);
                    _wholeTextRevealed = true;
                }
            }

            OnDialogueBlockStarted?.Invoke(block);
            EvaluateCharacterCues(block);
        }

        private void EvaluateCurrentBlock(float fallbackAudioRms)
        {
            var block = CurrentBlock;
            if (block == null)
            {
                CompletePlayback();
                return;
            }

            EvaluateCharacterCues(block);

            var visemeWeights = _visemePlayer.Evaluate(_blockElapsedSeconds, fallbackAudioRms);
            _facialMixer.SetVisemeWeights(visemeWeights);

            if (_blockElapsedSeconds >= BlockEnd(block) && !block.Pending)
            {
                if (_revealedBuilder.Length < (block.Text?.Length ?? 0))
                {
                    _revealedBuilder.Clear();
                    _revealedBuilder.Append(block.Text);
                }

                OnDialogueBlockEnded?.Invoke(block);

                var nextIndex = _currentBlockIndex + 1;
                var dialogue = _staging?.Timeline?.Dialogue;
                if (dialogue != null && nextIndex < dialogue.Count && IsBlockReady(nextIndex))
                {
                    AdvanceToBlock(nextIndex);
                }
                else if (_dialogueComplete && (dialogue == null || nextIndex >= dialogue.Count))
                {
                    CompletePlayback();
                }
                else
                {
                    WaitForBlock(nextIndex);
                }
            }
        }

        private bool IsBlockReady(int index)
        {
            return _isBlockReady == null || _isBlockReady(index);
        }

        // The end of the ready dialogue of a streamed staging: neutral mouth until the next block.
        private void WaitForBlock(int index)
        {
            _waitingBlockIndex = index;
            _visemePlayer.Clear();
            _facialMixer.ClearVisemes();
        }

        private bool ResumeWaitingBlock()
        {
            var index = _waitingBlockIndex;
            var dialogue = _staging?.Timeline?.Dialogue;
            if (dialogue != null && index < dialogue.Count && IsBlockReady(index))
            {
                _waitingBlockIndex = -1;
                AdvanceToBlock(index);
                return _isPlaying;
            }
            if (_dialogueComplete && (dialogue == null || index >= dialogue.Count))
            {
                _waitingBlockIndex = -1;
                CompletePlayback();
            }
            return false;
        }

        private void EvaluateCharacterCues(StagingDialogueBlock block)
        {
            if (_wholeTextRevealed || block.Characters == null || block.Characters.Count == 0)
            {
                return;
            }

            while (_nextCharCueIndex < block.Characters.Count)
            {
                var cue = block.Characters[_nextCharCueIndex];
                if (cue == null)
                {
                    _nextCharCueIndex++;
                    continue;
                }

                if (_blockElapsedSeconds + 1e-6 < cue.StartTime)
                {
                    break;
                }

                _nextCharCueIndex++;
                if (!string.IsNullOrEmpty(cue.Character))
                {
                    _revealedBuilder.Append(cue.Character);
                }
                OnCharacterRevealed?.Invoke(_revealedBuilder.ToString(), cue);
            }
        }

        private void EvaluateGlobalTracks(double timelineSeconds)
        {
            var timeline = _staging?.Timeline;
            if (timeline == null)
            {
                return;
            }
            // Revisions of a streamed staging can move clips that played.
            var streamed = _isBlockReady != null;

            if (timeline.Expressions != null)
            {
                for (var i = 0; i < timeline.Expressions.Count; i++)
                {
                    var clip = timeline.Expressions[i];
                    if (clip == null || string.IsNullOrEmpty(clip.Expression))
                    {
                        continue;
                    }

                    if (timelineSeconds + 1e-6 >= clip.StartTime &&
                        !_playedExpressions.Contains(clip.Id, clip.Expression, clip.StartTime, clip.EndTime, streamed))
                    {
                        _playedExpressions.Add(clip.Id, clip.Expression, clip.StartTime, clip.EndTime);
                        // A clip that a revision added after its start plays for the rest of its time.
                        var duration = Math.Max(0.15f, (float)(clip.EndTime - Math.Max(clip.StartTime, timelineSeconds)));
                        var weight = clip.Weight <= 0.0 ? 1f : (float)clip.Weight;
                        _facialMixer.PlayCue(clip.Expression, weight, duration, 0.12f, 0.2f);
                        OnExpressionClipTriggered?.Invoke(clip);
                    }
                }
            }

            if (timeline.Animations != null)
            {
                for (var i = 0; i < timeline.Animations.Count; i++)
                {
                    var clip = timeline.Animations[i];
                    if (clip == null || string.IsNullOrEmpty(clip.Animation))
                    {
                        continue;
                    }

                    if (timelineSeconds + 1e-6 >= clip.StartTime &&
                        !_playedAnimations.Contains(clip.Id, clip.Animation, clip.StartTime, clip.EndTime, streamed))
                    {
                        _playedAnimations.Add(clip.Id, clip.Animation, clip.StartTime, clip.EndTime);
                        OnAnimationClipTriggered?.Invoke(clip);
                    }
                }
            }
        }

        /// <summary>
        /// The block time at which a block ends: the end of its timing, or of its decoded audio
        /// when that is later. The estimated end of a pending block comes before the end of its
        /// complete audio; the voice keeps playing, so the clock does too.
        /// </summary>
        private double BlockEnd(StagingDialogueBlock block)
        {
            var timed = ComputeBlockDuration(block);
            var audio = _audioLength != null ? _audioLength(block) : 0.0;
            return audio > timed && !double.IsInfinity(audio) ? audio : timed;
        }

        private static double ComputeBlockDuration(StagingDialogueBlock block)
        {
            var maxEnd = block.Duration > 0.0
                ? block.Duration
                : Math.Max(0.0, block.EndTime - block.StartTime);

            if (block.Visemes != null)
            {
                for (var i = 0; i < block.Visemes.Count; i++)
                {
                    var v = block.Visemes[i];
                    if (v != null)
                    {
                        var vEnd = v.Time + Math.Max(0.02, v.Duration);
                        if (vEnd > maxEnd)
                        {
                            maxEnd = vEnd;
                        }
                    }
                }
            }

            if (block.Words != null)
            {
                for (var i = 0; i < block.Words.Count; i++)
                {
                    var w = block.Words[i];
                    if (w != null && w.EndTime > maxEnd)
                    {
                        maxEnd = w.EndTime;
                    }
                }
            }

            if (block.Characters != null)
            {
                for (var i = 0; i < block.Characters.Count; i++)
                {
                    var c = block.Characters[i];
                    if (c != null && c.EndTime > maxEnd)
                    {
                        maxEnd = c.EndTime;
                    }
                }
            }

            if (maxEnd <= 0.01 && !string.IsNullOrEmpty(block.Text))
            {
                maxEnd = Math.Max(0.4, block.Text.Length * 0.05);
            }

            return Math.Max(0.05, maxEnd);
        }

        private void CompletePlayback()
        {
            _isPlaying = false;
            _visemePlayer.Clear();
            _facialMixer.ClearVisemes();
            var completedStaging = _staging;
            if (completedStaging != null)
            {
                OnStagingCompleted?.Invoke(completedStaging);
            }
        }
    }
}
