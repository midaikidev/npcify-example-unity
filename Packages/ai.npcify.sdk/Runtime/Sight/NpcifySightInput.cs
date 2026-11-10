using System;
using System.Threading.Tasks;

namespace Npcify.Sdk.Sight
{
    /// <summary>
    /// The pictures of sight: a game view or a webcam. The engine part reads them; tests use a fake.
    /// A callback can run on any thread.
    /// </summary>
    public interface INpcifySightSource
    {
        /// <summary>"game" for a render of the game, "camera" for a webcam.</summary>
        string Kind { get; }

        /// <summary>False after the source ended: a destroyed camera or texture, or a webcam that stopped.</summary>
        bool Live { get; }

        /// <summary>Opens the source: done(null) when it gives pictures, else done(code).</summary>
        void Open(Action<string> done);

        /// <summary>The 32 x 18 luma grid (0 to 255, row by row) of the current picture, or null while there is no picture.</summary>
        void ReadLuminance(Action<byte[]> done);

        /// <summary>The current picture as a JPEG that fits inside 640 x 360, or null while there is no picture.</summary>
        void Capture(Action<NpcifySightFrame> done);

        /// <summary>Releases the source. Stops a webcam only when the SDK started it.</summary>
        void Stop();
    }

    /// <summary>The part of the conversation connection that sight uses. A callback can run on any thread.</summary>
    public interface INpcifySightConnection
    {
        /// <summary>Joins the conversation: done(null) when it joined, else done(code).</summary>
        void Join(string conversationId, Action<string> done);

        /// <summary>Sends one frame with conversations:sight and gives its acknowledgement.</summary>
        void SendFrame(NpcifySightFrameRequest request, Action<NpcifySightAck> done);
    }

    /// <summary>
    /// Sight of the conversation (docs/sight.md), as the web SDK (sight-input.ts): a motion check
    /// every 250 ms, a frame at the interval or after motion, and one frame in flight at a time on
    /// the conversation's connection. Call the methods and Tick on one thread; the results of the
    /// source and the connection come back through the post action of the constructor.
    /// </summary>
    public sealed class NpcifySightInput
    {
        /// <summary>A pause of the checks longer than this resets the motion grid (a paused game).</summary>
        public const int TickGapMs = 1000;

        private const string BakedMessage = "Sight is not available in a baked (offline) conversation.";

        private sealed class Session
        {
            public int IntervalMs;
            // Null: motion does not send frames.
            public float? Threshold;
            // Set when the source opened.
            public INpcifySightSource Source;
            // The last motion grid; null after a hidden or paused game.
            public byte[] Grid;
            public bool ReadingGrid;
            public long NextCheckAt;
            public bool HasSent;
            // The time of the last frame that went to the server.
            public long LastSentAt;
            // The picture changed after the last frame.
            public bool Motion;
            // A frame waits for its acknowledgement.
            public bool InFlight;
            // The connection was lost: join the conversation before the next frame.
            public bool Rejoin;
        }

        private readonly INpcifySightConnection _connection;
        private readonly Func<string> _conversationId;
        private readonly Func<long> _now;
        private readonly Func<long> _unixNow;
        private readonly Action<Action> _post;
        private Session _session;
        // Why sight cannot start; null when it can.
        private string _unavailable = "Sight needs the character info: start a conversation first.";
        private NpcifySightState _state = NpcifySightState.Unavailable;
        private bool _hidden;
        private bool _ticked;
        private long _lastTick;

        /// <param name="connection">The conversation connection.</param>
        /// <param name="conversationId">The current conversation, or null.</param>
        /// <param name="now">A monotonic clock in milliseconds, for the schedule.</param>
        /// <param name="unixNow">The time in Unix milliseconds, for capturedAt.</param>
        /// <param name="post">Runs an action on the thread of Tick, after the current one.</param>
        public NpcifySightInput(
            INpcifySightConnection connection,
            Func<string> conversationId,
            Func<long> now,
            Func<long> unixNow,
            Action<Action> post)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _conversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _unixNow = unixNow ?? throw new ArgumentNullException(nameof(unixNow));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        /// <summary>The state changed; the error is the failure that stopped sight or its start, else null.</summary>
        public event Action<NpcifySightState, NpcifySightException> StateChanged;

        /// <summary>An error stopped sight after it started.</summary>
        public event Action<NpcifySightException> Stopped;

        /// <summary>An observation of the current conversation.</summary>
        public event Action<NpcifySightObservation> Observation;

        /// <summary>A handler of an event threw.</summary>
        public event Action<Exception> CallbackError;

        public NpcifySightState State => _state;

        /// <summary>True after the server refused a frame with SIGHT_DISABLED, until Configure.</summary>
        public bool DisabledByServer { get; private set; }

        /// <summary>
        /// True while the game is paused or in the background: no frames, and no motion against a
        /// picture from before.
        /// </summary>
        public bool Hidden
        {
            get => _hidden;
            set
            {
                _hidden = value;
                if (value && _session != null)
                {
                    _session.Grid = null;
                }
            }
        }

        /// <summary>
        /// The character info was read. Sight is available only when the owner turned it on, and
        /// never in a baked conversation. Sight that becomes unavailable stops.
        /// </summary>
        public void Configure(bool enabled, bool baked)
        {
            DisabledByServer = false;
            _unavailable = baked ? BakedMessage : enabled ? null : NpcifySightCodes.OffMessage;
            if (_unavailable != null)
            {
                Stop();
            }
            else
            {
                Publish(null);
            }
        }

        /// <summary>
        /// Opens the source and joins the conversation. Completes when frames can go; faults with
        /// an NpcifySightException.
        /// </summary>
        public Task Start(NpcifySightOptions options, INpcifySightSource source)
        {
            if (_unavailable != null)
            {
                return Refuse(new NpcifySightException(NpcifySightCodes.Disabled, _unavailable));
            }
            if (_session != null)
            {
                return Refuse(new NpcifySightException(NpcifySightCodes.AlreadyStarted));
            }
            var conversationId = _conversationId();
            if (string.IsNullOrEmpty(conversationId))
            {
                return Refuse(new NpcifySightException(NpcifySightCodes.ConversationRequired));
            }
            if (source == null)
            {
                return Refuse(new NpcifySightException(NpcifySightCodes.SourceUnavailable));
            }
            options = options ?? new NpcifySightOptions();
            var session = new Session
            {
                IntervalMs = options.IntervalMs > 0 ? Math.Max(NpcifySightLimits.MinFrameIntervalMs, options.IntervalMs) : NpcifySightLimits.IntervalMs,
                Threshold = !options.Motion
                    ? (float?)null
                    : float.IsNaN(options.MotionThreshold) || float.IsInfinity(options.MotionThreshold)
                        ? NpcifySightLimits.MotionThreshold
                        : Math.Max(0f, options.MotionThreshold),
            };
            _session = session;
            Publish(null);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string joinError = null;
            string openError = null;
            var pending = 2;
            void Settled()
            {
                pending--;
                if (pending == 0)
                {
                    Opened(session, source, openError ?? joinError, result);
                }
            }
            // The source opens while the conversation joins.
            Guard(
                () => _connection.Join(conversationId, code => _post(() => { joinError = code; Settled(); })),
                () => { joinError = NpcifySightCodes.Disconnected; Settled(); });
            Guard(
                () => source.Open(code => _post(() => { openError = code; Settled(); })),
                () => { openError = SourceFailure(source); Settled(); });
            return result.Task;
        }

        /// <summary>Stops the frames and releases the source. Does nothing when sight is off.</summary>
        public void Stop()
        {
            if (_session != null)
            {
                End(_session);
            }
            Publish(null);
        }

        /// <summary>Call it every frame: it runs a check when 250 ms passed since the last one.</summary>
        public void Tick()
        {
            var now = _now();
            var session = _session;
            if (_ticked && now - _lastTick > TickGapMs && session != null)
            {
                // The game did not run (paused, or a long load): no motion against that picture.
                session.Grid = null;
            }
            _ticked = true;
            _lastTick = now;
            if (session?.Source == null || now < session.NextCheckAt)
            {
                return;
            }
            Check(session);
        }

        /// <summary>An observation of the current conversation (the caller checked the conversation ID).</summary>
        public void HandleObservation(NpcifySightObservation observation)
        {
            if (observation != null)
            {
                Invoke(() => Observation?.Invoke(observation));
            }
        }

        private Task Refuse(NpcifySightException error)
        {
            var refused = new TaskCompletionSource<bool>();
            refused.SetException(error);
            return refused.Task;
        }

        private void Opened(Session session, INpcifySightSource source, string failure, TaskCompletionSource<bool> result)
        {
            if (session != _session)
            {
                // Stopped while the source or the connection opened.
                source.Stop();
                result.TrySetResult(true);
                return;
            }
            if (failure != null)
            {
                source.Stop();
                _session = null;
                var error = new NpcifySightException(failure);
                Publish(error);
                result.TrySetException(error);
                return;
            }
            session.Source = source;
            Publish(null);
            result.TrySetResult(true);
            Check(session);
        }

        // One check: the end of the source, a hidden game, motion, then a due frame.
        private void Check(Session session)
        {
            var source = session.Source;
            if (session != _session || source == null)
            {
                return;
            }
            session.NextCheckAt = _now() + NpcifySightLimits.CheckMs;
            if (!source.Live)
            {
                Fail(session, NpcifySightCodes.SourceEnded);
                return;
            }
            if (_hidden)
            {
                session.Grid = null;
                return;
            }
            if (session.Threshold.HasValue && !session.ReadingGrid)
            {
                session.ReadingGrid = true;
                Guard(
                    () => source.ReadLuminance(grid => _post(() => GridRead(session, grid))),
                    () => GridRead(session, null));
                return;
            }
            SendWhenDue(session);
        }

        private void GridRead(Session session, byte[] grid)
        {
            session.ReadingGrid = false;
            if (session != _session)
            {
                return;
            }
            if (_hidden)
            {
                session.Grid = null;
                return;
            }
            if (grid != null && session.Grid != null && session.Threshold.HasValue &&
                NpcifySightImage.MeanLumaChange(session.Grid, grid) > session.Threshold.Value)
            {
                session.Motion = true;
            }
            session.Grid = grid;
            SendWhenDue(session);
        }

        // A frame is due at the interval, or after motion when one second passed since the last
        // frame. While a frame waits for its acknowledgement, a due frame waits too: the
        // acknowledgement sends it.
        private void SendWhenDue(Session session)
        {
            if (session != _session || session.InFlight || _hidden || session.Source == null)
            {
                return;
            }
            var elapsed = session.HasSent ? _now() - session.LastSentAt : long.MaxValue;
            if (elapsed >= session.IntervalMs || (session.Motion && elapsed >= NpcifySightLimits.MinFrameIntervalMs))
            {
                Send(session);
            }
        }

        private void Send(Session session)
        {
            var source = session.Source;
            session.InFlight = true;
            var capturedAt = _unixNow();
            Guard(
                () => source.Capture(frame => _post(() => Captured(session, capturedAt, frame))),
                () =>
                {
                    session.InFlight = false;
                    Fail(session, SourceFailure(source));
                });
        }

        private void Captured(Session session, long capturedAt, NpcifySightFrame frame)
        {
            if (frame == null || frame.Jpeg == null || frame.Jpeg.Length == 0 || session != _session)
            {
                // A source without a picture waits for the next check.
                session.InFlight = false;
                return;
            }
            var conversationId = _conversationId();
            if (string.IsNullOrEmpty(conversationId))
            {
                session.InFlight = false;
                Stop();
                return;
            }
            // Only a frame that went out lets its acknowledgement send the next one.
            session.HasSent = true;
            session.LastSentAt = _now();
            session.Motion = false;
            var request = new NpcifySightFrameRequest
            {
                ConversationId = conversationId,
                CapturedAt = capturedAt,
                Width = frame.Width,
                Height = frame.Height,
                Source = session.Source.Kind,
                Jpeg = frame.Jpeg,
            };
            if (session.Rejoin)
            {
                Guard(
                    () => _connection.Join(conversationId, code => _post(() => Rejoined(session, request, code))),
                    () => Rejoined(session, request, NpcifySightCodes.Disconnected));
                return;
            }
            Emit(session, request);
        }

        private void Rejoined(Session session, NpcifySightFrameRequest request, string code)
        {
            if (code != null)
            {
                Failed(session, code);
                Finish(session);
                return;
            }
            session.Rejoin = false;
            if (session != _session)
            {
                session.InFlight = false;
                return;
            }
            Emit(session, request);
        }

        private void Emit(Session session, NpcifySightFrameRequest request)
        {
            Guard(
                () => _connection.SendFrame(request, ack => _post(() => Acknowledged(session, ack))),
                () => Acknowledged(session, NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected)));
        }

        private void Acknowledged(Session session, NpcifySightAck ack)
        {
            ack = ack ?? NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected);
            if (ack.Answered)
            {
                // An accepted frame gives its observation as an event. Other codes
                // (SIGHT_RATE_LIMITED, SIGHT_PROVIDER_FAILED, ...) skip the frame.
                if (!ack.Ok && NpcifySightCodes.StopsSight(ack.Error))
                {
                    Fail(session, ack.Error);
                }
            }
            else
            {
                Failed(session, ack.Error);
            }
            Finish(session);
        }

        // A frame that did not reach the server or got no acknowledgement. A refused conversation
        // stops sight. A timeout skips the frame; a lost connection also joins the conversation
        // again before the next frame.
        private void Failed(Session session, string code)
        {
            if (NpcifySightCodes.StopsSight(code))
            {
                Fail(session, code);
                return;
            }
            if (code != NpcifySightCodes.Timeout)
            {
                session.Rejoin = true;
            }
        }

        private void Finish(Session session)
        {
            session.InFlight = false;
            SendWhenDue(session);
        }

        // Stops sight because of a code, and reports it.
        private void Fail(Session session, string code)
        {
            if (session != _session)
            {
                return;
            }
            End(session);
            if (code == NpcifySightCodes.Disabled)
            {
                _unavailable = NpcifySightCodes.OffMessage;
                DisabledByServer = true;
            }
            var error = new NpcifySightException(code);
            Publish(error);
            Invoke(() => Stopped?.Invoke(error));
        }

        private void End(Session session)
        {
            if (_session != session)
            {
                return;
            }
            _session = null;
            try
            {
                session.Source?.Stop();
            }
            catch (Exception error)
            {
                CallbackError?.Invoke(error);
            }
        }

        private void Publish(NpcifySightException error)
        {
            var session = _session;
            var next = session != null
                ? session.Source != null ? NpcifySightState.On : NpcifySightState.Starting
                : _unavailable != null ? NpcifySightState.Unavailable : NpcifySightState.Off;
            if (next == _state && error == null)
            {
                return;
            }
            _state = next;
            Invoke(() => StateChanged?.Invoke(next, error));
        }

        private static string SourceFailure(INpcifySightSource source)
        {
            return source?.Kind == "camera" ? NpcifySightCodes.CameraUnavailable : NpcifySightCodes.SourceUnavailable;
        }

        // Runs a call of the source or the connection; a throw runs the fallback.
        private void Guard(Action call, Action fallback)
        {
            try
            {
                call();
            }
            catch (Exception error)
            {
                CallbackError?.Invoke(error);
                fallback();
            }
        }

        private void Invoke(Action handler)
        {
            try
            {
                handler();
            }
            catch (Exception error)
            {
                CallbackError?.Invoke(error);
            }
        }
    }
}
