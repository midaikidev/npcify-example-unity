using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Api;
using Npcify.Sdk.Models;
using Npcify.Sdk.Motion;

namespace Npcify.Sdk.Character
{
    /// <summary>Live motion of the runtime (docs/realtime-motion.md).</summary>
    public sealed partial class NpcifyCharacterRuntime
    {
        /// <summary>The live motion frames of the stagings and the pose of the staging clock (docs/realtime-motion.md).</summary>
        public NpcifyLiveMotionPlayer LiveMotion => _liveMotion;

        /// <summary>
        /// Set by the engine integration while a rig can play live motion. Only then does the
        /// runtime declare the motion_frames capability; otherwise turns keep prepared clips.
        /// </summary>
        public bool LiveMotionTargetReady { get; set; }

        /// <summary>The conversation stream that carries the live motion frames, once a turn asked for them.</summary>
        public NpcifyConversationStream ConversationStream => _stream;

        /// <summary>Notes of the conversation stream, for logs.</summary>
        public event Action<string> OnStreamLog;

        /// <summary>Live motion frames arrived for a staging (on the thread that calls Tick).</summary>
        public event Action<NpcifyMotionFramesEvent> OnMotionFrames;

        // The character's live motion switch from its info; null until the info is read.
        private bool? _liveMotionInEffect;

        /// <summary>
        /// The character's info says that its body motion is generated live (null until the
        /// runtime read the info at the start of a conversation).
        /// </summary>
        public bool? LiveMotionInEffect => _liveMotionInEffect;

        private bool WantsLiveMotion =>
            _options.LiveMotion?.Enabled != false && LiveMotionTargetReady && _bakedConversation == null &&
            _liveMotionInEffect != false;

        private IReadOnlyList<string> LiveMotionCapabilities =>
            WantsLiveMotion ? new[] { NpcifyMotionFrames.Capability } : null;

        /// <summary>The longest time in milliseconds that a turn waits for the stream to join.</summary>
        public int TurnJoinWaitMilliseconds { get; set; } = 3000;

        // A turn stopped waiting for the stream of this conversation; later turns do not wait for it.
        private string _liveMotionJoinGivenUp;

        /// <summary>
        /// The capabilities of a turn. Live motion frames arrive on the conversation stream while the
        /// server makes the turn, so a turn declares them only when the stream has joined. A turn
        /// waits a few seconds for the join at most and does not wait for a join that is known to
        /// fail: it then gets the prepared clips, while the stream tries again in the background.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveTurnCapabilitiesAsync(
            string conversationId,
            CancellationToken cancellationToken)
        {
            if (!WantsLiveMotion)
            {
                return Array.Empty<string>();
            }
            if (_stream != null && _stream.IsJoined(conversationId))
            {
                return LiveMotionCapabilities;
            }
            if (_liveMotionJoinGivenUp == conversationId ||
                (_stream != null && (_stream.IsFailing(conversationId) || _stream.IsRefused(conversationId))))
            {
                return Array.Empty<string>();
            }
            var join = JoinLiveMotionStreamAsync(conversationId, CancellationToken.None);
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var finished = await Task.WhenAny(join, Task.Delay(Math.Max(0, TurnJoinWaitMilliseconds), wait.Token));
                wait.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (finished == join && join.Result)
                {
                    return LiveMotionCapabilities;
                }
            }
            _liveMotionJoinGivenUp = conversationId;
            return Array.Empty<string>();
        }

        /// <summary>
        /// Warms up live motion for the current conversation, once, on the player's intent: call it
        /// when the player starts to type a reply or presses to talk, before the message. It does
        /// nothing for a character without live motion or a rig that cannot play it; the server
        /// limits warm-ups per player and network. Returns true when a session warms up.
        /// </summary>
        public async Task<bool> PrepareLiveMotionAsync(CancellationToken cancellationToken = default)
        {
            var conversationId = _conversationId;
            if (!WantsLiveMotion || string.IsNullOrEmpty(conversationId) || _liveMotionPrepared == conversationId)
            {
                return false;
            }
            if (_stream != null && (_stream.IsRefused(conversationId) || _stream.IsFailing(conversationId)))
            {
                // The frames could not reach this client: a warm-up would cost the owner for nothing.
                return false;
            }
            _liveMotionPrepared = conversationId;
            // The stream joins in the background; the warm-up does not wait for it.
            _ = JoinLiveMotionStreamAsync(conversationId, CancellationToken.None);
            return await _api.WarmUpLiveMotionAsync(conversationId, cancellationToken);
        }

        private async Task<bool> JoinLiveMotionStreamAsync(string conversationId, CancellationToken cancellationToken)
        {
            if (_stream == null)
            {
                _stream = _streamFactory();
                _stream.StreamEvent += HandleStreamEvent;
                _stream.Log += message => _mainThreadWork.Enqueue(() => OnStreamLog?.Invoke(message));
            }
            try
            {
                return await _stream.JoinAsync(conversationId, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        // Background thread: decode here, apply on the thread that calls Tick.
        private void HandleStreamEvent(string conversationId, Dictionary<string, object> streamEvent)
        {
            var motion = NpcifyMotionFrames.TryParseEvent(streamEvent);
            if (motion != null)
            {
                _mainThreadWork.Enqueue(() =>
                {
                    _liveMotion.Push(motion);
                    OnMotionFrames?.Invoke(motion);
                });
            }
        }

        private void LoadStoredMotion(StagingResultPayload staging)
        {
            var track = staging.Timeline?.Motion;
            var stagingId = staging.StagingId;
            if (track == null || string.IsNullOrEmpty(stagingId) || _liveMotion.Has(stagingId))
            {
                return;
            }
            if (track.FramesData != null)
            {
                _mainThreadWork.Enqueue(() => _liveMotion.Load(stagingId, track, track.FramesData));
                return;
            }
            // A replay plays idle until its stored frames load.
            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = await _api.DownloadBytesAsync(track.FramesUrl).ConfigureAwait(false);
                    _mainThreadWork.Enqueue(() =>
                    {
                        try
                        {
                            _liveMotion.Load(stagingId, track, bytes);
                        }
                        catch (Exception error)
                        {
                            OnError?.Invoke(error);
                        }
                    });
                }
                catch (Exception error)
                {
                    _mainThreadWork.Enqueue(() => OnError?.Invoke(error));
                }
            });
        }
    }
}
