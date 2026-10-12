using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.History;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Tests
{
    /// <summary>F11 多目标备份与 F12 精确断点续传的测试（v0.3.0 重写版）。</summary>
    public static class BackupAndResumeTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            // ---------------- F11 多目标备份 ----------------

            yield return Case("F11 目标解析：未设置主目标时报错", () =>
            {
                var coordinator = NewCoordinator();
                Assert.False(coordinator.ResolveTargets(AppSettings.CreateDefault()).Success);
            });

            yield return Case("F11 目标解析：副目标与主目标相同会被拒绝", () =>
            {
                AppSettings settings = AppSettings.CreateDefault();
                settings.PrimaryTargetRoot = @"H:\photos";
                settings.SecondaryTargetRoot = @"H:\photos\";

                var result = NewCoordinator().ResolveTargets(settings);
                Assert.False(result.Success);
                Assert.Contains("不能是同一个目录", result.Message);
            });

            yield return Case("F11 目标解析是纯内存操作（不做文件系统访问）", () =>
            {
                AppSettings settings = AppSettings.CreateDefault();
                settings.PrimaryTargetRoot = @"Z:\不存在的网络路径\a";
                settings.SecondaryTargetRoot = @"Y:\同样不存在\b";

                var stopwatch = Stopwatch.StartNew();
                var result = NewCoordinator().ResolveTargets(settings);
                stopwatch.Stop();

                Assert.True(result.Success);
                Assert.Equal(2, result.Value.Count);
                Assert.True(stopwatch.ElapsedMilliseconds < 50, "解析目标不应有任何 I/O 开销");
            });

            yield return Case("F11 计划生成：两个目标各自生成同名项目目录", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var primary = new TempDirectory("primary"))
                using (var secondary = new TempDirectory("secondary"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 2, videoCount: 1, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.PrimaryTargetRoot = primary.Path;
                    settings.SecondaryTargetRoot = secondary.Path;

                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    BackupPlanSet planSet = NewCoordinator()
                        .BuildPlansAsync(NewGroups(scan, "2026\\日1_测试"), scan.RootPath, settings)
                        .GetAwaiter().GetResult().Value;

                    Assert.Equal(2, planSet.Units.Count);
                    Assert.Equal(0, planSet.UnavailableTargets.Count);
                    Assert.Equal(planSet.Units[0].Plan.ProjectRelativePath, planSet.Units[1].Plan.ProjectRelativePath);
                    Assert.False(planSet.Units[0].Plan.ProjectDirectory == planSet.Units[1].Plan.ProjectDirectory);
                }
            });

            yield return Case("F11 端到端：两个目标都必须写入并校验通过", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var primary = new TempDirectory("primary"))
                using (var secondary = new TempDirectory("secondary"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 3, videoCount: 1, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.PrimaryTargetRoot = primary.Path;
                    settings.SecondaryTargetRoot = secondary.Path;

                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var coordinator = NewCoordinator();
                    BackupPlanSet planSet = coordinator
                        .BuildPlansAsync(NewGroups(scan, "2026\\日1_测试"), scan.RootPath, settings)
                        .GetAwaiter().GetResult().Value;

                    BackupResult result = coordinator.RunAsync(planSet, scan, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.True(result.AllSucceeded, result.Summary);
                    Assert.Equal(2, result.Units.Count);
                    Assert.Contains("1 个项目全部完成", result.Summary);

                    foreach (BackupUnit unit in result.Units)
                    {
                        int files;
                        long bytes;
                        FixtureFactory.MeasureDirectory(unit.Plan.ProjectDirectory, out files, out bytes);
                        Assert.Equal(4, files);
                        Assert.Equal(scan.TotalBytes, bytes);
                        Assert.True(unit.Verify.AllPassed, unit.Message);
                    }
                }
            });

            yield return Case("F11 副目标掉线：主目标成功、整体标记部分成功", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var primary = new TempDirectory("primary"))
                using (var secondary = new TempDirectory("secondary"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 2, videoCount: 0, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.PrimaryTargetRoot = primary.Path;
                    settings.SecondaryTargetRoot = secondary.Path;

                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var coordinator = NewCoordinator();
                    BackupPlanSet planSet = coordinator
                        .BuildPlansAsync(NewGroups(scan, "2026\\日1_测试"), scan.RootPath, settings)
                        .GetAwaiter().GetResult().Value;

                    // 计划生成后让副目标消失，模拟移动硬盘被拔掉
                    Directory.Delete(secondary.Path, true);

                    BackupResult result = coordinator.RunAsync(planSet, scan, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.False(result.AllSucceeded);
                    Assert.True(result.AnySucceeded, "主目标应当已经拷贝成功");
                    Assert.Equal(1, result.FailedUnits().Count);
                    Assert.True(result.PrimaryUnit.Succeeded);
                }
            });

            yield return Case("F11 副目标从一开始就不可用：列入失败目标但不影响主目标", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var primary = new TempDirectory("primary"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 2, videoCount: 0, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.PrimaryTargetRoot = primary.Path;
                    settings.SecondaryTargetRoot = Path.Combine(primary.Path, "不存在的副目标");

                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var coordinator = NewCoordinator();
                    BackupPlanSet planSet = coordinator
                        .BuildPlansAsync(NewGroups(scan, "2026\\日1_测试"), scan.RootPath, settings)
                        .GetAwaiter().GetResult().Value;

                    Assert.Equal(1, planSet.Units.Count);
                    Assert.Equal(1, planSet.UnavailableTargets.Count);

                    BackupResult result = coordinator.RunAsync(planSet, scan, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.True(result.PrimaryUnit.Succeeded);
                    Assert.False(result.AllSucceeded, "存在不可用目标时不能算全部成功");
                    Assert.Equal(1, result.FailedUnits().Count);
                }
            });

            // ---------------- F12 精确断点续传 ----------------

            yield return Case("F12 计划文件可从拷贝计划生成并保存/读回", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                using (var store = new TempDirectory("plans"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 3, videoCount: 1, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    CopyPlan plan = new CopyPlanBuilder(NullLog.Instance)
                        .Build(scan, settings, NewContext(), target.Path).Value;

                    TaskPlanFile file = TaskPlanStore.FromPlans("task-1", "proj-1", card.Path,
                        new ProjectContextLike { ProjectName = plan.ProjectName, Location = "上海", ShootType = "婚礼" },
                        new List<CopyPlan> { plan });

                    Assert.Equal(4, file.TotalItemCount);
                    Assert.Equal(4, file.TotalPendingCount);
                    Assert.True(file.IsSourceAvailable());

                    var planStore = new TaskPlanStore(store.Path, NullLog.Instance);
                    Assert.True(planStore.Save(file).Success);

                    TaskPlanFile reloaded = planStore.Load("task-1");
                    Assert.NotNull(reloaded);
                    Assert.Equal(4, reloaded.TotalItemCount);
                    Assert.Equal("上海", reloaded.Location);
                    Assert.Equal(plan.ProjectDirectory, reloaded.Targets[0].ProjectDirectory);
                }
            });

            yield return Case("F12 续传只拷贝未完成的文件", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                using (var store = new TempDirectory("plans"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 4, videoCount: 1, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    CopyPlan plan = new CopyPlanBuilder(NullLog.Instance)
                        .Build(scan, settings, NewContext(), target.Path).Value;

                    TaskPlanFile file = TaskPlanStore.FromPlans("task-2", "proj-2", card.Path, null,
                        new List<CopyPlan> { plan });

                    Directory.CreateDirectory(plan.ProjectDirectory);
                    var partial = new CopyResult();
                    partial.EnsurePathCollections();
                    for (int i = 0; i < 2; i++)
                    {
                        CopyItem item = plan.Items[i];
                        string directory = Path.GetDirectoryName(item.TargetPath);
                        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
                        File.Copy(item.Source.FullPath, item.TargetPath, true);
                        partial.CompletedTargetPaths.Add(item.TargetPath);
                    }
                    TaskPlanStore.ApplyResult(file, plan.TargetRoot, partial);

                    Assert.Equal(2, file.Targets[0].DoneCount);
                    Assert.Equal(3, file.Targets[0].PendingCount);

                    var planStore = new TaskPlanStore(store.Path, NullLog.Instance);
                    planStore.Save(file);
                    IList<CopyPlan> resumePlans = TaskPlanStore.ToPlans(planStore.Load("task-2"), NullLog.Instance);

                    Assert.Equal(1, resumePlans.Count);
                    Assert.Equal(3, resumePlans[0].PendingCount);

                    CopyResult resumed = new CopyEngine(NullLog.Instance)
                        .RunAsync(resumePlans[0], settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.Equal(3, resumed.CopiedFiles);
                    Assert.Equal(0, resumed.FailedFiles);

                    int files;
                    long bytes;
                    FixtureFactory.MeasureDirectory(plan.ProjectDirectory, out files, out bytes);
                    Assert.Equal(5, files);
                    Assert.Equal(scan.TotalBytes, bytes);
                }
            });

            yield return Case("F12 续传还原的计划保留源文件时间与类型", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 1, videoCount: 1, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    CopyPlan plan = new CopyPlanBuilder(NullLog.Instance)
                        .Build(scan, settings, NewContext(), target.Path).Value;

                    TaskPlanFile file = TaskPlanStore.FromPlans("task-5", "proj-5", card.Path, null,
                        new List<CopyPlan> { plan });
                    IList<CopyPlan> restored = TaskPlanStore.ToPlans(file, NullLog.Instance);

                    Assert.Equal(plan.Items.Count, restored[0].Items.Count);
                    for (int i = 0; i < plan.Items.Count; i++)
                    {
                        Assert.Equal(plan.Items[i].TargetPath, restored[0].Items[i].TargetPath);
                        Assert.Equal(plan.Items[i].Kind, restored[0].Items[i].Kind);
                        Assert.Equal(plan.Items[i].Source.LastWriteTimeUtc, restored[0].Items[i].Source.LastWriteTimeUtc);
                    }
                }
            });

            yield return Case("F12 续传完成后可删除计划文件", () =>
            {
                using (var store = new TempDirectory("plans"))
                {
                    var planStore = new TaskPlanStore(store.Path, NullLog.Instance);
                    var file = new TaskPlanFile { TaskId = "task-3", SourceRoot = store.Path };
                    file.Targets.Add(new TaskPlanTarget { Root = store.Path, IsPrimary = true });
                    planStore.Save(file);
                    Assert.NotNull(planStore.Load("task-3"));

                    planStore.Delete("task-3");
                    Assert.Equal(null, planStore.Load("task-3"));
                }
            });

            yield return Case("F12 卡片不在时计划会被判定为不可续传", () =>
            {
                var file = new TaskPlanFile { TaskId = "task-4", SourceRoot = @"Z:\不存在的卡片路径" };
                Assert.False(file.IsSourceAvailable());
            });

            // ---------------- BUG-002 防护：后台安全与超时 ----------------

            yield return Case("BG-01 不可达目标由探测判定，不阻塞调用方", () =>
            {
                var stopwatch = Stopwatch.StartNew();
                TargetProbeResult probe = TargetProbe.CheckAsync(@"\\127.0.0.1\不存在的共享\子目录", 1500)
                    .GetAwaiter().GetResult();
                stopwatch.Stop();

                Assert.False(probe.Available);
                Assert.True(stopwatch.ElapsedMilliseconds < 4000, "探测必须在超时上限附近返回，实际 " + stopwatch.ElapsedMilliseconds + " ms");
                Assert.False(string.IsNullOrEmpty(probe.Message));
            });

            yield return Case("BG-02 可用目标探测通过并返回容量", () =>
            {
                using (var target = new TempDirectory("probe"))
                {
                    TargetProbeResult probe = TargetProbe.CheckAsync(target.Path, 3000).GetAwaiter().GetResult();
                    Assert.True(probe.Available, probe.Message);
                    Assert.True(probe.FreeBytes > 0, "应当能读到可用空间");
                }
            });

            yield return Case("BG-03 计划生成不做逐文件探测（400 个文件也必须秒级完成）", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateManyFiles(Path.Combine(card.Path, "DCIM", "100CANON"), 400, 128);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    Assert.Equal(400, scan.TotalCount);

                    var stopwatch = Stopwatch.StartNew();
                    OperationResult<CopyPlan> result = new CopyPlanBuilder(NullLog.Instance)
                        .Build(scan, settings, NewContext(), target.Path);
                    stopwatch.Stop();

                    Assert.True(result.Success, result.Message);
                    Assert.True(stopwatch.ElapsedMilliseconds < 1000,
                        "计划生成必须是纯内存计算，实际耗时 " + stopwatch.ElapsedMilliseconds + " ms");
                }
            });

            yield return Case("BG-04 计划阶段的动作全部为拷贝（跳过判断已移到工作线程）", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    FixtureFactory.CreateSimulatedCard(card.Path, photoCount: 3, videoCount: 0, otherCount: 0);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    CopyPlan plan = new CopyPlanBuilder(NullLog.Instance)
                        .Build(scan, settings, NewContext(), target.Path).Value;

                    foreach (CopyItem item in plan.Items)
                    {
                        Assert.Equal(CopyAction.Copy, item.Action);
                    }

                    // 第一次真实拷贝
                    CopyResult first = new CopyEngine(NullLog.Instance)
                        .RunAsync(plan, settings, null, new PauseToken(), CancellationToken.None).GetAwaiter().GetResult();
                    Assert.Equal(3, first.CopiedFiles);
                    Assert.Equal(0, first.SkippedFiles);

                    // 第二次：工作线程发现目标已存在且一致，应全部跳过
                    CopyResult second = new CopyEngine(NullLog.Instance)
                        .RunAsync(plan, settings, null, new PauseToken(), CancellationToken.None).GetAwaiter().GetResult();
                    Assert.Equal(0, second.CopiedFiles);
                    Assert.Equal(3, second.SkippedFiles);
                    Assert.Equal(3, second.SkippedTargetPaths.Count);
                }
            });

            yield return Case("BG-05 副目标为不可达网络路径时，计划阶段在超时上限内返回（BUG-002 复现场景）", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var primary = new TempDirectory("primary"))
                {
                    // 400 个文件：v0.2.0 会在这里对网络目标做 400 次同步探测而卡死界面
                    FixtureFactory.CreateManyFiles(Path.Combine(card.Path, "DCIM", "100CANON"), 400, 256);

                    AppSettings settings = AppSettings.CreateDefault();
                    settings.PrimaryTargetRoot = primary.Path;
                    settings.SecondaryTargetRoot = @"\\127.0.0.1\不存在的共享\备份";

                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);
                    var coordinator = NewCoordinator();

                    var stopwatch = Stopwatch.StartNew();
                    OperationResult<BackupPlanSet> result = coordinator
                        .BuildPlansAsync(NewGroups(scan, "2026\\日1_测试"), scan.RootPath, settings, 2000)
                        .GetAwaiter().GetResult();
                    stopwatch.Stop();

                    Assert.True(result.Success, result.Message);
                    Assert.Equal(1, result.Value.Units.Count);
                    Assert.Equal(1, result.Value.UnavailableTargets.Count);
                    Assert.Equal(400, result.Value.Units[0].Plan.TotalCount);
                    Assert.True(stopwatch.ElapsedMilliseconds < 4000,
                        "计划阶段必须在探测超时附近返回，实际 " + stopwatch.ElapsedMilliseconds + " ms");
                }
            });
        }

        /// <summary>构造一个"单项目"分组请求（测试用）。</summary>
        private static IList<ProjectGroupRequest> NewGroups(ScanResult scan, string relativeDirectory)
        {
            return new List<ProjectGroupRequest>
            {
                new ProjectGroupRequest
                {
                    Context = NewContext(),
                    Files = scan.Files,
                    RelativeDirectory = relativeDirectory,
                    DisplayName = "测试项目"
                }
            };
        }

        private static BackupCoordinator NewCoordinator()
        {
            return new BackupCoordinator(new CopyPlanBuilder(NullLog.Instance), new CopyEngine(NullLog.Instance), NullLog.Instance);
        }

        private static ProjectContext NewContext()
        {
            return new ProjectContext
            {
                ShootDate = new DateTime(2026, 3, 10),
                ShootType = "婚礼",
                Location = "上海"
            };
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
