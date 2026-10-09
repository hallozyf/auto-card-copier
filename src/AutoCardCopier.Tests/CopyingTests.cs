using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Tests
{
    /// <summary>命名模板、拷贝计划、拷贝引擎、校验与清单的单元与集成测试。</summary>
    public static class CopyingTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("命名模板展开日期与字段", () =>
            {
                var context = new ProjectContext
                {
                    ShootDate = new DateTime(2026, 3, 10, 15, 30, 0),
                    ShootType = "婚礼",
                    Location = "上海外滩"
                };
                string path = NameTemplateEngine.Expand("{yyyy}/{yyyy-MM-dd}_{type}_{location}", context);
                Assert.Equal("2026/2026-03-10_婚礼_上海外滩", path);
            });

            yield return Case("命名模板清洗非法字符并保留 emoji", () =>
            {
                var context = new ProjectContext
                {
                    ShootDate = new DateTime(2026, 3, 10),
                    ShootType = "婚礼",
                    Location = "上海:外滩*🎉?"
                };
                string path = NameTemplateEngine.Expand(NameTemplateEngine.DefaultTemplate, context);
                Assert.False(path.Contains(":"), path);
                Assert.False(path.Contains("*"), path);
                Assert.False(path.Contains("?"), path);
                Assert.Contains("🎉", path);
                Assert.Contains("2026", path);
            });

            yield return Case("命名模板保留未知占位符并丢弃空段", () =>
            {
                var context = new ProjectContext
                {
                    ShootDate = new DateTime(2026, 1, 2),
                    ShootType = string.Empty,
                    Location = "北京"
                };
                string path = NameTemplateEngine.Expand("{yyyy}/{unknown}/{location}", context);
                Assert.Equal("2026/{unknown}/北京", path);
            });

            yield return Case("拷贝计划生成正确的目录结构", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 3, videoCount: 1, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);

                    var context = new ProjectContext
                    {
                        ShootDate = new DateTime(2026, 3, 10),
                        ShootType = "婚礼",
                        Location = "上海外滩"
                    };

                    var builder = new CopyPlanBuilder(NullLog.Instance);
                    var planResult = builder.Build(scan, settings, context, target.Path);
                    Assert.True(planResult.Success, planResult.Message);

                    CopyPlan plan = planResult.Value;
                    Assert.Equal(4, plan.TotalCount);
                    Assert.True(plan.ProjectDirectory.StartsWith(target.Path, StringComparison.OrdinalIgnoreCase));
                    Assert.Contains("2026", plan.ProjectDirectory);
                    Assert.Contains("婚礼", plan.ProjectDirectory);

                    int photoItems = 0, videoItems = 0;
                    foreach (CopyItem item in plan.Items)
                    {
                        if (item.Kind == MediaKind.Photo) { photoItems++; Assert.Contains("照片", item.TargetPath); }
                        if (item.Kind == MediaKind.Video) { videoItems++; Assert.Contains("视频", item.TargetPath); }
                    }
                    Assert.Equal(3, photoItems);
                    Assert.Equal(1, videoItems);
                }
            });

            yield return Case("目标目录已存在时自动改名", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, 1, 0, 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var context = new ProjectContext { ShootDate = new DateTime(2026, 3, 10), ShootType = "婚礼", Location = "上海" };

                    var builder = new CopyPlanBuilder(NullLog.Instance);
                    CopyPlan first = builder.Build(scan, settings, context, target.Path).Value;
                    Directory.CreateDirectory(first.ProjectDirectory);

                    CopyPlan second = builder.Build(scan, settings, context, target.Path).Value;
                    Assert.False(second.ProjectDirectory == first.ProjectDirectory, "第二次应当生成不同的目录");
                    Assert.Contains("_2", second.ProjectDirectory);
                }
            });

            yield return Case("预检能拦住不存在的目标目录", () =>
            {
                using (var card = new TempDirectory("card"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, 1, 0, 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var builder = new CopyPlanBuilder(NullLog.Instance);

                    var planResult = builder.Build(scan, settings, ProjectContext.CreateDefault(), card.Path);
                    Assert.False(planResult.Success, "目标目录位于源内部时应当被拒绝");
                }
            });

            yield return Case("端到端拷贝并校验通过", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 4, videoCount: 1, otherCount: 0,
                        photoSize: 4096, videoSize: 8192);

                    AppSettings settings = AppSettings.CreateDefault();
                    settings.VerifyLevel = VerifyLevel.SizeAndSample;
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);

                    var builder = new CopyPlanBuilder(NullLog.Instance);
                    var context = new ProjectContext { ShootDate = new DateTime(2026, 3, 10), ShootType = "活动会议", Location = "杭州" };
                    CopyPlan plan = builder.Build(scan, settings, context, target.Path).Value;

                    var preflight = builder.Preflight(plan);
                    Assert.True(preflight.CanProceed, preflight.Message);

                    var engine = new CopyEngine(NullLog.Instance);
                    CopyResult copy = engine.RunAsync(plan, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert.Equal(5, copy.CopiedFiles);
                    Assert.Equal(0, copy.FailedFiles);
                    Assert.True(copy.AllSucceeded);

                    // 目标文件数与字节数必须与源一致
                    int targetFiles;
                    long targetBytes;
                    FixtureFactory.MeasureDirectory(plan.ProjectDirectory, out targetFiles, out targetBytes);
                    Assert.Equal(5, targetFiles);
                    Assert.Equal(scan.TotalBytes, targetBytes);

                    // 时间戳必须保留
                    foreach (CopyItem item in plan.Items)
                    {
                        Assert.True(File.Exists(item.TargetPath), "目标文件应当存在：" + item.TargetPath);
                        Assert.Equal(new FileInfo(item.Source.FullPath).LastWriteTimeUtc,
                            new FileInfo(item.TargetPath).LastWriteTimeUtc);
                    }

                    VerifyReport report = VerifyService.Verify(plan, settings, null, CancellationToken.None, NullLog.Instance);
                    Assert.True(report.AllPassed, report.Summary);
                    Assert.Equal(5, report.PassedFiles);

                    // 目标目录不应残留临时文件
                    string[] temps = Directory.GetFiles(plan.ProjectDirectory, "*" + ".acctmp", SearchOption.AllDirectories);
                    Assert.Equal(0, temps.Length);
                }
            });

            yield return Case("篡改目标文件后校验能发现问题", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 2, videoCount: 0, otherCount: 0, photoSize: 4096);
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.VerifyLevel = VerifyLevel.SizeAndSample;
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    CopyPlan plan = new CopyPlanBuilder(NullLog.Instance)
                        .Build(scan, settings, new ProjectContext { ShootDate = DateTime.Now, ShootType = "其他", Location = "测试" }, target.Path).Value;

                    new CopyEngine(NullLog.Instance).RunAsync(plan, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    // 篡改一个字节（不改长度）
                    CopyItem first = plan.Items[0];
                    using (var stream = new FileStream(first.TargetPath, FileMode.Open, FileAccess.ReadWrite))
                    {
                        stream.Seek(10, SeekOrigin.Begin);
                        int original = stream.ReadByte();
                        stream.Seek(10, SeekOrigin.Begin);
                        stream.WriteByte((byte)(original ^ 0xFF));
                    }

                    VerifyReport report = VerifyService.Verify(plan, settings, null, CancellationToken.None, NullLog.Instance);
                    Assert.False(report.AllPassed, "内容被篡改后应当校验失败");
                    Assert.Equal(1, report.FailedFiles);
                }
            });

            yield return Case("CRC32 与标准测试向量一致", () =>
            {
                using (var temp = new TempDirectory("crc"))
                {
                    string path = temp.Combine("check.txt");
                    File.WriteAllText(path, "123456789");
                    Assert.Equal("CBF43926", HashUtil.ComputeCrc32(path).ToString("X8"));
                }
            });

            yield return Case("SHA-256 与抽样哈希稳定", () =>
            {
                using (var temp = new TempDirectory("hash"))
                {
                    string path = temp.Combine("sample.bin");
                    FixtureFactory.WriteFile(path, 4 * 1024 * 1024, 42);

                    string first = HashUtil.ComputeSampledHash(path);
                    string second = HashUtil.ComputeSampledHash(path);
                    Assert.Equal(first, second);
                    Assert.True(first.StartsWith("S:"), "大文件应当走抽样哈希：" + first);

                    string sha = HashUtil.ComputeSha256(path);
                    Assert.Equal(64, sha.Length);
                }
            });

            yield return Case("拷贝被取消时不残留临时文件", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateManyFiles(card.Combine("DCIM", "100CANON"), 40, 200 * 1024);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    CopyPlan plan = new CopyPlanBuilder(NullLog.Instance)
                        .Build(scan, settings, new ProjectContext { ShootDate = DateTime.Now, ShootType = "其他", Location = "取消测试" }, target.Path).Value;

                    // 开始前就取消：这是确定性场景，用于验证取消路径不产生半截文件
                    var tokenSource = new CancellationTokenSource();
                    tokenSource.Cancel();
                    CopyResult result = new CopyEngine(NullLog.Instance)
                        .RunAsync(plan, settings, null, new PauseToken(), tokenSource.Token).GetAwaiter().GetResult();

                    Assert.True(result.Cancelled, "任务应当被标记为已取消");
                    string[] temps = Directory.GetFiles(plan.ProjectDirectory, "*.acctmp", SearchOption.AllDirectories);
                    Assert.Equal(0, temps.Length);
                }
            });

            yield return Case("重复拷贝会按策略跳过已存在文件", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 2, videoCount: 0, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    // 选择合并策略，第二次计划才会落在同一目录，从而触发跳过判断
                    settings.ConflictPolicy = ConflictPolicy.Merge;
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var builder = new CopyPlanBuilder(NullLog.Instance);
                    var context = new ProjectContext { ShootDate = new DateTime(2026, 3, 10), ShootType = "其他", Location = "跳过测试" };

                    CopyPlan first = builder.Build(scan, settings, context, target.Path).Value;
                    new CopyEngine(NullLog.Instance).RunAsync(first, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    CopyPlan second = builder.Build(scan, settings, context, target.Path).Value;
                    Assert.Equal(0, second.PendingCount);
                    Assert.Equal(2, second.Items.Count);
                    Assert.Equal("目标已存在且大小一致", second.Items[0].SkipReason);
                }
            });

            yield return Case("写入清单与项目元数据并可重新解析", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 3, videoCount: 1, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var context = new ProjectContext { ShootDate = new DateTime(2026, 3, 10), ShootType = "商业产品", Location = "深圳" };
                    CopyPlan plan = new CopyPlanBuilder(NullLog.Instance).Build(scan, settings, context, target.Path).Value;

                    CopyResult copy = new CopyEngine(NullLog.Instance)
                        .RunAsync(plan, settings, null, new PauseToken(), CancellationToken.None).GetAwaiter().GetResult();
                    VerifyReport verify = VerifyService.Verify(plan, settings, null, CancellationToken.None, NullLog.Instance);

                    var identity = new CardIdentity { SerialNumber = 0xABCD1234, Label = "EOS_DIGITAL", TotalBytes = 128L * 1024 * 1024 * 1024 };
                    string projectId = ProjectMeta.NewId();
                    var writer = new ManifestWriter(NullLog.Instance);
                    var writeResult = writer.WriteProjectFiles(plan, scan, context, settings, copy, verify, identity, projectId);
                    Assert.True(writeResult.Success, writeResult.Message);

                    string manifestPath = Path.Combine(plan.ProjectDirectory, ManifestWriter.ManifestFileName);
                    string projectPath = Path.Combine(plan.ProjectDirectory, ManifestWriter.ProjectFileName);
                    string logPath = Path.Combine(plan.ProjectDirectory, ManifestWriter.CopyLogFileName);
                    Assert.True(File.Exists(manifestPath), "应当生成 _manifest.json");
                    Assert.True(File.Exists(projectPath), "应当生成 _project.json");
                    Assert.True(File.Exists(logPath), "应当生成 _copy.log");

                    ManifestModel parsed = JsonStore.Deserialize<ManifestModel>(JsonStore.ReadAllText(manifestPath));
                    Assert.Equal(4, parsed.Files.Count);
                    Assert.Equal(4, parsed.Summary.FileCount);
                    Assert.Equal("深圳", JsonStore.Deserialize<ProjectMeta>(JsonStore.ReadAllText(projectPath)).Location);

                    string csv = Path.Combine(target.Path, "report.csv");
                    Assert.True(ManifestWriter.ExportCsv(parsed, csv).Success);
                    Assert.True(File.Exists(csv));
                }
            });
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
