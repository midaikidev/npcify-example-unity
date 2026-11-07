using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Conversation;
using Npcify.Sdk.Models;
using Npcify.Sdk.Protocol;
using Npcify.Sdk.Speech;

namespace Npcify.Sdk.Api
{
    public sealed class NpcifyApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string ResponseBody { get; }

        public NpcifyApiException(HttpStatusCode statusCode, string message, string responseBody = null)
            : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }

    public sealed class NpcifyApiClient : IDisposable
    {
        private readonly NpcifyOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        public NpcifyApiClient(NpcifyOptions options, HttpClient httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.CharacterId))
            {
                throw new ArgumentException("CharacterId is required in NpcifyOptions.", nameof(options));
            }

            if (httpClient != null)
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }
            else
            {
                // Each request has its own limit: RequestTimeout, or TurnTimeout for a turn.
                _httpClient = new HttpClient
                {
                    Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                };
                _ownsHttpClient = true;
            }
        }

        public NpcifyOptions Options => _options;

        /// <summary>
        /// The longest time that a request waits for its answer, except a turn request. An
        /// HttpClient of the caller keeps its own timeout.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The longest time that a turn request (CreateStagingAsync) waits for its answer. The
        /// server answers after it aligned the speech of the reply, so about as long as the speech
        /// lasts; a streamed reply plays before the answer.
        /// </summary>
        public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromSeconds(120);

        public string BaseUrl =>
            string.IsNullOrWhiteSpace(_options.BaseUrl)
                ? "https://api.npcify.ai"
                : _options.BaseUrl.TrimEnd('/');

        public async Task<NpcifyAssetInfo> GetInfoAsync(CancellationToken cancellationToken = default)
        {
            var url = $"{BaseUrl}/avatars/{Uri.EscapeDataString(_options.CharacterId)}/info";
            using (var request = CreateRequest(HttpMethod.Get, url, "application/json"))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseAssetInfoJson(json);
            }
        }

        public async Task<string> GetVrmDownloadUrlAsync(
            string assetProfile = null,
            CancellationToken cancellationToken = default
        )
        {
            var url = $"{BaseUrl}/avatars/{Uri.EscapeDataString(_options.CharacterId)}/download-url";
            if (!string.IsNullOrWhiteSpace(assetProfile) && !string.Equals(assetProfile, "original", StringComparison.Ordinal))
            {
                url += "?assetProfile=" + Uri.EscapeDataString(assetProfile);
            }

            using (var request = CreateRequest(HttpMethod.Get, url, "application/json"))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ExtractJsonStringField(json, "url");
            }
        }

        public async Task<string> GetVrmaDownloadUrlAsync(
            string animationId,
            string assetProfile = null,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(animationId))
            {
                throw new ArgumentException("Animation ID is required.", nameof(animationId));
            }

            var url =
                $"{BaseUrl}/avatars/{Uri.EscapeDataString(_options.CharacterId)}/animations/{Uri.EscapeDataString(animationId)}/download-url";
            if (!string.IsNullOrWhiteSpace(assetProfile) && !string.Equals(assetProfile, "original", StringComparison.Ordinal))
            {
                url += "?assetProfile=" + Uri.EscapeDataString(assetProfile);
            }

            using (var request = CreateRequest(HttpMethod.Get, url, "application/json"))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ExtractJsonStringField(json, "url");
            }
        }

        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("Download URL is required.", nameof(url));
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        public async Task<NpcifyBakedConversation> GetBakedConversationAsync(
            string overrideUrl = null,
            CancellationToken cancellationToken = default
        )
        {
            if (_options.BakedConversationAsset != null)
            {
                return new NpcifyBakedConversation(_options.BakedConversationAsset, _options.CharacterId);
            }
            if (_options.BakedConversationBytes != null && _options.BakedConversationBytes.Length > 0)
            {
                return NpcifyBakedConversation.FromBytes(_options.BakedConversationBytes, _options.CharacterId);
            }

            var url = !string.IsNullOrWhiteSpace(overrideUrl)
                ? overrideUrl
                : $"{BaseUrl}/avatars/{Uri.EscapeDataString(_options.CharacterId)}/baked-conversation/asset";

            using (var request = CreateRequest(HttpMethod.Get, url, NpcifyPerformanceCodec.MediaType))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return NpcifyBakedConversation.FromBytes(bytes, _options.CharacterId);
            }
        }

        public Task<ConversationCreationResult> CreateConversationAsync(
            string renderSessionId = null,
            CancellationToken cancellationToken = default
        )
        {
            return CreateConversationAsync(renderSessionId, null, cancellationToken);
        }

        /// <summary>
        /// Creates a conversation. <paramref name="capabilities"/> declares what this client plays,
        /// for example <c>motion_frames</c> (live motion, docs/realtime-motion.md).
        /// </summary>
        public async Task<ConversationCreationResult> CreateConversationAsync(
            string renderSessionId,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken = default
        )
        {
            var url = $"{BaseUrl}/conversations";
            var jsonBody = $"{{\"avatarId\":{EncodeJsonString(_options.CharacterId)}{EncodeCapabilities(capabilities)}}}";

            using (var request = CreateJsonPostRequest(url, jsonBody, NpcifyPerformanceCodec.MediaType, null, renderSessionId))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return NpcifySchemaMapper.DecodeConversation(responseBytes);
            }
        }

        public Task<StagingResultPayload> CreateStagingAsync(
            string conversationId,
            string message,
            string parentStagingId = null,
            bool silent = false,
            string requestId = null,
            string renderSessionId = null,
            int maxRetries = 6,
            CancellationToken cancellationToken = default
        )
        {
            return CreateStagingAsync(
                conversationId,
                message,
                parentStagingId,
                silent,
                requestId,
                renderSessionId,
                maxRetries,
                null,
                cancellationToken);
        }

        /// <summary>
        /// Requests a turn. <paramref name="capabilities"/> declares what this client plays for
        /// this turn, for example <c>motion_frames</c>; without it the turn gets prepared clips.
        /// </summary>
        public async Task<StagingResultPayload> CreateStagingAsync(
            string conversationId,
            string message,
            string parentStagingId,
            bool silent,
            string requestId,
            string renderSessionId,
            int maxRetries,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                throw new ArgumentException("Conversation ID is required.", nameof(conversationId));
            }
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Message is required.", nameof(message));
            }

            var url = $"{BaseUrl}/conversations/{Uri.EscapeDataString(conversationId)}/stagings";
            // The server requires a hyphenated UUID.
            var idempotencyKey = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("D") : requestId;
            var parentJson = string.IsNullOrWhiteSpace(parentStagingId)
                ? "null"
                : EncodeJsonString(parentStagingId);
            var jsonBody =
                $"{{\"content\":{EncodeJsonString(message)},\"silent\":{(silent ? "true" : "false")},\"incrementalPlayback\":true,\"parentStagingId\":{parentJson}{EncodeCapabilities(capabilities)}}}";

            int[] retryDelaysMs = { 0, 150, 300, 600, 1200, 2000, 2000 };
            Exception lastError = null;
            var totalAttempts = Math.Max(1, Math.Min(retryDelaysMs.Length, maxRetries + 1));

            for (var attempt = 0; attempt < totalAttempts; attempt++)
            {
                var delayMs = retryDelaysMs[attempt];
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    using (var request = CreateJsonPostRequest(
                        url,
                        jsonBody,
                        NpcifyPerformanceCodec.MediaType,
                        idempotencyKey,
                        renderSessionId))
                    using (var response = await SendAsync(request, TurnTimeout, cancellationToken).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            return NpcifySchemaMapper.DecodeStaging(responseBytes);
                        }

                        if (!ShouldRetryStaging(response.StatusCode) || attempt == totalAttempts - 1)
                        {
                            await EnsureSuccessAsync(response).ConfigureAwait(false);
                        }
                    }
                }
                catch (NpcifyApiException ex)
                {
                    lastError = ex;
                    if (!ShouldRetryStaging(ex.StatusCode) || attempt == totalAttempts - 1)
                    {
                        throw;
                    }
                }
            }

            throw lastError ?? new InvalidOperationException("Failed to create staging.");
        }

        /// <summary>
        /// Recognises one recording (docs/voice-input.md): a WAV file of 16-bit mono PCM at
        /// 16 kHz, at most 30 seconds. It starts no turn; send the text with CreateStagingAsync.
        /// </summary>
        public async Task<NpcifySpeechTranscript> RecognizeSpeechAsync(
            string conversationId,
            byte[] wav,
            string language = null,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                throw new ArgumentException("Conversation ID is required.", nameof(conversationId));
            }
            if (wav == null || wav.Length <= 44)
            {
                throw new ArgumentException("The recording is empty.", nameof(wav));
            }

            var url = $"{BaseUrl}/conversations/{Uri.EscapeDataString(conversationId)}/speech";
            if (!string.IsNullOrWhiteSpace(language))
            {
                url += "?language=" + Uri.EscapeDataString(language.Trim());
            }
            using (var request = CreateRequest(HttpMethod.Post, url, "application/json"))
            {
                request.Content = new ByteArrayContent(wav);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
                {
                    await EnsureSuccessAsync(response).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new NpcifySpeechTranscript
                    {
                        Text = ExtractJsonStringField(json, "text") ?? string.Empty,
                        Language = ExtractJsonStringField(json, "language"),
                        DurationMs = ExtractJsonIntField(json, "durationMs"),
                    };
                }
            }
        }

        /// <summary>
        /// Warms up the live motion session of a conversation on the player's intent (docs/realtime-motion.md):
        /// call it when the player starts to type or presses to talk. The server does nothing for a
        /// character without live motion and limits warm-ups per player and network. Returns true when
        /// a session warms up; a failure returns false and costs the turn nothing.
        /// </summary>
        public async Task<bool> WarmUpLiveMotionAsync(
            string conversationId,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return false;
            }
            var url = $"{BaseUrl}/conversations/{Uri.EscapeDataString(conversationId)}/motion/warm-up";
            var jsonBody = "{\"capabilities\":[\"" + Npcify.Sdk.Motion.NpcifyMotionFrames.Capability + "\"]}";
            try
            {
                using (var request = CreateJsonPostRequest(url, jsonBody, "application/json"))
                using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ExtractJsonBoolField(json, "warming");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<StagingResultPayload> GetStagingAsync(
            string conversationId,
            string stagingId,
            string renderSessionId = null,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                throw new ArgumentException("Conversation ID is required.", nameof(conversationId));
            }
            if (string.IsNullOrWhiteSpace(stagingId))
            {
                throw new ArgumentException("Staging ID is required.", nameof(stagingId));
            }

            var url =
                $"{BaseUrl}/conversations/{Uri.EscapeDataString(conversationId)}/stagings/{Uri.EscapeDataString(stagingId)}";
            using (var request = CreateRequest(HttpMethod.Get, url, NpcifyPerformanceCodec.MediaType, renderSessionId))
            using (var response = await SendAsync(request, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return NpcifySchemaMapper.DecodeStaging(responseBytes);
            }
        }

        private HttpRequestMessage CreateRequest(
            HttpMethod method,
            string url,
            string acceptMediaType,
            string renderSessionId = null
        )
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptMediaType));

            var token = _options.ResolvedToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("X-API-Token", token);
            }

            if (!string.IsNullOrWhiteSpace(renderSessionId))
            {
                request.Headers.TryAddWithoutValidation("X-Render-Session-Id", renderSessionId.Trim());
            }

            return request;
        }

        private HttpRequestMessage CreateJsonPostRequest(
            string url,
            string jsonBody,
            string acceptMediaType,
            string idempotencyKey = null,
            string renderSessionId = null
        )
        {
            var request = CreateRequest(HttpMethod.Post, url, acceptMediaType, renderSessionId);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            if (!_ownsHttpClient)
            {
                return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limit.CancelAfter(timeout ?? RequestTimeout);
                return await _httpClient.SendAsync(request, limit.Token).ConfigureAwait(false);
            }
        }

        private static bool ShouldRetryStaging(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 409 || code == 502 || code == 503 || code == 504;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            string body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore read failure on error payload
            }

            var serverMessage = ExtractJsonStringField(body, "message");
            var message = $"Npcify API request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).";
            if (!string.IsNullOrWhiteSpace(serverMessage))
            {
                message += " " + serverMessage;
            }
            throw new NpcifyApiException(response.StatusCode, message, body);
        }

        internal static NpcifyAssetInfo ParseAssetInfoJson(string json)
        {
            return new NpcifyAssetInfo
            {
                // The character name precedes the nested animation names in the info payload.
                Name = ExtractJsonStringField(json, "name"),
                IdleAnimationId = ExtractJsonStringField(json, "idleAnimationId"),
                AllowCustomReply = ExtractJsonBoolField(json, "allowCustomReply"),
                SpeechInputEnabled = ExtractJsonBoolField(json, "speechInputEnabled"),
                BakedConversationEnabled = ExtractJsonBoolField(json, "bakedConversationEnabled"),
                BakedConversationRevision = ExtractJsonIntField(json, "bakedConversationRevision"),
                RealtimeMotionEnabled = ExtractJsonBoolField(json, "realtimeMotionEnabled"),
            };
        }

        /// <summary>The capabilities field: none for null, an empty list for an empty one.</summary>
        private static string EncodeCapabilities(IReadOnlyList<string> capabilities)
        {
            if (capabilities == null)
            {
                return string.Empty;
            }
            var items = new List<string>(capabilities.Count);
            foreach (var capability in capabilities)
            {
                if (!string.IsNullOrWhiteSpace(capability))
                {
                    items.Add(EncodeJsonString(capability));
                }
            }
            return ",\"capabilities\":[" + string.Join(",", items) + "]";
        }

        private static string EncodeJsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string ExtractJsonStringField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var token = "\"" + fieldName + "\"";
            var idx = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }

            var colonIdx = json.IndexOf(':', idx + token.Length);
            if (colonIdx < 0)
            {
                return null;
            }

            var i = colonIdx + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            if (i >= json.Length || json[i] != '"')
            {
                return null;
            }

            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                var c = json[i++];
                if (c == '\\' && i < json.Length)
                {
                    var esc = json[i++];
                    switch (esc)
                    {
                        case '"':
                            sb.Append('"');
                            break;
                        case '\\':
                            sb.Append('\\');
                            break;
                        case '/':
                            sb.Append('/');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        default:
                            sb.Append(esc);
                            break;
                    }
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }

            return null;
        }

        private static bool ExtractJsonBoolField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            var token = "\"" + fieldName + "\"";
            var idx = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            var colonIdx = json.IndexOf(':', idx + token.Length);
            if (colonIdx < 0)
            {
                return false;
            }

            var i = colonIdx + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            return i + 4 <= json.Length &&
                   string.Compare(json, i, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static int ExtractJsonIntField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json))
            {
                return 0;
            }

            var token = "\"" + fieldName + "\"";
            var idx = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return 0;
            }

            var colonIdx = json.IndexOf(':', idx + token.Length);
            if (colonIdx < 0)
            {
                return 0;
            }

            var i = colonIdx + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            var start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-'))
            {
                i++;
            }

            if (i > start && int.TryParse(json.Substring(start, i - start), out var result))
            {
                return result;
            }
            return 0;
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
