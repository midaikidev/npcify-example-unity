#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Character;
using Npcify.Sdk.Conversation;
using Npcify.Sdk.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace Npcify.Sdk.Unity
{
    [AddComponentMenu("Npcify/Npcify Character")]
    [DisallowMultipleComponent]
    public sealed class NpcifyCharacter : MonoBehaviour
    {
        [Header("Npcify Connection")]
        [SerializeField] private string characterId = string.Empty;
        [SerializeField] private string apiKey = string.Empty;
        [SerializeField] private string endpoint = "https://api.npcify.ai";

        [Header("Character Rig & Audio")]
        [SerializeField] private Transform avatarRoot;
        [SerializeField] private Animator animatorComponent;
        [SerializeField] private AudioSource audioSourceComponent;
        [SerializeField] private Transform gazeTarget;
        [SerializeField] private bool spatialAudio = true;
        [SerializeField] private bool autoBlink = true;

        [Header("Offline Baked Conversation (Optional)")]
        [SerializeField] private TextAsset bakedConversationAsset;
        [SerializeField] private bool autoStartBakedConversation;

        private readonly List<SkinnedMeshRenderer> _skinnedMeshRenderers = new List<SkinnedMeshRenderer>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly float[] _audioSampleBuffer = new float[256];

        private readonly Dictionary<StagingDialogueBlock, AudioClip> _preparedClips =
            new Dictionary<StagingDialogueBlock, AudioClip>();

        private NpcifyCharacterRuntime _runtime;
        private AudioClip _activeDynamicClip;
        private bool _hasLookAtPosition;
        private Vector3 _lookAtWorldPosition;

        public event Action<StagingResultPayload> OnSpeechStart;
        public event Action<StagingResultPayload> OnSpeechEnd;
        public event Action<string, StagingCharacterCue> OnCharacterRevealed;
        /// <summary>Raised when a staging requests an animation ID from the character's animation catalog.</summary>
        public event Action<string, bool> OnAnimationRequested;
        public event Action<Exception> OnError;

        public string CharacterId
        {
            get => characterId;
            set => characterId = value ?? string.Empty;
        }

        public string ApiKey
        {
            get => apiKey;
            set => apiKey = value ?? string.Empty;
        }

        public string Endpoint
        {
            get => endpoint;
            set => endpoint = string.IsNullOrWhiteSpace(value) ? "https://api.npcify.ai" : value;
        }

        public Transform Root => avatarRoot != null ? avatarRoot : transform;

        public Animator Animator => animatorComponent;

        public AudioSource AudioSource => audioSourceComponent;

        public Transform GazeTarget
        {
            get => gazeTarget;
            set
            {
                gazeTarget = value;
                if (value != null)
                {
                    _hasLookAtPosition = false;
                }
            }
        }

        public IReadOnlyList<SkinnedMeshRenderer> SkinnedMeshRenderers => _skinnedMeshRenderers;

        public IReadOnlyList<Mesh> Meshes => _meshes;

        public IReadOnlyList<Material> Materials => _materials;

        public NpcifyCharacterRuntime Runtime
        {
            get
            {
                EnsureInitialized();
                return _runtime;
            }
        }

        public NpcifyFacialMixer Expressions => Runtime.Expressions;

        public NpcifySpeechState Speech => Runtime.Speech;

        public Transform GetBone(HumanBodyBones bone)
        {
            if (animatorComponent != null && animatorComponent.isHuman)
            {
                return animatorComponent.GetBoneTransform(bone);
            }
            return null;
        }

        public void Configure(NpcifyOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            characterId = options.CharacterId ?? string.Empty;
            apiKey = options.ResolvedToken;
            endpoint = string.IsNullOrWhiteSpace(options.BaseUrl) ? "https://api.npcify.ai" : options.BaseUrl;
            RebuildRuntime();
        }

        public void RefreshAvatarBindings()
        {
            if (avatarRoot == null)
            {
                avatarRoot = transform;
            }
            if (animatorComponent == null)
            {
                animatorComponent = avatarRoot.GetComponentInChildren<Animator>();
            }
            if (audioSourceComponent == null)
            {
                audioSourceComponent = GetComponent<AudioSource>();
                if (audioSourceComponent == null)
                {
                    audioSourceComponent = gameObject.AddComponent<AudioSource>();
                }
            }

            audioSourceComponent.playOnAwake = false;
            audioSourceComponent.spatialBlend = spatialAudio ? 1f : 0f;

            _skinnedMeshRenderers.Clear();
            _meshes.Clear();
            _materials.Clear();

            var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                {
                    continue;
                }
                _skinnedMeshRenderers.Add(r);
                if (r.sharedMesh != null && !_meshes.Contains(r.sharedMesh))
                {
                    _meshes.Add(r.sharedMesh);
                }
                var sharedMats = r.sharedMaterials;
                if (sharedMats != null)
                {
                    for (var m = 0; m < sharedMats.Length; m++)
                    {
                        var mat = sharedMats[m];
                        if (mat != null && !_materials.Contains(mat))
                        {
                            _materials.Add(mat);
                        }
                    }
                }
            }
        }

        public Task<StagingResultPayload> CreateConversationAsync(
            CancellationToken cancellationToken = default
        )
        {
            EnsureInitialized();
            return _runtime.CreateConversationAsync(cancellationToken);
        }

        public Task<StagingResultPayload> SpeakAsync(
            string prompt,
            CancellationToken cancellationToken = default
        )
        {
            EnsureInitialized();
            return _runtime.SpeakAsync(prompt, cancellationToken);
        }

        public Task<StagingResultPayload> ReplyAsync(
            string optionLabel,
            CancellationToken cancellationToken = default
        )
        {
            EnsureInitialized();
            return _runtime.ReplyAsync(optionLabel, cancellationToken);
        }

        public StagingResultPayload StartBakedConversation(string stagingId = null)
        {
            EnsureInitialized();
            return _runtime.StartBakedConversation(stagingId);
        }

        /// <summary>Starts the attached baked conversation after its opening audio is decoded.</summary>
        public Task<StagingResultPayload> StartBakedConversationAsync(
            string stagingId = null,
            CancellationToken cancellationToken = default
        )
        {
            EnsureInitialized();
            return _runtime.StartBakedConversationAsync(stagingId, cancellationToken);
        }

        /// <summary>Loads a baked conversation asset (`.npcp` bytes) for offline playback.</summary>
        public void AttachBakedConversation(byte[] npcpBytes)
        {
            EnsureInitialized();
            _runtime.AttachBakedConversation(NpcifyBakedConversation.FromBytes(npcpBytes));
        }

        /// <summary>Stops speech and forgets the active conversation.</summary>
        public void ResetConversation()
        {
            EnsureInitialized();
            if (audioSourceComponent != null && audioSourceComponent.isPlaying)
            {
                audioSourceComponent.Stop();
            }
            _runtime.ResetConversation();
        }

        public StagingResultPayload SelectBakedStaging(string stagingId)
        {
            EnsureInitialized();
            return _runtime.SelectBakedStaging(stagingId);
        }

        public void StopSpeaking()
        {
            EnsureInitialized();
            if (audioSourceComponent != null && audioSourceComponent.isPlaying)
            {
                audioSourceComponent.Stop();
            }
            _runtime.StopSpeaking();
        }

        public void SetEmotion(string expression, float weight = 1f)
        {
            EnsureInitialized();
            _runtime.Expressions.SetEmotion(expression, weight);
        }

        public void ClearEmotion()
        {
            EnsureInitialized();
            _runtime.Expressions.ClearEmotion();
        }

        public void PlayExpressionCue(
            string expression,
            float weight = 1f,
            float durationSeconds = 1.2f,
            float fadeInSeconds = 0.15f,
            float fadeOutSeconds = 0.25f
        )
        {
            EnsureInitialized();
            _runtime.Expressions.PlayCue(expression, weight, durationSeconds, fadeInSeconds, fadeOutSeconds);
        }

        public void PlayAnimation(string animationId, bool loop = false)
        {
            EnsureInitialized();
            _runtime.PlayAnimation(animationId, loop);
        }

        public void LookAt(Transform target)
        {
            EnsureInitialized();
            gazeTarget = target;
            _hasLookAtPosition = false;
            if (target == null)
            {
                _runtime.ClearLookAt();
            }
        }

        public void LookAt(Vector3 worldPosition)
        {
            EnsureInitialized();
            gazeTarget = null;
            _lookAtWorldPosition = worldPosition;
            _hasLookAtPosition = true;
            UpdateGazeLayer();
        }

        public void ClearLookAt()
        {
            EnsureInitialized();
            gazeTarget = null;
            _hasLookAtPosition = false;
            _runtime.ClearLookAt();
        }

        public float GetAudioRmsAmplitude()
        {
            if (audioSourceComponent == null || !audioSourceComponent.isPlaying)
            {
                return 0f;
            }

            audioSourceComponent.GetOutputData(_audioSampleBuffer, 0);
            var sumSquares = 0f;
            for (var i = 0; i < _audioSampleBuffer.Length; i++)
            {
                var sample = _audioSampleBuffer[i];
                sumSquares += sample * sample;
            }
            var rms = Mathf.Sqrt(sumSquares / _audioSampleBuffer.Length);
            return Mathf.Clamp01(rms * 4.5f);
        }

        private void Awake()
        {
            RefreshAvatarBindings();
            EnsureInitialized();
        }

        private void Start()
        {
            if (bakedConversationAsset != null && bakedConversationAsset.bytes != null && bakedConversationAsset.bytes.Length > 0)
            {
                var baked = NpcifyBakedConversation.FromBytes(bakedConversationAsset.bytes);
                _runtime.AttachBakedConversation(baked);
                if (autoStartBakedConversation)
                {
                    _runtime.StartBakedConversation();
                }
            }
        }

        private void Update()
        {
            EnsureInitialized();
            _runtime.Expressions.AutoBlinkEnabled = autoBlink;
            UpdateGazeLayer();
            var rms = GetAudioRmsAmplitude();
            _runtime.Tick(Time.deltaTime, rms);
        }

        private void LateUpdate()
        {
            ApplyBlendShapesToSkinnedMeshes();
        }

        private void OnDestroy()
        {
            ReleaseActiveDynamicClip();
            ReleasePreparedClips(null);
            if (_runtime != null)
            {
                _runtime.Dispose();
                _runtime = null;
            }
        }

        private void EnsureInitialized()
        {
            if (_runtime != null)
            {
                return;
            }
            RebuildRuntime();
        }

        private void RebuildRuntime()
        {
            _runtime?.Dispose();

            var options = new NpcifyOptions
            {
                CharacterId = string.IsNullOrWhiteSpace(characterId) ? "unconfigured-character" : characterId,
                ApiKey = apiKey,
                BaseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://api.npcify.ai" : endpoint,
            };

            _runtime = new NpcifyCharacterRuntime(options);
            _runtime.StagingPreparer = PrepareStagingAudioAsync;
            _runtime.Expressions.AutoBlinkEnabled = autoBlink;
            _runtime.OnSpeechStart += staging => OnSpeechStart?.Invoke(staging);
            _runtime.OnSpeechEnd += staging => OnSpeechEnd?.Invoke(staging);
            _runtime.OnCharacterRevealed += (text, cue) => OnCharacterRevealed?.Invoke(text, cue);
            _runtime.OnAudioBlock += HandleAudioBlock;
            _runtime.OnAnimationPlayRequested += HandleAnimationPlayRequested;
            _runtime.OnError += ex => OnError?.Invoke(ex);
        }

        private void HandleAudioBlock(StagingDialogueBlock block, NpcifyDecodedAudio decodedAudio)
        {
            if (audioSourceComponent == null || block == null)
            {
                return;
            }

            if (_preparedClips.TryGetValue(block, out var prepared) && prepared != null)
            {
                PlayClip(prepared, 0f);
                return;
            }

            if (decodedAudio != null && decodedAudio.Samples != null && decodedAudio.Samples.Length > 0)
            {
                var channels = Mathf.Max(1, decodedAudio.Channels);
                var lengthSamples = decodedAudio.Samples.Length / channels;
                if (lengthSamples <= 0)
                {
                    return;
                }

                ReleaseActiveDynamicClip();
                _activeDynamicClip = AudioClip.Create(
                    $"npcify-{block.Id ?? "speech"}",
                    lengthSamples,
                    channels,
                    Mathf.Max(8000, decodedAudio.SampleRate),
                    false
                );
                _activeDynamicClip.SetData(decodedAudio.Samples, 0);
                PlayClip(_activeDynamicClip, 0f);
                return;
            }

            if (HasEncodedAudio(block.Audio))
            {
                // Audio that was not prepared in advance (for example after a synchronous
                // StartBakedConversation call) starts late, at the current block time.
                _ = PlayWhenLoadedAsync(block);
            }
        }

        private async Task PlayWhenLoadedAsync(StagingDialogueBlock block)
        {
            try
            {
                var clip = await LoadEncodedClipAsync(block.Audio, CancellationToken.None);
                if (clip == null || this == null)
                {
                    return;
                }
                _preparedClips[block] = clip;
                var cursor = _runtime?.StagingPlayer;
                if (cursor != null && cursor.IsPlaying && cursor.CurrentBlock == block)
                {
                    PlayClip(clip, (float)cursor.BlockElapsedSeconds);
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }

        private void PlayClip(AudioClip clip, float offsetSeconds)
        {
            audioSourceComponent.Stop();
            audioSourceComponent.mute = _runtime.Muted;
            audioSourceComponent.volume = _runtime.Volume;
            audioSourceComponent.clip = clip;
            if (offsetSeconds > 0f && offsetSeconds < clip.length)
            {
                audioSourceComponent.time = offsetSeconds;
            }
            audioSourceComponent.Play();
        }

        private async Task PrepareStagingAudioAsync(StagingResultPayload staging, CancellationToken cancellationToken)
        {
            var dialogue = staging?.Timeline?.Dialogue;
            ReleasePreparedClips(dialogue);
            if (dialogue == null)
            {
                return;
            }

            for (var i = 0; i < dialogue.Count; i++)
            {
                var block = dialogue[i];
                if (block == null || _preparedClips.ContainsKey(block) || !HasEncodedAudio(block.Audio))
                {
                    continue;
                }
                try
                {
                    var clip = await LoadEncodedClipAsync(block.Audio, cancellationToken);
                    if (clip != null)
                    {
                        _preparedClips[block] = clip;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Missing audio must not block the conversation; the text and timeline still play.
                    OnError?.Invoke(ex);
                }
            }
        }

        private static bool HasEncodedAudio(StagingAudioSource audio)
        {
            if (audio == null)
            {
                return false;
            }
            if (audio.Data != null && audio.Data.Length > 0)
            {
                return !NpcifyCharacterRuntime.IsPcmAudio(audio);
            }
            return !string.IsNullOrWhiteSpace(audio.Url);
        }

        private static AudioType ResolveAudioType(StagingAudioSource audio)
        {
            var mime = audio.MimeType ?? string.Empty;
            if (mime.IndexOf("ogg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AudioType.OGGVORBIS;
            }
            if (mime.IndexOf("wav", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AudioType.WAV;
            }
            return AudioType.MPEG;
        }

        private static async Task<AudioClip> LoadEncodedClipAsync(StagingAudioSource audio, CancellationToken cancellationToken)
        {
            var audioType = ResolveAudioType(audio);
            string tempPath = null;
            var url = audio.Url;
            if (audio.Data != null && audio.Data.Length > 0)
            {
                var extension = audioType == AudioType.OGGVORBIS ? ".ogg" : audioType == AudioType.WAV ? ".wav" : ".mp3";
                tempPath = Path.Combine(Application.temporaryCachePath, "npcify-" + Guid.NewGuid().ToString("N") + extension);
                File.WriteAllBytes(tempPath, audio.Data);
                url = new Uri(tempPath).AbsoluteUri;
            }

            try
            {
                using (var request = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
                {
                    var handler = (DownloadHandlerAudioClip)request.downloadHandler;
                    handler.streamAudio = false;
                    handler.compressed = false;
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            request.Abort();
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        await Task.Yield();
                    }
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        throw new IOException($"Npcify audio download failed: {request.error}");
                    }
                    var clip = handler.audioClip;
                    if (clip != null)
                    {
                        clip.name = "npcify-speech";
                    }
                    return clip;
                }
            }
            finally
            {
                if (tempPath != null)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // The OS removes temporary cache files later.
                    }
                }
            }
        }

        private void ReleasePreparedClips(List<StagingDialogueBlock> keep)
        {
            if (_preparedClips.Count == 0)
            {
                return;
            }
            var stale = new List<StagingDialogueBlock>();
            foreach (var pair in _preparedClips)
            {
                if (keep == null || !keep.Contains(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }
            for (var i = 0; i < stale.Count; i++)
            {
                var clip = _preparedClips[stale[i]];
                _preparedClips.Remove(stale[i]);
                if (clip != null)
                {
                    if (audioSourceComponent != null && audioSourceComponent.clip == clip)
                    {
                        audioSourceComponent.Stop();
                        audioSourceComponent.clip = null;
                    }
                    Destroy(clip);
                }
            }
        }

        private void ReleaseActiveDynamicClip()
        {
            if (_activeDynamicClip == null)
            {
                return;
            }
            if (audioSourceComponent != null && audioSourceComponent.clip == _activeDynamicClip)
            {
                audioSourceComponent.Stop();
                audioSourceComponent.clip = null;
            }
            Destroy(_activeDynamicClip);
            _activeDynamicClip = null;
        }

        private void HandleAnimationPlayRequested(string animationId, bool loop)
        {
            if (string.IsNullOrWhiteSpace(animationId))
            {
                return;
            }

            OnAnimationRequested?.Invoke(animationId, loop);

            if (animatorComponent == null || animatorComponent.runtimeAnimatorController == null)
            {
                return;
            }
            var stateHash = Animator.StringToHash(animationId);
            if (animatorComponent.HasState(0, stateHash))
            {
                animatorComponent.CrossFadeInFixedTime(stateHash, 0.2f);
            }
        }

        private void UpdateGazeLayer()
        {
            if (gazeTarget == null && !_hasLookAtPosition)
            {
                _runtime.ClearLookAt();
                return;
            }

            var targetWorldPos = gazeTarget != null ? gazeTarget.position : _lookAtWorldPosition;
            var headTransform = GetBone(HumanBodyBones.Head) ?? Root;
            var localDir = headTransform.InverseTransformPoint(targetWorldPos);
            _runtime.LookAtDirection(localDir.x, localDir.y, localDir.z);
        }

        private void ApplyBlendShapesToSkinnedMeshes()
        {
            if (_runtime == null || _skinnedMeshRenderers.Count == 0)
            {
                return;
            }

            for (var rIdx = 0; rIdx < _skinnedMeshRenderers.Count; rIdx++)
            {
                var renderer = _skinnedMeshRenderers[rIdx];
                if (renderer == null || renderer.sharedMesh == null)
                {
                    continue;
                }

                var mesh = renderer.sharedMesh;
                var count = mesh.blendShapeCount;
                for (var bIdx = 0; bIdx < count; bIdx++)
                {
                    var shapeName = mesh.GetBlendShapeName(bIdx);
                    var weight01 = _runtime.Expressions.ResolveBlendShapeWeight01(shapeName);
                    renderer.SetBlendShapeWeight(bIdx, weight01 * 100f);
                }
            }
        }
    }
}
#endif
