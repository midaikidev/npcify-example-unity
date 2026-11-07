using System;
using System.Text;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    /// <summary>WAV and PCM decoding of the runtime.</summary>
    public sealed partial class NpcifyCharacterRuntime
    {
        /// <summary>
        /// True when the audio bytes are WAV or raw PCM, which <see cref="DecodeAudioBytes"/> can decode.
        /// Compressed audio, such as MPEG, must be decoded by the engine integration.
        /// </summary>
        public static bool IsPcmAudio(StagingAudioSource audio)
        {
            if (audio?.Data == null || audio.Data.Length == 0)
            {
                return false;
            }
            var data = audio.Data;
            if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F')
            {
                return true;
            }
            var mime = audio.MimeType ?? string.Empty;
            return mime.IndexOf("wav", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   mime.IndexOf("pcm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   mime.IndexOf("L16", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static NpcifyDecodedAudio DecodeAudioBytes(
            byte[] bytes,
            int defaultSampleRate = 24000,
            int defaultChannels = 1
        )
        {
            if (bytes == null || bytes.Length == 0)
            {
                return new NpcifyDecodedAudio
                {
                    Samples = Array.Empty<float>(),
                    SampleRate = defaultSampleRate > 0 ? defaultSampleRate : 24000,
                    Channels = defaultChannels > 0 ? defaultChannels : 1,
                };
            }

            if (IsWavBytes(bytes))
            {
                return DecodeWavBytes(bytes, defaultSampleRate, defaultChannels);
            }

            return DecodeRawPcm16(
                bytes,
                defaultSampleRate > 0 ? defaultSampleRate : 24000,
                defaultChannels > 0 ? defaultChannels : 1
            );
        }

        /// <summary>
        /// The length in seconds of the bytes that <see cref="DecodeAudioBytes"/> decodes, read from
        /// the WAV header (raw PCM: 16-bit samples at the default rate and channels), without a
        /// decode. 0 when the bytes hold no samples.
        /// </summary>
        public static double PcmDurationSeconds(byte[] bytes, int defaultSampleRate = 24000, int defaultChannels = 1)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return 0.0;
            }
            var rate = defaultSampleRate > 0 ? defaultSampleRate : 24000;
            var channels = defaultChannels > 0 ? defaultChannels : 1;
            if (!IsWavBytes(bytes))
            {
                return bytes.Length / 2 / (double)(rate * channels);
            }
            var header = ReadWavHeader(bytes, rate, channels);
            var bytesPerSample = header.Format == 3 && header.BitsPerSample == 32 ? 4 : header.BitsPerSample == 16 ? 2 : 0;
            if (header.DataOffset < 0 || header.DataSize <= 0 || bytesPerSample == 0 || header.SampleRate <= 0)
            {
                return 0.0;
            }
            return header.DataSize / bytesPerSample / (double)(header.SampleRate * header.Channels);
        }

        private static bool IsWavBytes(byte[] bytes)
        {
            return bytes.Length >= 44 &&
                   bytes[0] == (byte)'R' &&
                   bytes[1] == (byte)'I' &&
                   bytes[2] == (byte)'F' &&
                   bytes[3] == (byte)'F' &&
                   bytes[8] == (byte)'W' &&
                   bytes[9] == (byte)'A' &&
                   bytes[10] == (byte)'V' &&
                   bytes[11] == (byte)'E';
        }

        private struct WavHeader
        {
            public int Format;
            public int Channels;
            public int SampleRate;
            public int BitsPerSample;
            public int DataOffset;
            public int DataSize;
        }

        private static WavHeader ReadWavHeader(byte[] bytes, int fallbackRate, int fallbackChannels)
        {
            var header = new WavHeader
            {
                Format = 1,
                Channels = fallbackChannels > 0 ? fallbackChannels : 1,
                SampleRate = fallbackRate > 0 ? fallbackRate : 24000,
                BitsPerSample = 16,
                DataOffset = -1,
                DataSize = 0,
            };

            var offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
                var chunkSize =
                    bytes[offset + 4] |
                    (bytes[offset + 5] << 8) |
                    (bytes[offset + 6] << 16) |
                    (bytes[offset + 7] << 24);
                offset += 8;

                if (chunkSize < 0 || offset + chunkSize > bytes.Length)
                {
                    break;
                }

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    header.Format = bytes[offset] | (bytes[offset + 1] << 8);
                    header.Channels = Math.Max(1, bytes[offset + 2] | (bytes[offset + 3] << 8));
                    header.SampleRate =
                        bytes[offset + 4] |
                        (bytes[offset + 5] << 8) |
                        (bytes[offset + 6] << 16) |
                        (bytes[offset + 7] << 24);
                    header.BitsPerSample = bytes[offset + 14] | (bytes[offset + 15] << 8);
                }
                else if (chunkId == "data")
                {
                    header.DataOffset = offset;
                    header.DataSize = chunkSize;
                    break;
                }

                offset += chunkSize + (chunkSize & 1);
            }
            return header;
        }

        private static NpcifyDecodedAudio DecodeWavBytes(byte[] bytes, int fallbackRate, int fallbackChannels)
        {
            var header = ReadWavHeader(bytes, fallbackRate, fallbackChannels);
            var audioFormat = header.Format;
            var channels = header.Channels;
            var sampleRate = header.SampleRate;
            var bitsPerSample = header.BitsPerSample;
            var dataOffset = header.DataOffset;
            var dataSize = header.DataSize;

            if (dataOffset < 0 || dataSize <= 0)
            {
                return new NpcifyDecodedAudio
                {
                    Samples = Array.Empty<float>(),
                    SampleRate = sampleRate,
                    Channels = channels,
                };
            }

            if (audioFormat == 3 && bitsPerSample == 32)
            {
                var sampleCount = dataSize / 4;
                var samples = new float[sampleCount];
                Buffer.BlockCopy(bytes, dataOffset, samples, 0, sampleCount * 4);
                return new NpcifyDecodedAudio
                {
                    Samples = samples,
                    SampleRate = sampleRate,
                    Channels = channels,
                };
            }

            if (bitsPerSample == 16)
            {
                var sampleCount = dataSize / 2;
                var samples = new float[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    var pcm = (short)(bytes[dataOffset + i * 2] | (bytes[dataOffset + i * 2 + 1] << 8));
                    samples[i] = pcm / 32768f;
                }
                return new NpcifyDecodedAudio
                {
                    Samples = samples,
                    SampleRate = sampleRate,
                    Channels = channels,
                };
            }

            return new NpcifyDecodedAudio
            {
                Samples = Array.Empty<float>(),
                SampleRate = sampleRate,
                Channels = channels,
            };
        }

        private static NpcifyDecodedAudio DecodeRawPcm16(byte[] bytes, int sampleRate, int channels)
        {
            var sampleCount = bytes.Length / 2;
            var samples = new float[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                var pcm = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                samples[i] = pcm / 32768f;
            }
            return new NpcifyDecodedAudio
            {
                Samples = samples,
                SampleRate = sampleRate,
                Channels = channels,
            };
        }
    }
}
