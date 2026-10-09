using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Formatting;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Tests
{
    /// <summary>
    /// 格式化安全护栏的测试。
    /// 注意：这些用例只验证"拒绝路径"，绝不会真的执行格式化。
    /// </summary>
    public static class FormatTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("系统盘永远不会被允许格式化", () =>
            {
                string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                DiskSafetyReport report = DiskSafetyInspector.Inspect(systemRoot, true, NullLog.Instance);
                Assert.False(report.CanFormat, "系统盘必须被拒绝");
                Assert.Contains("系统盘", report.Reason);
            });

            yield return Case("没有完成校验的卡片不允许格式化", () =>
            {
                using (var temp = new TempDirectory("safety"))
                {
                    DiskSafetyReport report = DiskSafetyInspector.Inspect(temp.Path, false, NullLog.Instance);
                    Assert.False(report.CanFormat, "未完成拷贝校验时必须拒绝");
                }
            });

            yield return Case("本机固定磁盘不允许格式化", () =>
            {
                using (var temp = new TempDirectory("safety"))
                {
                    // 测试目录位于本机固定磁盘上：即使提供了"校验通过"凭证也必须被拒绝
                    string root = Path.GetPathRoot(temp.Path);
                    DiskSafetyReport report = DiskSafetyInspector.Inspect(root, true, NullLog.Instance);
                    Assert.False(report.CanFormat, "非 USB 外接设备必须被拒绝：" + report.Reason);
                }
            });

            yield return Case("网络路径不允许格式化", () =>
            {
                DiskSafetyReport report = DiskSafetyInspector.Inspect(@"\\127.0.0.1\share\", true, NullLog.Instance);
                Assert.False(report.CanFormat, "网络路径必须被拒绝");
            });

            yield return Case("格式化入口在护栏拒绝时不执行任何操作", () =>
            {
                string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                var formatter = new CardFormatter(NullLog.Instance);
                FormatResult result = formatter.Format(systemRoot, FormatFileSystem.ExFat, "TEST", hasVerifiedCopy: true);
                Assert.False(result.Success, "对系统盘的格式化请求必须失败");
                Assert.Contains("系统盘", result.Message);
            });

            yield return Case("没有校验凭证时清空卡片被拒绝且不删除文件", () =>
            {
                using (var temp = new TempDirectory("erase"))
                {
                    string file = temp.Combine("DCIM", "IMG_0001.CR3");
                    FixtureFactory.WriteFile(file, 1024);

                    var formatter = new CardFormatter(NullLog.Instance);
                    FormatResult result = formatter.EraseFiles(temp.Path, hasVerifiedCopy: false, null, CancellationToken.None);

                    Assert.False(result.Success, "没有校验凭证时必须拒绝清空");
                    Assert.True(File.Exists(file), "被拒绝的操作绝不能删除任何文件");
                }
            });

            yield return Case("有校验凭证时清空卡片只删除文件保留目录", () =>
            {
                using (var temp = new TempDirectory("erase"))
                {
                    string folder = temp.Combine("DCIM", "100CANON");
                    FixtureFactory.WriteFile(Path.Combine(folder, "IMG_0001.CR3"), 1024);
                    FixtureFactory.WriteFile(Path.Combine(folder, "IMG_0002.CR3"), 1024);

                    var formatter = new CardFormatter(NullLog.Instance);
                    FormatResult result = formatter.EraseFiles(temp.Path, hasVerifiedCopy: true, null, CancellationToken.None);

                    Assert.True(result.Success, result.Message);
                    Assert.True(Directory.Exists(folder), "目录结构应当保留，方便相机继续使用");
                    Assert.Equal(0, Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).Length);
                }
            });
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
