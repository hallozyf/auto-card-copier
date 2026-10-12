using System;
using System.Collections.Generic;
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
    /// <summary>F11 多目标备份与 F12 精确断点续传的测试。</summary>
    public static class BackupAndResumeTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            // ---------------- F11 多目标备份 ----------------

            yield return Case("F11 目标解析：未设置主目标时报错", () =>
            {
                AppSettings settings = AppSettings.CreateDefault();
                var coordinator = NewCoordinator();
                Assert.False(coordinator.ResolveTargets(settings).Success);
            });

            yield return Case("F11 目标解析：副目标与主目标相同会被拒绝", () =>
            {
                using (var primary = new TempDirectory("target"))
                {
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.PrimaryTargetRoot = primary.Path;
                    settings.SecondaryTargetRoot = primary.Path;

                    var coordinator = NewCoordinator();
                    var result = coordinator.ResolveTargets(settings);
                    Assert.False(result.Success);
                    Assert.Contains("不能是同一个目录", result.Message);
                }
            });

            yield return Case("F11 目标解析：副目标离线时只写主目标且不报错", () =>
            {
                using (var primary = new TempDirectory("target"))
                {
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.PrimaryTargetRoot = primary.Path;
                    settings.SecondaryTargetRoot = Path.Combine(primary.Path, "不存在的副目标");

                    var coordinator = NewCoordinator();
                    var result = coordinator.ResolveTargets(settings);
                    Assert.True(result.Success, result.Message);
                    Assert.Equal(1, result.Value.Count);
                }
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
                    var coordinator = NewCoordinator();
                    var plansResult = coordinator.BuildPlans(scan, settings, NewContext());

                    Assert.True(plansResult.Success, plansResult.Message);
                    Assert.Equal(2, plansResult.Value.Count);
                    Assert.Equal(primary.Path, plansResult.Value[0].TargetRoot);
                    Assert.Equal(secondary.Path, plansResult.Value[1].TargetRoot);
                    Assert.Equal(plansResult.Value[0].ProjectRelativePath, plansResult.Value[1].ProjectRelativePath);
                    Assert.False(plansResult.Value[0].ProjectDirectory == plansResult.Value[1].ProjectDirectory);
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
                    IList<CopyPlan> plans = coordinator.BuildPlans(scan, settings, NewContext()).Value;

                    BackupResult result = coordinator.RunAsync(plans, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.True(result.AllSucceeded, result.Summary);
                    Assert.True(result.AnySucceeded);
                    Assert.Equal(2, result.Targets.Count);
                    Assert.Contains("2 个目标", result.Summary);

                    // 两个目标都必须有完整素材
                    foreach (BackupTargetResult target in result.Targets)
                    {
                        int files;
                        long bytes;
                        FixtureFactory.MeasureDirectory(target.Plan.ProjectDirectory, out files, out bytes);
                        Assert.Equal(4, files);
                        Assert.Equal(scan.TotalBytes, bytes);
                        Assert.True(target.Verify.AllPassed, target.Message);
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
                    IList<CopyPlan> plans = coordinator.BuildPlans(scan, settings, NewContext()).Value;

                    // 计划生成后让副目标消失，模拟移动硬盘被拔掉
                    Directory.Delete(secondary.Path, true);

                    BackupResult result = coordinator.RunAsync(plans, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.False(result.AllSucceeded);
                    Assert.True(result.AnySucceeded, "主目标应当已经拷贝成功");
                    Assert.Equal(1, result.FailedTargets().Count);
                    Assert.Contains("部分目标成功", result.Summary);
                    Assert.True(result.Primary.Succeeded);

                    int files;
                    long bytes;
                    FixtureFactory.MeasureDirectory(result.Primary.Plan.ProjectDirectory, out files, out bytes);
                    Assert.Equal(2, files);
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

                    // 模拟第一轮只完成了前两个文件（例如中途断电）
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

                    // 重新载入并续传
                    TaskPlanFile reloaded = planStore.Load("task-2");
                    IList<CopyPlan> resumePlans = TaskPlanStore.ToPlans(reloaded, NullLog.Instance);
                    Assert.Equal(1, resumePlans.Count);
                    Assert.Equal(2, resumePlans[0].Items.Count - resumePlans[0].PendingCount);
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
