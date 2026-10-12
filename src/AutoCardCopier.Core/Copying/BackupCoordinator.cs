using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>一个待拷贝的项目分组（由界面按日期分组 + 用户命名后传入）。</summary>
    public sealed class ProjectGroupRequest
    {
        /// <summary>项目上下文（日期、类型、地点、摄影师、机型、卡信息）。</summary>
        public ProjectContext Context { get; set; }

        /// <summary>本项目包含的素材文件。</summary>
        public IList<ScannedFile> Files { get; set; }

        /// <summary>项目相对目录（含用户编辑后的项目名），例如 2026 下的 2026-10-03_人像写真_百旺公园。</summary>
        public string RelativeDirectory { get; set; }

        /// <summary>显示名（通常等于项目名）。</summary>
        public string DisplayName { get; set; }
    }

    /// <summary>一个执行单元：某个项目写入某个目标。</summary>
    public sealed class BackupUnit
    {
        public ProjectGroupRequest Group { get; set; }
        public string Root { get; set; }
        public bool IsPrimaryTarget { get; set; }
        public int ProjectIndex { get; set; }
        public int ProjectCount { get; set; }
        public TargetProbeResult Probe { get; set; }
        public PlanPreflight Preflight { get; set; }
        public CopyPlan Plan { get; set; }
        public CopyResult Copy { get; set; }
        public VerifyReport Verify { get; set; }
        public bool Succeeded { get; set; }
        public string Message { get; set; }

        /// <summary>执行单元显示名。</summary>
        public string DisplayName
        {
            get
            {
                string project = Group == null ? string.Empty : Group.DisplayName;
                return (IsPrimaryTarget ? "主目标 " : "副目标 ") + Root + " / " + project;
            }
        }
    }

    /// <summary>多目标、多项目的计划集合。</summary>
    public sealed class BackupPlanSet
    {
        public BackupPlanSet()
        {
            Units = new List<BackupUnit>();
            UnavailableTargets = new List<TargetProbeResult>();
            Groups = new List<ProjectGroupRequest>();
        }

        /// <summary>可执行的执行单元（项目 × 目标）。</summary>
        public IList<BackupUnit> Units { get; set; }

        /// <summary>探测失败或超时的目标。</summary>
        public IList<TargetProbeResult> UnavailableTargets { get; set; }

        /// <summary>本次涉及的项目分组。</summary>
        public IList<ProjectGroupRequest> Groups { get; set; }
    }

    /// <summary>按项目汇总的结果。</summary>
    public sealed class ProjectOutcome
    {
        public ProjectOutcome() { Units = new List<BackupUnit>(); }

        public string DisplayName { get; set; }
        public string ProjectDirectory { get; set; }
        public DateTime? Date { get; set; }
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
        public bool Succeeded { get; set; }
        public string Message { get; set; }
        public IList<BackupUnit> Units { get; private set; }
    }

    /// <summary>多目标、多项目备份的汇总结果。</summary>
    public sealed class BackupResult
    {
        public BackupResult()
        {
            Units = new List<BackupUnit>();
            Projects = new List<ProjectOutcome>();
        }

        public IList<BackupUnit> Units { get; set; }
        public IList<ProjectOutcome> Projects { get; set; }
        public bool AllSucceeded { get; set; }
        public bool AnySucceeded { get; set; }
        public bool Cancelled { get; set; }
        public string Summary { get; set; }

        /// <summary>成功写入的文件总数（跨项目、跨目标）。</summary>
        public int TotalCopiedFiles { get; set; }

        /// <summary>成功写入的总字节数（跨项目、跨目标）。</summary>
        public long TotalCopiedBytes { get; set; }

        /// <summary>跳过的文件总数（目标已存在且一致）。</summary>
        public int TotalSkippedFiles { get; set; }

        /// <summary>主目标上的第一个单元（供界面显示摘要）。</summary>
        public BackupUnit PrimaryUnit
        {
            get
            {
                foreach (BackupUnit unit in Units) { if (unit.IsPrimaryTarget) { return unit; } }
                return Units.Count > 0 ? Units[0] : null;
            }
        }

        /// <summary>失败的执行单元。</summary>
        public IList<BackupUnit> FailedUnits()
        {
            var failed = new List<BackupUnit>();
            foreach (BackupUnit unit in Units) { if (!unit.Succeeded) { failed.Add(unit); } }
            return failed;
        }
    }

    /// <summary>
    /// 多目标 + 多项目备份协调器（F11 + F13-D）。
    ///
    /// 关键约束（BUG-002 之后确立，必须保持）：
    /// 1. 目标探测统一走 TargetProbe（专用后台线程 + 超时），界面线程不做任何目标 I/O；
    /// 2. 计划生成是纯内存计算；
    /// 3. 多项目按 ParallelProjects 并行执行，但文件级并发由**共享闸门**统一限制，避免并发乘积暴涨；
    /// 4. 单个项目或单个目标失败不影响其它单元，最后统一汇总。
    /// </summary>
    public sealed class BackupCoordinator
    {
        private readonly CopyPlanBuilder _planBuilder;
        private readonly CopyEngine _engine;
        private readonly ILog _log;

        public BackupCoordinator(CopyPlanBuilder planBuilder, CopyEngine engine, ILog log)
        {
            _planBuilder = planBuilder;
            _engine = engine;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>解析本次要写入的目标（纯字符串逻辑，不访问文件系统）。</summary>
        public OperationResult<IList<string>> ResolveTargets(AppSettings settings)
        {
            var targets = new List<string>();
            if (settings == null) { return OperationResult.Fail<IList<string>>("设置为空"); }

            string primary = (settings.PrimaryTargetRoot ?? string.Empty).Trim();
            if (primary.Length == 0) { return OperationResult.Fail<IList<string>>("尚未设置素材目标目录"); }
            targets.Add(primary);

            string secondary = (settings.SecondaryTargetRoot ?? string.Empty).Trim();
            if (secondary.Length > 0)
            {
                if (string.Equals(secondary.TrimEnd(Path.DirectorySeparatorChar),
                        primary.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail<IList<string>>("副目标与主目标不能是同一个目录");
                }
                targets.Add(secondary);
            }
            return OperationResult.Ok<IList<string>>(targets);
        }

        /// <summary>
        /// 探测目标并为每个项目生成计划（项目 × 目标 = 执行单元）。
        /// 探测带超时，可安全地从界面线程 await。
        /// </summary>
        public async Task<OperationResult<BackupPlanSet>> BuildPlansAsync(IList<ProjectGroupRequest> groups,
            string sourceRoot, AppSettings settings, int timeoutMs = TargetProbe.DefaultTimeoutMs,
            CancellationToken token = default(CancellationToken))
        {
            if (groups == null || groups.Count == 0) { return OperationResult.Fail<BackupPlanSet>("没有可拷贝的项目分组"); }

            OperationResult<IList<string>> targetsResult = ResolveTargets(settings);
            if (!targetsResult.Success) { return OperationResult.Fail<BackupPlanSet>(targetsResult.Message); }

            var planSet = new BackupPlanSet();
            foreach (ProjectGroupRequest group in groups) { planSet.Groups.Add(group); }

            // 每个目标只探测一次，项目之间复用结果
            var availableTargets = new List<KeyValuePair<string, TargetProbeResult>>();
            foreach (string root in targetsResult.Value)
            {
                TargetProbeResult probe = await TargetProbe.CheckAsync(root, timeoutMs, token).ConfigureAwait(false);
                if (!probe.Available)
                {
                    _log.Warn("目标不可用：" + probe.Message);
                    planSet.UnavailableTargets.Add(probe);
                    continue;
                }
                availableTargets.Add(new KeyValuePair<string, TargetProbeResult>(root, probe));
            }

            if (availableTargets.Count == 0)
            {
                string reason = planSet.UnavailableTargets.Count > 0
                    ? planSet.UnavailableTargets[0].Message
                    : "没有可用的目标目录";
                return OperationResult.Fail<BackupPlanSet>(reason);
            }

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                ProjectGroupRequest group = groups[groupIndex];
                for (int targetIndex = 0; targetIndex < availableTargets.Count; targetIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    string root = availableTargets[targetIndex].Key;
                    TargetProbeResult probe = availableTargets[targetIndex].Value;

                    var warnings = new List<string>();
                    string projectDirectory = ResolveConflictDirectory(root, group.RelativeDirectory,
                        settings == null ? ConflictPolicy.AutoRenameDir : settings.ConflictPolicy, warnings);

                    OperationResult<CopyPlan> planResult = _planBuilder.BuildForFiles(
                        group.Files, sourceRoot, settings, group.Context, root, projectDirectory);
                    if (!planResult.Success)
                    {
                        return OperationResult.Fail<BackupPlanSet>("为 " + group.DisplayName + " 生成计划失败：" + planResult.Message);
                    }

                    foreach (string warning in warnings) { planResult.Value.Warnings.Add(warning); }
                    planSet.Units.Add(new BackupUnit
                    {
                        Group = group,
                        Root = root,
                        IsPrimaryTarget = targetIndex == 0,
                        ProjectIndex = groupIndex,
                        ProjectCount = groups.Count,
                        Probe = probe,
                        Plan = planResult.Value
                    });
                }
            }

            return OperationResult.Ok(planSet);
        }

        /// <summary>
        /// 并行执行所有执行单元（项目 × 目标），并按项目汇总。
        /// </summary>
        public async Task<BackupResult> RunAsync(BackupPlanSet planSet, ScanResult scan, AppSettings settings,
            IProgress<CopyProgress> progress, PauseToken pause, CancellationToken token)
        {
            var result = new BackupResult();
            if (planSet == null || planSet.Units.Count == 0)
            {
                result.Summary = "没有可执行的拷贝单元";
                return result;
            }

            settings = settings ?? AppSettings.CreateDefault();
            int projectConcurrency = Math.Max(1, Math.Min(4, settings.ParallelProjects));
            int fileConcurrency = Math.Max(1, Math.Min(8, settings.MaxParallelism));

            long totalBytes = 0;
            int totalFiles = 0;
            foreach (BackupUnit unit in planSet.Units)
            {
                totalBytes += unit.Plan.PendingBytes;
                totalFiles += unit.Plan.PendingCount;
            }

            long aggregateBytes = 0;
            int aggregateFiles = 0;
            var progressLock = new object();

            _log.Info(string.Format("开始执行：{0} 个项目 × {1} 个目标 = {2} 个单元，项目并行度 {3}，文件并发 {4}",
                planSet.Groups.Count, planSet.Units.Count / Math.Max(1, planSet.Groups.Count),
                planSet.Units.Count, projectConcurrency, fileConcurrency));

            // 探测失败的目标登记为失败单元（计划阶段没有为它们生成计划）
            foreach (TargetProbeResult unavailable in planSet.UnavailableTargets)
            {
                result.Units.Add(new BackupUnit
                {
                    Root = unavailable.Root,
                    IsPrimaryTarget = false,
                    Succeeded = false,
                    Message = unavailable.Message
                });
            }

            // 执行前复检一次目标可用性：从"生成计划"到"开始执行"之间，移动硬盘可能被拔掉、网络可能断开
            var reachable = new Dictionary<string, TargetProbeResult>(StringComparer.OrdinalIgnoreCase);
            foreach (BackupUnit probeUnit in planSet.Units)
            {
                if (reachable.ContainsKey(probeUnit.Root)) { continue; }
                reachable[probeUnit.Root] = await TargetProbe.CheckAsync(probeUnit.Root, TargetProbe.DefaultTimeoutMs, token)
                    .ConfigureAwait(false);
            }

            using (var projectGate = new SemaphoreSlim(projectConcurrency))
            using (var fileGate = new SemaphoreSlim(fileConcurrency))
            {
                var tasks = new List<Task>();
                foreach (BackupUnit unit in planSet.Units)
                {
                    token.ThrowIfCancellationRequested();

                    TargetProbeResult current;
                    if (reachable.TryGetValue(unit.Root, out current) && !current.Available)
                    {
                        unit.Succeeded = false;
                        unit.Message = current.Message;
                        _log.Warn("执行前复检发现目标不可用：" + unit.DisplayName + "：" + current.Message);
                        continue;
                    }

                    await projectGate.WaitAsync(token).ConfigureAwait(false);
                    BackupUnit captured = unit;

                    tasks.Add(Task.Run(async delegate
                    {
                        long unitBytes = 0;
                        try
                        {
                            captured.Preflight = CheckSpace(captured);
                            if (!captured.Preflight.CanProceed)
                            {
                                captured.Succeeded = false;
                                captured.Message = captured.Preflight.Message;
                                _log.Warn("预检未通过：" + captured.DisplayName + "：" + captured.Preflight.Message);
                                return;
                            }

                            long unitFiles = 0;
                            IProgress<CopyProgress> scoped = progress == null ? null : new Progress<CopyProgress>(delegate (CopyProgress value)
                            {
                                // 把各单元的进度累加成整体进度：先算出本单元本次的增量，再加到全局
                                long previousBytes = Interlocked.Exchange(ref unitBytes, value.CompletedBytes);
                                long total = Interlocked.Add(ref aggregateBytes, value.CompletedBytes - previousBytes);
                                long previousFiles = Interlocked.Exchange(ref unitFiles, value.CompletedFiles);
                                int files = Interlocked.Add(ref aggregateFiles, (int)(value.CompletedFiles - previousFiles));

                                lock (progressLock)
                                {
                                    progress.Report(new CopyProgress
                                    {
                                        CompletedFiles = files,
                                        TotalFiles = totalFiles,
                                        CompletedBytes = total,
                                        TotalBytes = totalBytes,
                                        CurrentFile = value.CurrentFile,
                                        BytesPerSecond = value.BytesPerSecond,
                                        Elapsed = value.Elapsed,
                                        Remaining = value.Remaining,
                                        IsPaused = value.IsPaused,
                                        TargetLabel = captured.DisplayName,
                                        TargetIndex = captured.ProjectIndex + 1,
                                        TargetCount = captured.ProjectCount
                                    });
                                }
                            });

                            captured.Copy = await _engine.RunAsync(captured.Plan, settings, scoped, pause, token, fileGate)
                                .ConfigureAwait(false);
                            captured.Verify = await Task.Run(delegate
                            {
                                return VerifyService.Verify(captured.Plan, settings, null, token, _log);
                            }, token).ConfigureAwait(false);

                            captured.Succeeded = captured.Copy.FailedFiles == 0 && !captured.Copy.Cancelled
                                                 && captured.Verify.AllPassed;
                            captured.Message = string.Format("拷贝 {0} 个，跳过 {1} 个（{2}），{3}",
                                Humanize.Count(captured.Copy.CopiedFiles),
                                Humanize.Count(captured.Copy.SkippedFiles),
                                Humanize.Bytes(captured.Copy.CopiedBytes),
                                captured.Verify.Summary);
                            _log.Info("单元完成（" + captured.DisplayName + "）：" + captured.Message);
                        }
                        catch (OperationCanceledException)
                        {
                            captured.Succeeded = false;
                            captured.Message = "已取消";
                        }
                        catch (Exception ex)
                        {
                            captured.Succeeded = false;
                            captured.Message = "执行失败：" + ex.Message;
                            _log.Error("单元执行失败：" + captured.DisplayName, ex);
                        }
                        finally
                        {
                            projectGate.Release();
                        }
                    }, token));
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = true;
                }
            }

            foreach (BackupUnit unit in planSet.Units)
            {
                result.Units.Add(unit);
                if (unit.Copy != null)
                {
                    result.TotalCopiedFiles += unit.Copy.CopiedFiles;
                    result.TotalCopiedBytes += unit.Copy.CopiedBytes;
                    result.TotalSkippedFiles += unit.Copy.SkippedFiles;
                }
            }

            BuildProjectOutcomes(planSet, result);

            int failedUnits = result.FailedUnits().Count;
            int failedProjects = 0;
            foreach (ProjectOutcome project in result.Projects) { if (!project.Succeeded) { failedProjects++; } }

            // 只要有一个执行单元写成功，就属于"部分成功"（例如主目标成功、副目标掉线）
            result.AnySucceeded = result.Units.Count > failedUnits;
            result.AllSucceeded = failedUnits == 0 && !result.Cancelled;

            if (result.Cancelled) { result.Summary = "任务已取消"; }
            else if (result.AllSucceeded)
            {
                result.Summary = string.Format("{0} 个项目全部完成并校验通过（{1} 个执行单元）",
                    result.Projects.Count, result.Units.Count);
            }
            else if (result.AnySucceeded)
            {
                result.Summary = string.Format("部分完成：{0} 个执行单元成功，{1} 个失败（{2} 个项目中有 {3} 个未全部完成）",
                    result.Units.Count - failedUnits, failedUnits, result.Projects.Count, failedProjects);
            }
            else { result.Summary = "全部失败"; }

            _log.Info("任务汇总：" + result.Summary);
            return result;
        }

        /// <summary>用计划生成时探测到的可用空间做空间预检（不再重复访问网络）。</summary>
        private PlanPreflight CheckSpace(BackupUnit unit)
        {
            var preflight = new PlanPreflight { RequiredBytes = unit.Plan.PendingBytes };
            long required = (long)(unit.Plan.PendingBytes * 1.05);

            if (unit.Probe != null && unit.Probe.FreeBytes > 0 && unit.Probe.FreeBytes < required)
            {
                preflight.Message = string.Format("目标空间不足：需要 {0}，可用 {1}",
                    Humanize.Bytes(required), Humanize.Bytes(unit.Probe.FreeBytes));
                return preflight;
            }

            preflight.AvailableBytes = unit.Probe == null ? 0 : unit.Probe.FreeBytes;
            preflight.CanProceed = true;
            preflight.Message = string.Format("预检通过：将拷贝 {0} 个文件，共 {1}",
                unit.Plan.PendingCount, Humanize.Bytes(unit.Plan.PendingBytes));
            return preflight;
        }

        /// <summary>按项目汇总各执行单元的结果。</summary>
        private static void BuildProjectOutcomes(BackupPlanSet planSet, BackupResult result)
        {
            var byProject = new Dictionary<int, ProjectOutcome>();
            foreach (BackupUnit unit in planSet.Units)
            {
                ProjectOutcome outcome;
                if (!byProject.TryGetValue(unit.ProjectIndex, out outcome))
                {
                    outcome = new ProjectOutcome
                    {
                        DisplayName = unit.Group == null ? "(未命名)" : unit.Group.DisplayName,
                        Date = unit.Group == null || unit.Group.Context == null ? (DateTime?)null : unit.Group.Context.ShootDate,
                        FileCount = unit.Plan.TotalCount,
                        TotalBytes = unit.Plan.TotalBytes,
                        Succeeded = true
                    };
                    byProject[unit.ProjectIndex] = outcome;
                    result.Projects.Add(outcome);
                }

                outcome.Units.Add(unit);
                if (unit.IsPrimaryTarget)
                {
                    outcome.ProjectDirectory = unit.Plan.ProjectDirectory;
                    outcome.Message = unit.Message;
                }
                if (!unit.Succeeded)
                {
                    outcome.Succeeded = false;
                    if (string.IsNullOrEmpty(outcome.Message)) { outcome.Message = unit.Message; }
                }
            }
        }

        /// <summary>解析项目目录并在冲突时改名（会访问文件系统，调用方须处于后台上下文）。</summary>
        private static string ResolveConflictDirectory(string targetRoot, string relativeDirectory,
            ConflictPolicy policy, IList<string> warnings)
        {
            string full = Path.Combine(targetRoot, relativeDirectory ?? string.Empty);
            try
            {
                if (!Directory.Exists(full)) { return full; }
            }
            catch (Exception)
            {
                return full;
            }

            if (policy == ConflictPolicy.Merge)
            {
                if (warnings != null) { warnings.Add("项目目录已存在，按设置合并到已有目录：" + full); }
                return full;
            }

            string renamed = PathUtil.MakeUniqueName(Path.GetDirectoryName(full), Path.GetFileName(full));
            if (warnings != null) { warnings.Add("项目目录已存在，已自动改名：" + renamed); }
            return Path.Combine(Path.GetDirectoryName(full), renamed);
        }
    }
}
