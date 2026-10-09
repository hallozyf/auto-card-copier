using System;
using System.Collections.Generic;
using AutoCardCopier.Core.Infrastructure;

namespace AutoCardCopier.Tests
{
    /// <summary>基础设施层（格式化、路径、JSON、时间、事件总线、结果类型）的单元测试。</summary>
    public static class InfrastructureTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("Humanize.Bytes 各量级", () =>
            {
                Assert.Equal("0 B", Humanize.Bytes(0));
                Assert.Equal("512 B", Humanize.Bytes(512));
                Assert.Equal("1.0 KB", Humanize.Bytes(1024));
                Assert.Equal("1.5 KB", Humanize.Bytes(1536));
                Assert.Equal("1.0 MB", Humanize.Bytes(1024 * 1024));
                Assert.Equal("250.0 GB", Humanize.Bytes(250L * 1024 * 1024 * 1024));
            });

            yield return Case("Humanize.Speed 与 Duration", () =>
            {
                Assert.Equal("0 B/s", Humanize.Speed(0));
                Assert.Equal("1.0 MB/s", Humanize.Speed(1024 * 1024));
                Assert.Equal("3:12", Humanize.Duration(TimeSpan.FromSeconds(192)));
                Assert.Equal("1:02:03", Humanize.Duration(new TimeSpan(1, 2, 3)));
                Assert.Equal("计算中…", Humanize.Remaining(double.NaN));
            });

            yield return Case("Humanize.Count 与 Percent", () =>
            {
                Assert.Equal("3,842", Humanize.Count(3842));
                Assert.Equal("62%", Humanize.Percent(0.623));
                Assert.Equal("100%", Humanize.Percent(1.5));
            });

            yield return Case("PathUtil 判断 UNC 与网络路径", () =>
            {
                Assert.True(PathUtil.IsUnc(@"\\server\share"));
                Assert.False(PathUtil.IsUnc(@"C:\temp"));
                Assert.False(PathUtil.IsUnc(@"\\?\C:\temp"));
            });

            yield return Case("PathUtil.Extend 生成长路径前缀", () =>
            {
                Assert.Equal(@"\\?\C:\Photos\a.jpg", PathUtil.Extend(@"C:\Photos\a.jpg"));
                Assert.Equal(@"\\?\UNC\server\share\a.jpg", PathUtil.Extend(@"\\server\share\a.jpg"));
                // 已是扩展路径时不重复添加
                Assert.Equal(@"\\?\C:\Photos\a.jpg", PathUtil.Extend(@"\\?\C:\Photos\a.jpg"));
                // 相对路径不做转换
                Assert.Equal(@"photos\a.jpg", PathUtil.Extend(@"photos\a.jpg"));
            });

            yield return Case("PathUtil.Unextend 还原普通路径", () =>
            {
                Assert.Equal(@"C:\Photos\a.jpg", PathUtil.Unextend(@"\\?\C:\Photos\a.jpg"));
                Assert.Equal(@"\\server\share", PathUtil.Unextend(@"\\?\UNC\server\share"));
            });

            yield return Case("PathUtil.IsSubPathOf 判断包含关系", () =>
            {
                string parent = System.IO.Path.GetTempPath();
                string child = System.IO.Path.Combine(parent, "sub", "file.txt");
                Assert.True(PathUtil.IsSubPathOf(parent, child));
                Assert.False(PathUtil.IsSubPathOf(child, parent));
            });

            yield return Case("PathUtil.SanitizeName 清洗非法字符", () =>
            {
                string cleaned = PathUtil.SanitizeName("2026-03-10_婚礼:上海*外滩?");
                Assert.False(cleaned.Contains(":"), cleaned);
                Assert.False(cleaned.Contains("*"), cleaned);
                Assert.False(cleaned.Contains("?"), cleaned);
                Assert.Equal("abc", PathUtil.SanitizeName("  abc.  "));
                Assert.True(PathUtil.SanitizeName(new string('x', 300)).Length <= 100);
                Assert.Equal(string.Empty, PathUtil.SanitizeName(null));
            });

            yield return Case("PathUtil.Combine 处理多余分隔符", () =>
            {
                Assert.Equal(@"C:\a\b", PathUtil.Combine(@"C:\a\", "\\b"));
            });

            yield return Case("PathUtil.MakeUniqueName 追加序号", () =>
            {
                using (var temp = new TempDirectory("unique"))
                {
                    System.IO.Directory.CreateDirectory(temp.Combine("2026-03-10_婚礼_上海"));
                    System.IO.Directory.CreateDirectory(temp.Combine("2026-03-10_婚礼_上海_2"));
                    string name = PathUtil.MakeUniqueName(temp.Path, "2026-03-10_婚礼_上海");
                    Assert.Equal("2026-03-10_婚礼_上海_3", name);
                }
            });

            yield return Case("TimeUtil 往返解析", () =>
            {
                DateTime now = new DateTime(2026, 3, 10, 18, 40, 5, DateTimeKind.Local);
                string iso = TimeUtil.ToIso(now);
                Assert.Equal("2026-03-10T18:40:05", iso);
                Assert.Equal(now, TimeUtil.ParseIso(iso, DateTime.MinValue));
                Assert.Equal(DateTime.MinValue, TimeUtil.ParseIso("不是时间", DateTime.MinValue));
            });

            yield return Case("SimpleJson 转义与美化", () =>
            {
                Assert.Equal("\"a\\nb\"", SimpleJson.Quote("a\nb"));
                string pretty = SimpleJson.Pretty("{\"a\":1,\"b\":[1,2]}");
                Assert.Contains("\"a\": 1", pretty);
                Assert.Contains("[", pretty);
            });

            yield return Case("OperationResult 成功与失败", () =>
            {
                var ok = OperationResult.Ok("完成");
                Assert.True(ok.Success);
                Assert.Equal("完成", ok.Message);

                var fail = OperationResult.Fail("失败原因");
                Assert.False(fail.Success);
                Assert.Equal("失败原因", fail.Message);

                var okValue = OperationResult.Ok(42);
                Assert.Equal(42, okValue.Value);
            });

            yield return Case("EventBus 分发与异常隔离", () =>
            {
                var bus = new EventBus();
                int received = 0;
                Action<int> bad = value => { throw new InvalidOperationException("订阅者异常"); };
                Action<int> good = value => { received = value; };

                bus.Subscribe(bad);
                bus.Subscribe(good);
                bus.Publish(7);
                Assert.Equal(7, received);

                bus.Unsubscribe(good);
                bus.Publish(9);
                Assert.Equal(7, received);
            });

            yield return Case("JsonStore 序列化可读性与反序列化", () =>
            {
                var settings = AutoCardCopier.Core.Settings.AppSettings.CreateDefault();
                string json = JsonStore.Serialize(settings);
                Assert.Contains("\n", json);
                Assert.Contains("primaryTargetRoot", json);

                var restored = JsonStore.Deserialize<AutoCardCopier.Core.Settings.AppSettings>(json);
                Assert.Equal(settings.MaxParallelism, restored.MaxParallelism);
                Assert.Equal(settings.NameTemplate, restored.NameTemplate);
                Assert.Equal(settings.VerifyLevel, restored.VerifyLevel);
            });
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
