using System;

namespace Npcify.Sdk.Character
{
    /// <summary>
    /// The audio of MP3 bytes as minimp3 and FFmpeg decode it (docs/streaming_reply.md): the
    /// samples of the MPEG audio frames after an ID3v2 tag. The server times a block on these
    /// samples. A Xing or Info tag frame is not audio, and the encoder delay and padding that its
    /// LAME extension gives are not audio either. Unity's MP3 decoder gives more samples:
    /// <see cref="TryLocate"/> finds the audio in its clip.
    /// </summary>
    internal readonly struct NpcifyMp3Audio
    {
        // The decoder delay that minimp3 and FFmpeg add to the encoder delay of a LAME tag.
        private const int DecoderDelay = 528 + 1;

        private static readonly int[] Mpeg1Layer1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 };
        private static readonly int[] Mpeg1Layer2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };
        private static readonly int[] Mpeg1Layer3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        private static readonly int[] Mpeg2Layer1 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 };
        private static readonly int[] Mpeg2Layer23 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
        private static readonly int[] Mpeg1Rates = { 44100, 48000, 32000 };

        private NpcifyMp3Audio(int sampleRate, int channels, int tagFrameSamples, long frameSamples, long delay, long samples)
        {
            SampleRate = sampleRate;
            Channels = channels;
            TagFrameSamples = tagFrameSamples;
            FrameSamples = frameSamples;
            Delay = delay;
            Samples = samples;
        }

        /// <summary>The sample rate of the first audio frame in Hz.</summary>
        public int SampleRate { get; }

        /// <summary>The channels of the first audio frame.</summary>
        public int Channels { get; }

        /// <summary>The samples per channel of the Xing or Info tag frame, or 0 without that frame.</summary>
        public int TagFrameSamples { get; }

        /// <summary>The samples per channel of the audio frames (without the tag frame).</summary>
        public long FrameSamples { get; }

        /// <summary>The samples per channel before the audio in the audio frames (the delay of a LAME tag).</summary>
        public long Delay { get; }

        /// <summary>The samples per channel of the audio.</summary>
        public long Samples { get; }

        /// <summary>The length of the audio in seconds.</summary>
        public double Seconds => SampleRate > 0 ? (double)Samples / SampleRate : 0.0;

        /// <summary>
        /// Reads the frames of MP3 bytes. Gives false for bytes without MPEG audio frames and for
        /// free-format frames.
        /// </summary>
        public static bool TryRead(byte[] data, out NpcifyMp3Audio audio)
        {
            audio = default;
            if (data == null || data.Length < 4)
            {
                return false;
            }
            var end = data.Length;
            var offset = SkipId3v2(data);
            end = SkipEndTags(data, offset, end);
            var first = FindFrame(data, offset, end, null);
            if (first < 0 || !TryReadHeader(data, first, end, out var header))
            {
                return false;
            }

            var tagFrameSamples = 0;
            long declaredSamples = 0;
            long delay = 0;
            uint tagFrames = 0;
            long encoderDelay = 0;
            long encoderPadding = 0;
            var tag = header.Layer == 3 ? ReadTag(data, first, header, out tagFrames, out encoderDelay, out encoderPadding) : 0;
            if (tag != 0)
            {
                // minimp3 and FFmpeg do not decode the tag frame.
                tagFrameSamples = header.Samples;
                offset = first + header.Bytes;
                if (tag > 0)
                {
                    delay = encoderDelay;
                    declaredSamples = (long)tagFrames * header.Samples;
                    if (declaredSamples >= delay)
                    {
                        declaredSamples -= delay;
                    }
                    if (encoderPadding > 0 && declaredSamples >= encoderPadding)
                    {
                        declaredSamples -= encoderPadding;
                    }
                    if (declaredSamples == 0)
                    {
                        return false;
                    }
                }
            }
            else
            {
                offset = first;
            }

            long frameSamples = 0;
            while (offset < end)
            {
                var frame = FindFrame(data, offset, end, header);
                if (frame < 0 || !TryReadHeader(data, frame, end, out var next))
                {
                    break;
                }
                frameSamples += next.Samples;
                offset = frame + next.Bytes;
            }

            var samples = Math.Max(0, frameSamples - delay);
            if (declaredSamples > 0 && samples > declaredSamples)
            {
                samples = declaredSamples;
            }
            if (samples == 0)
            {
                return false;
            }
            audio = new NpcifyMp3Audio(header.SampleRate, header.Channels, tagFrameSamples, frameSamples, delay, samples);
            return true;
        }

        /// <summary>
        /// Finds the audio in a clip from Unity's MP3 decoder (FMOD). That decoder decodes a tag
        /// frame as silence and keeps the delay and padding of a LAME tag. It cannot get the length
        /// of a stream without a Xing or Info tag from the bytes: it gives too many samples, and
        /// the samples after the last frame are zero. The audio thus starts after the tag frame and
        /// the delay. Gives false when the clip does not have this layout: too few samples, a tag
        /// frame that is not silent, or samples after the last frame that are not silent.
        /// </summary>
        /// <param name="clipSamples">The samples per channel of the clip.</param>
        /// <param name="isSilent">Gives true when all samples of the clip from a first sample, for a count of samples, are zero.</param>
        /// <param name="start">The first sample of the audio in the clip.</param>
        public bool TryLocate(long clipSamples, Func<long, long, bool> isSilent, out long start)
        {
            start = 0;
            if (Samples <= 0 || isSilent == null)
            {
                return false;
            }
            var first = TagFrameSamples + Delay;
            if (clipSamples < first + Samples)
            {
                return false;
            }
            if (TagFrameSamples > 0 && !isSilent(0, TagFrameSamples))
            {
                return false;
            }
            var framesEnd = TagFrameSamples + FrameSamples;
            if (clipSamples > framesEnd && !isSilent(framesEnd, clipSamples - framesEnd))
            {
                return false;
            }
            start = first;
            return true;
        }

        private struct Header
        {
            public int Version;
            public int Layer;
            public int SampleRate;
            public int Channels;
            public int Samples;
            public int Bytes;
            public bool Crc;
            public byte VersionLayerBits;
            public int RateBits;
        }

        private static bool TryReadHeader(byte[] data, int offset, int end, out Header header)
        {
            header = default;
            if (offset < 0 || offset > end - 4 || data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0)
            {
                return false;
            }
            var b1 = data[offset + 1];
            var b2 = data[offset + 2];
            var version = (b1 >> 3) & 3;
            var layerBits = (b1 >> 1) & 3;
            var bitrateIndex = b2 >> 4;
            var rateIndex = (b2 >> 2) & 3;
            // As minimp3: MPEG-2.5 only with Layer III; no free format.
            if (version == 1 || layerBits == 0 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3 ||
                (version == 0 && layerBits != 1))
            {
                return false;
            }
            var layer = 4 - layerBits;
            var mpeg1 = version == 3;
            var rates = mpeg1
                ? (layer == 1 ? Mpeg1Layer1 : layer == 2 ? Mpeg1Layer2 : Mpeg1Layer3)
                : (layer == 1 ? Mpeg2Layer1 : Mpeg2Layer23);
            var kbps = rates[bitrateIndex];
            var sampleRate = Mpeg1Rates[rateIndex] >> (mpeg1 ? 0 : version == 2 ? 1 : 2);
            var samples = layer == 1 ? 384 : layer == 3 && !mpeg1 ? 576 : 1152;
            var bytes = samples * kbps * 125 / sampleRate;
            var padding = (b2 >> 1) & 1;
            bytes = layer == 1 ? (bytes & ~3) + padding * 4 : bytes + padding;
            if (bytes <= 4)
            {
                return false;
            }
            header = new Header
            {
                Version = version,
                Layer = layer,
                SampleRate = sampleRate,
                Channels = (data[offset + 3] >> 6) == 3 ? 1 : 2,
                Samples = samples,
                Bytes = bytes,
                Crc = (b1 & 1) == 0,
                VersionLayerBits = (byte)(b1 & 0xFE),
                RateBits = rateIndex,
            };
            return true;
        }

        // A frame of the stream: same MPEG version, layer and sample rate (minimp3 hdr_compare).
        private static bool Matches(Header header, Header reference)
        {
            return header.VersionLayerBits == reference.VersionLayerBits && header.RateBits == reference.RateBits;
        }

        // The first frame at or after an offset whose end is the end of the data or the header of a
        // matching frame, as minimp3 finds frames. A frame with other bytes after it is not audio.
        // -1: no frame.
        private static int FindFrame(byte[] data, int offset, int end, Header? reference)
        {
            for (var position = offset; position <= end - 4; position++)
            {
                if (!TryReadHeader(data, position, end, out var header) ||
                    (reference.HasValue && !Matches(header, reference.Value)) ||
                    position + header.Bytes > end)
                {
                    continue;
                }
                var next = position + header.Bytes;
                if (next == end || (TryReadHeader(data, next, end, out var following) && Matches(following, header)))
                {
                    return position;
                }
            }
            return -1;
        }

        // 1: a Xing or Info tag with a frame count; -1: a tag without a frame count (no length,
        // no delay); 0: no tag (minimp3 mp3dec_check_vbrtag).
        private static int ReadTag(byte[] data, int frame, Header header, out uint frames, out long delay, out long padding)
        {
            frames = 0;
            delay = 0;
            padding = 0;
            var sideInfo = header.Version == 3 ? (header.Channels == 1 ? 17 : 32) : (header.Channels == 1 ? 9 : 17);
            var position = frame + 4 + (header.Crc ? 2 : 0) + sideInfo;
            var frameEnd = frame + header.Bytes;
            if (position + 8 > frameEnd)
            {
                return 0;
            }
            var xing = data[position] == 'X' && data[position + 1] == 'i' && data[position + 2] == 'n' && data[position + 3] == 'g';
            var info = data[position] == 'I' && data[position + 1] == 'n' && data[position + 2] == 'f' && data[position + 3] == 'o';
            if (!xing && !info)
            {
                return 0;
            }
            var flags = data[position + 7];
            if ((flags & 1) == 0)
            {
                return -1;
            }
            position += 8;
            if (position + 4 > frameEnd)
            {
                return 0;
            }
            frames = (uint)(data[position] << 24 | data[position + 1] << 16 | data[position + 2] << 8 | data[position + 3]);
            position += 4;
            var skip = ((flags & 2) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 100 : 0) + ((flags & 8) != 0 ? 4 : 0);
            if (position + skip > frameEnd)
            {
                return 0;
            }
            position += skip;
            if (position + 1 > frameEnd)
            {
                return 0;
            }
            if (data[position] != 0)
            {
                // An encoder extension (LAME, Lavc): the delay and padding of the encoder.
                if (frameEnd - position <= 35)
                {
                    return 0;
                }
                position += 21;
                delay = ((data[position] << 4) | (data[position + 1] >> 4)) + DecoderDelay;
                padding = (((data[position + 1] & 0xF) << 8) | data[position + 2]) - DecoderDelay;
            }
            return 1;
        }

        private static int SkipId3v2(byte[] data)
        {
            if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3' ||
                (data[5] & 15) != 0 || ((data[6] | data[7] | data[8] | data[9]) & 0x80) != 0)
            {
                return 0;
            }
            var size = ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F)) + 10;
            if ((data[5] & 16) != 0)
            {
                size += 10;
            }
            return Math.Min(size, data.Length);
        }

        // The end of the frames before an ID3v1 or APEv2 tag (minimp3 mp3dec_skip_id3v1).
        private static int SkipEndTags(byte[] data, int start, int end)
        {
            if (end - start >= 128 && data[end - 128] == 'T' && data[end - 127] == 'A' && data[end - 126] == 'G')
            {
                end -= 128;
                if (end - start >= 227 && data[end - 227] == 'T' && data[end - 226] == 'A' && data[end - 225] == 'G' &&
                    data[end - 224] == '+')
                {
                    end -= 227;
                }
            }
            if (end - start > 32 && data[end - 32] == 'A' && data[end - 31] == 'P' && data[end - 30] == 'E' &&
                data[end - 29] == 'T' && data[end - 28] == 'A' && data[end - 27] == 'G' && data[end - 26] == 'E' &&
                data[end - 25] == 'X')
            {
                end -= 32;
                var size = (long)((uint)data[end + 12] | (uint)data[end + 13] << 8 | (uint)data[end + 14] << 16 | (uint)data[end + 15] << 24);
                if (end - start >= size)
                {
                    end -= (int)size;
                }
            }
            return end;
        }
    }
}
