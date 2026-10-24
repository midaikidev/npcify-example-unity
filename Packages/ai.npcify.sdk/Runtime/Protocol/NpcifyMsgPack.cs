using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Npcify.Sdk.Protocol
{
    /// <summary>
    /// Portable MessagePack reader and writer implementing the NPCP v1.0 wire subset and safety limits.
    /// </summary>
    public static partial class NpcifyMsgPack
    {
        public const int MaxDepth = 64;
        public const int MaxTotalValues = 2_000_000;
        public const int MaxStringBytes = 1024 * 1024;
        public const int MaxBinaryBytes = 256 * 1024 * 1024;
        public const int MaxArrayLength = 1_000_000;
        public const int MaxMapLength = 10_000;
        public const long MaxSafeInteger = 9_007_199_254_740_991L; // 2^53 - 1

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> ReservedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "__proto__",
            "constructor",
            "prototype",
        };

        public static byte[] Encode(object value)
        {
            var normalized = NormalizeWireValue(value, 0);
            using var stream = new MemoryStream();
            WriteValue(stream, normalized, 0);
            var bytes = stream.ToArray();
            Validate(bytes);
            return bytes;
        }

        public static object Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            Validate(bytes);
            var offset = 0;
            var totalValues = 0;
            var decoded = ReadValue(bytes, ref offset, 0, ref totalValues);
            if (offset != bytes.Length)
            {
                throw new InvalidDataException("Trailing MessagePack performance data.");
            }

            return NormalizeWireValue(decoded, 0);
        }

        public static void Validate(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidDataException("Truncated MessagePack performance.");
            }

            var offset = 0;
            var values = 0;
            ValidateValue(bytes, ref offset, 0, ref values);
            if (offset != bytes.Length)
            {
                throw new InvalidDataException("Trailing MessagePack performance data.");
            }
        }

        private static object NormalizeWireValue(object input, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("Performance nesting exceeds the protocol limit.");
            }

            if (input == null || input is string || input is bool)
            {
                return input;
            }

            if (input is byte[] binary)
            {
                var copy = new byte[binary.Length];
                Buffer.BlockCopy(binary, 0, copy, 0, binary.Length);
                return copy;
            }

            if (input is sbyte || input is byte || input is short || input is ushort || input is int || input is uint)
            {
                return Convert.ToInt64(input);
            }

            if (input is long longValue)
            {
                if (longValue < -MaxSafeInteger || longValue > MaxSafeInteger)
                {
                    throw new InvalidDataException("Performance integers must be exactly representable.");
                }

                return longValue;
            }

            if (input is ulong ulongValue)
            {
                if (ulongValue > (ulong)MaxSafeInteger)
                {
                    throw new InvalidDataException("Performance integers must be exactly representable.");
                }

                return (long)ulongValue;
            }

            if (input is float floatValue)
            {
                if (float.IsNaN(floatValue) || float.IsInfinity(floatValue))
                {
                    throw new InvalidDataException("Unsupported non-finite performance float.");
                }

                return (double)floatValue;
            }

            if (input is double doubleValue)
            {
                if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
                {
                    throw new InvalidDataException("Unsupported non-finite performance number.");
                }

                if (Math.Truncate(doubleValue) == doubleValue &&
                    (doubleValue < -MaxSafeInteger || doubleValue > MaxSafeInteger))
                {
                    throw new InvalidDataException("Performance integers must be exactly representable.");
                }

                return doubleValue;
            }

            if (input is IDictionary dictionary)
            {
                var keys = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!(entry.Key is string key))
                    {
                        throw new InvalidDataException("Performance map keys must be strings.");
                    }

                    if (ReservedKeys.Contains(key))
                    {
                        throw new InvalidDataException("Invalid performance map key.");
                    }

                    keys.Add(key);
                }

                keys.Sort(StringComparer.Ordinal);
                var result = new Dictionary<string, object>(keys.Count, StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    var val = dictionary[key];
                    result[key] = NormalizeWireValue(val, depth + 1);
                }

                if (result.TryGetValue("type", out var typeObj) &&
                    typeObj is string typeStr &&
                    (typeStr == "base64" || typeStr == "speech_chunk") &&
                    result.TryGetValue("data", out var dataObj) &&
                    dataObj is string base64Str)
                {
                    result["data"] = Convert.FromBase64String(base64Str);
                    if (typeStr == "base64")
                    {
                        result["type"] = "bytes";
                    }
                }

                return result;
            }

            if (input is IEnumerable enumerable)
            {
                var list = new List<object>();
                foreach (var item in enumerable)
                {
                    list.Add(NormalizeWireValue(item, depth + 1));
                }

                return list;
            }

            throw new InvalidDataException("Unsupported performance value.");
        }

        private static void ValidateValue(byte[] bytes, ref int offset, int depth, ref int values)
        {
            if (depth > MaxDepth || ++values > MaxTotalValues)
            {
                throw new InvalidDataException("Performance nesting or value count exceeds the limit.");
            }

            var tag = ReadByte(bytes, ref offset);
            if (tag < 0x80 || tag >= 0xe0 || tag == 0xc0 || tag == 0xc2 || tag == 0xc3)
            {
                return;
            }

            if ((tag >= 0xa0 && tag <= 0xbf) || (tag >= 0xd9 && tag <= 0xdb))
            {
                ReadStringByTag(bytes, ref offset, tag);
                return;
            }

            if (tag >= 0x80 && tag <= 0x8f)
            {
                ValidateSequence(bytes, ref offset, tag & 15, depth, true, ref values);
                return;
            }

            if (tag >= 0x90 && tag <= 0x9f)
            {
                ValidateSequence(bytes, ref offset, tag & 15, depth, false, ref values);
                return;
            }

            if (tag >= 0xc4 && tag <= 0xc6)
            {
                var length = ReadLength(bytes, ref offset, 1 << (tag - 0xc4));
                if (length > MaxBinaryBytes)
                {
                    throw new InvalidDataException("Performance binary exceeds the limit.");
                }

                Advance(bytes, ref offset, length);
                return;
            }

            if (tag >= 0xca && tag <= 0xd3)
            {
                int[] widths = { 4, 8, 1, 2, 4, 8, 1, 2, 4, 8 };
                Advance(bytes, ref offset, widths[tag - 0xca]);
                return;
            }

            if (tag >= 0xdc && tag <= 0xdf)
            {
                var count = ReadLength(bytes, ref offset, tag % 2 == 0 ? 2 : 4);
                ValidateSequence(bytes, ref offset, count, depth, tag >= 0xde, ref values);
                return;
            }

            throw new InvalidDataException("Unsupported MessagePack extension or value.");
        }

        private static void ValidateSequence(
            byte[] bytes,
            ref int offset,
            int count,
            int depth,
            bool isMap,
            ref int values)
        {
            if (count > (isMap ? MaxMapLength : MaxArrayLength))
            {
                throw new InvalidDataException("Performance collection exceeds the limit.");
            }

            HashSet<string> keys = isMap ? new HashSet<string>(StringComparer.Ordinal) : null;
            for (var i = 0; i < count; i++)
            {
                if (isMap)
                {
                    var keyTag = ReadByte(bytes, ref offset);
                    var key = ReadStringByTag(bytes, ref offset, keyTag);
                    if (!keys.Add(key) || ReservedKeys.Contains(key))
                    {
                        throw new InvalidDataException("Duplicate or reserved performance map key.");
                    }
                }

                ValidateValue(bytes, ref offset, depth + 1, ref values);
            }
        }

        private static object ReadValue(byte[] bytes, ref int offset, int depth, ref int values)
        {
            if (depth > MaxDepth || ++values > MaxTotalValues)
            {
                throw new InvalidDataException("Performance nesting or value count exceeds the limit.");
            }

            var tag = ReadByte(bytes, ref offset);
            if (tag <= 0x7f)
            {
                return (long)tag;
            }

            if (tag >= 0xe0)
            {
                return (long)(sbyte)tag;
            }

            if (tag >= 0x80 && tag <= 0x8f)
            {
                return ReadMap(bytes, ref offset, tag & 15, depth, ref values);
            }

            if (tag >= 0x90 && tag <= 0x9f)
            {
                return ReadArray(bytes, ref offset, tag & 15, depth, ref values);
            }

            if (tag >= 0xa0 && tag <= 0xbf)
            {
                return ReadUtf8(bytes, ref offset, tag & 31);
            }

            switch (tag)
            {
                case 0xc0:
                    return null;
                case 0xc2:
                    return false;
                case 0xc3:
                    return true;
                case 0xc4:
                case 0xc5:
                case 0xc6:
                {
                    var len = ReadLength(bytes, ref offset, 1 << (tag - 0xc4));
                    if (len > MaxBinaryBytes)
                    {
                        throw new InvalidDataException("Performance binary exceeds the limit.");
                    }

                    var start = Advance(bytes, ref offset, len);
                    var bin = new byte[len];
                    Buffer.BlockCopy(bytes, start, bin, 0, len);
                    return bin;
                }
                case 0xca:
                {
                    var raw = ReadUInt32Be(bytes, ref offset);
                    var flt = BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
                    if (float.IsNaN(flt) || float.IsInfinity(flt))
                    {
                        throw new InvalidDataException("Non-finite float32 in performance.");
                    }

                    return (double)flt;
                }
                case 0xcb:
                {
                    var raw = ReadUInt64Be(bytes, ref offset);
                    var dbl = BitConverter.Int64BitsToDouble((long)raw);
                    if (double.IsNaN(dbl) || double.IsInfinity(dbl))
                    {
                        throw new InvalidDataException("Non-finite float64 in performance.");
                    }

                    return dbl;
                }
                case 0xcc:
                    return (long)ReadByte(bytes, ref offset);
                case 0xcd:
                    return (long)ReadUInt16Be(bytes, ref offset);
                case 0xce:
                    return (long)ReadUInt32Be(bytes, ref offset);
                case 0xcf:
                {
                    var u64 = ReadUInt64Be(bytes, ref offset);
                    if (u64 > (ulong)MaxSafeInteger)
                    {
                        throw new InvalidDataException("Performance integers must be exactly representable.");
                    }

                    return (long)u64;
                }
                case 0xd0:
                    return (long)(sbyte)ReadByte(bytes, ref offset);
                case 0xd1:
                    return (long)(short)ReadUInt16Be(bytes, ref offset);
                case 0xd2:
                    return (long)(int)ReadUInt32Be(bytes, ref offset);
                case 0xd3:
                {
                    var i64 = (long)ReadUInt64Be(bytes, ref offset);
                    if (i64 < -MaxSafeInteger || i64 > MaxSafeInteger)
                    {
                        throw new InvalidDataException("Performance integers must be exactly representable.");
                    }

                    return i64;
                }
                case 0xd9:
                case 0xda:
                case 0xdb:
                    return ReadUtf8(bytes, ref offset, ReadLength(bytes, ref offset, 1 << (tag - 0xd9)));
                case 0xdc:
                case 0xdd:
                    return ReadArray(bytes, ref offset, ReadLength(bytes, ref offset, tag == 0xdc ? 2 : 4), depth, ref values);
                case 0xde:
                case 0xdf:
                    return ReadMap(bytes, ref offset, ReadLength(bytes, ref offset, tag == 0xde ? 2 : 4), depth, ref values);
                default:
                    throw new InvalidDataException("Unsupported MessagePack extension or value.");
            }
        }

        private static List<object> ReadArray(byte[] bytes, ref int offset, int count, int depth, ref int values)
        {
            if (count > MaxArrayLength)
            {
                throw new InvalidDataException("Performance collection exceeds the limit.");
            }

            var list = new List<object>(Math.Min(count, 1024));
            for (var i = 0; i < count; i++)
            {
                list.Add(ReadValue(bytes, ref offset, depth + 1, ref values));
            }

            return list;
        }

        private static Dictionary<string, object> ReadMap(byte[] bytes, ref int offset, int count, int depth, ref int values)
        {
            if (count > MaxMapLength)
            {
                throw new InvalidDataException("Performance collection exceeds the limit.");
            }

            var map = new Dictionary<string, object>(Math.Min(count, 256), StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var keyTag = ReadByte(bytes, ref offset);
                var key = ReadStringByTag(bytes, ref offset, keyTag);
                if (map.ContainsKey(key) || ReservedKeys.Contains(key))
                {
                    throw new InvalidDataException("Duplicate or reserved performance map key.");
                }

                map[key] = ReadValue(bytes, ref offset, depth + 1, ref values);
            }

            return map;
        }

        private static string ReadStringByTag(byte[] bytes, ref int offset, byte tag)
        {
            if (tag >= 0xa0 && tag <= 0xbf)
            {
                return ReadUtf8(bytes, ref offset, tag & 31);
            }

            if (tag >= 0xd9 && tag <= 0xdb)
            {
                return ReadUtf8(bytes, ref offset, ReadLength(bytes, ref offset, 1 << (tag - 0xd9)));
            }

            throw new InvalidDataException("Performance map keys must be strings.");
        }

        private static string ReadUtf8(byte[] bytes, ref int offset, int length)
        {
            if (length > MaxStringBytes)
            {
                throw new InvalidDataException("Performance string exceeds the limit.");
            }

            var start = Advance(bytes, ref offset, length);
            try
            {
                return StrictUtf8.GetString(bytes, start, length);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Invalid UTF-8 string in MessagePack performance.", ex);
            }
        }

        private static byte ReadByte(byte[] bytes, ref int offset)
        {
            var start = Advance(bytes, ref offset, 1);
            return bytes[start];
        }

        private static int ReadLength(byte[] bytes, ref int offset, int width)
        {
            if (width == 1)
            {
                return ReadByte(bytes, ref offset);
            }

            if (width == 2)
            {
                return ReadUInt16Be(bytes, ref offset);
            }

            var u32 = ReadUInt32Be(bytes, ref offset);
            if (u32 > int.MaxValue)
            {
                throw new InvalidDataException("MessagePack length exceeds int.MaxValue.");
            }

            return (int)u32;
        }

        private static ushort ReadUInt16Be(byte[] bytes, ref int offset)
        {
            var start = Advance(bytes, ref offset, 2);
            return (ushort)((bytes[start] << 8) | bytes[start + 1]);
        }

        private static uint ReadUInt32Be(byte[] bytes, ref int offset)
        {
            var start = Advance(bytes, ref offset, 4);
            return ((uint)bytes[start] << 24) |
                   ((uint)bytes[start + 1] << 16) |
                   ((uint)bytes[start + 2] << 8) |
                   bytes[start + 3];
        }

        private static ulong ReadUInt64Be(byte[] bytes, ref int offset)
        {
            var hi = ReadUInt32Be(bytes, ref offset);
            var lo = ReadUInt32Be(bytes, ref offset);
            return ((ulong)hi << 32) | lo;
        }

        private static int Advance(byte[] bytes, ref int offset, int length)
        {
            if (length < 0 || offset > bytes.Length - length)
            {
                throw new InvalidDataException("Truncated MessagePack performance.");
            }

            var start = offset;
            offset += length;
            return start;
        }
    }
}
