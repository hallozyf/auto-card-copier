using System;
using System.Collections.Generic;
using System.IO;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>
    /// 拷贝计划生成器：负责"把扫描结果变成一份可执行的拷贝计划"，并做执行前的预检。
    /// 这里是自动化建目录的核心——用户不再需要手工创建任何文件夹。
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
        /// 生成拷贝计划。
        /// </summary>
        /// <param name="scan">素材扫描结果。</param>
        /// <param name="settings">设置。</param>
        /// <param name="context">拍摄项目上下文（日期、类型、地点等）。</param>
        /// <param name="targetRoot">目标根目录。</param>
        public OperationResult<CopyPlan> Build(ScanResult scan, AppSettings settings,
            ProjectContext context, string targetRoot)
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

                // 1. 展开命名模板并处理目录冲突
                string relative = NameTemplateEngine.ToWindowsRelativePath(
                    NameTemplateEngine.Expand(settings.NameTemplate, context));
                string projectName = relative;
                int lastSeparator = relative.LastIndexOf(Path.DirectorySeparatorChar);
                if (lastSeparator >= 0) { projectName = relative.Substring(lastSeparator + 1); }

                string projectDirectory = Path.Combine(targetRoot, relative);
                projectDirectory = ResolveConflict(projectDirectory, settings.ConflictPolicy, plan.Warnings);

                plan.ProjectRelativePath = MakeRelativePath(targetRoot, projectDirectory);
                plan.ProjectDirectory = projectDirectory;
                plan.ProjectName = projectName;

                // 2. 目标目录绝不能位于源目录内部（否则会自我递归）
                if (PathUtil.IsSubPathOf(scan.RootPath, projectDirectory))
                {
                    return OperationResult.Fail<CopyPlan>("目标目录位于源卡片内部：" + projectDirectory);
                }

                // 3. 逐文件规划目标路径
                var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ScannedFile file in scan.Files)
                {
                    string subFolder = MediaClassifier.ResolveSubFolder(file.Kind, file.FullPath, settings.SubFolderMode);
                    string fileName = Path.GetFileName(file.FullPath);
                    string targetPath = string.IsNullOrEmpty(subFolder)
                        ? Path.Combine(projectDirectory, fileName)
                        : Path.Combine(Path.Combine(projectDirectory, subFolder.Replace('/', Path.DirectorySeparatorChar)), fileName);

                    // 同一批文件内部也可能重名（不同子目录的同名文件被归并到同一目录）
                    targetPath = EnsureUniqueTarget(targetPath, usedTargets);

                    var item = new CopyItem
                    {
                        Source = file,
                        TargetPath = targetPath,
                        Action = CopyAction.Copy
                    };

                    if (ShouldSkipExisting(item, settings))
                    {
                        item.Action = CopyAction.Skip;
                        item.SkipReason = "目标已存在且大小一致";
                    }
                    plan.Items.Add(item);
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
        /// 执行前预检：目标根可达、可写、空间充足。
        /// </summary>
        public PlanPreflight Preflight(CopyPlan plan)
        {
            var result = new PlanPreflight();
            if (plan == null)
            {
                result.Message = "计划为空";
                return result;
            }

            result.RequiredBytes = plan.PendingBytes;

            try
            {
                if (!Directory.Exists(plan.TargetRoot))
                {
                    result.Message = "目标目录不存在或不可访问：" + plan.TargetRoot;
                    return result;
                }

                if (!AppPaths.IsWritable(plan.TargetRoot))
                {
                    result.Message = "目标目录不可写：" + plan.TargetRoot;
                    return result;
                }

                VolumeInfo volume = VolumeApi.Read(Path.GetPathRoot(Path.GetFullPath(plan.TargetRoot)));
                if (volume == null)
                {
                    result.Message = "无法读取目标磁盘信息：" + plan.TargetRoot;
                    return result;
                }

                result.AvailableBytes = volume.FreeBytes;
                long required = (long)(plan.PendingBytes * SpaceHeadroom);
                if (volume.FreeBytes < required)
                {
                    result.Message = string.Format("目标空间不足：需要 {0}，可用 {1}",
                        Humanize.Bytes(required), Humanize.Bytes(volume.FreeBytes));
                    return result;
                }

                result.CanProceed = true;
                result.Message = string.Format("预检通过：将拷贝 {0} 个文件，共 {1}",
                    plan.PendingCount, Humanize.Bytes(plan.PendingBytes));
                return result;
            }
            catch (Exception ex)
            {
                _log.Error("拷贝预检失败", ex);
                result.Message = "预检失败：" + ex.Message;
                return result;
            }
        }

        /// <summary>按冲突策略决定最终的项目目录。</summary>
        private string ResolveConflict(string projectDirectory, ConflictPolicy policy, IList<string> warnings)
        {
            if (!Directory.Exists(projectDirectory)) { return projectDirectory; }

            switch (policy)
            {
                case ConflictPolicy.Merge:
                    warnings.Add("项目目录已存在，按设置合并到已有目录：" + projectDirectory);
                    return projectDirectory;

                case ConflictPolicy.Ask:
                default:
                    // 界面层会在需要时询问；此处为安全起见仍然自动改名，避免覆盖已有素材
                    string renamed = PathUtil.MakeUniqueName(Path.GetDirectoryName(projectDirectory), Path.GetFileName(projectDirectory));
                    warnings.Add("项目目录已存在，已自动改名：" + renamed);
                    return Path.Combine(Path.GetDirectoryName(projectDirectory), renamed);
            }
        }

        /// <summary>判断是否需要跳过（目标存在且符合策略）。</summary>
        private static bool ShouldSkipExisting(CopyItem item, AppSettings settings)
        {
            try
            {
                string extended = PathUtil.Extend(item.TargetPath);
                if (!File.Exists(extended)) { return false; }

                if (settings.CopyMode == CopyMode.Force) { return false; }

                var target = new FileInfo(extended);
                if (settings.CopyMode == CopyMode.Skip) { return target.Length == item.Length; }

                // CopyIfNewer：目标不早于源文件时跳过
                return target.Length == item.Length &&
                       target.LastWriteTimeUtc >= item.Source.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                return false;
            }
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
