using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Protocol;
using Npcify.Sdk.Sight;

namespace Npcify.Sdk.Api
{
    /// <summary>Sight on the conversation stream (docs/sight.md, Transport).</summary>
    public sealed partial class NpcifyConversationStream
    {
        private const string SightFrameEvent = "conversations:sight";
        private const string SightObservationEvent = "sight_observation";

        /// <summary>A sight_observation event of any conversation of this connection (on a background thread).</summary>
        public event Action<NpcifySightObservation> SightObservation;

        /// <summary>
        /// Sends one frame with conversations:sight on the connection that joined its conversation:
        /// a binary Socket.IO event (the JSON body with a placeholder, then the JPEG as an
        /// attachment). Gives the acknowledgement of the server, CONVERSATION_TIMEOUT after the
        /// timeout, or CONVERSATION_DISCONNECTED when the conversation is not joined or the
        /// connection closed before the answer.
        /// </summary>
        public async Task<NpcifySightAck> SendSightFrameAsync(
            NpcifySightFrameRequest request,
            int timeoutMs = NpcifySightLimits.AckTimeoutMs,
            CancellationToken cancellationToken = default)
        {
            if (request == null || request.Jpeg == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            var conversationId = Normalize(request.ConversationId);
            Connection connection;
            lock (_gate)
            {
                connection = _connection;
                if (_disposed || conversationId == null || connection == null || connection.Closed ||
                    _joinedConversationId != conversationId)
                {
                    return NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected);
                }
            }
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            long ackId;
            lock (connection.Acks)
            {
                ackId = connection.NextAckId++;
                connection.Acks[ackId] = reply;
            }
            try
            {
                var header = "451-" + ackId.ToString(CultureInfo.InvariantCulture) +
                             "[" + NpcifySocketIo.JsonString(SightFrameEvent) + "," + request.ToJson() + "]";
                if (!await SendBinaryEventAsync(connection, header, request.Jpeg, cancellationToken).ConfigureAwait(false))
                {
                    return NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected);
                }
                string json;
                try
                {
                    json = await WithTimeout(reply.Task, TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return NpcifySightAck.NotAnswered(NpcifySightCodes.Timeout);
                }
                // Close completes the waiting acknowledgements with null.
                return json == null ? NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected) : NpcifySightAck.FromAckJson(json);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Log?.Invoke("A sight frame did not go out: " + error.Message);
                return NpcifySightAck.NotAnswered(NpcifySightCodes.Disconnected);
            }
            finally
            {
                lock (connection.Acks)
                {
                    connection.Acks.Remove(ackId);
                }
            }
        }

        // The header and its attachment go out together: the server reads the next binary message
        // as the attachment of the header.
        private static async Task<bool> SendBinaryEventAsync(Connection connection, string header, byte[] attachment, CancellationToken cancellationToken)
        {
            await connection.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (connection.Closed)
                {
                    return false;
                }
                await connection.Transport.SendTextAsync(header, cancellationToken).ConfigureAwait(false);
                await connection.Transport.SendBinaryAsync(attachment, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                connection.SendLock.Release();
            }
        }

        private void DispatchSightObservation(string json)
        {
            var arguments = NpcifyJson.TryParse(json) as List<object>;
            var observation = arguments != null && arguments.Count > 1
                ? NpcifySightObservation.FromMap(arguments[1] as Dictionary<string, object>)
                : null;
            if (observation == null)
            {
                Log?.Invoke("A sight observation without its fields was dropped.");
                return;
            }
            try
            {
                SightObservation?.Invoke(observation);
            }
            catch (Exception error)
            {
                Log?.Invoke("A sight observation handler failed: " + error.Message);
            }
        }
    }
}
