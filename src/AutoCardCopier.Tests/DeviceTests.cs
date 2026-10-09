using System;
using System.Collections.Generic;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Tests
{
    /// <summary>卡片识别与卷信息读取的单元测试。</summary>
    public static class DeviceTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("卡片指纹稳定且可区分", () =>
            {
                var a = new CardIdentity { SerialNumber = 0x1A2B3C4D, Label = "EOS_DIGITAL", TotalBytes = 256L * 1024 * 1024 * 1024 };
                var b = new CardIdentity { SerialNumber = 0x1A2B3C4D, Label = "EOS_DIGITAL", TotalBytes = 256L * 1024 * 1024 * 1024 };
                var c = new CardIdentity { SerialNumber = 0x1A2B3C4E, Label = "EOS_DIGITAL", TotalBytes = 256L * 1024 * 1024 * 1024 };

                Assert.Equal(a.Fingerprint, b.Fingerprint);
                Assert.False(a.Fingerprint == c.Fingerprint);
                Assert.Contains("1A2B3C4D", a.Fingerprint);
                Assert.Contains("EOS_DIGITAL", a.Display);
            });

            yield return Case("读取系统盘卷信息", () =>
            {
                string systemRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                VolumeInfo info = VolumeApi.Read(systemRoot, NullLog.Instance);
                Assert.NotNull(info, "应当能读到系统盘卷信息");
                Assert.False(string.IsNullOrEmpty(info.FileSystem), "文件系统名称不应为空");
                Assert.True(info.TotalBytes > 0, "总容量应当大于 0");
            });

            yield return Case("枚举卷不会抛出异常", () =>
            {
                var volumes = VolumeApi.EnumerateVolumes(NullLog.Instance);
                Assert.NotNull(volumes);
                Assert.True(volumes.Count >= 1, "至少应当枚举到一个卷");
            });

            yield return Case("卡片检测不会把系统盘当作存储卡", () =>
            {
                using (var detector = new CardDetector(NullLog.Instance, pollSeconds: 0))
                {
                    var cards = detector.DetectCards();
                    string systemRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                    foreach (CardVolume card in cards)
                    {
                        Assert.False(string.Equals(card.RootPath.TrimEnd('\\'), systemRoot.TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase), "系统盘不应被识别为存储卡");
                    }
                }
            });
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
