using System;
using System.IO;
using System.IO.Compression;

namespace Npcify.Sdk.Protocol
{
    public enum NpcifyPerformanceKind : byte
    {
        Staging = 1,
        Stream = 2,
        Turn = 3,
        Conversation = 4,
        Baked = 5,
        BakedResult = 6,
        StoredTimeline = 7,
        CachedOpening = 8,
    }

    /// <summary>
    /// NPCP v1.0 envelope encoder and decoder (`application/vnd.npcify.performance`, `.npcp`).
    /// </summary>
    public static class NpcifyPerformanceCodec
    {
        public const string MediaType = "application/vnd.npcify.performance";
        public const string FileExtension = ".npcp";
        public const int HeaderSize = 24;
        public const int MaxBytes = 256 * 1024 * 1024;
        private const uint Magic = 0x5043504eU; // "NPCP" little-endian

        private static readonly uint[] CrcTable = BuildCrcTable();

        public static uint Checksum(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return Checksum(bytes, 0, bytes.Length);
        }

        public static uint Checksum(byte[] bytes, int offset, int count)
        {
            var crc = 0xffffffffU;
            var end = offset + count;
            for (var i = offset; i < end; i++)
            {
                crc = CrcTable[(crc ^ bytes[i]) & 0xff] ^ (crc >> 8);
            }

            return crc ^ 0xffffffffU;
        }

        public static byte[] Encode(
            NpcifyPerformanceKind kind,
            object value,
            bool compress = false)
        {
            var plain = NpcifyMsgPack.Encode(value);
            if (plain.Length == 0 || plain.Length > MaxBytes)
            {
                throw new InvalidDataException("Performance exceeds the protocol size limit.");
            }

            var body = compress ? GzipCompress(plain) : plain;
            var result = new byte[HeaderSize + body.Length];
            WriteUInt32Le(result, 0, Magic);
            result[4] = 1; // Major version
            result[5] = 0; // Minor version
            result[6] = (byte)kind;
            result[7] = compress ? (byte)1 : (byte)0;
            WriteUInt16Le(result, 8, (ushort)HeaderSize);
            WriteUInt16Le(result, 10, 0); // Flags
            WriteUInt32Le(result, 12, (uint)body.Length);
            WriteUInt32Le(result, 16, (uint)plain.Length);
            WriteUInt32Le(result, 20, Checksum(plain));
            Buffer.BlockCopy(body, 0, result, HeaderSize, body.Length);
            return result;
        }

        public static object Decode(byte[] bytes, NpcifyPerformanceKind expectedKind)
        {
            if (bytes == null || bytes.Length < HeaderSize)
            {
                throw new InvalidDataException("Truncated performance header.");
            }

            if (ReadUInt32Le(bytes, 0) != Magic || bytes[4] != 1)
            {
                throw new InvalidDataException("Unsupported performance format or major version.");
            }

            if (bytes[6] != (byte)expectedKind)
            {
                throw new InvalidDataException("Unexpected performance message kind.");
            }

            var compression = bytes[7];
            var headerSize = (int)ReadUInt16Le(bytes, 8);
            var flags = ReadUInt16Le(bytes, 10);
            var bodySize = ReadUInt32Le(bytes, 12);
            var plainSize = ReadUInt32Le(bytes, 16);
            var expectedCrc = ReadUInt32Le(bytes, 20);

            if (compression > 1 ||
                flags != 0 ||
                headerSize < HeaderSize ||
                headerSize > bytes.Length ||
                bodySize != (uint)(bytes.Length - headerSize) ||
                bodySize > MaxBytes ||
                plainSize > MaxBytes ||
                plainSize == 0)
            {
                throw new InvalidDataException("Invalid performance header or size.");
            }

            var bodyLength = (int)bodySize;
            var plainLength = (int)plainSize;
            byte[] plain;
            if (compression == 1)
            {
                if (bodyLength < 18 ||
                    bytes[headerSize] != 31 ||
                    bytes[headerSize + 1] != 139 ||
                    bytes[headerSize + 2] != 8)
                {
                    throw new InvalidDataException("Invalid gzip performance.");
                }

                var trailerOffset = headerSize + bodyLength - 8;
                var trailerCrc = ReadUInt32Le(bytes, trailerOffset);
                var trailerSize = ReadUInt32Le(bytes, trailerOffset + 4);
                if (trailerCrc != expectedCrc || trailerSize != plainSize)
                {
                    throw new InvalidDataException("Performance gzip checksum or size mismatch.");
                }

                plain = InflateBounded(bytes, headerSize, bodyLength, plainLength);
            }
            else
            {
                plain = new byte[bodyLength];
                Buffer.BlockCopy(bytes, headerSize, plain, 0, bodyLength);
            }

            if (plain.Length != plainLength || Checksum(plain) != expectedCrc)
            {
                throw new InvalidDataException("Performance checksum or length mismatch.");
            }

            return NpcifyMsgPack.Decode(plain);
        }

        private static byte[] GzipCompress(byte[] plain)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
            {
                gzip.Write(plain, 0, plain.Length);
            }

            return output.ToArray();
        }

        private static byte[] InflateBounded(byte[] buffer, int offset, int count, int expectedLength)
        {
            var result = new byte[expectedLength];
            var totalRead = 0;
            using var input = new MemoryStream(buffer, offset, count, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var scratch = new byte[4096];
            while (true)
            {
                var read = gzip.Read(scratch, 0, scratch.Length);
                if (read <= 0)
                {
                    break;
                }

                if (totalRead + read > expectedLength)
                {
                    throw new InvalidDataException("Performance decompression exceeds its declared size.");
                }

                Buffer.BlockCopy(scratch, 0, result, totalRead, read);
                totalRead += read;
            }

            if (totalRead != expectedLength)
            {
                throw new InvalidDataException("Truncated compressed performance.");
            }

            return result;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value >> 1) ^ ((value & 1U) != 0 ? 0xedb88320U : 0U);
                }

                table[index] = value;
            }

            return table;
        }

        private static ushort ReadUInt16Le(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32Le(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] |
                   ((uint)bytes[offset + 1] << 8) |
                   ((uint)bytes[offset + 2] << 16) |
                   ((uint)bytes[offset + 3] << 24);
        }

        private static void WriteUInt16Le(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32Le(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }
    }
}
