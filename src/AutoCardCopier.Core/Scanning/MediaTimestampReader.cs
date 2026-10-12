using System;
using System.Globalization;
using System.IO;
using System.Text;
using AutoCardCopier.Core.Scanning;

namespace AutoCardCopier.Core.Scanning
{
    /// <summary>拍摄时间的来源。</summary>
    public enum ShootTimeSource
    {
        /// <summary>无法确定。</summary>
        Unknown = 0,
        /// <summary>EXIF 原始拍摄时间（DateTimeOriginal）。</summary>
        ExifOriginal = 1,
        /// <summary>EXIF 数字化时间（DateTimeDigitized）。</summary>
        ExifDigitized = 2,
        /// <summary>EXIF 通用时间（IFD0 DateTime）。</summary>
        ExifCommon = 3,
        /// <summary>视频容器创建时间（MP4/MOV 的 mvhd）。</summary>
        Container = 4,
        /// <summary>回退到文件修改时间。</summary>
        FileTime = 5
    }

    /// <summary>一次时间提取的结果。</summary>
    public sealed class ShootTimeResult
    {
        /// <summary>拍摄时间（本地时间；无法确定时为 null）。</summary>
        public DateTime? Time { get; set; }

        /// <summary>时间来源。</summary>
        public ShootTimeSource Source { get; set; }

        /// <summary>是否成功提取。</summary>
        public bool Success { get { return Time.HasValue; } }

        /// <summary>来源说明（用于界面展示）。</summary>
        public static string Describe(ShootTimeSource source)
        {
            switch (source)
            {
                case ShootTimeSource.ExifOriginal: return "EXIF 原始拍摄时间";
                case ShootTimeSource.ExifDigitized: return "EXIF 数字化时间";
                case ShootTimeSource.ExifCommon: return "EXIF 通用时间";
                case ShootTimeSource.Container: return "视频容器时间";
                case ShootTimeSource.FileTime: return "文件修改时间";
                default: return "未知";
            }
        }
    }

    /// <summary>
    /// 拍摄时间读取器（F13-A）。
    ///
    /// 规则（已获需求方批准）：
    ///   照片：EXIF DateTimeOriginal → DateTimeDigitized → IFD0 DateTime；
    ///   视频：MP4/MOV 容器 mvhd 创建时间（按规范视为 UTC，换算为本地时间）；
    ///   回退：文件最后写入时间；
    ///   全部失败 → 来源标记为 Unknown（宁可不分组，也不猜错日期）。
    ///
    /// 性能约束：只读取文件头部（必要时补读尾部，因为有些录像机的 moov 盒写在文件末尾），
    /// 绝不整文件读取；异常时间（早于 1990 年或晚于当前时间一天）一律不采信。
    /// </summary>
    public static class MediaTimestampReader
    {
        /// <summary>照片读取的头部字节数（64 KB，足以覆盖 EXIF）。</summary>
        private const int PhotoHeaderBytes = 64 * 1024;

        /// <summary>视频容器搜索的窗口大小（头尾各 256 KB）。</summary>
        private const int VideoWindowBytes = 256 * 1024;

        /// <summary>可采信的最早时间。</summary>
        private static readonly DateTime MinPlausible = new DateTime(1990, 1, 1);

        /// <summary>
        /// 读取一个素材文件的拍摄时间。
        /// </summary>
        /// <param name="path">文件完整路径。</param>
        /// <param name="kind">素材类型。</param>
        /// <param name="videoUseFileTime">
        /// true 表示视频直接使用文件时间（应对个别相机容器时间不准的情况）。
        /// </param>
        public static ShootTimeResult Read(string path, MediaKind kind, bool videoUseFileTime)
        {
            var result = new ShootTimeResult { Source = ShootTimeSource.Unknown };
            DateTime fileTime;
            try
            {
                fileTime = File.GetLastWriteTime(path);
            }
            catch (Exception)
            {
                return result;
            }

            try
            {
                if (kind == MediaKind.Photo)
                {
                    DateTime exifTime;
                    ShootTimeSource exifSource;
                    if (TryReadExifTime(path, out exifTime, out exifSource))
                    {
                        result.Time = exifTime;
                        result.Source = exifSource;
                        return result;
                    }
                }
                else if (kind == MediaKind.Video && !videoUseFileTime)
                {
                    DateTime containerTime;
                    if (TryReadContainerTime(path, out containerTime))
                    {
                        result.Time = containerTime;
                        result.Source = ShootTimeSource.Container;
                        return result;
                    }
                }
            }
            catch (Exception)
            {
                // 任何解析异常都退回到文件时间
            }

            result.Time = fileTime;
            result.Source = ShootTimeSource.FileTime;
            return result;
        }

        // ---------------- EXIF ----------------

        /// <summary>读取照片的 EXIF 拍摄时间。</summary>
        public static bool TryReadExifTime(string path, out DateTime time, out ShootTimeSource source)
        {
            time = DateTime.MinValue;
            source = ShootTimeSource.Unknown;

            byte[] buffer;
            int length;
            if (!TryReadHead(path, PhotoHeaderBytes, out buffer, out length)) { return false; }

            int tiffStart = FindTiffHeader(buffer, length);
            if (tiffStart < 0) { return false; }

            bool littleEndian;
            if (buffer[tiffStart] == 0x49 && buffer[tiffStart + 1] == 0x49) { littleEndian = true; }
            else if (buffer[tiffStart] == 0x4D && buffer[tiffStart + 1] == 0x4D) { littleEndian = false; }
            else { return false; }

            int ifdOffset = (int)ReadUInt32(buffer, length, tiffStart + 4, littleEndian);
            if (ifdOffset <= 0) { return false; }

            string commonTime = null;
            int exifIfdOffset = 0;
            if (!WalkIfd(buffer, length, tiffStart, ifdOffset, littleEndian,
                    delegate (int tag, string ascii, int subIfdOffset)
                    {
                        if (tag == 0x0132) { commonTime = ascii; }
                        else if (tag == 0x8769) { exifIfdOffset = subIfdOffset; }
                    }))
            {
                return false;
            }

            string original = null;
            string digitized = null;
            if (exifIfdOffset > 0)
            {
                WalkIfd(buffer, length, tiffStart, exifIfdOffset, littleEndian,
                    delegate (int tag, string ascii, int subIfdOffset)
                    {
                        if (tag == 0x9003) { original = ascii; }
                        else if (tag == 0x9004) { digitized = ascii; }
                    });
            }

            DateTime parsed;
            if (TryParseExifDate(original, out parsed)) { time = parsed; source = ShootTimeSource.ExifOriginal; return true; }
            if (TryParseExifDate(digitized, out parsed)) { time = parsed; source = ShootTimeSource.ExifDigitized; return true; }
            if (TryParseExifDate(commonTime, out parsed)) { time = parsed; source = ShootTimeSource.ExifCommon; return true; }
            return false;
        }

        /// <summary>遍历一个 IFD，把 ASCII 标签与子 IFD 偏移回调给调用方。</summary>
        private static bool WalkIfd(byte[] buffer, int length, int tiffStart, int ifdOffset, bool littleEndian,
            Action<int, string, int> onEntry)
        {
            int ifdStart = tiffStart + ifdOffset;
            if (ifdStart < 0 || ifdStart + 2 > length) { return false; }

            int entryCount = ReadUInt16(buffer, length, ifdStart, littleEndian);
            if (entryCount <= 0 || entryCount > 512) { return false; }

            for (int i = 0; i < entryCount; i++)
            {
                int entry = ifdStart + 2 + i * 12;
                if (entry + 12 > length) { break; }

                int tag = ReadUInt16(buffer, length, entry, littleEndian);
                int type = ReadUInt16(buffer, length, entry + 2, littleEndian);
                int count = (int)ReadUInt32(buffer, length, entry + 4, littleEndian);

                if (type == 2 && count > 0 && count <= 64)
                {
                    string ascii;
                    if (count <= 4)
                    {
                        ascii = ReadAscii(buffer, length, entry + 8, count);
                    }
                    else
                    {
                        int valueOffset = (int)ReadUInt32(buffer, length, entry + 8, littleEndian);
                        ascii = ReadAscii(buffer, length, tiffStart + valueOffset, count);
                    }
                    onEntry(tag, ascii, 0);
                }
                else if (tag == 0x8769 && type == 4)
                {
                    int subOffset = (int)ReadUInt32(buffer, length, entry + 8, littleEndian);
                    onEntry(tag, null, subOffset);
                }
            }
            return true;
        }

        private static bool TryParseExifDate(string text, out DateTime time)
        {
            time = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(text)) { return false; }

            string value = text.Trim();
            DateTime parsed;
            if (!DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
            {
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    return false;
                }
            }
            if (!IsPlausible(parsed)) { return false; }
            time = parsed;
            return true;
        }

        // ---------------- 视频容器（MP4 / MOV） ----------------

        /// <summary>读取 MP4/MOV 的 mvhd 创建时间。</summary>
        public static bool TryReadContainerTime(string path, out DateTime time)
        {
            time = DateTime.MinValue;

            byte[] head;
            int headLength;
            if (!TryReadHead(path, VideoWindowBytes, out head, out headLength)) { return false; }

            int index = IndexOfBox(head, headLength, "mvhd");
            if (index < 0)
            {
                // 有些录像机把 moov 写在文件末尾，补读尾部窗口
                byte[] tail;
                int tailLength;
                if (TryReadTail(path, VideoWindowBytes, out tail, out tailLength))
                {
                    int tailIndex = IndexOfBox(tail, tailLength, "mvhd");
                    if (tailIndex >= 0) { return TryParseMvhd(tail, tailLength, tailIndex, out time); }
                }
                return false;
            }
            return TryParseMvhd(head, headLength, index, out time);
        }

        /// <summary>解析 mvhd 创建时间（1904-01-01 起的秒数，规范视为 UTC）。</summary>
        private static bool TryParseMvhd(byte[] buffer, int length, int boxIndex, out DateTime time)
        {
            time = DateTime.MinValue;
            int versionOffset = boxIndex + 4;              // 跳过 'mvhd' 四个字节
            if (versionOffset + 8 > length) { return false; }

            byte version = buffer[versionOffset];
            long seconds;
            if (version == 1)
            {
                if (versionOffset + 12 > length) { return false; }
                seconds = (long)ReadUInt64(buffer, length, versionOffset + 4);
            }
            else
            {
                seconds = ReadUInt32(buffer, length, versionOffset + 4, false);
            }

            if (seconds <= 0) { return false; }

            var epoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime utc;
            try
            {
                utc = epoch.AddSeconds(seconds);
            }
            catch (Exception)
            {
                return false;
            }

            DateTime local = utc.ToLocalTime();
            if (!IsPlausible(local)) { return false; }
            time = local;
            return true;
        }

        // ---------------- 通用工具 ----------------

        private static bool IsPlausible(DateTime time)
        {
            if (time < MinPlausible) { return false; }
            if (time > DateTime.Now.AddDays(1)) { return false; }
            return true;
        }

        private static bool TryReadHead(string path, int count, out byte[] buffer, out int length)
        {
            buffer = null;
            length = 0;
            using (var stream = new FileStream(Infrastructure.PathUtil.Extend(path), FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                length = (int)Math.Min(count, stream.Length);
                if (length <= 8) { return false; }
                buffer = new byte[length];
                length = stream.Read(buffer, 0, length);
                return length > 8;
            }
        }

        private static bool TryReadTail(string path, int count, out byte[] buffer, out int length)
        {
            buffer = null;
            length = 0;
            using (var stream = new FileStream(Infrastructure.PathUtil.Extend(path), FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length <= 8) { return false; }
                long start = Math.Max(0, stream.Length - count);
                stream.Seek(start, SeekOrigin.Begin);
                length = (int)(stream.Length - start);
                buffer = new byte[length];
                length = stream.Read(buffer, 0, length);
                return length > 8;
            }
        }

        /// <summary>在缓冲区中查找盒类型（形如 m v h d 的四个 ASCII 字节）。</summary>
        private static int IndexOfBox(byte[] buffer, int length, string boxType)
        {
            if (string.IsNullOrEmpty(boxType) || buffer == null) { return -1; }
            byte[] pattern = Encoding.ASCII.GetBytes(boxType);
            int limit = length - pattern.Length;
            for (int i = 0; i <= limit; i++)
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

        /// <summary>定位 TIFF 头（JPEG 的 APP1 段、RAW 文件开头，或容器内的 Exif 标识）。</summary>
        private static int FindTiffHeader(byte[] buffer, int length)
        {
            if (length >= 8 && IsTiffHeader(buffer, 0)) { return 0; }

            byte[] marker = Encoding.ASCII.GetBytes("Exif");
            int index = IndexOfBox(buffer, length, "Exif");
            if (index >= 0 && index + 6 < length && IsTiffHeader(buffer, index + 6)) { return index + 6; }
            if (index < 0) { index = 0; }
            for (int i = index; i + 8 < length; i++)
            {
                if (IsTiffHeader(buffer, i)) { return i; }
            }
            return -1;
        }

        private static bool IsTiffHeader(byte[] buffer, int offset)
        {
            if (offset < 0 || offset + 8 > buffer.Length) { return false; }
            bool little = buffer[offset] == 0x49 && buffer[offset + 1] == 0x49;
            bool big = buffer[offset] == 0x4D && buffer[offset + 1] == 0x4D;
            if (!little && !big) { return false; }
            if (little) { return buffer[offset + 2] == 0x2A && buffer[offset + 3] == 0x00; }
            return buffer[offset + 2] == 0x00 && buffer[offset + 3] == 0x2A;
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

        private static ulong ReadUInt64(byte[] buffer, int length, int offset)
        {
            if (offset < 0 || offset + 8 > length) { return 0; }
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | buffer[offset + i];
            }
            return value;
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
