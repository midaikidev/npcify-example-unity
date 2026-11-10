using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Npcify.Sdk.Protocol
{
    /// <summary>
    /// A small JSON reader for the events of the conversation stream: objects become
    /// <c>Dictionary&lt;string, object&gt;</c>, arrays <c>List&lt;object&gt;</c>, numbers
    /// <c>double</c>, and <c>true</c>, <c>false</c> and <c>null</c> their values.
    /// </summary>
    public static class NpcifyJson
    {
        private const int MaxDepth = 64;

        /// <summary>The value of a JSON text. Throws FormatException when the text is not valid JSON.</summary>
        public static object Parse(string json)
        {
            if (json == null)
            {
                throw new FormatException("The JSON text is null.");
            }
            var index = 0;
            var value = ReadValue(json, ref index, 0);
            index = SkipSpace(json, index);
            if (index != json.Length)
            {
                throw new FormatException("The JSON text has data after its value.");
            }
            return value;
        }

        /// <summary>The value of a JSON text, or null when the text is not valid JSON.</summary>
        public static object TryParse(string json)
        {
            try
            {
                return Parse(json);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static object ReadValue(string json, ref int index, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("The JSON text is nested too deeply.");
            }
            index = SkipSpace(json, index);
            if (index >= json.Length)
            {
                throw new FormatException("The JSON text ended before a value.");
            }
            switch (json[index])
            {
                case '{':
                    return ReadObject(json, ref index, depth);
                case '[':
                    return ReadArray(json, ref index, depth);
                case '"':
                    return ReadString(json, ref index);
                case 't':
                    ReadWord(json, ref index, "true");
                    return true;
                case 'f':
                    ReadWord(json, ref index, "false");
                    return false;
                case 'n':
                    ReadWord(json, ref index, "null");
                    return null;
                default:
                    return ReadNumber(json, ref index);
            }
        }

        private static Dictionary<string, object> ReadObject(string json, ref int index, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            index = SkipSpace(json, index + 1);
            if (index < json.Length && json[index] == '}')
            {
                index++;
                return result;
            }
            while (true)
            {
                index = SkipSpace(json, index);
                if (index >= json.Length || json[index] != '"')
                {
                    throw new FormatException("A JSON object key must be a string.");
                }
                var key = ReadString(json, ref index);
                index = SkipSpace(json, index);
                if (index >= json.Length || json[index] != ':')
                {
                    throw new FormatException("A JSON object key must have a colon.");
                }
                index++;
                result[key] = ReadValue(json, ref index, depth + 1);
                index = SkipSpace(json, index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (index < json.Length && json[index] == '}')
                {
                    index++;
                    return result;
                }
                throw new FormatException("A JSON object must end with a brace.");
            }
        }

        private static List<object> ReadArray(string json, ref int index, int depth)
        {
            var result = new List<object>();
            index = SkipSpace(json, index + 1);
            if (index < json.Length && json[index] == ']')
            {
                index++;
                return result;
            }
            while (true)
            {
                result.Add(ReadValue(json, ref index, depth + 1));
                index = SkipSpace(json, index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (index < json.Length && json[index] == ']')
                {
                    index++;
                    return result;
                }
                throw new FormatException("A JSON array must end with a bracket.");
            }
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
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (index >= json.Length)
                {
                    break;
                }
                var escape = json[index++];
                switch (escape)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escape);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (index + 4 > json.Length ||
                            !int.TryParse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw new FormatException("A JSON string has a bad unicode escape.");
                        }
                        builder.Append((char)code);
                        index += 4;
                        break;
                    default:
                        throw new FormatException("A JSON string has a bad escape.");
                }
            }
            throw new FormatException("A JSON string has no end quote.");
        }

        private static double ReadNumber(string json, ref int index)
        {
            var start = index;
            while (index < json.Length && "+-0123456789.eE".IndexOf(json[index]) >= 0)
            {
                index++;
            }
            if (index == start ||
                !double.TryParse(json.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException("The JSON text has a value that is not valid.");
            }
            return value;
        }

        private static void ReadWord(string json, ref int index, string word)
        {
            if (string.CompareOrdinal(json, index, word, 0, word.Length) != 0)
            {
                throw new FormatException("The JSON text has a value that is not valid.");
            }
            index += word.Length;
        }

        private static int SkipSpace(string json, int index)
        {
            while (index < json.Length && (json[index] == ' ' || json[index] == '\t' || json[index] == '\n' || json[index] == '\r'))
            {
                index++;
            }
            return index;
        }
    }
}
