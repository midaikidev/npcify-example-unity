using System;
using System.Collections.Generic;

namespace Npcify.Sdk.Character
{
    public enum NpcifyExpressionLayer
    {
        Emotion = 0,
        Viseme = 1,
        Blink = 2,
        Gaze = 3,
    }

    public sealed class NpcifyFacialMixer
    {
        private static readonly string[] CanonicalExpressions =
        {
            "neutral",
            "happy",
            "angry",
            "sad",
            "relaxed",
            "surprised",
            "aa",
            "ih",
            "ou",
            "ee",
            "oh",
            "blink",
            "blinkLeft",
            "blinkRight",
            "lookUp",
            "lookDown",
            "lookLeft",
            "lookRight",
        };

        private static readonly Dictionary<string, string> AliasToCanonical =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["neutral"] = "neutral",
                ["happy"] = "happy",
                ["joy"] = "happy",
                ["smile"] = "happy",
                ["angry"] = "angry",
                ["anger"] = "angry",
                ["sad"] = "sad",
                ["sorrow"] = "sad",
                ["relaxed"] = "relaxed",
                ["fun"] = "relaxed",
                ["calm"] = "relaxed",
                ["surprised"] = "surprised",
                ["surprise"] = "surprised",
                ["aa"] = "aa",
                ["a"] = "aa",
                ["fcl_mth_a"] = "aa",
                ["vrc.v_aa"] = "aa",
                ["jawopen"] = "aa",
                ["mouthopen"] = "aa",
                ["ih"] = "ih",
                ["i"] = "ih",
                ["fcl_mth_i"] = "ih",
                ["vrc.v_ih"] = "ih",
                ["ou"] = "ou",
                ["u"] = "ou",
                ["fcl_mth_u"] = "ou",
                ["vrc.v_ou"] = "ou",
                ["ee"] = "ee",
                ["e"] = "ee",
                ["fcl_mth_e"] = "ee",
                ["vrc.v_e"] = "ee",
                ["oh"] = "oh",
                ["o"] = "oh",
                ["fcl_mth_o"] = "oh",
                ["vrc.v_oh"] = "oh",
                ["blink"] = "blink",
                ["fcl_eye_close"] = "blink",
                ["blinkleft"] = "blinkLeft",
                ["blink_l"] = "blinkLeft",
                ["fcl_eye_close_l"] = "blinkLeft",
                ["eyeblinkleft"] = "blinkLeft",
                ["blinkright"] = "blinkRight",
                ["blink_r"] = "blinkRight",
                ["fcl_eye_close_r"] = "blinkRight",
                ["eyeblinkright"] = "blinkRight",
                ["lookup"] = "lookUp",
                ["look_up"] = "lookUp",
                ["lookdown"] = "lookDown",
                ["look_down"] = "lookDown",
                ["lookleft"] = "lookLeft",
                ["look_left"] = "lookLeft",
                ["lookright"] = "lookRight",
                ["look_right"] = "lookRight",
            };

        private static readonly HashSet<string> VisemeKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "aa",
            "ih",
            "ou",
            "ee",
            "oh",
        };

        private sealed class ActiveCue
        {
            public string Expression;
            public float TargetWeight;
            public float Duration;
            public float FadeIn;
            public float FadeOut;
            public float Elapsed;
        }

        private readonly Dictionary<NpcifyExpressionLayer, Dictionary<string, float>> _layers =
            new Dictionary<NpcifyExpressionLayer, Dictionary<string, float>>();
        private readonly Dictionary<string, float> _smoothedWeights =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly List<ActiveCue> _activeCues = new List<ActiveCue>();

        private float _blinkTimer;
        private float _nextBlinkInterval = 3.6f;
        private float _blinkDuration = 0.14f;
        private bool _isBlinking;
        private uint _rngState = 0x6D2B79F5u;

        public NpcifyFacialMixer()
        {
            foreach (NpcifyExpressionLayer layer in Enum.GetValues(typeof(NpcifyExpressionLayer)))
            {
                _layers[layer] = new Dictionary<string, float>(StringComparer.Ordinal);
            }
            for (var i = 0; i < CanonicalExpressions.Length; i++)
            {
                _smoothedWeights[CanonicalExpressions[i]] = 0f;
            }
        }

        public bool AutoBlinkEnabled { get; set; } = true;

        public float SmoothingRate { get; set; } = 18f;

        public IReadOnlyDictionary<string, float> Weights => _smoothedWeights;

        public float MouthOpenness
        {
            get
            {
                var aa = GetWeight("aa");
                var oh = GetWeight("oh") * 0.85f;
                var ee = GetWeight("ee") * 0.55f;
                var ih = GetWeight("ih") * 0.45f;
                var ou = GetWeight("ou") * 0.5f;
                return Clamp01(Math.Max(aa, Math.Max(oh, Math.Max(ee, Math.Max(ih, ou)))));
            }
        }

        public static string NormalizeExpressionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "neutral";
            }

            var trimmed = name.Trim();
            if (AliasToCanonical.TryGetValue(trimmed, out var canonical))
            {
                return canonical;
            }
            return trimmed;
        }

        public float GetWeight(string expressionName)
        {
            var canonical = NormalizeExpressionName(expressionName);
            return _smoothedWeights.TryGetValue(canonical, out var value) ? value : 0f;
        }

        public void SetLayerWeight(NpcifyExpressionLayer layer, string expressionName, float weight)
        {
            var canonical = NormalizeExpressionName(expressionName);
            _layers[layer][canonical] = Clamp01(weight);
        }

        public void ClearLayer(NpcifyExpressionLayer layer)
        {
            _layers[layer].Clear();
        }

        public void SetEmotion(string expressionName, float weight = 1f)
        {
            _layers[NpcifyExpressionLayer.Emotion].Clear();
            var canonical = NormalizeExpressionName(expressionName);
            if (!string.Equals(canonical, "neutral", StringComparison.Ordinal))
            {
                _layers[NpcifyExpressionLayer.Emotion][canonical] = Clamp01(weight);
            }
        }

        public void ClearEmotion()
        {
            _layers[NpcifyExpressionLayer.Emotion].Clear();
            _activeCues.Clear();
        }

        public void PlayCue(
            string expressionName,
            float weight = 1f,
            float durationSeconds = 1.2f,
            float fadeInSeconds = 0.15f,
            float fadeOutSeconds = 0.25f
        )
        {
            var canonical = NormalizeExpressionName(expressionName);
            if (string.Equals(canonical, "neutral", StringComparison.Ordinal))
            {
                return;
            }

            _activeCues.Add(new ActiveCue
            {
                Expression = canonical,
                TargetWeight = Clamp01(weight),
                Duration = Math.Max(0.05f, durationSeconds),
                FadeIn = Math.Max(0f, fadeInSeconds),
                FadeOut = Math.Max(0f, fadeOutSeconds),
                Elapsed = 0f,
            });
        }

        public void SetVisemeWeights(IReadOnlyDictionary<string, float> visemes)
        {
            var layer = _layers[NpcifyExpressionLayer.Viseme];
            layer.Clear();
            if (visemes == null)
            {
                return;
            }

            foreach (var entry in visemes)
            {
                var canonical = NormalizeExpressionName(entry.Key);
                if (VisemeKeys.Contains(canonical))
                {
                    layer[canonical] = Clamp01(entry.Value);
                }
            }
        }

        public void ClearVisemes()
        {
            _layers[NpcifyExpressionLayer.Viseme].Clear();
        }

        public void SetMouthOpenness(float openness)
        {
            var layer = _layers[NpcifyExpressionLayer.Viseme];
            layer.Clear();
            var clamped = Clamp01(openness);
            if (clamped > 0.001f)
            {
                layer["aa"] = clamped;
                layer["oh"] = clamped * 0.35f;
            }
        }

        public void SetGazeAngles(
            float yawDegrees,
            float pitchDegrees,
            float maxYawDegrees = 35f,
            float maxPitchDegrees = 25f
        )
        {
            var safeMaxYaw = Math.Max(1f, maxYawDegrees);
            var safeMaxPitch = Math.Max(1f, maxPitchDegrees);
            var layer = _layers[NpcifyExpressionLayer.Gaze];
            layer["lookRight"] = Clamp01(yawDegrees / safeMaxYaw);
            layer["lookLeft"] = Clamp01(-yawDegrees / safeMaxYaw);
            layer["lookUp"] = Clamp01(pitchDegrees / safeMaxPitch);
            layer["lookDown"] = Clamp01(-pitchDegrees / safeMaxPitch);
        }

        public void SetGazeDirection(
            float localX,
            float localY,
            float localZ,
            float maxYawDegrees = 35f,
            float maxPitchDegrees = 25f
        )
        {
            var magnitude = (float)Math.Sqrt(localX * localX + localY * localY + localZ * localZ);
            if (magnitude <= 0.0001f)
            {
                ClearGaze();
                return;
            }

            var nx = localX / magnitude;
            var ny = Math.Max(-1f, Math.Min(1f, localY / magnitude));
            var nz = localZ / magnitude;
            var yawDegrees = (float)(Math.Atan2(nx, nz) * (180.0 / Math.PI));
            var pitchDegrees = (float)(Math.Asin(ny) * (180.0 / Math.PI));
            SetGazeAngles(yawDegrees, pitchDegrees, maxYawDegrees, maxPitchDegrees);
        }

        public void ClearGaze()
        {
            _layers[NpcifyExpressionLayer.Gaze].Clear();
        }

        public void Reset()
        {
            foreach (var layer in _layers.Values)
            {
                layer.Clear();
            }
            _activeCues.Clear();
            var keys = new List<string>(_smoothedWeights.Keys);
            for (var i = 0; i < keys.Count; i++)
            {
                _smoothedWeights[keys[i]] = 0f;
            }
            _blinkTimer = 0f;
            _isBlinking = false;
        }

        public void Update(float deltaTimeSeconds)
        {
            var dt = Math.Max(0f, deltaTimeSeconds);
            UpdateAutoBlink(dt);
            UpdateActiveCues(dt);

            var targets = ComputeCombinedTargets();
            var alpha = dt <= 0f ? 1f : (float)(1.0 - Math.Exp(-SmoothingRate * dt));

            foreach (var key in CanonicalExpressions)
            {
                var target = targets.TryGetValue(key, out var t) ? t : 0f;
                var current = _smoothedWeights.TryGetValue(key, out var c) ? c : 0f;
                var next = current + (target - current) * alpha;
                _smoothedWeights[key] = next < 0.0005f && target == 0f ? 0f : Clamp01(next);
            }
        }

        public float ResolveBlendShapeWeight01(string blendShapeName)
        {
            if (string.IsNullOrWhiteSpace(blendShapeName))
            {
                return 0f;
            }

            if (AliasToCanonical.TryGetValue(blendShapeName.Trim(), out var canonical))
            {
                if ((canonical == "blinkLeft" || canonical == "blinkRight") &&
                    _smoothedWeights.TryGetValue("blink", out var fullBlink) &&
                    fullBlink > 0f)
                {
                    var side = _smoothedWeights.TryGetValue(canonical, out var s) ? s : 0f;
                    return Clamp01(Math.Max(fullBlink, side));
                }

                return _smoothedWeights.TryGetValue(canonical, out var w) ? w : 0f;
            }

            return 0f;
        }

        private Dictionary<string, float> ComputeCombinedTargets()
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);

            foreach (var layer in _layers.Values)
            {
                foreach (var kv in layer)
                {
                    var existing = result.TryGetValue(kv.Key, out var cur) ? cur : 0f;
                    result[kv.Key] = Clamp01(Math.Max(existing, kv.Value));
                }
            }

            for (var i = 0; i < _activeCues.Count; i++)
            {
                var cue = _activeCues[i];
                var envelope = EvaluateCueEnvelope(cue);
                var existing = result.TryGetValue(cue.Expression, out var cur) ? cur : 0f;
                result[cue.Expression] = Clamp01(Math.Max(existing, cue.TargetWeight * envelope));
            }

            // Attenuate strong emotion mouth shapes slightly during active viseme articulation
            var visemePeak = 0f;
            foreach (var viseme in VisemeKeys)
            {
                if (result.TryGetValue(viseme, out var v) && v > visemePeak)
                {
                    visemePeak = v;
                }
            }

            if (visemePeak > 0.1f)
            {
                var emotionScale = 1f - 0.35f * visemePeak;
                string[] emotionKeys = { "happy", "angry", "sad", "relaxed", "surprised" };
                for (var i = 0; i < emotionKeys.Length; i++)
                {
                    var key = emotionKeys[i];
                    if (result.TryGetValue(key, out var val))
                    {
                        result[key] = Clamp01(val * emotionScale);
                    }
                }
            }

            return result;
        }

        private void UpdateActiveCues(float dt)
        {
            for (var i = _activeCues.Count - 1; i >= 0; i--)
            {
                var cue = _activeCues[i];
                cue.Elapsed += dt;
                if (cue.Elapsed >= cue.Duration)
                {
                    _activeCues.RemoveAt(i);
                }
            }
        }

        private static float EvaluateCueEnvelope(ActiveCue cue)
        {
            if (cue.Elapsed < 0f || cue.Elapsed >= cue.Duration)
            {
                return 0f;
            }

            var fadeInFactor = cue.FadeIn > 0.0001f ? Clamp01(cue.Elapsed / cue.FadeIn) : 1f;
            var remaining = cue.Duration - cue.Elapsed;
            var fadeOutFactor = cue.FadeOut > 0.0001f ? Clamp01(remaining / cue.FadeOut) : 1f;
            return Math.Min(fadeInFactor, fadeOutFactor);
        }

        private void UpdateAutoBlink(float dt)
        {
            if (!AutoBlinkEnabled)
            {
                return;
            }

            _blinkTimer += dt;
            if (!_isBlinking)
            {
                if (_blinkTimer >= _nextBlinkInterval)
                {
                    _isBlinking = true;
                    _blinkTimer = 0f;
                }
                return;
            }

            var progress = _blinkTimer / _blinkDuration;
            if (progress >= 1f)
            {
                _isBlinking = false;
                _blinkTimer = 0f;
                _layers[NpcifyExpressionLayer.Blink]["blink"] = 0f;
                _nextBlinkInterval = 2.4f + NextRandom01() * 3.2f;
            }
            else
            {
                var wave = (float)Math.Sin(progress * Math.PI);
                _layers[NpcifyExpressionLayer.Blink]["blink"] = Clamp01(wave);
            }
        }

        private float NextRandom01()
        {
            _rngState ^= _rngState << 13;
            _rngState ^= _rngState >> 17;
            _rngState ^= _rngState << 5;
            return (_rngState & 0x00FFFFFFu) / (float)0x01000000u;
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
