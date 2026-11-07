#if UNITY_5_3_OR_NEWER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npcify.Sdk.Speech;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Npcify.Sdk.Unity
{
    /// <summary>
    /// Voice input of NpcifyCharacter (docs/voice-input.md): the microphone records while
    /// the player holds the push-to-talk key or button, or until code stops it. At the
    /// release the recording goes to the server, and its text becomes the player's message.
    /// </summary>
    public sealed partial class NpcifyCharacter
    {
        [Header("Voice Input")]
        [Tooltip("How the player can reply. Empty: every mode that the character permits. One mode forces it.")]
        [SerializeField] private List<NpcifyInputMode> inputModes = new List<NpcifyInputMode>();
        [Tooltip("Hold this key to talk (legacy Input Manager). None: call BeginPushToTalk and EndPushToTalk from your own input.")]
        [SerializeField] private KeyCode pushToTalkKey = KeyCode.None;
        [Tooltip("A BCP 47 language hint, for example ja, zh or en. Empty: the language is detected.")]
        [SerializeField] private string speechLanguage = string.Empty;
        [Tooltip("The microphone device name. Empty: the default microphone.")]
        [SerializeField] private string microphoneDevice = string.Empty;

        private const int MicrophoneBufferSeconds = 31;
        private const float MicrophoneStartTimeoutSeconds = 2f;

        private NpcifyListeningState _listeningState = NpcifyListeningState.Idle;
        private TaskCompletionSource<NpcifySpeechInputResult> _listening;
        private NpcifyListeningOptions _listenOptions;
        private NpcifySilenceDetector _silence;
        private Coroutine _microphoneStart;
        private AudioClip _microphoneClip;
        private string _microphoneDeviceInUse;
        private int _microphoneReadPosition;
        private readonly List<float> _recordedSamples = new List<float>();
        private float _listeningStartedAt;
        private bool _pushToTalkActive;
        private float _pushToTalkPressedAt;
#if !ENABLE_LEGACY_INPUT_MANAGER
        private bool _pushToTalkKeyWarned;
#endif

        public event Action<NpcifyListeningState> ListeningStateChanged;
        /// <summary>The recognised text of the player's speech.</summary>
        public event Action<string> TranscriptReceived;

        /// <summary>
        /// A push-to-talk press sent nothing: it was shorter than 0.3 s, or it ended before the
        /// microphone gave any audio (a permission prompt, a slow device). A UI can say "hold to talk".
        /// </summary>
        public event Action PushToTalkShortPress;

        public NpcifyListeningState ListeningState => _listeningState;

        public IReadOnlyList<NpcifyInputMode> InputModes => Runtime.InputModes;

        public KeyCode PushToTalkKey
        {
            get => pushToTalkKey;
            set => pushToTalkKey = value;
        }

        /// <summary>
        /// Starts recording. Resolves with the result when listening ends (StopListeningAsync,
        /// EndOnSilenceSeconds or MaxDurationSeconds), or with null when it is cancelled. The
        /// first call asks for the microphone permission where the platform needs one.
        /// </summary>
        public Task<NpcifySpeechInputResult> StartListening(NpcifyListeningOptions options = null)
        {
            EnsureInitialized();
            if (_listeningState != NpcifyListeningState.Idle)
            {
                throw new InvalidOperationException("Listening has already started.");
            }
            _runtime.RequireVoice();
            options = options ?? new NpcifyListeningOptions();
            if (string.IsNullOrWhiteSpace(options.Language) && !string.IsNullOrWhiteSpace(speechLanguage))
            {
                options.Language = speechLanguage.Trim();
            }
            options.MaxDurationSeconds = Mathf.Clamp(
                options.MaxDurationSeconds,
                1f,
                NpcifySpeechAudio.MaxDurationSeconds);
            _listenOptions = options;
            _silence = options.EndOnSilenceSeconds > 0f
                ? new NpcifySilenceDetector(options.EndOnSilenceSeconds)
                : null;
            _listening = new TaskCompletionSource<NpcifySpeechInputResult>();
            var listening = _listening;
            SetListeningState(NpcifyListeningState.Starting);
            // A talk press is the player's intent to reply: warm up live motion.
            PrepareLiveMotionOnIntent();
            _microphoneStart = StartCoroutine(OpenMicrophone(listening));
            return listening.Task;
        }

        /// <summary>
        /// Ends the recording, recognises it and sends its text (unless Send is false). Before the
        /// microphone gave any audio, it cancels and returns null.
        /// </summary>
        public async Task<NpcifySpeechInputResult> StopListeningAsync()
        {
            if (_listeningState == NpcifyListeningState.Starting && !TakeStartingAudio())
            {
                CancelListening();
                return null;
            }
            if (_listeningState != NpcifyListeningState.Listening)
            {
                return _listening != null ? await _listening.Task : null;
            }
            var listening = _listening;
            ReadMicrophone();
            var samples = _recordedSamples.ToArray();
            var rate = _microphoneClip != null ? _microphoneClip.frequency : NpcifySpeechAudio.SampleRate;
            var channels = _microphoneClip != null ? _microphoneClip.channels : 1;
            CloseMicrophone();
            SetListeningState(NpcifyListeningState.Recognizing);
            try
            {
                var pcm = NpcifySpeechAudio.ToPcm16Mono16k(samples, channels, rate);
                // Less than 100 ms is no speech: nothing is sent or charged.
                var result = pcm.Length < NpcifySpeechAudio.SampleRate / 10
                    ? new NpcifySpeechInputResult()
                    : await _runtime.RecognizeAndReplyAsync(
                        NpcifySpeechAudio.EncodeWav(pcm),
                        _listenOptions);
                SetListeningState(NpcifyListeningState.Idle);
                listening.TrySetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                SetListeningState(NpcifyListeningState.Idle);
                listening.TrySetException(ex);
                throw;
            }
        }

        /// <summary>Discards the recording. Nothing is recognised or charged.</summary>
        public void CancelListening()
        {
            if (_listeningState == NpcifyListeningState.Idle ||
                _listeningState == NpcifyListeningState.Recognizing)
            {
                return;
            }
            CloseMicrophone();
            SetListeningState(NpcifyListeningState.Idle);
            _listening?.TrySetResult(null);
        }

        /// <summary>Press of a push-to-talk control: the key, a UI button or your own input action.</summary>
        public void BeginPushToTalk()
        {
            if (_pushToTalkActive || _listeningState != NpcifyListeningState.Idle)
            {
                return;
            }
            _pushToTalkActive = true;
            _pushToTalkPressedAt = Time.realtimeSinceStartup;
            Task<NpcifySpeechInputResult> listening;
            try
            {
                // Push-to-talk ends at the release, never at a silence.
                listening = StartListening(new NpcifyListeningOptions { Language = speechLanguage });
            }
            catch (Exception ex)
            {
                _pushToTalkActive = false;
                OnError?.Invoke(ex);
                return;
            }
            _ = ObserveListeningAsync(listening);
        }

        /// <summary>Release of a push-to-talk control. A press shorter than 0.3 s sends nothing.</summary>
        public void EndPushToTalk()
        {
            if (!_pushToTalkActive)
            {
                return;
            }
            _pushToTalkActive = false;
            if (Time.realtimeSinceStartup - _pushToTalkPressedAt < NpcifySpeechAudio.MinHoldSeconds)
            {
                CancelListening();
                PushToTalkShortPress?.Invoke();
                return;
            }
            // A release before the microphone gave any audio records nothing: the player has to
            // hold longer. Audio that the microphone has given is sent.
            if (_listeningState == NpcifyListeningState.Starting && !TakeStartingAudio())
            {
                CancelListening();
                PushToTalkShortPress?.Invoke();
                return;
            }
            _ = ObserveListeningAsync(StopListeningAsync());
        }

        /// <summary>The push-to-talk control lost the press (focus loss, a disabled button).</summary>
        public void CancelPushToTalk()
        {
            if (_pushToTalkActive)
            {
                _pushToTalkActive = false;
                CancelListening();
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                CancelPushToTalk();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                CancelPushToTalk();
            }
        }

        private void UpdateVoiceInput()
        {
            if (pushToTalkKey != KeyCode.None)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                if (Input.GetKeyDown(pushToTalkKey))
                {
                    BeginPushToTalk();
                }
                else if (Input.GetKeyUp(pushToTalkKey))
                {
                    EndPushToTalk();
                }
#else
                if (!_pushToTalkKeyWarned)
                {
                    _pushToTalkKeyWarned = true;
                    Debug.LogWarning(
                        "NpcifyCharacter: PushToTalkKey needs the legacy Input Manager. Call BeginPushToTalk and EndPushToTalk from an Input System action.");
                }
#endif
            }

            if (_listeningState != NpcifyListeningState.Listening)
            {
                return;
            }
            ReadMicrophone();
            if (Time.realtimeSinceStartup - _listeningStartedAt >= _listenOptions.MaxDurationSeconds)
            {
                _ = ObserveListeningAsync(StopListeningAsync());
            }
        }

        private IEnumerator OpenMicrophone(TaskCompletionSource<NpcifySpeechInputResult> listening)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            FailListening(listening, "MICROPHONE_UNAVAILABLE", "Unity WebGL has no microphone access. Use the web SDK.");
            yield break;
#else
#if UNITY_IOS && !UNITY_EDITOR
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            }
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                FailListening(listening, "MICROPHONE_PERMISSION_DENIED", "Microphone access was denied.");
                yield break;
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                var asked = Time.realtimeSinceStartup;
                while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) &&
                       Time.realtimeSinceStartup - asked < 30f && listening == _listening)
                {
                    yield return null;
                }
                if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
                {
                    FailListening(listening, "MICROPHONE_PERMISSION_DENIED", "Microphone access was denied.");
                    yield break;
                }
            }
#endif
            if (listening != _listening || _listeningState != NpcifyListeningState.Starting)
            {
                yield break;
            }
            if (Microphone.devices.Length == 0)
            {
                FailListening(listening, "MICROPHONE_UNAVAILABLE", "No microphone is available.");
                yield break;
            }
            _microphoneDeviceInUse = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
            Microphone.GetDeviceCaps(_microphoneDeviceInUse, out var minFrequency, out var maxFrequency);
            var frequency = NpcifySpeechAudio.SampleRate;
            if (minFrequency != 0 || maxFrequency != 0)
            {
                frequency = Mathf.Clamp(frequency, minFrequency, maxFrequency);
            }
            _microphoneClip = Microphone.Start(_microphoneDeviceInUse, true, MicrophoneBufferSeconds, frequency);
            if (_microphoneClip == null)
            {
                FailListening(listening, "MICROPHONE_UNAVAILABLE", "The microphone did not start.");
                yield break;
            }
            var started = Time.realtimeSinceStartup;
            while (Microphone.GetPosition(_microphoneDeviceInUse) <= 0 &&
                   Time.realtimeSinceStartup - started < MicrophoneStartTimeoutSeconds)
            {
                yield return null;
            }
            if (listening != _listening || _listeningState != NpcifyListeningState.Starting)
            {
                yield break;
            }
            _microphoneReadPosition = 0;
            _recordedSamples.Clear();
            _listeningStartedAt = Time.realtimeSinceStartup;
            _microphoneStart = null;
            SetListeningState(NpcifyListeningState.Listening);
#endif
        }

        /// <summary>
        /// While the microphone starts: true when it gives audio already. Then the listening
        /// begins now, so that a stop sends that audio.
        /// </summary>
        private bool TakeStartingAudio()
        {
            if (_listeningState != NpcifyListeningState.Starting || _microphoneClip == null ||
                Microphone.GetPosition(_microphoneDeviceInUse) <= 0)
            {
                return false;
            }
            if (_microphoneStart != null)
            {
                StopCoroutine(_microphoneStart);
                _microphoneStart = null;
            }
            _microphoneReadPosition = 0;
            _recordedSamples.Clear();
            _listeningStartedAt = Time.realtimeSinceStartup;
            SetListeningState(NpcifyListeningState.Listening);
            return true;
        }

        /// <summary>Copies the new samples of the looping microphone clip and applies the silence rule.</summary>
        private void ReadMicrophone()
        {
            if (_microphoneClip == null)
            {
                return;
            }
            var position = Microphone.GetPosition(_microphoneDeviceInUse);
            if (position < 0 || position == _microphoneReadPosition)
            {
                return;
            }
            var channels = _microphoneClip.channels;
            var total = _microphoneClip.samples;
            var count = position > _microphoneReadPosition
                ? position - _microphoneReadPosition
                : total - _microphoneReadPosition + position;
            var buffer = new float[count * channels];
            var first = Math.Min(count, total - _microphoneReadPosition);
            if (first == count)
            {
                _microphoneClip.GetData(buffer, _microphoneReadPosition);
            }
            else
            {
                var head = new float[first * channels];
                var tail = new float[(count - first) * channels];
                _microphoneClip.GetData(head, _microphoneReadPosition);
                _microphoneClip.GetData(tail, 0);
                Array.Copy(head, buffer, head.Length);
                Array.Copy(tail, 0, buffer, head.Length, tail.Length);
            }
            _microphoneReadPosition = position;
            _recordedSamples.AddRange(buffer);
            if (_silence != null &&
                _silence.Push(
                    NpcifySpeechAudio.Level(buffer, 0, buffer.Length),
                    count / (float)_microphoneClip.frequency))
            {
                _ = ObserveListeningAsync(StopListeningAsync());
            }
        }

        private void CloseMicrophone()
        {
            if (_microphoneStart != null)
            {
                StopCoroutine(_microphoneStart);
                _microphoneStart = null;
            }
            if (_microphoneClip != null)
            {
                Microphone.End(_microphoneDeviceInUse);
                Destroy(_microphoneClip);
                _microphoneClip = null;
            }
            _recordedSamples.Clear();
        }

        private void FailListening(
            TaskCompletionSource<NpcifySpeechInputResult> listening,
            string code,
            string message)
        {
            if (listening != _listening)
            {
                return;
            }
            CloseMicrophone();
            SetListeningState(NpcifyListeningState.Idle);
            var error = new NpcifyMicrophoneException(code, message);
            OnError?.Invoke(error);
            listening.TrySetException(error);
        }

        private void SetListeningState(NpcifyListeningState state)
        {
            if (_listeningState == state)
            {
                return;
            }
            _listeningState = state;
            ListeningStateChanged?.Invoke(state);
        }

        /// <summary>Push-to-talk has no caller that awaits: the runtime and FailListening report errors to OnError.</summary>
        private static async Task ObserveListeningAsync(Task<NpcifySpeechInputResult> listening)
        {
            try
            {
                await listening;
            }
            catch (Exception)
            {
                // Reported already.
            }
        }
    }

    /// <summary>The microphone refused or is missing: MICROPHONE_PERMISSION_DENIED or MICROPHONE_UNAVAILABLE.</summary>
    public sealed class NpcifyMicrophoneException : Exception
    {
        public NpcifyMicrophoneException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
#endif
