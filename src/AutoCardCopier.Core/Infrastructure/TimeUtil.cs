using System;
using System.Globalization;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// 时间工具：项目文件中的时间统一使用 ISO 8601 字符串存储，
    /// 既不依赖序列化器的时间格式（DataContractJsonSerializer 会写成 /Date(...)/），
    /// 也方便人工阅读与跨工具解析。
    /// </summary>
    public static class TimeUtil
    {
        /// <summary>ISO 8601 本地时间格式（精确到秒）。</summary>
        public const string IsoFormat = "yyyy-MM-dd'T'HH:mm:ss";

        /// <summary>把时间转换为 ISO 字符串（本地时间）。</summary>
        public static string ToIso(DateTime time)
        {
            return time.ToString(IsoFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>把 UTC 时间转换为 ISO 字符串。</summary>
        public static string ToIsoUtc(DateTime time)
        {
            return time.ToUniversalTime().ToString(IsoFormat, CultureInfo.InvariantCulture) + "Z";
        }

        /// <summary>解析 ISO 字符串，失败时返回 fallback。</summary>
        public static DateTime ParseIso(string text, DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) { return fallback; }
            DateTime parsed;
            // 带 Z 后缀的按 UTC 解析并转本地时间；不带后缀的视为本地时间原样使用
            if (text.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.TryParse(text.TrimEnd('Z', 'z'), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                {
                    return parsed.ToLocalTime();
                }
            }
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        /// <summary>格式化为界面显示用的日期时间。</summary>
        public static string Display(DateTime time)
        {
            return time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>格式化为界面显示用的日期。</summary>
        public static string DisplayDate(DateTime time)
        {
            return time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
