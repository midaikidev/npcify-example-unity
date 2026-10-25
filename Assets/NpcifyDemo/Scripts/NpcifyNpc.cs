using System;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Api;
using Npcify.Sdk.Models;
using Npcify.Sdk.Unity;
using UnityEngine;
using UniVRM10;

namespace NpcifyDemo
{
    public enum NpcConnectionMode
    {
        /// <summary>Baked conversation and model files ship with the game. No network.</summary>
        OfflineBaked,
        /// <summary>Model, animations and replies come from the Npcify API.</summary>
        OnlineApi,
    }

    public enum NpcLoadState
    {
        Loading,
        Ready,
        Failed,
    }

    /// <summary>
    /// An Npcify character in the scene: loads its VRM model at runtime, plays its idle
    /// animation, and gives the dialogue UI a simple conversation API.
    /// </summary>
    public sealed class NpcifyNpc : MonoBehaviour
    {
        [SerializeField] private NpcConnectionMode mode = NpcConnectionMode.OfflineBaked;
        [Tooltip("Folder under StreamingAssets/Npcify for the offline character.")]
        [SerializeField] private string offlineFolder = "hinata";
        [Tooltip("Character ID that is used when the .env file does not set one.")]
        [SerializeField] private string defaultCharacterId = string.Empty;
        [SerializeField] private float turnSpeedDegrees = 240f;

        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private NpcifyApiClient _downloads;
        private NpcifyCharacter _character;
        private NpcVrmaPlayer _animations;
        private Vrm10Instance _vrm;
        private Transform _faceTarget;
        private Quaternion _restRotation;

        public NpcConnectionMode Mode => mode;
        public NpcLoadState State { get; private set; } = NpcLoadState.Loading;
        public string StatusMessage { get; private set; } = "Loading…";
        public string DisplayName { get; private set; }
        public bool AllowCustomReply { get; private set; }
        public NpcifyCharacter Character => _character;
        public bool IsReady => State == NpcLoadState.Ready;

        /// <summary>A point above the head, for the name label and the interaction prompt.</summary>
        public Vector3 LabelPosition
        {
            get
            {
                var head = _character != null ? _character.GetBone(HumanBodyBones.Head) : null;
                return head != null ? head.position + Vector3.up * 0.32f : transform.position + Vector3.up * 1.9f;
            }
        }

        /// <summary>The point that cameras look at during a conversation.</summary>
        public Vector3 FocusPosition
        {
            get
            {
                var head = _character != null ? _character.GetBone(HumanBodyBones.Head) : null;
                return head != null ? head.position : transform.position + Vector3.up * 1.45f;
            }
        }

        public string ModeLabel => mode == NpcConnectionMode.OfflineBaked ? "Offline · baked" : "Online · API";

        private string CharacterIdEnvKey =>
            mode == NpcConnectionMode.OfflineBaked ? DemoEnv.OfflineCharacterIdKey : DemoEnv.OnlineCharacterIdKey;

        private void Awake()
        {
            _restRotation = transform.rotation;
            DisplayName = gameObject.name;
        }

        private async void Start()
        {
            try
            {
                await LoadAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                // The scene was unloaded while the character loaded.
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                Debug.LogException(ex, this);
            }
        }

        private void OnDestroy()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _downloads?.Dispose();
        }

        public async Task<StagingResultPayload> BeginConversationAsync(CancellationToken cancellationToken)
        {
            EnsureReady();
            _character.ResetConversation();
            return await _character.CreateConversationAsync(cancellationToken);
        }

        public async Task<StagingResultPayload> ReplyAsync(string message, CancellationToken cancellationToken)
        {
            EnsureReady();
            return await _character.ReplyAsync(message, cancellationToken);
        }

        public void EndConversation()
        {
            if (_character != null)
            {
                _character.StopSpeaking();
            }
            FaceTowards(null);
        }

        /// <summary>Turns the NPC towards a target, or back to its start rotation for null.</summary>
        public void FaceTowards(Transform target)
        {
            _faceTarget = target;
            if (_vrm != null)
            {
                _vrm.LookAtTargetType = target != null
                    ? VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform
                    : VRM10ObjectLookAt.LookAtTargetTypes.YawPitchValue;
                _vrm.LookAtTarget = target;
            }
        }

        private void Update()
        {
            var desired = _restRotation;
            if (_faceTarget != null)
            {
                var flat = _faceTarget.position - transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f)
                {
                    desired = Quaternion.LookRotation(flat.normalized, Vector3.up);
                }
            }
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, turnSpeedDegrees * Time.deltaTime);
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            var characterId = DemoEnv.Get(CharacterIdEnvKey, defaultCharacterId);
            var options = new NpcifyOptions
            {
                BaseUrl = DemoEnv.ApiBaseUrl,
                CharacterId = characterId,
                ApiKey = DemoEnv.ApiKey,
            };
            INpcCharacterSource source;
            byte[] bakedConversation = null;

            if (mode == NpcConnectionMode.OfflineBaked)
            {
                var offline = new OfflineCharacterSource(offlineFolder);
                if (!offline.Exists)
                {
                    Fail("Offline files are missing. Run Npcify > Download Offline Character.");
                    return;
                }
                source = offline;
                bakedConversation = await offline.LoadConversationAsync(cancellationToken);
            }
            else
            {
                if (!DemoEnv.HasApiKey)
                {
                    Fail($"Set {DemoEnv.ApiKeyKey} in .env to talk to this character.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(characterId))
                {
                    Fail($"Set {DemoEnv.OnlineCharacterIdKey} in .env.");
                    return;
                }
                _downloads = new NpcifyApiClient(options);
                source = new OnlineCharacterSource(_downloads);
            }

            StatusMessage = "Loading character…";
            var manifest = await source.LoadManifestAsync(cancellationToken);
            DisplayName = string.IsNullOrWhiteSpace(manifest.Name) ? gameObject.name : manifest.Name;
            AllowCustomReply = manifest.AllowCustomReply;

            StatusMessage = "Loading model…";
            var vrmBytes = await source.LoadVrmAsync(cancellationToken);
            _vrm = await Vrm10.LoadBytesAsync(
                vrmBytes,
                canLoadVrm0X: true,
                controlRigGenerationOption: ControlRigGenerationOption.Generate,
                showMeshes: true,
                materialGenerator: new UrpVrm10MaterialDescriptorGenerator(),
                ct: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // The SDK writes face blend shapes in LateUpdate. UniVRM must update first.
            _vrm.UpdateType = Vrm10Instance.UpdateTypes.Update;
            // UniVRM makes the control rig on the first Runtime access and keeps the bone
            // world rotations of that moment as the T-pose. Make it before the model gets
            // the NPC's rotation, or every animation plays mirrored (raised arms).
            _ = _vrm.Runtime;
            _vrm.transform.SetParent(transform, false);
            _vrm.transform.localPosition = Vector3.zero;
            _vrm.transform.localRotation = Quaternion.identity;
            foreach (var skinned in _vrm.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skinned.updateWhenOffscreen = true;
            }

            AttachCharacterToModel(options, _vrm.transform);
            if (bakedConversation != null)
            {
                _character.AttachBakedConversation(bakedConversation);
            }

            _animations = _vrm.gameObject.AddComponent<NpcVrmaPlayer>();
            _animations.Initialize(_vrm, manifest.IdleAnimationId, source.LoadAnimationAsync);
            _character.OnAnimationRequested += (animationId, loop) => _ = _animations.PlayAsync(animationId, loop);
            _character.OnError += ex => Debug.LogWarning($"[Npcify demo] {DisplayName}: {ex.Message}");
            await _animations.PlayIdleAsync();

            State = NpcLoadState.Ready;
            StatusMessage = string.Empty;
        }

        private void AttachCharacterToModel(NpcifyOptions options, Transform modelRoot)
        {
            var character = modelRoot.gameObject.AddComponent<NpcifyCharacter>();
            character.Configure(options);
            character.RefreshAvatarBindings();
            var audioSource = character.AudioSource;
            audioSource.minDistance = 2.5f;
            audioSource.maxDistance = 30f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            _character = character;
        }

        private void EnsureReady()
        {
            if (!IsReady)
            {
                throw new InvalidOperationException(StatusMessage);
            }
        }

        private void Fail(string message)
        {
            State = NpcLoadState.Failed;
            StatusMessage = message;
            Debug.LogWarning($"[Npcify demo] {gameObject.name}: {message}", this);
        }
    }
}
