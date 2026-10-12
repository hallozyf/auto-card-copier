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
    /// <summary>
    /// 拷贝计划生成器：把扫描结果变成可执行的拷贝计划，并做执行前的目标检查。
    ///
    /// BUG-002 修复要点（后续改动必须遵守的约束）：
    /// 1. Build 不做任何文件系统访问，纯内存计算，可安全地在任何线程调用；
    /// 2. 目录冲突判断放到 ResolveProjectDirectory，由调用方在后台线程执行；
    /// 3. 目标可用性检查放到 PreflightAsync，内部使用带超时的后台探测；
    /// 4. "目标已存在则跳过"的判断移到拷贝工作线程（CopyEngine），计划阶段不再逐文件探测。
    /// </summary>
    public sealed class CopyPlanBuilder
    {
        /// <summary>空间余量系数：目标可用空间必须大于素材总量的 1.05 倍。</summary>
        private const double SpaceHeadroom = 1.05;

        private readonly ILog _log;

        public CopyPlanBuilder(ILog log)
        {
            _log = log ?? NullLog.Instance;
        }

        /// <summary>
        /// 生成拷贝计划（纯内存计算，不访问文件系统）。
        /// </summary>
        /// <param name="resolvedProjectDirectory">
        /// 已经过冲突处理的项目目录（由 ResolveProjectDirectory 在后台线程算好）；
        /// 传 null 表示直接按模板拼接，不做冲突检查。
        /// </param>
        public OperationResult<CopyPlan> Build(ScanResult scan, AppSettings settings,
            ProjectContext context, string targetRoot, string resolvedProjectDirectory = null)
        {
            if (scan == null) { return OperationResult.Fail<CopyPlan>("尚未扫描素材"); }
            if (settings == null) { settings = AppSettings.CreateDefault(); }
            if (context == null) { context = ProjectContext.CreateDefault(); }
            if (string.IsNullOrWhiteSpace(targetRoot)) { return OperationResult.Fail<CopyPlan>("尚未设置素材目标目录"); }

            try
            {
                var plan = new CopyPlan
                {
                    SourceRoot = scan.RootPath,
                    TargetRoot = targetRoot
                };

                string relative = NameTemplateEngine.ToWindowsRelativePath(
                    NameTemplateEngine.Expand(settings.NameTemplate, context));
                string projectName = relative;
                int lastSeparator = relative.LastIndexOf(Path.DirectorySeparatorChar);
                if (lastSeparator >= 0) { projectName = relative.Substring(lastSeparator + 1); }

                string projectDirectory = string.IsNullOrWhiteSpace(resolvedProjectDirectory)
                    ? Path.Combine(targetRoot, relative)
                    : resolvedProjectDirectory;

                plan.ProjectRelativePath = MakeRelativePath(targetRoot, projectDirectory);
                plan.ProjectDirectory = projectDirectory;
                plan.ProjectName = projectName;

                if (PathUtil.IsSubPathOf(scan.RootPath, projectDirectory))
                {
                    return OperationResult.Fail<CopyPlan>("目标目录位于源卡片内部：" + projectDirectory);
                }

                var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ScannedFile file in scan.Files)
                {
                    string subFolder = MediaClassifier.ResolveSubFolder(file.Kind, file.FullPath, settings.SubFolderMode);
                    string fileName = Path.GetFileName(file.FullPath);
                    string targetPath = string.IsNullOrEmpty(subFolder)
                        ? Path.Combine(projectDirectory, fileName)
                        : Path.Combine(Path.Combine(projectDirectory, subFolder.Replace('/', Path.DirectorySeparatorChar)), fileName);

                    targetPath = EnsureUniqueTarget(targetPath, usedTargets);

                    plan.Items.Add(new CopyItem
                    {
                        Source = file,
                        TargetPath = targetPath,
                        Action = CopyAction.Copy
                    });
                }

                _log.Info(string.Format("拷贝计划已生成：{0} 个文件，{1}，目标目录 {2}",
                    plan.TotalCount, Humanize.Bytes(plan.TotalBytes), plan.ProjectDirectory));
                return OperationResult.Ok(plan);
            }
            catch (Exception ex)
            {
                _log.Error("生成拷贝计划失败", ex);
                return OperationResult.Fail<CopyPlan>("生成拷贝计划失败：" + ex.Message, ex);
            }
        }

        /// <summary>
        /// 解析最终的项目目录（含冲突处理）。本方法会访问文件系统，
        /// 必须在后台线程调用，不得在界面线程直接调用。
        /// </summary>
        public string ResolveProjectDirectory(string targetRoot, AppSettings settings,
            ProjectContext context, IList<string> warnings)
        {
            if (settings == null) { settings = AppSettings.CreateDefault(); }
            if (context == null) { context = ProjectContext.CreateDefault(); }

            string relative = NameTemplateEngine.ToWindowsRelativePath(
                NameTemplateEngine.Expand(settings.NameTemplate, context));
            string projectDirectory = Path.Combine(targetRoot, relative);

            try
            {
                if (!Directory.Exists(projectDirectory)) { return projectDirectory; }
            }
            catch (Exception)
            {
                return projectDirectory;
            }

            if (settings.ConflictPolicy == ConflictPolicy.Merge)
            {
                if (warnings != null) { warnings.Add("项目目录已存在，按设置合并到已有目录：" + projectDirectory); }
                return projectDirectory;
            }

            string renamed = PathUtil.MakeUniqueName(Path.GetDirectoryName(projectDirectory),
                Path.GetFileName(projectDirectory));
            if (warnings != null) { warnings.Add("项目目录已存在，已自动改名：" + renamed); }
            return Path.Combine(Path.GetDirectoryName(projectDirectory), renamed);
        }

        /// <summary>
        /// 执行前预检（异步、带超时）。目标探测在专用后台线程上完成，不会阻塞界面。
        /// </summary>
        public async Task<PlanPreflight> PreflightAsync(CopyPlan plan, int timeoutMs = TargetProbe.DefaultTimeoutMs,
            CancellationToken token = default(CancellationToken))
        {
            var result = new PlanPreflight();
            if (plan == null)
            {
                result.Message = "计划为空";
                return result;
            }

            result.RequiredBytes = plan.PendingBytes;

            TargetProbeResult probe = await TargetProbe.CheckAsync(plan.TargetRoot, timeoutMs, token).ConfigureAwait(false);
            if (!probe.Available)
            {
                result.Message = probe.Message;
                return result;
            }

            result.AvailableBytes = probe.FreeBytes;
            long required = (long)(plan.PendingBytes * SpaceHeadroom);
            if (probe.FreeBytes > 0 && probe.FreeBytes < required)
            {
                result.Message = string.Format("目标空间不足：需要 {0}，可用 {1}",
                    Humanize.Bytes(required), Humanize.Bytes(probe.FreeBytes));
                return result;
            }

            result.CanProceed = true;
            result.Message = string.Format("预检通过：将拷贝 {0} 个文件，共 {1}",
                plan.PendingCount, Humanize.Bytes(plan.PendingBytes));
            return result;
        }

        /// <summary>
        /// 同步预检（仅供测试与后台线程使用）。
        /// 禁止在界面线程调用——它会阻塞等待目标探测结果，请改用 PreflightAsync。
        /// </summary>
        public PlanPreflight Preflight(CopyPlan plan)
        {
            return PreflightAsync(plan).GetAwaiter().GetResult();
        }

        /// <summary>保证同一份计划内的目标路径唯一。</summary>
        private static string EnsureUniqueTarget(string targetPath, HashSet<string> used)
        {
            if (used.Add(targetPath)) { return targetPath; }

            string directory = Path.GetDirectoryName(targetPath);
            string fileName = Path.GetFileNameWithoutExtension(targetPath);
            string extension = Path.GetExtension(targetPath);
            int index = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(directory, string.Format("{0}_{1}{2}", fileName, index, extension));
                index++;
            } while (!used.Add(candidate));
            return candidate;
        }

        /// <summary>计算相对于目标根的路径（用于写入清单，便于跨机器查看）。</summary>
        private static string MakeRelativePath(string root, string fullPath)
        {
            try
            {
                string normalizedRoot = PathUtil.EnsureTrailingSeparator(Path.GetFullPath(root));
                string normalizedFull = Path.GetFullPath(fullPath);
                if (normalizedFull.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedFull.Substring(normalizedRoot.Length);
                }
            }
            catch (Exception)
            {
            }
            return fullPath;
        }
    }
}
