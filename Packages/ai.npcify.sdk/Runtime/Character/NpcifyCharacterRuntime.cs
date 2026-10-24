using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Api;
using Npcify.Sdk.Conversation;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    public sealed class NpcifyDecodedAudio
    {
        public float[] Samples { get; set; } = Array.Empty<float>();
        public int SampleRate { get; set; } = 24000;
        public int Channels { get; set; } = 1;
        public double DurationSeconds =>
            SampleRate > 0 && Channels > 0
                ? Samples.Length / (double)(SampleRate * Channels)
                : 0.0;
    }

    public sealed class NpcifyCharacterRuntime : IDisposable
    {
        private readonly NpcifyOptions _options;
        private readonly NpcifyApiClient _api;
        private readonly NpcifyFacialMixer _expressions;
        private readonly NpcifyVisemePlayer _visemePlayer;
        private readonly NpcifyStagingPlayer _stagingPlayer;

        private NpcifyBakedConversation _bakedConversation;
        private string _conversationId;
        private string _activeStagingId;
        private bool _thinking;
        private float _volume = 1f;
        private bool _muted;
        private string _currentAnimation = "idle";

        public NpcifyCharacterRuntime(NpcifyOptions options, NpcifyApiClient apiClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _volume = options.Volume;
            _muted = options.Muted;
            _api = apiClient ?? new NpcifyApiClient(options);
            _expressions = new NpcifyFacialMixer();
            _visemePlayer = new NpcifyVisemePlayer();
            _stagingPlayer = new NpcifyStagingPlayer(_expressions, _visemePlayer);

            _stagingPlayer.OnDialogueBlockStarted += HandleDialogueBlockStarted;
            _stagingPlayer.OnCharacterRevealed += (text, cue) => OnCharacterRevealed?.Invoke(text, cue);
            _stagingPlayer.OnAnimationClipTriggered += clip =>
                PlayAnimation(clip.Animation, clip.Loop);
            _stagingPlayer.OnStagingCompleted += staging => OnSpeechEnd?.Invoke(staging);
        }

        public event Action<StagingResultPayload> OnSpeechStart;
        public event Action<StagingResultPayload> OnSpeechEnd;
        public event Action<StagingDialogueBlock, NpcifyDecodedAudio> OnAudioBlock;
        public event Action<string, StagingCharacterCue> OnCharacterRevealed;
        public event Action<string, bool> OnAnimationPlayRequested;
        public event Action<Exception> OnError;

        public NpcifyOptions Options => _options;

        public NpcifyApiClient Api => _api;

        public NpcifyFacialMixer Expressions => _expressions;

        public NpcifyVisemePlayer VisemePlayer => _visemePlayer;

        public NpcifyStagingPlayer StagingPlayer => _stagingPlayer;

        public NpcifyBakedConversation BakedConversation => _bakedConversation;

        public string ConversationId => _conversationId;

        public string ActiveStagingId => _activeStagingId;

        public string CurrentAnimation => _currentAnimation;

        public float Volume
        {
            get => _volume;
            set => _volume = value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        public bool Muted
        {
            get => _muted;
            set => _muted = value;
        }

        public NpcifySpeechState Speech =>
            new NpcifySpeechState
            {
                Speaking = _stagingPlayer.IsPlaying,
                Thinking = _thinking,
                CurrentSpeech = _stagingPlayer.CurrentBlock,
                CurrentText = _stagingPlayer.CurrentText,
                RevealedText = _stagingPlayer.RevealedText,
                Options = _stagingPlayer.CurrentStaging?.Options != null
                    ? (IReadOnlyList<StagingReplyOption>)_stagingPlayer.CurrentStaging.Options
                    : Array.Empty<StagingReplyOption>(),
                Cursor = _stagingPlayer.Cursor,
            };

        /// <summary>
        /// Optional hook that runs before a staging starts to play, for example to download
        /// or decode its audio. Async conversation methods await it, so playback, audio and
        /// lip-sync start together. The hook runs on the caller's synchronization context.
        /// </summary>
        public Func<StagingResultPayload, CancellationToken, Task> StagingPreparer { get; set; }

        public void AttachBakedConversation(NpcifyBakedConversation bakedConversation)
        {
            _bakedConversation = bakedConversation ?? throw new ArgumentNullException(nameof(bakedConversation));
            _conversationId = "baked:" + bakedConversation.Asset.AvatarId;
        }

        public StagingResultPayload StartBakedConversation(string stagingId = null)
        {
            var staging = SelectBakedOpening(stagingId);
            PlayStaging(staging);
            return staging;
        }

        public async Task<StagingResultPayload> StartBakedConversationAsync(
            string stagingId = null,
            CancellationToken cancellationToken = default
        )
        {
            var staging = SelectBakedOpening(stagingId);
            await PrepareAndPlayAsync(staging, cancellationToken);
            return staging;
        }

        public async Task<StagingResultPayload> CreateConversationAsync(
            CancellationToken cancellationToken = default
        )
        {
            if (_bakedConversation != null)
            {
                return await StartBakedConversationAsync(null, cancellationToken);
            }

            _thinking = true;
            try
            {
                // No ConfigureAwait(false): game engines resume on their main thread.
                var created = await _api.CreateConversationAsync(null, cancellationToken);
                _conversationId = created.ConversationId;
                _activeStagingId = created.OpeningStaging?.StagingId;
                if (created.OpeningStaging != null)
                {
                    await PrepareAndPlayAsync(created.OpeningStaging, cancellationToken);
                }
                _thinking = false;
                return created.OpeningStaging;
            }
            catch (Exception ex)
            {
                _thinking = false;
                OnError?.Invoke(ex);
                throw;
            }
        }

        public async Task<StagingResultPayload> SpeakAsync(
            string message,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Message must not be empty.", nameof(message));
            }

            if (_bakedConversation != null)
            {
                if (string.IsNullOrEmpty(_bakedConversation.ActiveStagingId))
                {
                    SelectBakedOpening(null);
                }
                var bakedReply = _bakedConversation.Reply(message);
                _activeStagingId = _bakedConversation.ActiveStagingId;
                if (!bakedReply.Ended && bakedReply.Payload != null)
                {
                    await PrepareAndPlayAsync(bakedReply.Payload, cancellationToken);
                }
                return bakedReply.Payload;
            }

            _thinking = true;
            try
            {
                if (string.IsNullOrEmpty(_conversationId))
                {
                    var createdConv = await _api.CreateConversationAsync(null, cancellationToken);
                    _conversationId = createdConv.ConversationId;
                    _activeStagingId = createdConv.OpeningStaging?.StagingId;
                }

                var staging = await _api.CreateStagingAsync(
                    _conversationId,
                    message,
                    _activeStagingId,
                    false,
                    null,
                    null,
                    6,
                    cancellationToken
                );

                _activeStagingId = staging.StagingId;
                await PrepareAndPlayAsync(staging, cancellationToken);
                _thinking = false;
                return staging;
            }
            catch (Exception ex)
            {
                _thinking = false;
                OnError?.Invoke(ex);
                throw;
            }
        }

        public Task<StagingResultPayload> ReplyAsync(
            string optionLabel,
            CancellationToken cancellationToken = default
        )
        {
            return SpeakAsync(optionLabel, cancellationToken);
        }

        public StagingResultPayload SelectBakedStaging(string stagingId)
        {
            if (_bakedConversation == null)
            {
                throw new InvalidOperationException("No baked conversation is attached.");
            }

            var staging = _bakedConversation.Select(stagingId);
            _activeStagingId = _bakedConversation.ActiveStagingId;
            PlayStaging(staging);
            return staging;
        }

        /// <summary>Forgets the active live or baked conversation. The next call starts a new one.</summary>
        public void ResetConversation()
        {
            StopSpeaking();
            _activeStagingId = null;
            _conversationId = _bakedConversation != null ? "baked:" + _bakedConversation.Asset.AvatarId : null;
        }

        public void PlayStaging(StagingResultPayload staging)
        {
            if (staging == null)
            {
                throw new ArgumentNullException(nameof(staging));
            }

            _thinking = false;
            _activeStagingId = staging.StagingId;
            OnSpeechStart?.Invoke(staging);
            _stagingPlayer.Play(staging);
        }

        private StagingResultPayload SelectBakedOpening(string stagingId)
        {
            if (_bakedConversation == null)
            {
                throw new InvalidOperationException("No baked conversation is attached.");
            }

            var staging = string.IsNullOrEmpty(stagingId)
                ? _bakedConversation.Start()
                : _bakedConversation.Select(stagingId);
            _activeStagingId = _bakedConversation.ActiveStagingId;
            return staging;
        }

        private async Task PrepareAndPlayAsync(StagingResultPayload staging, CancellationToken cancellationToken)
        {
            var preparer = StagingPreparer;
            if (preparer != null)
            {
                await preparer(staging, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            PlayStaging(staging);
        }

        public void StopSpeaking()
        {
            _thinking = false;
            _stagingPlayer.Stop();
        }

        public void PlayAnimation(string animationId, bool loop = false)
        {
            if (string.IsNullOrWhiteSpace(animationId))
            {
                return;
            }
            _currentAnimation = animationId;
            OnAnimationPlayRequested?.Invoke(animationId, loop);
        }

        public void LookAt(float yawDegrees, float pitchDegrees)
        {
            _expressions.SetGazeAngles(yawDegrees, pitchDegrees);
        }

        public void LookAtDirection(float localX, float localY, float localZ)
        {
            _expressions.SetGazeDirection(localX, localY, localZ);
        }

        public void ClearLookAt()
        {
            _expressions.ClearGaze();
        }

        public void Tick(double deltaTimeSeconds, float fallbackAudioRms = 0f)
        {
            _stagingPlayer.Tick(deltaTimeSeconds, fallbackAudioRms);
        }

        private void HandleDialogueBlockStarted(StagingDialogueBlock block)
        {
            if (block?.Audio?.Data != null && block.Audio.Data.Length > 0 && IsPcmAudio(block.Audio))
            {
                var decoded = DecodeAudioBytes(block.Audio.Data);
                OnAudioBlock?.Invoke(block, decoded);
            }
            else
            {
                OnAudioBlock?.Invoke(block, null);
            }
        }

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

            if (bytes.Length >= 44 &&
                bytes[0] == (byte)'R' &&
                bytes[1] == (byte)'I' &&
                bytes[2] == (byte)'F' &&
                bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' &&
                bytes[9] == (byte)'A' &&
                bytes[10] == (byte)'V' &&
                bytes[11] == (byte)'E')
            {
                return DecodeWavBytes(bytes, defaultSampleRate, defaultChannels);
            }

            return DecodeRawPcm16(
                bytes,
                defaultSampleRate > 0 ? defaultSampleRate : 24000,
                defaultChannels > 0 ? defaultChannels : 1
            );
        }

        private static NpcifyDecodedAudio DecodeWavBytes(byte[] bytes, int fallbackRate, int fallbackChannels)
        {
            var audioFormat = 1;
            var channels = fallbackChannels > 0 ? fallbackChannels : 1;
            var sampleRate = fallbackRate > 0 ? fallbackRate : 24000;
            var bitsPerSample = 16;
            var dataOffset = -1;
            var dataSize = 0;

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
                    audioFormat = bytes[offset] | (bytes[offset + 1] << 8);
                    channels = Math.Max(1, bytes[offset + 2] | (bytes[offset + 3] << 8));
                    sampleRate =
                        bytes[offset + 4] |
                        (bytes[offset + 5] << 8) |
                        (bytes[offset + 6] << 16) |
                        (bytes[offset + 7] << 24);
                    bitsPerSample = bytes[offset + 14] | (bytes[offset + 15] << 8);
                }
                else if (chunkId == "data")
                {
                    dataOffset = offset;
                    dataSize = chunkSize;
                    break;
                }

                offset += chunkSize + (chunkSize & 1);
            }

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

        public void Dispose()
        {
            _api.Dispose();
        }
    }
}
