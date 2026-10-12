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
        /// <summary>目标根目录。</summary>
        public string Root { get; set; }

        /// <summary>是否为主目标。</summary>
        public bool IsPrimary { get; set; }

        /// <summary>本目标的拷贝计划。</summary>
        public CopyPlan Plan { get; set; }

        /// <summary>拷贝结果。</summary>
        public CopyResult Copy { get; set; }

        /// <summary>校验结果。</summary>
        public VerifyReport Verify { get; set; }

        /// <summary>预检结果。</summary>
        public PlanPreflight Preflight { get; set; }

        /// <summary>本目标是否成功（拷贝与校验都通过）。</summary>
        public bool Succeeded { get; set; }

        /// <summary>结果说明（面向界面）。</summary>
        public string Message { get; set; }

        /// <summary>目标显示名（主目标 / 副目标）。</summary>
        public string DisplayName { get { return (IsPrimary ? "主目标 " : "副目标 ") + Root; } }
    }

    /// <summary>多目标备份的汇总结果。</summary>
    public sealed class BackupResult
    {
        public BackupResult() { Targets = new List<BackupTargetResult>(); }

        /// <summary>各目标结果（顺序与计划一致，主目标在前）。</summary>
        public IList<BackupTargetResult> Targets { get; set; }

        /// <summary>是否全部目标都成功。</summary>
        public bool AllSucceeded { get; set; }

        /// <summary>是否至少有一个目标成功。</summary>
        public bool AnySucceeded { get; set; }

        /// <summary>是否被用户取消。</summary>
        public bool Cancelled { get; set; }

        /// <summary>汇总文本。</summary>
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
    /// 设计要点：
    /// 1. 先为每个目标各自生成一份拷贝计划（同名项目目录写入不同根目录）；
    /// 2. 串行执行（默认先主后备），避免同一物理磁盘的两个目标互相抢磁头导致整体更慢；
    /// 3. 副目标失败不影响主目标：主目标该怎么拷就怎么拷，最后统一汇总"部分成功"；
    /// 4. 每个目标独立校验，避免"主目标校验通过"掩盖"副目标写入失败"。
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

        /// <summary>解析出本次要写入的目标列表（主目标 + 可选副目标）。</summary>
        public OperationResult<IList<string>> ResolveTargets(AppSettings settings)
        {
            var targets = new List<string>();
            if (settings == null) { return OperationResult.Fail<IList<string>>("设置为空"); }

            string primary = (settings.PrimaryTargetRoot ?? string.Empty).Trim();
            if (primary.Length == 0) { return OperationResult.Fail<IList<string>>("尚未设置素材目标目录"); }
            if (!Directory.Exists(primary)) { return OperationResult.Fail<IList<string>>("主目标目录不存在：" + primary); }
            targets.Add(primary);

            string secondary = (settings.SecondaryTargetRoot ?? string.Empty).Trim();
            if (secondary.Length > 0)
            {
                if (string.Equals(secondary.TrimEnd(Path.DirectorySeparatorChar),
                        primary.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail<IList<string>>("副目标与主目标不能是同一个目录");
                }
                if (!Directory.Exists(secondary))
                {
                    // 副目标离线时只提示，不阻止主目标拷贝
                    _log.Warn("副目标目录不可用，本次将只写入主目标：" + secondary);
                }
                else
                {
                    targets.Add(secondary);
                }
            }
            return OperationResult.Ok<IList<string>>(targets);
        }

        /// <summary>为每个目标生成拷贝计划。</summary>
        public OperationResult<IList<CopyPlan>> BuildPlans(ScanResult scan, AppSettings settings, ProjectContext context)
        {
            OperationResult<IList<string>> targetsResult = ResolveTargets(settings);
            if (!targetsResult.Success) { return OperationResult.Fail<IList<CopyPlan>>(targetsResult.Message); }

            var plans = new List<CopyPlan>();
            foreach (string root in targetsResult.Value)
            {
                OperationResult<CopyPlan> planResult = _planBuilder.Build(scan, settings, context, root);
                if (!planResult.Success)
                {
                    return OperationResult.Fail<IList<CopyPlan>>("为 " + root + " 生成计划失败：" + planResult.Message);
                }
                plans.Add(planResult.Value);
            }
            return OperationResult.Ok<IList<CopyPlan>>(plans);
        }

        /// <summary>
        /// 依次执行各目标的拷贝与校验。
        /// </summary>
        public async Task<BackupResult> RunAsync(IList<CopyPlan> plans, AppSettings settings,
            IProgress<CopyProgress> progress, PauseToken pause, CancellationToken token)
        {
            var result = new BackupResult();
            if (plans == null || plans.Count == 0)
            {
                result.Summary = "没有可执行的拷贝计划";
                return result;
            }

            int succeededCount = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                CopyPlan plan = plans[i];
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

                    outcome.Preflight = _planBuilder.Preflight(plan);
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
                            value.TargetCount = plans.Count;
                            progress.Report(value);
                        });

                    outcome.Copy = await _engine.RunAsync(plan, settings, scoped, pause, token).ConfigureAwait(false);
                    outcome.Verify = await Task.Run(delegate
                    {
                        return VerifyService.Verify(plan, settings, null, token, _log);
                    }, token).ConfigureAwait(false);

                    outcome.Succeeded = outcome.Copy.FailedFiles == 0 && !outcome.Copy.Cancelled && outcome.Verify.AllPassed;
                    outcome.Message = string.Format("{0}：拷贝 {1} 个（{2}），{3}",
                        isPrimary ? "主目标" : "副目标",
                        Humanize.Count(outcome.Copy.CopiedFiles),
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
            result.AllSucceeded = succeededCount == plans.Count && !result.Cancelled;

            if (result.Cancelled) { result.Summary = "任务已取消"; }
            else if (result.AllSucceeded)
            {
                result.Summary = plans.Count > 1
                    ? string.Format("全部 {0} 个目标均已完成并校验通过", plans.Count)
                    : "拷贝与校验全部通过";
            }
            else if (result.AnySucceeded) { result.Summary = "部分目标成功，存在失败目标"; }
            else { result.Summary = "全部目标均失败"; }

            return result;
        }
    }
}
