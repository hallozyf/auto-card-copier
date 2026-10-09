using System;
using System.Globalization;

namespace AutoCardCopier.Core.Devices
{
    /// <summary>
    /// 卡片身份指纹：由卷序列号、卷标与总容量组合而成。
    /// 用途：
    /// 1. 识别"同一张卡"（盘符变化不影响）；
    /// 2. 防止同一张卡被重复导入；
    /// 3. 格式化前确认目标卡确实是刚刚拷贝成功的那张。
    /// </summary>
    public sealed class CardIdentity
    {
        /// <summary>卷序列号。</summary>
        public uint SerialNumber { get; set; }

        /// <summary>卷标。</summary>
        public string Label { get; set; }

        /// <summary>总容量（字节）。</summary>
        public long TotalBytes { get; set; }

        /// <summary>指纹字符串（十六进制序列号 + 卷标 + 容量）。</summary>
        public string Fingerprint
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:X8}|{1}|{2}",
                    SerialNumber, (Label ?? string.Empty).Trim(), TotalBytes);
            }
        }

        /// <summary>面向用户的简短描述。</summary>
        public string Display
        {
            get
            {
                string label = string.IsNullOrWhiteSpace(Label) ? "未命名卡" : Label.Trim();
                return label + "（" + Infrastructure.Humanize.Bytes(TotalBytes) + "）";
            }
        }

        /// <summary>根据卷信息创建指纹。</summary>
        public static CardIdentity FromVolume(VolumeInfo volume)
        {
            if (volume == null) { throw new ArgumentNullException("volume"); }
            return new CardIdentity
            {
                SerialNumber = volume.SerialNumber,
                Label = volume.Label,
                TotalBytes = volume.TotalBytes
            };
        }
    }
}
