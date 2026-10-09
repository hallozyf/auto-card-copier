using System;
using System.Globalization;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// 面向界面的数值格式化工具：字节数、速度、时长、数量。
    /// 统一放在这里，避免各处重复实现导致单位不一致。
    /// </summary>
    public static class Humanize
    {
        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>把字节数格式化为带单位的字符串，例如 268.4 GB。</summary>
        public static string Bytes(long bytes)
        {
            if (bytes < 0) { return "-" + Bytes(-bytes); }
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < SizeUnits.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            // 字节不带小数，KB 及以上保留一位小数，读起来更自然
            return unit == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:0} B", value)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", value, SizeUnits[unit]);
        }

        /// <summary>把每秒字节数格式化为速度，例如 145.2 MB/s。</summary>
        public static string Speed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) { return "0 B/s"; }
            return Bytes((long)bytesPerSecond) + "/s";
        }

        /// <summary>把时长格式化为 时:分:秒 或 分:秒。</summary>
        public static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 0) { span = TimeSpan.Zero; }
            if (span.TotalHours >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
                    (int)span.TotalHours, span.Minutes, span.Seconds);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", span.Minutes, span.Seconds);
        }

        /// <summary>把剩余秒数格式化为"约 x"文本，用于剩余时间显示。</summary>
        public static string Remaining(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) { return "计算中…"; }
            return Duration(TimeSpan.FromSeconds(seconds));
        }

        /// <summary>把整数格式化为带千位分隔符的字符串。</summary>
        public static string Count(long count)
        {
            return count.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>把 0 到 1 的比例格式化为百分比。</summary>
        public static string Percent(double ratio)
        {
            if (double.IsNaN(ratio)) { return "0%"; }
            if (ratio < 0) { ratio = 0; }
            if (ratio > 1) { ratio = 1; }
            return string.Format(CultureInfo.InvariantCulture, "{0:0}%", ratio * 100);
        }
    }
}
