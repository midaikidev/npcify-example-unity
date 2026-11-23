using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;

namespace Npcify.Sdk.Character
{
    public sealed partial class NpcifyCharacterRuntime
    {
        private string _activeVariant = NpcifyVariantSummary.DefaultKey;
        // Variant cues of the staging clock in the current Tick; they apply after the live pose of the tick.
        private readonly List<string> _tickVariantCues = new List<string>();
        private bool _ticking;
        private List<NpcifyVariantSummary> _variants = new List<NpcifyVariantSummary>();

        public string ActiveVariant => _activeVariant;

        public IReadOnlyList<NpcifyVariantSummary> Variants => _variants;

        public event Action<string> OnVariantChanged;

        public event Action<NpcifyStagingToolCall> OnToolCall;

        public void SwitchVariant(string variantKey)
        {
            var next = string.IsNullOrWhiteSpace(variantKey)
                ? NpcifyVariantSummary.DefaultKey
                : variantKey.Trim();
            if (string.Equals(_activeVariant, next, StringComparison.Ordinal))
            {
                return;
            }
            _activeVariant = next;
            OnVariantChanged?.Invoke(_activeVariant);
        }

        public async Task<StagingResultPayload> ReplayStagingAsync(
            string stagingId,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(stagingId))
            {
                throw new ArgumentException("Staging ID is required.", nameof(stagingId));
            }

            if (_bakedConversation != null)
            {
                var bakedStaging = _bakedConversation.Select(stagingId);
                _activeStagingId = _bakedConversation.ActiveStagingId;
                await PrepareAndPlayAsync(bakedStaging, cancellationToken);
                return bakedStaging;
            }

            if (string.IsNullOrWhiteSpace(_conversationId))
            {
                throw new InvalidOperationException("No active conversation.");
            }

            var staging = await _api.GetStagingAsync(_conversationId, stagingId, null, cancellationToken);
            _activeStagingId = staging.StagingId;
            await PrepareAndPlayAsync(staging, cancellationToken);
            return staging;
        }

        public Task<ConversationWorldline> GetConversationWorldlineAsync(
            CancellationToken cancellationToken = default
        )
        {
            if (_bakedConversation != null)
            {
                return Task.FromResult(_bakedConversation.Worldline(_conversationId));
            }
            if (string.IsNullOrWhiteSpace(_conversationId))
            {
                throw new InvalidOperationException("No active conversation.");
            }
            return _api.GetConversationWorldlineAsync(_conversationId, cancellationToken);
        }

        private void BindVariants()
        {
            _stagingPlayer.OnToolCallTriggered += call => OnToolCall?.Invoke(call);
            _stagingPlayer.OnVariantCue += (variantKey, _) =>
            {
                if (_ticking)
                {
                    _tickVariantCues.Add(variantKey);
                }
                else
                {
                    SwitchVariant(variantKey);
                }
            };
        }

        // A change of variant in a tick changes the model after the live pose moved to the time of
        // the tick: the next model starts with the pose of this frame, not of the last one.
        private void ApplyTickVariantCues()
        {
            if (_tickVariantCues.Count == 0)
            {
                return;
            }
            var cues = _tickVariantCues.ToArray();
            _tickVariantCues.Clear();
            foreach (var cue in cues)
            {
                SwitchVariant(cue);
            }
        }

        private void ConfigureVariants(NpcifyAssetInfo info)
        {
            _variants = info?.Variants != null
                ? new List<NpcifyVariantSummary>(info.Variants)
                : new List<NpcifyVariantSummary>();
        }
    }
}
