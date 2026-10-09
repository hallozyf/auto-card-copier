using System;
using System.Collections.Generic;
using System.IO;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Tests
{
    /// <summary>设置存储与日志模块的单元测试。</summary>
    public static class SettingsAndLoggingTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("设置首次加载返回默认值", () =>
            {
                using (var temp = new TempDirectory("settings"))
                {
                    var store = new SettingsStore(temp.Combine("settings.json"), NullLog.Instance);
                    var settings = store.Load();
                    Assert.Equal(4, settings.MaxParallelism);
                    Assert.Equal("{yyyy}/{yyyy-MM-dd}_{type}_{location}", settings.NameTemplate);
                    Assert.Equal(VerifyLevel.SizeAndSample, settings.VerifyLevel);
                    Assert.False(settings.AutoFormatAfterVerify);
                }
            });

            yield return Case("设置保存后可原样读回", () =>
            {
                using (var temp = new TempDirectory("settings"))
                {
                    string path = temp.Combine("settings.json");
                    var store = new SettingsStore(path, NullLog.Instance);
                    var settings = store.Load();
                    settings.PrimaryTargetRoot = @"Z:\2026";
                    settings.MaxParallelism = 6;
                    settings.Theme = ThemeMode.Dark;
                    settings.IgnoredDirectories = new[] { "System Volume Information" };

                    var saved = store.Save(settings);
                    Assert.True(saved.Success, saved.Message);

                    var reloaded = new SettingsStore(path, NullLog.Instance).Load();
                    Assert.Equal(@"Z:\2026", reloaded.PrimaryTargetRoot);
                    Assert.Equal(6, reloaded.MaxParallelism);
                    Assert.Equal(ThemeMode.Dark, reloaded.Theme);
                }
            });

            yield return Case("配置损坏时自愈并备份", () =>
            {
                using (var temp = new TempDirectory("settings"))
                {
                    string path = temp.Combine("settings.json");
                    File.WriteAllText(path, "{ 这不是合法的 JSON ");
                    var log = new MemoryLog();
                    var store = new SettingsStore(path, log);

                    var settings = store.Load();
                    Assert.Equal(4, settings.MaxParallelism);
                    Assert.True(File.Exists(path + ".bad"), "应当生成 .bad 备份文件");
                    Assert.True(log.Lines.Count > 0, "应当记录错误日志");
                }
            });

            yield return Case("非法设置值被归一化", () =>
            {
                var settings = AppSettings.CreateDefault();
                settings.MaxParallelism = 99;
                settings.BufferSizeKb = 1;
                settings.LogRetentionDays = 0;
                settings.NameTemplate = "   ";
                settings.IgnoredDirectories = null;
                settings.Normalize();

                Assert.Equal(8, settings.MaxParallelism);
                Assert.Equal(64, settings.BufferSizeKb);
                Assert.Equal(7, settings.LogRetentionDays);
                Assert.Equal("{yyyy}/{yyyy-MM-dd}_{type}_{location}", settings.NameTemplate);
                Assert.NotNull(settings.IgnoredDirectories);
            });

            yield return Case("文件日志写入与读取", () =>
            {
                using (var temp = new TempDirectory("log"))
                {
                    using (var logger = new FileLogger(temp.Path, "run"))
                    {
                        logger.Info("开始拷贝");
                        logger.Warn("网络较慢");
                        logger.Error("拷贝失败", new InvalidOperationException("磁盘已满"));
                    }

                    var files = Directory.GetFiles(temp.Path, "*.log");
                    Assert.Equal(1, files.Length);
                    string content = File.ReadAllText(files[0]);
                    Assert.Contains("开始拷贝", content);
                    Assert.Contains("[WARN]", content);
                    Assert.Contains("磁盘已满", content);
                    Assert.Contains("InvalidOperationException", content);
                }
            });

            yield return Case("文件日志超过上限时切换文件", () =>
            {
                using (var temp = new TempDirectory("log"))
                {
                    using (var logger = new FileLogger(temp.Path, "run", maxBytesPerFile: 512))
                    {
                        for (int i = 0; i < 40; i++) { logger.Info("这是一条用于撑大日志文件的测试消息 " + i); }
                    }
                    var files = Directory.GetFiles(temp.Path, "*.log");
                    Assert.True(files.Length >= 2, "应当产生多个日志文件，实际 " + files.Length);
                }
            });

            yield return Case("任务流水日志追加与读取", () =>
            {
                using (var temp = new TempDirectory("journal"))
                {
                    using (var journal = new TaskJournal(temp.Path, "task-1"))
                    {
                        journal.Append("started", new KeyValuePair<string, object>("files", 10));
                        journal.Append("completed", new KeyValuePair<string, object>("bytes", 1024L));
                    }

                    string path = Path.Combine(temp.Path, "task-1.jsonl");
                    Assert.True(File.Exists(path));
                    var lines = File.ReadAllLines(path);
                    Assert.Equal(2, lines.Length);
                    Assert.Contains("\"event\":\"started\"", lines[0]);
                    Assert.Contains("\"files\":10", lines[0]);
                }
            });
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
