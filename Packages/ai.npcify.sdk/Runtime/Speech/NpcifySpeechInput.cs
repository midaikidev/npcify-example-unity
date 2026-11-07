using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Speech
{
    /// <summary>How the player can reply (docs/voice-input.md).</summary>
    public enum NpcifyInputMode
    {
        Options,
        Text,
        Voice,
    }

    /// <summary>An input-mode configuration that is invalid or that the character does not permit.</summary>
    public sealed class NpcifyInputModeException : InvalidOperationException
    {
        public NpcifyInputModeException(string message)
            : base(message)
        {
        }
    }

    public static class NpcifyInputModes
    {
        private static readonly NpcifyInputMode[] All =
        {
            NpcifyInputMode.Options,
            NpcifyInputMode.Text,
            NpcifyInputMode.Voice,
        };

        public static string Name(NpcifyInputMode mode)
        {
            switch (mode)
            {
                case NpcifyInputMode.Options:
                    return "options";
                case NpcifyInputMode.Text:
                    return "text";
                default:
                    return "voice";
            }
        }

        /// <summary>
        /// The modes that a character permits. Voice and typed text are custom replies; voice
        /// also needs the owner's voice input (speechInputEnabled, the STT tab).
        /// </summary>
        public static IReadOnlyList<NpcifyInputMode> Permitted(
            bool allowCustomReply,
            bool baked,
            bool speechInputEnabled
        )
        {
            if (baked || !allowCustomReply)
            {
                return new[] { NpcifyInputMode.Options };
            }
            return speechInputEnabled ? All : new[] { NpcifyInputMode.Options, NpcifyInputMode.Text };
        }

        /// <summary>
        /// Checks the shape of a configuration: null, or one or more different modes.
        /// An empty list is no configuration in the Unity Inspector, so it is null here.
        /// </summary>
        public static IReadOnlyList<NpcifyInputMode> Validate(IList<NpcifyInputMode> configured)
        {
            if (configured == null || configured.Count == 0)
            {
                return null;
            }
            var modes = new List<NpcifyInputMode>();
            foreach (var mode in configured)
            {
                if (!Enum.IsDefined(typeof(NpcifyInputMode), mode))
                {
                    throw new NpcifyInputModeException(
                        "InputModes has an unknown mode. Use Options, Text or Voice.");
                }
                if (modes.Contains(mode))
                {
                    throw new NpcifyInputModeException($"InputModes has \"{Name(mode)}\" two times.");
                }
                modes.Add(mode);
            }
            return modes;
        }

        /// <summary>
        /// The modes in use: the configured modes, or every mode that the character permits.
        /// A baked conversation permits only Options; a character without custom replies
        /// permits only Options; a character with voice input off does not permit Voice.
        /// </summary>
        public static IReadOnlyList<NpcifyInputMode> Resolve(
            IList<NpcifyInputMode> configured,
            bool allowCustomReply,
            bool baked,
            bool speechInputEnabled
        )
        {
            var modes = Validate(configured);
            var permitted = Permitted(allowCustomReply, baked, speechInputEnabled);
            if (modes == null)
            {
                return permitted;
            }
            if (baked && !(modes.Count == 1 && modes[0] == NpcifyInputMode.Options))
            {
                throw new NpcifyInputModeException(
                    "A baked (offline) conversation accepts only reply options. Set InputModes to Options or leave it empty.");
            }
            var refused = new List<string>();
            foreach (var mode in modes)
            {
                if (!Contains(permitted, mode))
                {
                    refused.Add("\"" + Name(mode) + "\"");
                }
            }
            if (refused.Count > 0)
            {
                throw new NpcifyInputModeException(
                    (allowCustomReply
                        ? "Voice input is off for this character (its owner turns it on in the STT tab). Remove "
                        : "This character accepts only its reply options (Allow custom replies is off). Remove ")
                    + string.Join(" and ", refused) + " from InputModes.");
            }
            return modes;
        }

        public static bool Contains(IReadOnlyList<NpcifyInputMode> modes, NpcifyInputMode mode)
        {
            for (var i = 0; i < modes.Count; i++)
            {
                if (modes[i] == mode)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public enum NpcifyListeningState
    {
        Idle,
        Starting,
        Listening,
        Recognizing,
    }

    public sealed class NpcifyListeningOptions
    {
        /// <summary>BCP 47 hint, for example "ja", "zh" or "en". Empty: the language is detected.</summary>
        public string Language;
        /// <summary>False: recognise only; send the text later with SendTranscriptAsync.</summary>
        public bool Send = true;
        /// <summary>The recording stops at this time. Default and maximum: 30 seconds.</summary>
        public float MaxDurationSeconds = NpcifySpeechAudio.MaxDurationSeconds;
        /// <summary>For listening without a release: stop after this much silence that follows speech. 0: off.</summary>
        public float EndOnSilenceSeconds;
        public bool Silent;
    }

    /// <summary>The response of POST /conversations/:id/speech.</summary>
    public sealed class NpcifySpeechTranscript
    {
        public string Text = string.Empty;
        public string Language;
        public int DurationMs;
    }

    public sealed class NpcifySpeechInputResult
    {
        /// <summary>The recognised text; empty when no speech was recognised (then no turn starts).</summary>
        public string Text = string.Empty;
        public string Language;
        public int DurationMs;
        /// <summary>True when the text was sent as the player's message.</summary>
        public bool Sent;
        /// <summary>The turn's final payload when the text was sent; null otherwise.</summary>
        public Task<StagingResultPayload> Turn;
    }

    /// <summary>16-bit mono PCM at 16 kHz in a WAV file: the format of the speech endpoint.</summary>
    public static class NpcifySpeechAudio
    {
        public const int SampleRate = 16000;
        public const float MaxDurationSeconds = 30f;
        public const int MaxSamples = (int)(SampleRate * MaxDurationSeconds);
        /// <summary>A shorter push-to-talk press sends nothing.</summary>
        public const float MinHoldSeconds = 0.3f;

        /// <summary>
        /// Interleaved float samples at any rate to 16 kHz mono 16-bit. Downsampling
        /// averages the input samples of each output sample, as the web SDK does.
        /// </summary>
        public static short[] ToPcm16Mono16k(float[] samples, int channels, int inputRate, int count = -1)
        {
            if (samples == null || channels <= 0 || inputRate <= 0)
            {
                return Array.Empty<short>();
            }
            var frames = (count < 0 ? samples.Length : Math.Min(count, samples.Length)) / channels;
            var ratio = inputRate / (double)SampleRate;
            var output = new List<short>((int)(frames / ratio) + 1);
            double sum = 0;
            var summed = 0;
            double phase = 0;
            for (var frame = 0; frame < frames; frame++)
            {
                double value = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    value += samples[frame * channels + channel];
                }
                sum += value / channels;
                summed++;
                phase += 1;
                while (phase >= ratio)
                {
                    phase -= ratio;
                    output.Add(ToShort(sum / Math.Max(1, summed)));
                    if (ratio >= 1)
                    {
                        sum = 0;
                        summed = 0;
                    }
                }
                if (ratio < 1)
                {
                    sum = 0;
                    summed = 0;
                }
            }
            if (summed > 0 && ratio >= 1)
            {
                output.Add(ToShort(sum / summed));
            }
            return output.Count > MaxSamples ? output.GetRange(0, MaxSamples).ToArray() : output.ToArray();
        }

        public static byte[] EncodeWav(short[] pcm)
        {
            pcm = pcm ?? Array.Empty<short>();
            using (var stream = new MemoryStream(44 + pcm.Length * 2))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + pcm.Length * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(SampleRate);
                writer.Write(SampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(pcm.Length * 2);
                foreach (var sample in pcm)
                {
                    writer.Write(sample);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>The loudness of float samples (RMS, 0 to 1).</summary>
        public static float Level(float[] samples, int offset, int count)
        {
            if (samples == null || count <= 0)
            {
                return 0f;
            }
            double sum = 0;
            var end = Math.Min(samples.Length, offset + count);
            for (var i = offset; i < end; i++)
            {
                sum += samples[i] * samples[i];
            }
            return (float)Math.Sqrt(sum / Math.Max(1, end - offset));
        }

        private static short ToShort(double value)
        {
            var clamped = Math.Max(-1.0, Math.Min(1.0, value));
            return (short)Math.Round(clamped < 0 ? clamped * 32768 : clamped * 32767);
        }
    }

    /// <summary>
    /// The end of speech for listening without a release (EndOnSilenceSeconds), with the
    /// rule of the web SDK: it follows the noise floor and ends after the given silence
    /// that follows at least 200 ms of speech. Push-to-talk never uses it.
    /// </summary>
    public sealed class NpcifySilenceDetector
    {
        private const float MinSpeechLevel = 0.015f;
        private const float SpeechToNoise = 3f;
        private const float SpeechStartSeconds = 0.2f;

        private readonly float _silenceSeconds;
        private float _floor = -1f;
        private float _loudSeconds;
        private float _quietSeconds;

        public NpcifySilenceDetector(float silenceSeconds)
        {
            _silenceSeconds = silenceSeconds;
        }

        public bool HeardSpeech { get; private set; }

        /// <summary>True when the speech has ended.</summary>
        public bool Push(float level, float frameSeconds)
        {
            var floor = _floor < 0f ? MinSpeechLevel / SpeechToNoise : _floor;
            var threshold = Math.Max(MinSpeechLevel, floor * SpeechToNoise);
            if (level >= threshold)
            {
                _loudSeconds += frameSeconds;
                _quietSeconds = 0f;
                if (_loudSeconds >= SpeechStartSeconds - 1e-4f)
                {
                    HeardSpeech = true;
                }
                return false;
            }
            _floor = _floor < 0f ? level : _floor * 0.9f + level * 0.1f;
            _loudSeconds = 0f;
            if (!HeardSpeech)
            {
                return false;
            }
            _quietSeconds += frameSeconds;
            return _quietSeconds >= _silenceSeconds - 1e-4f;
        }
    }
}
