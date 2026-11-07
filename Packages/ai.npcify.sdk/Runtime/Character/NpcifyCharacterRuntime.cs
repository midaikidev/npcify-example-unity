using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Api;
using Npcify.Sdk.Conversation;
using Npcify.Sdk.Models;
using Npcify.Sdk.Motion;
using Npcify.Sdk.Speech;

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

    public sealed partial class NpcifyCharacterRuntime : IDisposable
    {
        private readonly NpcifyOptions _options;
        private readonly NpcifyApiClient _api;
        private readonly NpcifyFacialMixer _expressions;
        private readonly NpcifyVisemePlayer _visemePlayer;
        private readonly NpcifyStagingPlayer _stagingPlayer;
        private readonly NpcifyLiveMotionPlayer _liveMotion;
        private readonly Func<NpcifyConversationStream> _streamFactory;
        // Live motion work that background threads hand to the thread that calls Tick.
        private readonly ConcurrentQueue<Action> _mainThreadWork = new ConcurrentQueue<Action>();
        private NpcifyConversationStream _stream;
        private string _liveMotionPrepared;

        private NpcifyBakedConversation _bakedConversation;
        private string _conversationId;
        private string _activeStagingId;
        private bool _thinking;
        private float _volume = 1f;
        private bool _muted;
        private string _currentAnimation = "idle";
        private IReadOnlyList<NpcifyInputMode> _inputModes;
        // The character's voice input switch, after its info was read.
        private bool? _speechInputEnabled;

        public NpcifyCharacterRuntime(
            NpcifyOptions options,
            NpcifyApiClient apiClient = null,
            Func<NpcifyConversationStream> streamFactory = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            NpcifyInputModes.Validate(options.InputModes);
            _volume = options.Volume;
            _muted = options.Muted;
            _api = apiClient ?? new NpcifyApiClient(options);
            _streamFactory = streamFactory ?? (() => new NpcifyConversationStream(options));
            _expressions = new NpcifyFacialMixer();
            _visemePlayer = new NpcifyVisemePlayer();
            _stagingPlayer = new NpcifyStagingPlayer(_expressions, _visemePlayer);
            _liveMotion = new NpcifyLiveMotionPlayer(options.LiveMotion);

            _stagingPlayer.OnDialogueBlockStarted += HandleDialogueBlockStarted;
            _stagingPlayer.OnCharacterRevealed += (text, cue) => OnCharacterRevealed?.Invoke(text, cue);
            _stagingPlayer.OnAnimationClipTriggered += clip =>
                PlayAnimation(clip.Animation, clip.Loop);
            _stagingPlayer.OnStagingCompleted += HandleStagingCompleted;
        }

        public event Action<StagingResultPayload> OnSpeechStart;
        public event Action<StagingResultPayload> OnSpeechEnd;
        public event Action<StagingDialogueBlock, NpcifyDecodedAudio> OnAudioBlock;
        public event Action<string, StagingCharacterCue> OnCharacterRevealed;
        public event Action<string, bool> OnAnimationPlayRequested;
        public event Action<Exception> OnError;
        /// <summary>The recognised text of the player's speech.</summary>
        public event Action<NpcifySpeechTranscript> OnTranscript;

        public NpcifyOptions Options => _options;

        /// <summary>
        /// The input modes in use: the configured modes after the character's check, or every
        /// mode that the character permits.
        /// </summary>
        public IReadOnlyList<NpcifyInputMode> InputModes =>
            _inputModes
            ?? NpcifyInputModes.Validate(_options.InputModes)
            ?? NpcifyInputModes.Permitted(true, _bakedConversation != null, true);

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
            if (bakedConversation == null)
            {
                throw new ArgumentNullException(nameof(bakedConversation));
            }
            _inputModes = NpcifyInputModes.Resolve(_options.InputModes, false, true, false);
            _bakedConversation = bakedConversation;
            _conversationId = "baked:" + bakedConversation.Asset.AvatarId;
        }

        /// <summary>
        /// Compares the input modes with the character. Throws NpcifyInputModeException when the
        /// character does not permit a configured mode.
        /// </summary>
        public void ApplyCharacterInfo(NpcifyAssetInfo info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }
            _speechInputEnabled = info.SpeechInputEnabled;
            _liveMotionInEffect = info.RealtimeMotionEnabled;
            _inputModes = NpcifyInputModes.Resolve(
                _options.InputModes,
                info.AllowCustomReply,
                info.BakedConversationEnabled || _bakedConversation != null,
                info.SpeechInputEnabled);
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

            await EnsureInputModesAsync(cancellationToken);
            _thinking = true;
            try
            {
                // No ConfigureAwait(false): game engines resume on their main thread.
                var created = await _api.CreateConversationAsync(null, LiveMotionCapabilities, cancellationToken);
                _conversationId = created.ConversationId;
                _activeStagingId = created.OpeningStaging?.StagingId;
                if (WantsLiveMotion || StreamPlaybackEnabled)
                {
                    // Join the stream early, so that the first turn does not wait for it.
                    _ = JoinStreamAsync(_conversationId, CancellationToken.None);
                }
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

        /// <summary>Sends typed text as the player's message. With InputModes set, it needs Text.</summary>
        public Task<StagingResultPayload> SpeakAsync(
            string message,
            CancellationToken cancellationToken = default
        )
        {
            if (_options.InputModes != null && _options.InputModes.Count > 0)
            {
                RequireMode(NpcifyInputMode.Text, "Typed replies are off");
            }
            return SendAsync(message, cancellationToken);
        }

        /// <summary>Sends one of the reply options. With InputModes set, it needs Options or Text.</summary>
        public Task<StagingResultPayload> ReplyAsync(
            string optionLabel,
            CancellationToken cancellationToken = default
        )
        {
            if (_options.InputModes != null && _options.InputModes.Count > 0 &&
                !NpcifyInputModes.Contains(InputModes, NpcifyInputMode.Text))
            {
                RequireMode(NpcifyInputMode.Options, "Reply options are off");
            }
            return SendAsync(optionLabel, cancellationToken);
        }

        /// <summary>
        /// Recognises one recording (WAV, 16-bit mono PCM at 16 kHz) and, unless
        /// options.Send is false, sends the text as the player's message. An empty text
        /// starts no turn. It needs the Voice mode.
        /// </summary>
        public async Task<NpcifySpeechInputResult> RecognizeAndReplyAsync(
            byte[] wav,
            NpcifyListeningOptions options = null,
            CancellationToken cancellationToken = default
        )
        {
            RequireVoice();
            options = options ?? new NpcifyListeningOptions();
            await EnsureInputModesAsync(cancellationToken);
            RequireVoice();
            if (string.IsNullOrEmpty(_conversationId))
            {
                var createdConv = await _api.CreateConversationAsync(null, LiveMotionCapabilities, cancellationToken);
                _conversationId = createdConv.ConversationId;
                _activeStagingId = createdConv.OpeningStaging?.StagingId;
            }
            NpcifySpeechTranscript transcript;
            try
            {
                transcript = await _api.RecognizeSpeechAsync(
                    _conversationId,
                    wav,
                    options.Language,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                throw;
            }
            OnTranscript?.Invoke(transcript);
            var text = transcript.Text?.Trim() ?? string.Empty;
            var result = new NpcifySpeechInputResult
            {
                Text = text,
                Language = transcript.Language,
                DurationMs = transcript.DurationMs,
            };
            if (options.Send && text.Length > 0)
            {
                result.Sent = true;
                result.Turn = SendAsync(text, cancellationToken);
            }
            return result;
        }

        /// <summary>Sends a recognised text that RecognizeAndReplyAsync did not send. It needs Voice.</summary>
        public Task<StagingResultPayload> SendTranscriptAsync(
            string text,
            CancellationToken cancellationToken = default
        )
        {
            RequireVoice();
            return SendAsync(text, cancellationToken);
        }

        /// <summary>Throws NpcifyInputModeException when the input modes or the conversation do not permit voice.</summary>
        public void RequireVoice()
        {
            if (_bakedConversation != null)
            {
                throw new NpcifyInputModeException(
                    "Voice input is not available in a baked (offline) conversation.");
            }
            if (_speechInputEnabled == false)
            {
                throw new NpcifyInputModeException(
                    "Voice input is off for this character. Its owner turns it on in the STT tab.");
            }
            RequireMode(NpcifyInputMode.Voice, "Voice input is off");
        }

        private void RequireMode(NpcifyInputMode mode, string reason)
        {
            var modes = InputModes;
            if (!NpcifyInputModes.Contains(modes, mode))
            {
                var names = new List<string>();
                foreach (var entry in modes)
                {
                    names.Add(NpcifyInputModes.Name(entry));
                }
                throw new NpcifyInputModeException(
                    reason + ": InputModes is [" + string.Join(", ", names) + "].");
            }
        }

        /// <summary>
        /// The character's info is read once: a configured InputModes is checked with it, and
        /// voice needs the owner's voice input. Without a configuration, a failed read leaves
        /// the rules to the server, as before.
        /// </summary>
        private async Task EnsureInputModesAsync(CancellationToken cancellationToken)
        {
            if (_inputModes != null)
            {
                return;
            }
            var configured = _options.InputModes != null && _options.InputModes.Count > 0;
            NpcifyAssetInfo info;
            try
            {
                info = await _api.GetInfoAsync(cancellationToken);
            }
            catch (Exception error) when (!configured && !(error is OperationCanceledException))
            {
                return;
            }
            ApplyCharacterInfo(info);
        }

        private async Task<StagingResultPayload> SendAsync(
            string message,
            CancellationToken cancellationToken
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
                    var createdConv = await _api.CreateConversationAsync(null, LiveMotionCapabilities, cancellationToken);
                    _conversationId = createdConv.ConversationId;
                    _activeStagingId = createdConv.OpeningStaging?.StagingId;
                }

                var staging = await RequestTurnAsync(message, cancellationToken);
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
            StopStreamTurns();
            _stream?.Leave();
            _liveMotionPrepared = null;
            _streamJoinGivenUp = null;
            _activeStagingId = null;
            _conversationId = _bakedConversation != null ? "baked:" + _bakedConversation.Asset.AvatarId : null;
        }

        public void PlayStaging(StagingResultPayload staging)
        {
            if (staging == null)
            {
                throw new ArgumentNullException(nameof(staging));
            }

            lock (_playbackGate)
            {
                // A staging played as a whole replaces a streamed reply.
                _playingTurn?.Stop();
                _playingTurn = null;
                _thinking = false;
                _activeStagingId = staging.StagingId;
                LoadStoredMotion(staging);
                OnSpeechStart?.Invoke(staging);
                _stagingPlayer.Play(staging);
            }
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

        /// <summary>Stops the speech that plays; a streamed reply that plays does not continue.</summary>
        public void StopSpeaking()
        {
            lock (_playbackGate)
            {
                _thinking = false;
                _playingTurn?.Stop();
                _playingTurn = null;
                _stagingPlayer.Stop();
            }
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
            lock (_playbackGate)
            {
                while (_mainThreadWork.TryDequeue(out var work))
                {
                    work();
                }
                _stagingPlayer.Tick(deltaTimeSeconds, fallbackAudioRms);
                // Live motion follows the staging clock; no staging: blend back to idle.
                var playing = _stagingPlayer.IsPlaying ? _stagingPlayer.CurrentStaging : null;
                _liveMotion.Update(
                    deltaTimeSeconds,
                    playing?.StagingId,
                    playing != null ? _stagingPlayer.TimelineElapsedSeconds : (double?)null);
            }
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

        public void Dispose()
        {
            StopStreamTurns();
            _stream?.Dispose();
            _api.Dispose();
        }
    }
}
