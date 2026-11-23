#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;
using UnityEngine;
using UnityEngine.Events;

namespace Npcify.Sdk.Unity
{
    [Serializable]
    public sealed class NpcifyVariantBinding
    {
        public string variantKey = NpcifyVariantSummary.DefaultKey;
        public GameObject avatarRoot;
    }

    public sealed partial class NpcifyCharacter
    {
        [Header("Appearance Variants (Optional)")]
        [SerializeField] private List<NpcifyVariantBinding> variantBindings = new List<NpcifyVariantBinding>();
        [SerializeField] private UnityEvent<string> onVariantChanged = new UnityEvent<string>();

        public event Action<string> OnVariantChanged;
        public event Action<NpcifyStagingToolCall> OnToolCall;

        public string ActiveVariant => Runtime.ActiveVariant;

        public IReadOnlyList<NpcifyVariantSummary> Variants => Runtime.Variants;

        public IReadOnlyList<NpcifyVariantBinding> VariantBindings => variantBindings;

        public UnityEvent<string> VariantChanged => onVariantChanged;

        public void SwitchVariant(string variantKey)
        {
            EnsureInitialized();
            _runtime.SwitchVariant(variantKey);
        }

        public Task<StagingResultPayload> ReplayStagingAsync(
            string stagingId,
            CancellationToken cancellationToken = default
        )
        {
            EnsureInitialized();
            return _runtime.ReplayStagingAsync(stagingId, cancellationToken);
        }

        private void BindVariants()
        {
            _runtime.OnVariantChanged += HandleVariantChanged;
            _runtime.OnToolCall += call => OnToolCall?.Invoke(call);
        }

        private void HandleVariantChanged(string variantKey)
        {
            if (variantBindings != null && variantBindings.Count > 0)
            {
                GameObject matchedRoot = null;
                for (var i = 0; i < variantBindings.Count; i++)
                {
                    var binding = variantBindings[i];
                    if (binding != null &&
                        binding.avatarRoot != null &&
                        string.Equals(binding.variantKey, variantKey, StringComparison.Ordinal))
                    {
                        matchedRoot = binding.avatarRoot;
                        break;
                    }
                }

                if (matchedRoot != null)
                {
                    // A change of variant changes the model only: read the animation state of the
                    // model that leaves before it becomes inactive.
                    var previousAnimator = animatorComponent;
                    var previousState = matchedRoot.transform != avatarRoot
                        ? NpcifyAnimatorHandover.Capture(previousAnimator)
                        : null;
                    var previousTarget = IsAlive(_liveMotionTarget) ? _liveMotionTarget : null;
                    for (var i = 0; i < variantBindings.Count; i++)
                    {
                        var binding = variantBindings[i];
                        if (binding?.avatarRoot != null && binding.avatarRoot != matchedRoot)
                        {
                            binding.avatarRoot.SetActive(false);
                        }
                    }

                    matchedRoot.SetActive(true);
                    avatarRoot = matchedRoot.transform;
                    var nextAnimator = avatarRoot.GetComponentInChildren<Animator>();
                    if (nextAnimator != null)
                    {
                        animatorComponent = nextAnimator;
                    }
                    RefreshAvatarBindings();
                    if (previousState != null && animatorComponent != previousAnimator)
                    {
                        NpcifyAnimatorHandover.Apply(previousState, animatorComponent);
                    }
                    if (previousTarget != null && !ReferenceEquals(previousTarget, _liveMotionTarget) &&
                        _liveMotionTarget is INpcifyLiveMotionHandover handover)
                    {
                        handover.TakeOver(previousTarget);
                    }
                }
            }

            OnVariantChanged?.Invoke(variantKey);
            onVariantChanged?.Invoke(variantKey);
        }
    }
}
#endif
