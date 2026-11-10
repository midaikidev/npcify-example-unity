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

        /// <summary>
        /// The conversation stream: it carries the streamed replies and the live motion frames,
        /// once a conversation or a turn needed it.
        /// </summary>
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
        private string _streamJoinGivenUp;

        private struct TurnStream
        {
            public bool Joined;
            // Always sent: without the list the server uses the capabilities of the conversation.
            public IReadOnlyList<string> Capabilities;
        }

        /// <summary>
        /// The stream of a turn. The streamed reply and the live motion frames arrive on the
        /// conversation stream while the server makes the turn, so a turn uses them only when the
        /// stream has joined. A turn waits a few seconds for the join at most and does not wait for
        /// a join that is known to fail: the reply then plays after its HTTP result with the
        /// prepared clips, while the stream tries again in the background.
        /// </summary>
        private async Task<TurnStream> ResolveTurnStreamAsync(
            string conversationId,
            CancellationToken cancellationToken)
        {
            var none = new TurnStream { Joined = false, Capabilities = Array.Empty<string>() };
            var live = WantsLiveMotion;
            if (!live && !StreamPlaybackEnabled)
            {
                return none;
            }
            var joined = new TurnStream
            {
                Joined = true,
                Capabilities = live ? LiveMotionCapabilities : Array.Empty<string>(),
            };
            if (_stream != null && _stream.IsJoined(conversationId))
            {
                return joined;
            }
            if (_streamJoinGivenUp == conversationId ||
                (_stream != null && (_stream.IsFailing(conversationId) || _stream.IsRefused(conversationId))))
            {
                return none;
            }
            var join = JoinStreamAsync(conversationId, CancellationToken.None);
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var finished = await Task.WhenAny(join, Task.Delay(Math.Max(0, TurnJoinWaitMilliseconds), wait.Token));
                wait.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (finished == join && join.Result)
                {
                    return joined;
                }
            }
            _streamJoinGivenUp = conversationId;
            return none;
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
            _ = JoinStreamAsync(conversationId, CancellationToken.None);
            return await _api.WarmUpLiveMotionAsync(conversationId, cancellationToken);
        }

        private async Task<bool> JoinStreamAsync(string conversationId, CancellationToken cancellationToken)
        {
            if (_stream == null)
            {
                _stream = _streamFactory();
                _stream.StreamEvent += HandleStreamEvent;
                _stream.SightObservation += HandleSightObservation;
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
                return;
            }
            if (streamEvent != null && streamEvent.TryGetValue("type", out var type) && IsTurnEvent(type as string))
            {
                EnqueueTurnEvent(streamEvent);
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
