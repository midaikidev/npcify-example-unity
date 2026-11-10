using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;
using Npcify.Sdk.Sight;

namespace Npcify.Sdk.Character
{
    /// <summary>Sight of the runtime (docs/sight.md): frames of a source go on the conversation stream.</summary>
    public sealed partial class NpcifyCharacterRuntime
    {
        private static readonly Stopwatch SightClock = Stopwatch.StartNew();
        // Results of the source and the connection, for the thread that calls Tick.
        private readonly ConcurrentQueue<Action> _sightWork = new ConcurrentQueue<Action>();
        // The schedule exists only after the first StartSight; until then the state comes from these.
        private NpcifySightInput _sight;
        private bool _sightInfoRead;
        private bool _sightEnabled;
        private bool _sightBaked;
        private bool _sightHidden;

        /// <summary>The sight state changed; the error is the failure that stopped sight or its start, else null.</summary>
        public event Action<NpcifySightState, NpcifySightException> OnSightState;

        /// <summary>An observation of the current conversation (on the thread that calls Tick).</summary>
        public event Action<NpcifySightObservation> OnSight;

        /// <summary>
        /// Unavailable until the character info turns sight on (its owner's Sight tab), and in a
        /// baked conversation; then Off, Starting or On.
        /// </summary>
        public NpcifySightState SightState => _sight?.State ?? ConfiguredSightState;

        /// <summary>True while the game is paused or in the background: sight sends no frames.</summary>
        public bool SightHidden
        {
            get => _sightHidden;
            set
            {
                _sightHidden = value;
                if (_sight != null)
                {
                    _sight.Hidden = value;
                }
            }
        }

        private NpcifySightState ConfiguredSightState =>
            _sightInfoRead && _sightEnabled && !_sightBaked ? NpcifySightState.Off : NpcifySightState.Unavailable;

        /// <summary>
        /// Lets the character see the pictures of a source (docs/sight.md). Needs a conversation.
        /// Completes when frames can go; faults with an NpcifySightException: SIGHT_DISABLED when
        /// sight is unavailable, CONVERSATION_REQUIRED, SIGHT_ALREADY_STARTED, a code of the source,
        /// or the code of a failed join.
        /// </summary>
        public Task StartSight(NpcifySightOptions options, INpcifySightSource source)
        {
            _sight = _sight ?? CreateSight();
            return _sight.Start(options, source);
        }

        /// <summary>Stops the frames and releases the source.</summary>
        public void StopSight()
        {
            _sight?.Stop();
        }

        private NpcifySightInput CreateSight()
        {
            var sight = new NpcifySightInput(
                new SightConnection(this),
                () => _bakedConversation != null ? null : _conversationId,
                () => SightClock.ElapsedMilliseconds,
                () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                work => _sightWork.Enqueue(work))
            {
                Hidden = _sightHidden,
            };
            // The state that the events reported already: configured before the handlers.
            if (_sightInfoRead)
            {
                sight.Configure(_sightEnabled, _sightBaked);
            }
            sight.StateChanged += (state, error) => OnSightState?.Invoke(state, error);
            sight.Stopped += error => OnError?.Invoke(error);
            sight.Observation += observation => OnSight?.Invoke(observation);
            sight.CallbackError += error => OnError?.Invoke(error);
            return sight;
        }

        private void ConfigureSight(NpcifyAssetInfo info)
        {
            SetSightConfiguration(info.SightEnabled, info.BakedConversationEnabled || _bakedConversation != null);
        }

        // The character info or a baked conversation: the schedule takes it, or the state event
        // reports the change while there is no schedule.
        private void SetSightConfiguration(bool enabled, bool baked)
        {
            var before = SightState;
            _sightInfoRead = true;
            _sightEnabled = enabled;
            _sightBaked = baked;
            if (_sight != null)
            {
                _sight.Configure(enabled, baked);
                return;
            }
            var after = ConfiguredSightState;
            if (after != before)
            {
                OnSightState?.Invoke(after, null);
            }
        }

        // After SIGHT_DISABLED the character info is read again before the next conversation.
        private bool SightInfoStale => _sight != null && _sight.DisabledByServer;

        private void TickSight()
        {
            while (_sightWork.TryDequeue(out var work))
            {
                work();
            }
            _sight?.Tick();
        }

        // Background thread: the observation of a conversation; only the current one counts.
        private void HandleSightObservation(NpcifySightObservation observation)
        {
            _sightWork.Enqueue(() =>
            {
                if (_bakedConversation != null || observation.ConversationId != _conversationId)
                {
                    return;
                }
                if (_sight != null)
                {
                    _sight.HandleObservation(observation);
                }
                else
                {
                    OnSight?.Invoke(observation);
                }
            });
        }

        // A note of each frame for OnStreamLog: its size and its acknowledgement.
        private void NoteSight(NpcifySightFrameRequest request, NpcifySightAck ack, long milliseconds)
        {
            if (OnStreamLog == null)
            {
                return;
            }
            var result = ack.Ok ? "ok " + ack.ObservationId : ack.Answered ? ack.Error : "no acknowledgement, " + ack.Error;
            var note = "Sight frame " + request.Width + "x" + request.Height + ", " + request.Jpeg.Length + " bytes: " + result +
                       " after " + milliseconds + " ms";
            _mainThreadWork.Enqueue(() => OnStreamLog?.Invoke(note));
        }

        /// <summary>The conversation stream as the connection of sight.</summary>
        private sealed class SightConnection : INpcifySightConnection
        {
            private readonly NpcifyCharacterRuntime _runtime;

            public SightConnection(NpcifyCharacterRuntime runtime)
            {
                _runtime = runtime;
            }

            public void Join(string conversationId, Action<string> done)
            {
                _runtime.JoinStreamAsync(conversationId, CancellationToken.None).ContinueWith(
                    joined =>
                    {
                        var stream = _runtime._stream;
                        if (joined.Status == TaskStatus.RanToCompletion && joined.Result)
                        {
                            done(null);
                        }
                        else
                        {
                            done(stream != null && stream.IsRefused(conversationId)
                                ? NpcifySightCodes.AccessDenied
                                : NpcifySightCodes.Disconnected);
                        }
                    },
                    TaskScheduler.Default);
            }

            public void SendFrame(NpcifySightFrameRequest request, Action<NpcifySightAck> done)
            {
                var stream = _runtime._stream;
                if (stream == null)
                {
                    done(NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected));
                    return;
                }
                var watch = Stopwatch.StartNew();
                stream.SendSightFrameAsync(request).ContinueWith(
                    sent =>
                    {
                        var ack = sent.Status == TaskStatus.RanToCompletion
                            ? sent.Result
                            : NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected);
                        _runtime.NoteSight(request, ack, watch.ElapsedMilliseconds);
                        done(ack);
                    },
                    TaskScheduler.Default);
            }
        }
    }
}
