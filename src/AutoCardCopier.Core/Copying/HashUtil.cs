using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>
    /// 哈希工具：CRC32（快速全量）、SHA-256（强校验）与抽样哈希（首尾与中点各取一段）。
    /// 说明：CRC32 自行实现（查表法），避免引入第三方库。
    /// </summary>
    public static class HashUtil
    {
        /// <summary>默认抽样块大小（1 MB）。</summary>
        public const int DefaultSampleSize = 1024 * 1024;

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        /// <summary>计算文件的 CRC32 值。</summary>
        public static uint ComputeCrc32(string path)
        {
            using (var stream = OpenRead(path))
            {
                return ComputeCrc32(stream);
            }
        }

        /// <summary>计算流的 CRC32 值。</summary>
        public static uint ComputeCrc32(Stream stream)
        {
            uint crc = 0xFFFFFFFF;
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    crc = (crc >> 8) ^ Crc32Table[(crc ^ buffer[i]) & 0xFF];
                }
            }
            return ~crc;
        }

        /// <summary>计算文件的 SHA-256（十六进制字符串）。</summary>
        public static string ComputeSha256(string path)
        {
            using (var stream = OpenRead(path))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                return ToHex(hash);
            }
        }

        /// <summary>
        /// 抽样哈希：对文件首、中、尾各取一段（默认 1 MB）分别计算 SHA-256 后拼接。
        /// 大文件（视频、RAW）用它可以快速发现绝大多数损坏，而耗时远小于全量哈希。
        /// 文件小于三段之和时退化为全文件哈希。
        /// </summary>
        public static string ComputeSampledHash(string path, int sampleSize = DefaultSampleSize)
        {
            using (var stream = OpenRead(path))
            {
                long length = stream.Length;
                if (length <= sampleSize * 3L)
                {
                    using (var sha = SHA256.Create())
                    {
                        return "F:" + ToHex(sha.ComputeHash(stream));
                    }
                }

                var builder = new StringBuilder();
                builder.Append("S:").Append(length.ToString(CultureInfo.InvariantCulture)).Append(':');
                builder.Append(HashRange(stream, 0, sampleSize)).Append(':');
                builder.Append(HashRange(stream, (length - sampleSize) / 2, sampleSize)).Append(':');
                builder.Append(HashRange(stream, length - sampleSize, sampleSize));
                return builder.ToString();
            }
        }

        /// <summary>按校验档位为文件计算哈希；不需要哈希时返回空字符串。</summary>
        public static string ComputeByLevel(string path, Settings.VerifyLevel level)
        {
            switch (level)
            {
                case Settings.VerifyLevel.Crc32:
                    return "CRC32:" + ComputeCrc32(path).ToString("X8", CultureInfo.InvariantCulture);
                case Settings.VerifyLevel.Sha256:
                    return "SHA256:" + ComputeSha256(path);
                case Settings.VerifyLevel.SizeAndSample:
                    return ComputeSampledHash(path);
                default:
                    return string.Empty;
            }
        }

        private static string HashRange(Stream stream, long offset, int count)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) { break; }
                total += read;
            }
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(buffer, 0, total));
            }
        }

        private static FileStream OpenRead(string path)
        {
            return new FileStream(Infrastructure.PathUtil.Extend(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) { builder.Append(b.ToString("x2", CultureInfo.InvariantCulture)); }
            return builder.ToString();
        }

        private static uint[] BuildCrc32Table()
        {
            const uint polynomial = 0xEDB88320;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) == 1 ? (value >> 1) ^ polynomial : value >> 1;
                }
                table[i] = value;
            }
            return table;
        }
    }
}
