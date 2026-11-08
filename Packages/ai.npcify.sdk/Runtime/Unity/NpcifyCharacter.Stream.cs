#if UNITY_5_3_OR_NEWER
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;
using UnityEngine;

namespace Npcify.Sdk.Unity
{
    /// <summary>Replies that play while they stream (docs/streaming_reply.md).</summary>
    public sealed partial class NpcifyCharacter
    {
        [Header("Replies")]
        [Tooltip("Play a reply while the server makes it: its first block plays when its audio is ready. Off: a reply plays after it is complete.")]
        [SerializeField] private bool streamReplies = true;

        /// <summary>
        /// Play a reply while the server makes it. Off: a reply plays after it is complete. Takes
        /// effect for the next runtime (Configure or a new component).
        /// </summary>
        public bool StreamReplies
        {
            get => streamReplies;
            set => streamReplies = value;
        }

        // Decodes the audio of one block of a streamed reply before the block starts.
        private async Task PrepareBlockAudioAsync(StagingDialogueBlock block, CancellationToken cancellationToken)
        {
            if (block == null || _preparedClips.ContainsKey(block) || !HasEncodedAudio(block.Audio))
            {
                return;
            }
            var speech = await LoadEncodedClipAsync(block.Audio, cancellationToken);
            if (speech == null)
            {
                return;
            }
            if (this == null || cancellationToken.IsCancellationRequested)
            {
                Destroy(speech.Clip);
                return;
            }
            _preparedClips[block] = speech;
        }

        // The length of the speech in the decoded clip of a block: a block of a streamed reply plays
        // to its end. For MP3 audio it is the length of the MP3 audio, as the server counts it.
        private double PreparedClipLength(StagingDialogueBlock block)
        {
            return block != null && _preparedClips.TryGetValue(block, out var speech) && speech?.Clip != null ? speech.Seconds : 0.0;
        }

        // A staging that starts to play releases the clips of other stagings.
        private void ReleaseClipsOfOtherStagings(StagingResultPayload staging)
        {
            var dialogue = staging?.Timeline?.Dialogue;
            if (dialogue != null)
            {
                ReleasePreparedClips(dialogue);
            }
        }
    }
}
#endif
