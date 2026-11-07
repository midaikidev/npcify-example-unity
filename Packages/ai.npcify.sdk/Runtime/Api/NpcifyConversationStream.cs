using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;
using Npcify.Sdk.Protocol;

namespace Npcify.Sdk.Api
{
    /// <summary>One complete WebSocket message.</summary>
    public sealed class NpcifySocketMessage
    {
        public bool IsBinary;
        public string Text;
        public byte[] Data;
    }

    /// <summary>The WebSocket under <see cref="NpcifyConversationStream"/>; tests replace it.</summary>
    public interface INpcifySocketTransport : IDisposable
    {
        Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

        Task SendTextAsync(string text, CancellationToken cancellationToken);

        /// <summary>One complete message, or null when the connection closed.</summary>
        Task<NpcifySocketMessage> ReceiveAsync(CancellationToken cancellationToken);

        void Abort();
    }

    /// <summary>The default transport: <see cref="ClientWebSocket"/> (not available on WebGL).</summary>
    public sealed class NpcifyClientWebSocketTransport : INpcifySocketTransport
    {
        private const int MaxMessageBytes = 16 * 1024 * 1024;
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly byte[] _buffer = new byte[64 * 1024];

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            _socket.Options.KeepAliveInterval = TimeSpan.Zero;
            return _socket.ConnectAsync(uri, cancellationToken);
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            return _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        public async Task<NpcifySocketMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            using (var stream = new MemoryStream())
            {
                while (true)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(_buffer), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }
                    stream.Write(_buffer, 0, result.Count);
                    if (stream.Length > MaxMessageBytes)
                    {
                        throw new InvalidDataException("The conversation stream sent a message that is too large.");
                    }
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }
                    var data = stream.ToArray();
                    return result.MessageType == WebSocketMessageType.Binary
                        ? new NpcifySocketMessage { IsBinary = true, Data = data }
                        : new NpcifySocketMessage { Text = System.Text.Encoding.UTF8.GetString(data) };
                }
            }
        }

        public void Abort()
        {
            _socket.Abort();
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }

    /// <summary>
    /// The conversation stream of the server (<c>conversations:stream</c> over Socket.IO): the
    /// SDK joins the room of its conversation before a turn, so that live motion frames reach it
    /// while the server generates the turn. It reconnects after a lost connection and joins the
    /// conversation again. Events arrive on a background thread.
    /// </summary>
    public sealed class NpcifyConversationStream : IDisposable
    {
        private const int TimeoutMs = 10_000;

        private sealed class Connection
        {
            public INpcifySocketTransport Transport;
            public readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
            public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
            public readonly TaskCompletionSource<bool> Connected =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly Dictionary<long, TaskCompletionSource<string>> Acks = new Dictionary<long, TaskCompletionSource<string>>();
            public long NextAckId;
            public long LastReceivedTicks = DateTime.UtcNow.Ticks;
            public long PingTimeoutMs = 45_000;
            public bool Closed;
            public string BinaryEvent;
            public int BinaryExpected;
            public readonly List<byte[]> BinaryParts = new List<byte[]>();
        }

        private readonly object _gate = new object();
        private readonly NpcifyOptions _options;
        private readonly Func<INpcifySocketTransport> _transportFactory;
        private Connection _connection;
        private Task<Connection> _connecting;
        private string _desiredConversationId;
        private string _joinedConversationId;
        private bool _disposed;
        private bool _reconnecting;

        public NpcifyConversationStream(NpcifyOptions options, Func<INpcifySocketTransport> transportFactory = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transportFactory = transportFactory ?? (() => new NpcifyClientWebSocketTransport());
        }

        /// <summary>A stream event of the joined conversation: its ID and the decoded event map.</summary>
        public event Action<string, Dictionary<string, object>> StreamEvent;

        /// <summary>Connection notes for diagnostics.</summary>
        public event Action<string> Log;

        public string JoinedConversationId
        {
            get
            {
                lock (_gate)
                {
                    return _joinedConversationId;
                }
            }
        }

        public bool IsJoined(string conversationId)
        {
            lock (_gate)
            {
                return conversationId != null && _joinedConversationId == conversationId && _connection != null && !_connection.Closed;
            }
        }

        /// <summary>The Socket.IO URL of an API base URL (its origin; the path is not used).</summary>
        public static Uri BuildUri(string baseUrl)
        {
            var api = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? "https://api.npcify.ai" : baseUrl);
            var scheme = api.Scheme == Uri.UriSchemeHttps || api.Scheme == "wss" ? "wss" : "ws";
            var builder = new UriBuilder(scheme, api.Host, api.Port, "/socket.io/") { Query = "EIO=4&transport=websocket" };
            return builder.Uri;
        }

        /// <summary>
        /// Joins the room of a conversation; connects first when needed. Returns false when the
        /// connection or the join fails; the turn then plays prepared clips.
        /// </summary>
        public async Task<bool> JoinAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return false;
            }
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }
                _desiredConversationId = conversationId;
                if (_joinedConversationId == conversationId && _connection != null && !_connection.Closed)
                {
                    return true;
                }
            }
            try
            {
                var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (connection == null)
                {
                    return false;
                }
                var reply = await RequestAsync(
                    connection,
                    "conversations:join",
                    "{\"conversationId\":" + NpcifySocketIo.JsonString(conversationId) + "}",
                    cancellationToken).ConfigureAwait(false);
                if (reply == null || !NpcifySocketIo.ReadTrue(reply, "ok"))
                {
                    Log?.Invoke("The conversation stream refused the join.");
                    return false;
                }
                lock (_gate)
                {
                    if (_desiredConversationId != conversationId || connection.Closed)
                    {
                        return false;
                    }
                    _joinedConversationId = conversationId;
                }
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Log?.Invoke("The conversation stream failed: " + error.Message);
                return false;
            }
        }

        /// <summary>Leaves the current conversation; the connection stays open for the next one.</summary>
        public void Leave()
        {
            Connection connection;
            string left;
            lock (_gate)
            {
                left = _joinedConversationId ?? _desiredConversationId;
                _desiredConversationId = null;
                _joinedConversationId = null;
                connection = _connection;
            }
            if (left != null && connection != null && !connection.Closed)
            {
                _ = SendAsync(connection, NpcifySocketIo.Event(
                    "conversations:leave",
                    "{\"conversationId\":" + NpcifySocketIo.JsonString(left) + "}"), CancellationToken.None);
            }
        }

        public void Dispose()
        {
            Connection connection;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _desiredConversationId = null;
                _joinedConversationId = null;
                connection = _connection;
                _connection = null;
            }
            if (connection != null)
            {
                Close(connection);
            }
        }

        private Task<Connection> ConnectAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_connection != null && !_connection.Closed)
                {
                    return Task.FromResult(_connection);
                }
                if (_connecting == null || _connecting.IsCompleted)
                {
                    _connecting = OpenAsync();
                }
                return WithTimeout(_connecting, Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        private async Task<Connection> OpenAsync()
        {
            var connection = new Connection { Transport = _transportFactory() };
            try
            {
                using (var timeout = new CancellationTokenSource(TimeoutMs))
                {
                    await connection.Transport.ConnectAsync(BuildUri(_options.BaseUrl), timeout.Token).ConfigureAwait(false);
                }
                _ = Task.Run(() => ReceiveLoopAsync(connection));
                _ = Task.Run(() => WatchdogAsync(connection));
                var connected = await WithTimeout(connection.Connected.Task, TimeSpan.FromMilliseconds(TimeoutMs), CancellationToken.None).ConfigureAwait(false);
                if (!connected)
                {
                    Close(connection);
                    return null;
                }
                lock (_gate)
                {
                    if (_disposed)
                    {
                        Close(connection);
                        return null;
                    }
                    _connection = connection;
                }
                return connection;
            }
            catch (Exception error)
            {
                Log?.Invoke("The conversation stream could not connect: " + error.Message);
                Close(connection);
                return null;
            }
        }

        private async Task<string> RequestAsync(Connection connection, string eventName, string json, CancellationToken cancellationToken)
        {
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            long ackId;
            lock (connection.Acks)
            {
                ackId = connection.NextAckId++;
                connection.Acks[ackId] = reply;
            }
            try
            {
                await SendAsync(connection, NpcifySocketIo.Event(eventName, json, ackId), cancellationToken).ConfigureAwait(false);
                return await WithTimeout(reply.Task, TimeSpan.FromMilliseconds(TimeoutMs), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                lock (connection.Acks)
                {
                    connection.Acks.Remove(ackId);
                }
            }
        }

        private static async Task SendAsync(Connection connection, string text, CancellationToken cancellationToken)
        {
            await connection.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!connection.Closed)
                {
                    await connection.Transport.SendTextAsync(text, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                connection.SendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(Connection connection)
        {
            try
            {
                while (!connection.Lifetime.IsCancellationRequested)
                {
                    var message = await connection.Transport.ReceiveAsync(connection.Lifetime.Token).ConfigureAwait(false);
                    if (message == null)
                    {
                        break;
                    }
                    Interlocked.Exchange(ref connection.LastReceivedTicks, DateTime.UtcNow.Ticks);
                    if (message.IsBinary)
                    {
                        ReceiveAttachment(connection, message.Data);
                        continue;
                    }
                    if (!await HandleTextAsync(connection, message.Text).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (Exception error) when (!(error is OperationCanceledException && connection.Lifetime.IsCancellationRequested))
            {
                Log?.Invoke("The conversation stream closed: " + error.Message);
            }
            catch (OperationCanceledException)
            {
                // Closed by the SDK.
            }
            Close(connection);
            ScheduleReconnect(connection);
        }

        private async Task<bool> HandleTextAsync(Connection connection, string text)
        {
            var packet = NpcifySocketIo.Parse(text);
            switch (packet.EngineType)
            {
                case 0:
                {
                    var interval = NpcifySocketIo.ReadNumber(packet.Json, "pingInterval", 25_000);
                    var timeout = NpcifySocketIo.ReadNumber(packet.Json, "pingTimeout", 20_000);
                    connection.PingTimeoutMs = Math.Max(5_000, interval + timeout);
                    await SendAsync(connection, NpcifySocketIo.Connect(_options.ResolvedToken), connection.Lifetime.Token).ConfigureAwait(false);
                    return true;
                }
                case 1:
                    return false;
                case 2:
                    await SendAsync(connection, "3", connection.Lifetime.Token).ConfigureAwait(false);
                    return true;
                case 4:
                    break;
                default:
                    return true;
            }
            if (packet.Namespace != "/")
            {
                return true;
            }
            switch (packet.SocketType)
            {
                case 0:
                    connection.Connected.TrySetResult(true);
                    return true;
                case 1:
                    return false;
                case 3:
                    TaskCompletionSource<string> ack = null;
                    lock (connection.Acks)
                    {
                        if (packet.AckId >= 0 && connection.Acks.TryGetValue(packet.AckId, out ack))
                        {
                            connection.Acks.Remove(packet.AckId);
                        }
                    }
                    ack?.TrySetResult(packet.Json);
                    return true;
                case 4:
                    Log?.Invoke("The conversation stream refused the connection: " +
                                (NpcifySocketIo.ReadStringField(packet.Json, "message") ?? "no reason"));
                    connection.Connected.TrySetResult(false);
                    return false;
                case 2:
                    if (NpcifySocketIo.ReadEventName(packet.Json) == "exception")
                    {
                        Log?.Invoke("The conversation stream reported: " + packet.Json);
                    }
                    return true;
                case 5:
                case 6:
                    connection.BinaryEvent = packet.SocketType == 5 ? NpcifySocketIo.ReadEventName(packet.Json) : null;
                    connection.BinaryExpected = packet.Attachments;
                    connection.BinaryParts.Clear();
                    if (packet.Attachments == 0)
                    {
                        DispatchBinary(connection);
                    }
                    return true;
                default:
                    return true;
            }
        }

        private void ReceiveAttachment(Connection connection, byte[] data)
        {
            if (connection.BinaryExpected <= connection.BinaryParts.Count)
            {
                return;
            }
            connection.BinaryParts.Add(data);
            if (connection.BinaryParts.Count == connection.BinaryExpected)
            {
                DispatchBinary(connection);
            }
        }

        private void DispatchBinary(Connection connection)
        {
            var name = connection.BinaryEvent;
            var parts = connection.BinaryParts.ToArray();
            connection.BinaryEvent = null;
            connection.BinaryExpected = 0;
            connection.BinaryParts.Clear();
            if (name != "conversations:stream" || parts.Length != 1)
            {
                return;
            }
            try
            {
                var decoded = NpcifyPerformanceCodec.Decode(parts[0], NpcifyPerformanceKind.Stream) as Dictionary<string, object>;
                if (decoded == null ||
                    !(decoded.TryGetValue("conversationId", out var idValue) && idValue is string conversationId) ||
                    !(decoded.TryGetValue("event", out var eventValue) && eventValue is Dictionary<string, object> streamEvent))
                {
                    return;
                }
                lock (_gate)
                {
                    if (conversationId != _joinedConversationId && conversationId != _desiredConversationId)
                    {
                        return;
                    }
                }
                StreamEvent?.Invoke(conversationId, streamEvent);
            }
            catch (Exception error)
            {
                Log?.Invoke("A conversation stream event was dropped: " + error.Message);
            }
        }

        private async Task WatchdogAsync(Connection connection)
        {
            try
            {
                while (!connection.Lifetime.IsCancellationRequested)
                {
                    await Task.Delay(1_000, connection.Lifetime.Token).ConfigureAwait(false);
                    var silentMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref connection.LastReceivedTicks)) / TimeSpan.TicksPerMillisecond;
                    if (silentMs > connection.PingTimeoutMs)
                    {
                        Log?.Invoke("The conversation stream timed out.");
                        connection.Transport.Abort();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Closed.
            }
        }

        private void Close(Connection connection)
        {
            List<TaskCompletionSource<string>> pending;
            lock (_gate)
            {
                if (connection.Closed)
                {
                    return;
                }
                connection.Closed = true;
                if (_connection == connection)
                {
                    _connection = null;
                    _joinedConversationId = null;
                }
            }
            lock (connection.Acks)
            {
                pending = new List<TaskCompletionSource<string>>(connection.Acks.Values);
                connection.Acks.Clear();
            }
            foreach (var ack in pending)
            {
                ack.TrySetResult(null);
            }
            connection.Connected.TrySetResult(false);
            connection.Lifetime.Cancel();
            try
            {
                connection.Transport.Abort();
                connection.Transport.Dispose();
            }
            catch (Exception)
            {
                // The transport is gone already.
            }
        }

        private void ScheduleReconnect(Connection connection)
        {
            string conversationId;
            lock (_gate)
            {
                if (_disposed || _reconnecting || _desiredConversationId == null ||
                    (_connection != null && _connection != connection))
                {
                    return;
                }
                _reconnecting = true;
                conversationId = _desiredConversationId;
            }
            _ = Task.Run(() => ReconnectAsync(conversationId));
        }

        private async Task ReconnectAsync(string conversationId)
        {
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    await Task.Delay(Math.Min(10_000, 1_000 << Math.Min(attempt, 4))).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_disposed || _desiredConversationId != conversationId)
                        {
                            return;
                        }
                    }
                    if (await JoinAsync(conversationId).ConfigureAwait(false))
                    {
                        Log?.Invoke("The conversation stream reconnected.");
                        return;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _reconnecting = false;
                }
            }
        }

        /// <summary>The result of a task, or a TimeoutException after the timeout.</summary>
        private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }
            using (var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(timeout, delayCancel.Token);
                var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (finished == task)
                {
                    delayCancel.Cancel();
                    return await task.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
        }
    }
}
