using System;
using System.Collections.Generic;
using System.Globalization;
using Npcify.Sdk.Character;
using Npcify.Sdk.Models;
using UnityEngine;

namespace NpcifyDemo
{
    /// <summary>
    /// Measures how far the staging clock of a streamed reply is behind its voice. The end of a
    /// pending block is an estimate; when the clock waits there while the audio plays on, the
    /// visemes after the estimate come late. Sample once per frame, after the character ticked.
    /// </summary>
    public sealed class DemoClockProbe
    {
        private readonly List<(double Audio, double Clock)> _samples = new List<(double Audio, double Clock)>();
        private StagingDialogueBlock _block;
        private float _blockStart;
        private bool _pendingAtStart;
        private double _estimate;
        private double _finalAt = -1.0;
        private double _finalEnd;
        private float _clipLength;
        private double _firstCue = -1.0;

        public void Sample(NpcifyCharacterRuntime runtime, AudioSource source, Action<string> log)
        {
            var player = runtime.StagingPlayer;
            var block = player.IsPlaying ? player.CurrentBlock : null;
            if (!ReferenceEquals(block, _block))
            {
                Report(log);
                Begin(block);
            }
            if (_block == null || _block.Type != "speech")
            {
                return;
            }
            var playing = source != null && source.isPlaying && source.clip != null;
            if (playing && _clipLength <= 0f)
            {
                _clipLength = source.clip.length;
            }
            // The voice: the time of the clip that plays, else the time since the block started.
            var audio = playing ? source.time : Time.realtimeSinceStartup - _blockStart;
            if (_block.Pending)
            {
                _estimate = Math.Max(_estimate, _block.EndTime - _block.StartTime);
            }
            else if (_finalAt < 0.0)
            {
                _finalAt = audio;
                _finalEnd = _block.EndTime - _block.StartTime;
                foreach (var cue in _block.Visemes)
                {
                    if (cue != null && cue.Time > _estimate && (_firstCue < 0.0 || cue.Time < _firstCue))
                    {
                        _firstCue = cue.Time;
                    }
                }
            }
            _samples.Add((audio, player.BlockElapsedSeconds));
        }

        public void Finish(Action<string> log)
        {
            Report(log);
            Begin(null);
        }

        private void Begin(StagingDialogueBlock block)
        {
            _block = block;
            _samples.Clear();
            _blockStart = Time.realtimeSinceStartup;
            _pendingAtStart = block != null && block.Pending;
            _estimate = block != null ? block.EndTime - block.StartTime : 0.0;
            _finalAt = block != null && block.Pending ? -1.0 : 0.0;
            _finalEnd = _estimate;
            _clipLength = 0f;
            _firstCue = -1.0;
        }

        private void Report(Action<string> log)
        {
            if (_block == null || _block.Type != "speech" || _samples.Count == 0)
            {
                return;
            }
            var end = _clipLength > 0f ? _clipLength : _finalEnd;
            var before = 0.0;
            var after = 0.0;
            var cueLag = double.NaN;
            foreach (var (audio, clock) in _samples)
            {
                var lag = audio - clock;
                if (audio <= _estimate)
                {
                    before = Math.Max(before, lag);
                }
                else if (audio <= end)
                {
                    after = Math.Max(after, lag);
                }
                if (_firstCue >= 0.0 && double.IsNaN(cueLag) && clock >= _firstCue)
                {
                    cueLag = lag;
                }
            }
            var c = CultureInfo.InvariantCulture;
            log(string.Format(c,
                "clock probe: block {0} pending at start {1}, estimated end {2:F3} s, audio {3:F3} s, final version at {4} (end {5:F3} s); " +
                "clock lag before the estimate {6:F3} s, between the estimate and the end of the audio {7:F3} s; first viseme after the estimate {8}",
                _block.Id, _pendingAtStart, _estimate, _clipLength,
                _finalAt < 0.0 ? "none" : _finalAt.ToString("F3", c) + " s of the audio", _finalEnd, before, after,
                _firstCue < 0.0 ? "none" : string.Format(c, "at {0:F3} s, lag {1:F3} s", _firstCue, cueLag)));
        }
    }
}
