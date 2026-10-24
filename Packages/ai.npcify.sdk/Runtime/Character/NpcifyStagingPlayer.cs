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
        private readonly HashSet<string> _triggeredExpressionIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _triggeredAnimationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly StringBuilder _revealedBuilder = new StringBuilder();

        private StagingResultPayload _staging;
        private int _currentBlockIndex = -1;
        private double _blockElapsedSeconds;
        private double _timelineElapsedSeconds;
        private int _nextCharCueIndex;
        private bool _isPlaying;

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
            if (staging == null)
            {
                throw new ArgumentNullException(nameof(staging));
            }

            _staging = staging;
            _triggeredExpressionIds.Clear();
            _triggeredAnimationIds.Clear();
            _revealedBuilder.Clear();
            _timelineElapsedSeconds = 0.0;
            _blockElapsedSeconds = 0.0;
            _currentBlockIndex = -1;
            _nextCharCueIndex = 0;

            var dialogue = staging.Timeline?.Dialogue;
            if (dialogue == null || dialogue.Count == 0)
            {
                _isPlaying = false;
                EvaluateGlobalTracks(0.0);
                OnStagingCompleted?.Invoke(staging);
                return;
            }

            _isPlaying = true;
            AdvanceToBlock(0);
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

            _timelineElapsedSeconds += dt;
            _blockElapsedSeconds += dt;

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

            var block = dialogue[index];
            _visemePlayer.LoadCues(block.Visemes);

            if (block.Characters == null || block.Characters.Count == 0)
            {
                if (!string.IsNullOrEmpty(block.Text))
                {
                    _revealedBuilder.Append(block.Text);
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

            var duration = ComputeBlockDuration(block);
            if (_blockElapsedSeconds >= duration)
            {
                if (_revealedBuilder.Length < (block.Text?.Length ?? 0))
                {
                    _revealedBuilder.Clear();
                    _revealedBuilder.Append(block.Text);
                }

                OnDialogueBlockEnded?.Invoke(block);

                var nextIndex = _currentBlockIndex + 1;
                var dialogue = _staging?.Timeline?.Dialogue;
                if (dialogue != null && nextIndex < dialogue.Count)
                {
                    AdvanceToBlock(nextIndex);
                }
                else
                {
                    CompletePlayback();
                }
            }
        }

        private void EvaluateCharacterCues(StagingDialogueBlock block)
        {
            if (block.Characters == null || block.Characters.Count == 0)
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

            if (timeline.Expressions != null)
            {
                for (var i = 0; i < timeline.Expressions.Count; i++)
                {
                    var clip = timeline.Expressions[i];
                    if (clip == null || string.IsNullOrEmpty(clip.Expression))
                    {
                        continue;
                    }

                    var clipKey = !string.IsNullOrEmpty(clip.Id)
                        ? clip.Id
                        : $"{i}:{clip.Expression}:{clip.StartTime:F3}";

                    if (!_triggeredExpressionIds.Contains(clipKey) && timelineSeconds + 1e-6 >= clip.StartTime)
                    {
                        _triggeredExpressionIds.Add(clipKey);
                        var duration = Math.Max(0.15f, (float)(clip.EndTime - clip.StartTime));
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

                    var clipKey = !string.IsNullOrEmpty(clip.Id)
                        ? clip.Id
                        : $"{i}:{clip.Animation}:{clip.StartTime:F3}";

                    if (!_triggeredAnimationIds.Contains(clipKey) && timelineSeconds + 1e-6 >= clip.StartTime)
                    {
                        _triggeredAnimationIds.Add(clipKey);
                        OnAnimationClipTriggered?.Invoke(clip);
                    }
                }
            }
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
