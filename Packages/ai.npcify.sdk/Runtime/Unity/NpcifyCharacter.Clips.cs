#if UNITY_5_3_OR_NEWER
using System;
using Npcify.Sdk.Character;
using UnityEngine;
using UnityEngine.Networking;

namespace Npcify.Sdk.Unity
{
    /// <summary>
    /// The speech in the clips that Unity decodes (docs/streaming_reply.md). Unity's MP3 decoder
    /// gives more samples than the MP3 has: zero samples after the last frame, and for an MP3 with
    /// a LAME tag also a silent tag frame and the encoder delay before the audio. A block plays and
    /// measures only the samples of its MP3 audio, as minimp3 and the server count them.
    /// </summary>
    public sealed partial class NpcifyCharacter
    {
        // A sample above this value is sound. Unity decodes MP3 to 16-bit samples: silence is 0.
        private const float SilenceLimit = 1e-4f;

        /// <summary>A decoded clip of a dialogue block and the samples of its speech.</summary>
        private sealed class SpeechClip
        {
            public SpeechClip(AudioClip clip, int startSample, int samples)
            {
                Clip = clip;
                StartSample = startSample;
                Samples = samples;
            }

            public AudioClip Clip { get; }

            /// <summary>The first sample of the speech in the clip.</summary>
            public int StartSample { get; }

            /// <summary>The samples of the speech, per channel.</summary>
            public int Samples { get; }

            /// <summary>The length of the speech in seconds.</summary>
            public double Seconds => Clip != null && Clip.frequency > 0 ? (double)Samples / Clip.frequency : 0.0;
        }

        // The speech of a decoded clip: the samples of the MP3 audio for MP3 bytes in the layout of
        // Unity's decoder, else all samples of the clip.
        private static SpeechClip FindSpeech(AudioClip clip, byte[] mp3)
        {
            var all = new SpeechClip(clip, 0, clip.samples);
            if (mp3 == null || !NpcifyMp3Audio.TryRead(mp3, out var audio) ||
                audio.SampleRate != clip.frequency || audio.Channels != clip.channels ||
                !audio.TryLocate(clip.samples, (start, count) => IsSilent(clip, start, count), out var first))
            {
                return all;
            }
            return new SpeechClip(clip, (int)first, (int)audio.Samples);
        }

        // The bytes of a download, or null when the platform does not give them: the clip then
        // plays with all of its samples.
        private static byte[] DownloadedBytes(DownloadHandler handler)
        {
            try
            {
                return handler.data;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsSilent(AudioClip clip, long start, long count)
        {
            var samples = new float[count * Math.Max(1, clip.channels)];
            if (!clip.GetData(samples, (int)start))
            {
                return false;
            }
            for (var i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) > SilenceLimit)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
#endif
