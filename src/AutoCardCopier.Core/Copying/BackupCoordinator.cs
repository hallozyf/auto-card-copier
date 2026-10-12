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
    /// <summary>单个目标的执行结果。</summary>
    public sealed class BackupTargetResult
    {
        public string Root { get; set; }
        public bool IsPrimary { get; set; }
        public CopyPlan Plan { get; set; }
        public CopyResult Copy { get; set; }
        public VerifyReport Verify { get; set; }
        public PlanPreflight Preflight { get; set; }
        public bool Succeeded { get; set; }
        public string Message { get; set; }

        /// <summary>目标显示名（主目标 / 副目标）。</summary>
        public string DisplayName { get { return (IsPrimary ? "主目标 " : "副目标 ") + Root; } }
    }

    /// <summary>多目标计划集合：可执行计划 + 不可用目标。</summary>
    public sealed class BackupPlanSet
    {
        public BackupPlanSet()
        {
            Plans = new List<CopyPlan>();
            UnavailableTargets = new List<TargetProbeResult>();
        }

        /// <summary>可执行的目标计划（可用目标，主目标在前）。</summary>
        public IList<CopyPlan> Plans { get; set; }

        /// <summary>探测失败或超时的目标（仍会出现在结果里，标记为失败）。</summary>
        public IList<TargetProbeResult> UnavailableTargets { get; set; }
    }

    /// <summary>多目标备份的汇总结果。</summary>
    public sealed class BackupResult
    {
        public BackupResult() { Targets = new List<BackupTargetResult>(); }

        public IList<BackupTargetResult> Targets { get; set; }
        public bool AllSucceeded { get; set; }
        public bool AnySucceeded { get; set; }
        public bool Cancelled { get; set; }
        public string Summary { get; set; }

        /// <summary>主目标结果。</summary>
        public BackupTargetResult Primary
        {
            get
            {
                foreach (BackupTargetResult target in Targets) { if (target.IsPrimary) { return target; } }
                return Targets.Count > 0 ? Targets[0] : null;
            }
        }

        /// <summary>失败的目标。</summary>
        public IList<BackupTargetResult> FailedTargets()
        {
            var failed = new List<BackupTargetResult>();
            foreach (BackupTargetResult target in Targets) { if (!target.Succeeded) { failed.Add(target); } }
            return failed;
        }
    }

    /// <summary>
    /// 多目标备份协调器（F11）。
    ///
    /// BUG-002 修复要点：
    /// 1. 目标探测统一走 TargetProbe（专用后台线程 + 超时），不存在无超时的同步探测；
    /// 2. 计划生成是纯内存计算；
    /// 3. 目录冲突处理与预检都通过 await 完成，界面线程不会阻塞；
    /// 4. 探测失败或超时的目标会列入结果并标记失败（从而阻止解锁格式化并提示用户），
    ///    但不影响主目标的拷贝。
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

        /// <summary>
        /// 解析本次要写入的目标（纯字符串逻辑，不访问文件系统，可安全地在任何线程调用）。
        /// </summary>
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
        /// 探测目标并生成计划。探测带超时，整个过程可以安全地从界面线程 await。
        /// </summary>
        public async Task<OperationResult<BackupPlanSet>> BuildPlansAsync(ScanResult scan, AppSettings settings,
            ProjectContext context, int timeoutMs = TargetProbe.DefaultTimeoutMs,
            CancellationToken token = default(CancellationToken))
        {
            OperationResult<IList<string>> targetsResult = ResolveTargets(settings);
            if (!targetsResult.Success) { return OperationResult.Fail<BackupPlanSet>(targetsResult.Message); }

            var planSet = new BackupPlanSet();
            for (int i = 0; i < targetsResult.Value.Count; i++)
            {
                string root = targetsResult.Value[i];
                bool isPrimary = i == 0;

                TargetProbeResult probe = await TargetProbe.CheckAsync(root, timeoutMs, token).ConfigureAwait(false);
                if (!probe.Available)
                {
                    _log.Warn("目标不可用（" + (isPrimary ? "主目标" : "副目标") + "）：" + probe.Message);
                    planSet.UnavailableTargets.Add(probe);
                    continue;
                }

                // 冲突处理会访问文件系统：此处已在 await 之后的后台线程上下文执行
                var warnings = new List<string>();
                string projectDirectory = _planBuilder.ResolveProjectDirectory(root, settings, context, warnings);

                OperationResult<CopyPlan> planResult = _planBuilder.Build(scan, settings, context, root, projectDirectory);
                if (!planResult.Success)
                {
                    return OperationResult.Fail<BackupPlanSet>("为 " + root + " 生成计划失败：" + planResult.Message);
                }
                planResult.Value.Warnings.Clear();
                foreach (string warning in warnings) { planResult.Value.Warnings.Add(warning); }
                planSet.Plans.Add(planResult.Value);
            }

            if (planSet.Plans.Count == 0)
            {
                string reason = planSet.UnavailableTargets.Count > 0
                    ? planSet.UnavailableTargets[0].Message
                    : "没有可用的目标目录";
                return OperationResult.Fail<BackupPlanSet>(reason);
            }
            return OperationResult.Ok(planSet);
        }

        /// <summary>
        /// 依次执行各目标的拷贝与校验（串行，主目标在前）。
        /// </summary>
        public async Task<BackupResult> RunAsync(BackupPlanSet planSet, AppSettings settings,
            IProgress<CopyProgress> progress, PauseToken pause, CancellationToken token)
        {
            var result = new BackupResult();
            if (planSet == null || planSet.Plans.Count == 0)
            {
                result.Summary = "没有可执行的拷贝计划";
                return result;
            }

            int totalTargets = planSet.Plans.Count + planSet.UnavailableTargets.Count;
            int succeededCount = 0;

            // 不可用目标：直接记为失败目标（用于汇总并阻止解锁格式化）
            foreach (TargetProbeResult unavailable in planSet.UnavailableTargets)
            {
                result.Targets.Add(new BackupTargetResult
                {
                    Root = unavailable.Root,
                    IsPrimary = false,
                    Succeeded = false,
                    Message = unavailable.Message
                });
            }

            for (int i = 0; i < planSet.Plans.Count; i++)
            {
                CopyPlan plan = planSet.Plans[i];
                bool isPrimary = i == 0;
                int targetIndex = i + 1;

                var outcome = new BackupTargetResult
                {
                    Root = plan.TargetRoot,
                    IsPrimary = isPrimary,
                    Plan = plan
                };

                try
                {
                    token.ThrowIfCancellationRequested();

                    outcome.Preflight = await _planBuilder.PreflightAsync(plan, TargetProbe.DefaultTimeoutMs, token)
                        .ConfigureAwait(false);
                    if (!outcome.Preflight.CanProceed)
                    {
                        outcome.Succeeded = false;
                        outcome.Message = outcome.Preflight.Message;
                        result.Targets.Add(outcome);
                        _log.Warn("目标预检未通过（" + plan.TargetRoot + "）：" + outcome.Preflight.Message);
                        continue;
                    }

                    IProgress<CopyProgress> scoped = progress == null
                        ? null
                        : new Progress<CopyProgress>(delegate (CopyProgress value)
                        {
                            value.TargetLabel = (isPrimary ? "主目标 " : "副目标 ") + plan.TargetRoot;
                            value.TargetIndex = targetIndex;
                            value.TargetCount = totalTargets;
                            progress.Report(value);
                        });

                    outcome.Copy = await _engine.RunAsync(plan, settings, scoped, pause, token).ConfigureAwait(false);
                    outcome.Verify = await Task.Run(delegate
                    {
                        return VerifyService.Verify(plan, settings, null, token, _log);
                    }, token).ConfigureAwait(false);

                    outcome.Succeeded = outcome.Copy.FailedFiles == 0 && !outcome.Copy.Cancelled && outcome.Verify.AllPassed;
                    outcome.Message = string.Format("{0}：拷贝 {1} 个，跳过 {2} 个（{3}），{4}",
                        isPrimary ? "主目标" : "副目标",
                        Humanize.Count(outcome.Copy.CopiedFiles),
                        Humanize.Count(outcome.Copy.SkippedFiles),
                        Humanize.Bytes(outcome.Copy.CopiedBytes),
                        outcome.Verify.Summary);

                    if (outcome.Succeeded) { succeededCount++; }
                    _log.Info("目标完成（" + plan.TargetRoot + "）：" + outcome.Message);
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = true;
                    outcome.Succeeded = false;
                    outcome.Message = "已取消";
                    result.Targets.Add(outcome);
                    break;
                }
                catch (Exception ex)
                {
                    outcome.Succeeded = false;
                    outcome.Message = "执行失败：" + ex.Message;
                    _log.Error("目标执行失败：" + plan.TargetRoot, ex);
                }

                result.Targets.Add(outcome);
            }

            result.AnySucceeded = succeededCount > 0;
            result.AllSucceeded = succeededCount > 0 && succeededCount == result.Targets.Count && !result.Cancelled;

            if (result.Cancelled) { result.Summary = "任务已取消"; }
            else if (result.AllSucceeded)
            {
                result.Summary = result.Targets.Count > 1
                    ? string.Format("全部 {0} 个目标均已完成并校验通过", result.Targets.Count)
                    : "拷贝与校验全部通过";
            }
            else if (result.AnySucceeded) { result.Summary = "部分目标成功，存在失败目标"; }
            else { result.Summary = "全部目标均失败"; }

            return result;
        }
    }
}
