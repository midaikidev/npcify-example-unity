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
                _httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30),
                };
                _ownsHttpClient = true;
            }
        }

        public NpcifyOptions Options => _options;

        public string BaseUrl =>
            string.IsNullOrWhiteSpace(_options.BaseUrl)
                ? "https://api.npcify.ai"
                : _options.BaseUrl.TrimEnd('/');

        public async Task<NpcifyAssetInfo> GetInfoAsync(CancellationToken cancellationToken = default)
        {
            var url = $"{BaseUrl}/avatars/{Uri.EscapeDataString(_options.CharacterId)}/info";
            using (var request = CreateRequest(HttpMethod.Get, url, "application/json"))
            using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
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
            using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
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
            using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
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
            using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
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
            using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return NpcifyBakedConversation.FromBytes(bytes, _options.CharacterId);
            }
        }

        public async Task<ConversationCreationResult> CreateConversationAsync(
            string renderSessionId = null,
            CancellationToken cancellationToken = default
        )
        {
            var url = $"{BaseUrl}/conversations";
            var jsonBody = $"{{\"avatarId\":{EncodeJsonString(_options.CharacterId)}}}";

            using (var request = CreateJsonPostRequest(url, jsonBody, NpcifyPerformanceCodec.MediaType, null, renderSessionId))
            using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return NpcifySchemaMapper.DecodeConversation(responseBytes);
            }
        }

        public async Task<StagingResultPayload> CreateStagingAsync(
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
                $"{{\"content\":{EncodeJsonString(message)},\"silent\":{(silent ? "true" : "false")},\"incrementalPlayback\":true,\"parentStagingId\":{parentJson}}}";

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
                    using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
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
            using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
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
                BakedConversationEnabled = ExtractJsonBoolField(json, "bakedConversationEnabled"),
                BakedConversationRevision = ExtractJsonIntField(json, "bakedConversationRevision"),
            };
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
