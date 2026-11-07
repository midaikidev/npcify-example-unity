using System;
using System.Collections.Generic;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    /// <summary>The MP3 chunks of the speech of a streamed turn (docs/streaming_reply.md).</summary>
    internal sealed partial class NpcifyStreamTurn
    {
        private sealed class AudioStream
        {
            public readonly List<byte[]> Chunks = new List<byte[]>();
            public long NextIndex;
            public bool Broken;
            public byte[] Bytes;
        }

        private readonly Dictionary<string, AudioStream> _audio = new Dictionary<string, AudioStream>(StringComparer.Ordinal);

        /// <summary>The block can play: a pause, silent speech, or speech whose audio arrived completely.</summary>
        public static bool HasPlayableAudio(StagingDialogueBlock block)
        {
            if (block == null || block.Type != "speech")
            {
                return true;
            }
            var audio = block.Audio;
            if (audio == null || audio.Type == "silent")
            {
                return true;
            }
            if (audio.Type == "stream")
            {
                return false;
            }
            return (audio.Data != null && audio.Data.Length > 0) || !string.IsNullOrWhiteSpace(audio.Url);
        }

        private void AppendChunk(Dictionary<string, object> streamEvent)
        {
            var streamId = GetString(streamEvent, "streamId");
            if (string.IsNullOrEmpty(streamId))
            {
                return;
            }
            var audio = GetAudio(streamId);
            if (audio.Bytes != null || audio.Broken)
            {
                return;
            }
            var index = GetLong(streamEvent, "chunkIndex", -1);
            var data = GetBytes(streamEvent, "data");
            if (index != audio.NextIndex || data == null)
            {
                // A missed chunk: this speech plays the stored audio of the HTTP result.
                BreakAudio(streamId);
                return;
            }
            audio.Chunks.Add(data);
            audio.NextIndex++;
        }

        private NpcifyStreamChange CompleteAudio(string streamId)
        {
            if (string.IsNullOrEmpty(streamId))
            {
                return NpcifyStreamChange.None;
            }
            var audio = GetAudio(streamId);
            if (audio.Bytes != null || audio.Broken || audio.Chunks.Count == 0)
            {
                return NpcifyStreamChange.None;
            }
            var length = 0;
            foreach (var chunk in audio.Chunks)
            {
                length += chunk.Length;
            }
            var bytes = new byte[length];
            var offset = 0;
            foreach (var chunk in audio.Chunks)
            {
                Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length);
                offset += chunk.Length;
            }
            audio.Chunks.Clear();
            audio.Bytes = bytes;
            var changed = false;
            foreach (var block in Payload.Timeline.Dialogue)
            {
                changed |= AttachAudio(block);
            }
            return changed ? NpcifyStreamChange.Audio : NpcifyStreamChange.None;
        }

        private void BreakAudio(string streamId)
        {
            if (string.IsNullOrEmpty(streamId))
            {
                return;
            }
            var audio = GetAudio(streamId);
            if (audio.Bytes == null)
            {
                audio.Broken = true;
                audio.Chunks.Clear();
            }
        }

        private AudioStream GetAudio(string streamId)
        {
            if (!_audio.TryGetValue(streamId, out var audio))
            {
                audio = new AudioStream();
                _audio[streamId] = audio;
            }
            return audio;
        }

        // A stream block whose chunks are complete gets them as MP3 bytes.
        private bool AttachAudio(StagingDialogueBlock block)
        {
            var audio = block?.Audio;
            if (audio == null || audio.Type != "stream" || string.IsNullOrEmpty(audio.StreamId) ||
                !_audio.TryGetValue(audio.StreamId, out var stream) || stream.Bytes == null)
            {
                return false;
            }
            audio.Type = "bytes";
            audio.Data = stream.Bytes;
            audio.MimeType = string.IsNullOrEmpty(audio.MimeType) ? "audio/mpeg" : audio.MimeType;
            return true;
        }

        // The audio streams of a run that the turn forgets. Each synthesis has a new stream ID, so
        // the streams of a rerun that came before its first revision stay.
        private void ForgetAudioOf(List<StagingDialogueBlock> dialogue)
        {
            foreach (var block in dialogue)
            {
                var streamId = block?.Audio?.StreamId;
                if (!string.IsNullOrEmpty(streamId))
                {
                    _audio.Remove(streamId);
                }
            }
        }

        private static byte[] GetBytes(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value))
            {
                return null;
            }
            if (value is byte[] bytes)
            {
                return bytes;
            }
            if (value is string base64)
            {
                try
                {
                    return Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    return null;
                }
            }
            return null;
        }
    }
}
