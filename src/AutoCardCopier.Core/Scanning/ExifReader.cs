using System;
using System.IO;
using System.Text;

namespace AutoCardCopier.Core.Scanning
{
    /// <summary>
    /// 极简 EXIF 读取器：只解析相机厂商与型号两个字段，用于在卡片上显示"这是什么机器拍的"。
    /// 实现思路：在文件头部定位 TIFF 头（JPEG 的 APP1 段或 RAW 文件开头），
    /// 然后解析 IFD0 中的 Make(0x010F) 与 Model(0x0110)。
    /// 任何异常都返回空值，绝不影响扫描主流程。
    /// </summary>
    public static class ExifReader
    {
        private const int HeaderBytesToRead = 256 * 1024;

        /// <summary>读取相机型号；失败返回空字符串。</summary>
        public static string TryReadCameraModel(string filePath)
        {
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    int length = (int)Math.Min(HeaderBytesToRead, stream.Length);
                    if (length <= 8) { return string.Empty; }
                    var buffer = new byte[length];
                    int read = stream.Read(buffer, 0, length);
                    if (read <= 8) { return string.Empty; }
                    return Parse(buffer, read);
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>在给定字节缓冲区中解析型号。</summary>
        public static string Parse(byte[] buffer, int length)
        {
            int tiffStart = FindTiffHeader(buffer, length);
            if (tiffStart < 0) { return string.Empty; }

            bool littleEndian;
            if (buffer[tiffStart] == 0x49 && buffer[tiffStart + 1] == 0x49) { littleEndian = true; }
            else if (buffer[tiffStart] == 0x4D && buffer[tiffStart + 1] == 0x4D) { littleEndian = false; }
            else { return string.Empty; }

            int ifdOffset = (int)ReadUInt32(buffer, length, tiffStart + 4, littleEndian);
            if (ifdOffset <= 0) { return string.Empty; }

            int ifdStart = tiffStart + ifdOffset;
            if (ifdStart + 2 > length) { return string.Empty; }

            int entryCount = ReadUInt16(buffer, length, ifdStart, littleEndian);
            string make = null;
            string model = null;

            for (int i = 0; i < entryCount; i++)
            {
                int entry = ifdStart + 2 + i * 12;
                if (entry + 12 > length) { break; }

                int tag = ReadUInt16(buffer, length, entry, littleEndian);
                int type = ReadUInt16(buffer, length, entry + 2, littleEndian);
                int count = (int)ReadUInt32(buffer, length, entry + 4, littleEndian);
                if (type != 2 || count <= 0 || count > 512) { continue; }

                string value;
                if (count <= 4)
                {
                    value = ReadAscii(buffer, length, entry + 8, count);
                }
                else
                {
                    int valueOffset = (int)ReadUInt32(buffer, length, entry + 8, littleEndian);
                    value = ReadAscii(buffer, length, tiffStart + valueOffset, count);
                }

                if (tag == 0x010F && !string.IsNullOrWhiteSpace(value)) { make = value.Trim(); }
                else if (tag == 0x0110 && !string.IsNullOrWhiteSpace(value)) { model = value.Trim(); }
            }

            return Compose(make, model);
        }

        /// <summary>把厂商与型号合成一句可读文本，并去掉重复词。</summary>
        private static string Compose(string make, string model)
        {
            if (string.IsNullOrEmpty(make) && string.IsNullOrEmpty(model)) { return string.Empty; }
            if (string.IsNullOrEmpty(make)) { return model; }
            if (string.IsNullOrEmpty(model)) { return make; }

            // 有些相机把厂商名也写在型号里（如 "NIKON Z 7"），此时不再重复厂商
            if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) { return model; }
            return make + " " + model;
        }

        /// <summary>
        /// 定位 TIFF 头：优先按 JPEG 的 APP1 段查找，其次按文件开头的 TIFF 头，
        /// 最后在缓冲区中搜索，以覆盖 CR3 等容器格式。
        /// </summary>
        private static int FindTiffHeader(byte[] buffer, int length)
        {
            // 情况一：文件本身就是 TIFF 结构（多数 RAW）
            if (length >= 8 && IsTiffHeader(buffer, 0)) { return 0; }

            // 情况二：JPEG，查找 Exif 标识
            if (length >= 4 && buffer[0] == 0xFF && buffer[1] == 0xD8)
            {
                int index = IndexOf(buffer, length, ExifMarker, 2);
                if (index >= 0 && index + 6 < length) { return index + 6; }
            }

            // 情况三：其他容器（如 CR3），在头部范围内搜索 Exif 标识或 TIFF 头
            int marker = IndexOf(buffer, length, ExifMarker, 0);
            if (marker >= 0 && marker + 6 < length && IsTiffHeader(buffer, marker + 6)) { return marker + 6; }

            for (int i = 0; i + 8 < length; i++)
            {
                if (IsTiffHeader(buffer, i)) { return i; }
            }
            return -1;
        }

        private static readonly byte[] ExifMarker = Encoding.ASCII.GetBytes("Exif");

        private static bool IsTiffHeader(byte[] buffer, int offset)
        {
            if (offset < 0 || offset + 8 > buffer.Length) { return false; }
            bool little = buffer[offset] == 0x49 && buffer[offset + 1] == 0x49;
            bool big = buffer[offset] == 0x4D && buffer[offset + 1] == 0x4D;
            if (!little && !big) { return false; }
            if (little) { return buffer[offset + 2] == 0x2A && buffer[offset + 3] == 0x00; }
            return buffer[offset + 2] == 0x00 && buffer[offset + 3] == 0x2A;
        }

        private static int IndexOf(byte[] buffer, int length, byte[] pattern, int start)
        {
            int limit = length - pattern.Length;
            for (int i = Math.Max(start, 0); i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) { return i; }
            }
            return -1;
        }

        private static int ReadUInt16(byte[] buffer, int length, int offset, bool littleEndian)
        {
            if (offset < 0 || offset + 2 > length) { return 0; }
            return littleEndian
                ? buffer[offset] | (buffer[offset + 1] << 8)
                : (buffer[offset] << 8) | buffer[offset + 1];
        }

        private static long ReadUInt32(byte[] buffer, int length, int offset, bool littleEndian)
        {
            if (offset < 0 || offset + 4 > length) { return 0; }
            if (littleEndian)
            {
                return (uint)(buffer[offset] | (buffer[offset + 1] << 8) |
                              (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
            }
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) |
                          (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        private static string ReadAscii(byte[] buffer, int length, int offset, int count)
        {
            if (offset < 0 || offset >= length) { return string.Empty; }
            int available = Math.Min(count, length - offset);
            if (available <= 0) { return string.Empty; }
            string text = Encoding.ASCII.GetString(buffer, offset, available);
            int terminator = text.IndexOf('\0');
            if (terminator >= 0) { text = text.Substring(0, terminator); }
            return text.Trim();
        }
    }
}
