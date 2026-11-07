using System;
using System.Globalization;
using System.Text;

namespace Npcify.Sdk.Api
{
    /// <summary>One Engine.IO v4 text packet, with its Socket.IO v5 packet when it is a message.</summary>
    public struct NpcifySocketIoPacket
    {
        /// <summary>Engine.IO type: 0 open, 1 close, 2 ping, 3 pong, 4 message, 6 noop; -1 invalid.</summary>
        public int EngineType;
        /// <summary>Socket.IO type: 0 connect, 1 disconnect, 2 event, 3 ack, 4 connect error, 5 binary event, 6 binary ack; -1 none.</summary>
        public int SocketType;
        public int Attachments;
        public string Namespace;
        public long AckId;
        public string Json;
    }

    /// <summary>
    /// Parses and builds the text packets of the Socket.IO protocol (v5 over Engine.IO v4 on a
    /// WebSocket). Binary attachments follow a binary packet as binary WebSocket messages.
    /// </summary>
    public static class NpcifySocketIo
    {
        public static NpcifySocketIoPacket Parse(string text)
        {
            var packet = new NpcifySocketIoPacket { EngineType = -1, SocketType = -1, AckId = -1, Namespace = "/", Json = string.Empty };
            if (string.IsNullOrEmpty(text) || text[0] < '0' || text[0] > '6')
            {
                return packet;
            }
            packet.EngineType = text[0] - '0';
            if (packet.EngineType != 4)
            {
                packet.Json = text.Substring(1);
                return packet;
            }
            if (text.Length < 2 || text[1] < '0' || text[1] > '6')
            {
                packet.EngineType = -1;
                return packet;
            }
            packet.SocketType = text[1] - '0';
            var index = 2;
            if (packet.SocketType == 5 || packet.SocketType == 6)
            {
                var start = index;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }
                if (index == start || index >= text.Length || text[index] != '-' ||
                    !int.TryParse(text.Substring(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out packet.Attachments))
                {
                    packet.EngineType = -1;
                    return packet;
                }
                index++;
            }
            if (index < text.Length && text[index] == '/')
            {
                var comma = text.IndexOf(',', index);
                if (comma < 0)
                {
                    packet.Namespace = text.Substring(index);
                    return packet;
                }
                packet.Namespace = text.Substring(index, comma - index);
                index = comma + 1;
            }
            var idStart = index;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }
            if (index > idStart && long.TryParse(text.Substring(idStart, index - idStart), NumberStyles.None, CultureInfo.InvariantCulture, out var ackId))
            {
                packet.AckId = ackId;
            }
            packet.Json = text.Substring(index);
            return packet;
        }

        /// <summary>The CONNECT packet of the main namespace, with the API key as auth when there is one.</summary>
        public static string Connect(string apiToken)
        {
            return string.IsNullOrWhiteSpace(apiToken) ? "40" : "40{\"apiToken\":" + JsonString(apiToken.Trim()) + "}";
        }

        /// <summary>An EVENT packet with one JSON argument and an acknowledgement ID (-1: none).</summary>
        public static string Event(string name, string jsonArgument, long ackId = -1)
        {
            var builder = new StringBuilder("42");
            if (ackId >= 0)
            {
                builder.Append(ackId.ToString(CultureInfo.InvariantCulture));
            }
            builder.Append('[').Append(JsonString(name));
            if (jsonArgument != null)
            {
                builder.Append(',').Append(jsonArgument);
            }
            return builder.Append(']').ToString();
        }

        /// <summary>The first string of a JSON array: the event name of an event packet.</summary>
        public static string ReadEventName(string json)
        {
            var index = SkipSpace(json, 0);
            if (index >= json.Length || json[index] != '[')
            {
                return null;
            }
            index = SkipSpace(json, index + 1);
            return index < json.Length && json[index] == '"' ? ReadString(json, ref index) : null;
        }

        /// <summary>A whole-number field of a JSON object, for example pingInterval of the open packet.</summary>
        public static long ReadNumber(string json, string key, long fallback)
        {
            var index = FindValue(json, key);
            if (index < 0)
            {
                return fallback;
            }
            var start = index;
            while (index < json.Length && (char.IsDigit(json[index]) || json[index] == '-'))
            {
                index++;
            }
            return long.TryParse(json.Substring(start, index - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        /// <summary>A string field of a JSON object, or null.</summary>
        public static string ReadStringField(string json, string key)
        {
            var index = FindValue(json, key);
            return index >= 0 && index < json.Length && json[index] == '"' ? ReadString(json, ref index) : null;
        }

        /// <summary>
        /// The conversation that an exception event names: cause.data.conversationId of a refused
        /// conversations:join, or null when the event names none.
        /// </summary>
        public static string ReadCauseConversationId(string json)
        {
            var cause = FindValue(json, "cause");
            if (cause < 0)
            {
                return null;
            }
            var causeJson = json.Substring(cause);
            var data = FindValue(causeJson, "data");
            var id = data < 0 ? null : ReadStringField(causeJson.Substring(data), "conversationId");
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>True when a JSON object has the field with the literal true.</summary>
        public static bool ReadTrue(string json, string key)
        {
            var index = FindValue(json, key);
            return index >= 0 && string.CompareOrdinal(json, index, "true", 0, 4) == 0;
        }

        public static string JsonString(string value)
        {
            var builder = new StringBuilder(value.Length + 2).Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        private static int FindValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
            {
                return -1;
            }
            var token = "\"" + key + "\"";
            var index = json.IndexOf(token, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }
            index = SkipSpace(json, index + token.Length);
            if (index >= json.Length || json[index] != ':')
            {
                return -1;
            }
            return SkipSpace(json, index + 1);
        }

        private static int SkipSpace(string json, int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
            return index;
        }

        private static string ReadString(string json, ref int index)
        {
            var builder = new StringBuilder();
            index++;
            while (index < json.Length)
            {
                var c = json[index++];
                if (c == '"')
                {
                    return builder.ToString();
                }
                if (c != '\\' || index >= json.Length)
                {
                    builder.Append(c);
                    continue;
                }
                var escape = json[index++];
                switch (escape)
                {
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'u':
                        if (index + 4 <= json.Length &&
                            int.TryParse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            index += 4;
                        }
                        break;
                    default:
                        builder.Append(escape);
                        break;
                }
            }
            return null;
        }
    }
}
