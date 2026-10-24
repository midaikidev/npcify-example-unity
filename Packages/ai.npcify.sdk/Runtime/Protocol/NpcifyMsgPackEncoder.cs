using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace Npcify.Sdk.Protocol
{
    public static partial class NpcifyMsgPack
    {
        private static void WriteValue(Stream stream, object value, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("Performance nesting exceeds the protocol limit.");
            }

            if (value == null)
            {
                stream.WriteByte(0xc0);
                return;
            }

            if (value is bool b)
            {
                stream.WriteByte(b ? (byte)0xc3 : (byte)0xc2);
                return;
            }

            if (value is long l)
            {
                WriteInteger(stream, l);
                return;
            }

            if (value is double d)
            {
                if (Math.Truncate(d) == d && d >= -MaxSafeInteger && d <= MaxSafeInteger)
                {
                    WriteInteger(stream, (long)d);
                    return;
                }

                stream.WriteByte(0xcb);
                WriteUInt64Be(stream, (ulong)BitConverter.DoubleToInt64Bits(d));
                return;
            }

            if (value is string s)
            {
                var utf8 = StrictUtf8.GetBytes(s);
                if (utf8.Length <= 31)
                {
                    stream.WriteByte((byte)(0xa0 | utf8.Length));
                }
                else if (utf8.Length <= byte.MaxValue)
                {
                    stream.WriteByte(0xd9);
                    stream.WriteByte((byte)utf8.Length);
                }
                else if (utf8.Length <= ushort.MaxValue)
                {
                    stream.WriteByte(0xda);
                    WriteUInt16Be(stream, (ushort)utf8.Length);
                }
                else
                {
                    stream.WriteByte(0xdb);
                    WriteUInt32Be(stream, (uint)utf8.Length);
                }

                stream.Write(utf8, 0, utf8.Length);
                return;
            }

            if (value is byte[] bin)
            {
                if (bin.Length <= byte.MaxValue)
                {
                    stream.WriteByte(0xc4);
                    stream.WriteByte((byte)bin.Length);
                }
                else if (bin.Length <= ushort.MaxValue)
                {
                    stream.WriteByte(0xc5);
                    WriteUInt16Be(stream, (ushort)bin.Length);
                }
                else
                {
                    stream.WriteByte(0xc6);
                    WriteUInt32Be(stream, (uint)bin.Length);
                }

                stream.Write(bin, 0, bin.Length);
                return;
            }

            if (value is IList list)
            {
                if (list.Count <= 15)
                {
                    stream.WriteByte((byte)(0x90 | list.Count));
                }
                else if (list.Count <= ushort.MaxValue)
                {
                    stream.WriteByte(0xdc);
                    WriteUInt16Be(stream, (ushort)list.Count);
                }
                else
                {
                    stream.WriteByte(0xdd);
                    WriteUInt32Be(stream, (uint)list.Count);
                }

                for (var i = 0; i < list.Count; i++)
                {
                    WriteValue(stream, list[i], depth + 1);
                }

                return;
            }

            if (value is Dictionary<string, object> map)
            {
                var keys = new List<string>(map.Keys);
                keys.Sort(StringComparer.Ordinal);
                if (keys.Count <= 15)
                {
                    stream.WriteByte((byte)(0x80 | keys.Count));
                }
                else if (keys.Count <= ushort.MaxValue)
                {
                    stream.WriteByte(0xde);
                    WriteUInt16Be(stream, (ushort)keys.Count);
                }
                else
                {
                    stream.WriteByte(0xdf);
                    WriteUInt32Be(stream, (uint)keys.Count);
                }

                foreach (var key in keys)
                {
                    WriteValue(stream, key, depth + 1);
                    WriteValue(stream, map[key], depth + 1);
                }

                return;
            }

            throw new InvalidDataException("Unsupported performance value.");
        }

        private static void WriteInteger(Stream stream, long value)
        {
            if (value >= 0)
            {
                if (value <= 0x7f)
                {
                    stream.WriteByte((byte)value);
                }
                else if (value <= byte.MaxValue)
                {
                    stream.WriteByte(0xcc);
                    stream.WriteByte((byte)value);
                }
                else if (value <= ushort.MaxValue)
                {
                    stream.WriteByte(0xcd);
                    WriteUInt16Be(stream, (ushort)value);
                }
                else if (value <= uint.MaxValue)
                {
                    stream.WriteByte(0xce);
                    WriteUInt32Be(stream, (uint)value);
                }
                else
                {
                    stream.WriteByte(0xcf);
                    WriteUInt64Be(stream, (ulong)value);
                }
            }
            else if (value >= -32)
            {
                stream.WriteByte((byte)(sbyte)value);
            }
            else if (value >= sbyte.MinValue)
            {
                stream.WriteByte(0xd0);
                stream.WriteByte((byte)(sbyte)value);
            }
            else if (value >= short.MinValue)
            {
                stream.WriteByte(0xd1);
                WriteUInt16Be(stream, (ushort)(short)value);
            }
            else if (value >= int.MinValue)
            {
                stream.WriteByte(0xd2);
                WriteUInt32Be(stream, (uint)(int)value);
            }
            else
            {
                stream.WriteByte(0xd3);
                WriteUInt64Be(stream, (ulong)value);
            }
        }

        private static void WriteUInt16Be(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt32Be(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt64Be(Stream stream, ulong value)
        {
            WriteUInt32Be(stream, (uint)(value >> 32));
            WriteUInt32Be(stream, (uint)value);
        }
    }
}
