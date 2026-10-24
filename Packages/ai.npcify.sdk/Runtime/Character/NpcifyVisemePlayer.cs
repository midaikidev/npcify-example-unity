using System;
using System.Collections.Generic;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    public sealed class NpcifyVisemePlayer
    {
        private static readonly Dictionary<string, (string Primary, float PrimaryWeight, string Secondary, float SecondaryWeight)>
            VisemeMap =
                new Dictionary<string, (string, float, string, float)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["aa"] = ("aa", 0.9f, "oh", 0.15f),
                    ["a"] = ("aa", 0.9f, "oh", 0.15f),
                    ["ih"] = ("ih", 0.85f, "ee", 0.2f),
                    ["i"] = ("ih", 0.85f, "ee", 0.2f),
                    ["ou"] = ("ou", 0.9f, "oh", 0.2f),
                    ["u"] = ("ou", 0.9f, "oh", 0.2f),
                    ["ee"] = ("ee", 0.88f, "ih", 0.2f),
                    ["e"] = ("ee", 0.88f, "ih", 0.2f),
                    ["oh"] = ("oh", 0.9f, "ou", 0.18f),
                    ["o"] = ("oh", 0.9f, "ou", 0.18f),
                    ["PP"] = ("ou", 0.12f, null, 0f),
                    ["FF"] = ("ih", 0.35f, "ou", 0.2f),
                    ["TH"] = ("ee", 0.4f, "aa", 0.2f),
                    ["DD"] = ("ih", 0.45f, "aa", 0.2f),
                    ["kk"] = ("aa", 0.45f, "ih", 0.25f),
                    ["CH"] = ("ou", 0.45f, "ih", 0.35f),
                    ["SS"] = ("ih", 0.5f, "ee", 0.25f),
                    ["nn"] = ("ih", 0.35f, "aa", 0.15f),
                    ["RR"] = ("ou", 0.55f, "oh", 0.25f),
                    ["sil"] = (null, 0f, null, 0f),
                    ["pau"] = (null, 0f, null, 0f),
                };

        private readonly List<StagingVisemeCue> _cues = new List<StagingVisemeCue>();
        private readonly Dictionary<string, float> _currentWeights =
            new Dictionary<string, float>(StringComparer.Ordinal);

        public float CoarticulationSeconds { get; set; } = 0.045f;

        public IReadOnlyList<StagingVisemeCue> Cues => _cues;

        public void LoadCues(IEnumerable<StagingVisemeCue> cues)
        {
            _cues.Clear();
            if (cues != null)
            {
                _cues.AddRange(cues);
                _cues.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            _currentWeights.Clear();
        }

        public void Clear()
        {
            _cues.Clear();
            _currentWeights.Clear();
        }

        public IReadOnlyDictionary<string, float> Evaluate(double timeSeconds, float fallbackRmsAmplitude = 0f)
        {
            _currentWeights.Clear();

            if (_cues.Count == 0)
            {
                if (fallbackRmsAmplitude > 0.01f)
                {
                    var amp = Clamp01(fallbackRmsAmplitude);
                    _currentWeights["aa"] = amp;
                    _currentWeights["oh"] = amp * 0.35f;
                }
                return _currentWeights;
            }

            var feather = Math.Max(0.01, CoarticulationSeconds);

            for (var i = 0; i < _cues.Count; i++)
            {
                var cue = _cues[i];
                if (cue == null || string.IsNullOrEmpty(cue.Viseme))
                {
                    continue;
                }

                var duration = Math.Max(0.02, cue.Duration <= 0.0 ? 0.08 : cue.Duration);
                var start = cue.Time - feather;
                var end = cue.Time + duration + feather;
                if (timeSeconds < start || timeSeconds > end)
                {
                    continue;
                }

                var center = cue.Time + duration * 0.5;
                var halfSpan = duration * 0.5 + feather;
                var normalizedDist = Math.Abs(timeSeconds - center) / halfSpan;
                var envelope = (float)Math.Max(0.0, 1.0 - normalizedDist * normalizedDist);
                // Weight 0 is a lip closure; the mapper defaults a missing weight to 1.
                var cueWeight = (float)cue.Weight;
                var cueScale = Clamp01(cueWeight * envelope);

                ApplyVisemeContribution(cue.Viseme, cueScale);
            }

            return _currentWeights;
        }

        private void ApplyVisemeContribution(string visemeName, float scale)
        {
            if (scale <= 0.001f)
            {
                return;
            }

            if (VisemeMap.TryGetValue(visemeName.Trim(), out var mapped))
            {
                if (!string.IsNullOrEmpty(mapped.Primary))
                {
                    Accumulate(mapped.Primary, mapped.PrimaryWeight * scale);
                }
                if (!string.IsNullOrEmpty(mapped.Secondary))
                {
                    Accumulate(mapped.Secondary, mapped.SecondaryWeight * scale);
                }
                return;
            }

            var normalized = NpcifyFacialMixer.NormalizeExpressionName(visemeName);
            if (normalized == "aa" || normalized == "ih" || normalized == "ou" || normalized == "ee" || normalized == "oh")
            {
                Accumulate(normalized, scale);
            }
        }

        private void Accumulate(string key, float value)
        {
            var clamped = Clamp01(value);
            if (_currentWeights.TryGetValue(key, out var existing))
            {
                _currentWeights[key] = Math.Max(existing, clamped);
            }
            else
            {
                _currentWeights[key] = clamped;
            }
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }
            if (value > 1f)
            {
                return 1f;
            }
            return value;
        }
    }
}
