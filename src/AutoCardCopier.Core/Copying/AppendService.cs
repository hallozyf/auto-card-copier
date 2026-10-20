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
    /// <summary>追加请求。</summary>
    public sealed class AppendRequest
    {
        public ProjectMeta Project { get; set; }
        public IList<ScannedFile> Files { get; set; }
        public ProjectLayout Layout { get; set; }
        public string CardFingerprint { get; set; }
    }

    /// <summary>追加结果。</summary>
    public sealed class AppendResult
    {
        public AppendResult() { OutOfRangeFiles = new List<string>(); }

        public bool Success { get; set; }
        public string Message { get; set; }
        public IList<string> OutOfRangeFiles { get; private set; }
        public CopyPlan Plan { get; set; }
        public CopyResult Copy { get; set; }
        public VerifyReport Verify { get; set; }
        public int AddedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int ReplacedFiles { get; set; }
        public string RangeText { get; set; }
    }

    /// <summary>
    /// 向已有项目追加素材（F14-B）。
    ///
    /// 已批准行为：
    /// 1. 严格日期校验：有任一文件超出项目日期范围 → 整批拒绝并列出越界文件；
    /// 2. 同名冲突交给 IConflictResolver（覆盖 / 替换 / 都要），软件不自动决定；
    /// 3. 大小与 CRC32 完全一致视为重复，直接跳过不询问；
    /// 4. 只校验新追加的文件，不动已有素材。
    /// </summary>
    public sealed class AppendService
    {
        private readonly CopyPlanBuilder _planBuilder;
        private readonly CopyEngine _engine;
        private readonly ILog _log;

        public AppendService(CopyPlanBuilder planBuilder, CopyEngine engine, ILog log)
        {
            _planBuilder = planBuilder;
            _engine = engine;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>执行追加；日期不匹配时不写入任何文件。</summary>
        public async Task<AppendResult> RunAsync(AppendRequest request, AppSettings settings,
            IConflictResolver resolver, IProgress<CopyProgress> progress, PauseToken pause, CancellationToken token)
        {
            var result = new AppendResult();
            if (request == null || request.Project == null) { result.Message = "没有指定目标项目"; return result; }
            if (request.Files == null || request.Files.Count == 0) { result.Message = "没有可追加的素材"; return result; }

            ProjectMeta project = request.Project;
            DateTime start, end;
            if (!TryResolveRange(project, out start, out end))
            {
                result.Message = "项目缺少日期信息，无法校验追加素材";
                return result;
            }
            result.RangeText = start == end ? start.ToString("yyyy-MM-dd")
                : start.ToString("yyyy-MM-dd") + " ~ " + end.ToString("yyyy-MM-dd");

            foreach (ScannedFile file in request.Files)
            {
                if (!file.ShootTime.HasValue)
                {
                    result.OutOfRangeFiles.Add(Path.GetFileName(file.FullPath) + "（拍摄时间未知）");
                    continue;
                }
                DateTime day = file.ShootTime.Value.Date;
                if (day < start || day > end)
                {
                    result.OutOfRangeFiles.Add(string.Format("{0}（{1}）",
                        Path.GetFileName(file.FullPath), day.ToString("yyyy-MM-dd")));
                }
            }

            if (result.OutOfRangeFiles.Count > 0)
            {
                result.Message = string.Format("有 {0} 个素材的拍摄日期不在项目范围（{1}）内，已整批拒绝",
                    result.OutOfRangeFiles.Count, result.RangeText);
                _log.Warn("追加被拒绝：" + result.Message);
                return result;
            }

            string projectDirectory = project.TargetPath;
            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            {
                result.Message = "目标项目目录不存在：" + projectDirectory;
                return result;
            }

            var context = new ProjectContext
            {
                ShootDate = start,
                ShootType = project.ShootType ?? string.Empty,
                Location = project.Location ?? string.Empty,
                Photographer = project.Photographer ?? string.Empty,
                CameraModel = project.CameraModel ?? string.Empty,
                CardLabel = project.CardLabel ?? string.Empty
            };

            OperationResult<CopyPlan> planResult = _planBuilder.BuildForFiles(request.Files,
                string.Empty, settings, context, projectDirectory, projectDirectory);
            if (!planResult.Success) { result.Message = planResult.Message; return result; }

            CopyPlan plan = planResult.Value;
            if (request.Layout == ProjectLayout.ByDay) { LayoutHelper.ApplyByDay(plan); }
            result.Plan = plan;

            var accepted = new List<CopyItem>();
            foreach (CopyItem item in plan.Items)
            {
                token.ThrowIfCancellationRequested();

                if (!File.Exists(item.TargetPath)) { accepted.Add(item); continue; }

                long existingLength = new FileInfo(item.TargetPath).Length;
                bool identical = existingLength == item.Source.Length && SameContent(item);
                if (identical) { result.SkippedFiles++; continue; }

                ConflictAction action = resolver == null
                    ? ConflictAction.KeepBoth
                    : resolver.Resolve(new ConflictContext
                    {
                        TargetPath = item.TargetPath,
                        FileName = Path.GetFileName(item.TargetPath),
                        ExistingLength = existingLength,
                        SourceLength = item.Source.Length
                    });

                if (action == ConflictAction.Skip) { result.SkippedFiles++; continue; }

                if (action == ConflictAction.Overwrite)
                {
                    TryDelete(item.TargetPath);
                    result.ReplacedFiles++;
                    accepted.Add(item);
                }
                else if (action == ConflictAction.Replace)
                {
                    string backup = MakeBackupName(item.TargetPath);
                    try { File.Move(item.TargetPath, backup); }
                    catch (Exception ex) { result.Message = "替换时无法保留旧文件：" + ex.Message; return result; }
                    result.ReplacedFiles++;
                    accepted.Add(item);
                }
                else
                {
                    string directory = Path.GetDirectoryName(item.TargetPath) ?? projectDirectory;
                    string name = PathUtil.MakeUniqueName(directory, Path.GetFileNameWithoutExtension(item.TargetPath));
                    item.TargetPath = Path.Combine(directory, name + Path.GetExtension(item.TargetPath));
                    accepted.Add(item);
                }
            }

            if (accepted.Count == 0)
            {
                result.Success = true;
                result.Message = string.Format("没有需要新增的文件（跳过 {0} 个）", result.SkippedFiles);
                return result;
            }

            var planToRun = new CopyPlan
            {
                SourceRoot = plan.SourceRoot,
                TargetRoot = plan.TargetRoot,
                ProjectDirectory = plan.ProjectDirectory,
                ProjectRelativePath = plan.ProjectRelativePath,
                ProjectName = plan.ProjectName
            };
            foreach (CopyItem item in accepted) { planToRun.Items.Add(item); }

            result.Copy = await _engine.RunAsync(planToRun, settings, progress, pause, token).ConfigureAwait(false);
            result.Verify = await Task.Run(delegate
            {
                return VerifyService.Verify(planToRun, settings, null, token, _log);
            }, token).ConfigureAwait(false);

            result.AddedFiles = result.Copy.CopiedFiles;
            result.Success = result.Copy.FailedFiles == 0 && !result.Copy.Cancelled && result.Verify.AllPassed;
            result.Message = string.Format("追加完成：新增 {0} 个，替换 {1} 个，跳过 {2} 个；{3}",
                result.AddedFiles, result.ReplacedFiles, result.SkippedFiles, result.Verify.Summary);
            _log.Info("追加到项目 " + project.Name + "：" + result.Message);
            return result;
        }

        /// <summary>解析项目日期范围（优先起止日期，缺失退回拍摄日期）。</summary>
        public static bool TryResolveRange(ProjectMeta project, out DateTime start, out DateTime end)
        {
            start = DateTime.MinValue;
            end = DateTime.MinValue;
            if (project == null) { return false; }

            DateTime parsed;
            if (!string.IsNullOrEmpty(project.StartDate) && DateTime.TryParse(project.StartDate, out parsed)) { start = parsed.Date; }
            else if (!string.IsNullOrEmpty(project.ShootDate) && DateTime.TryParse(project.ShootDate, out parsed)) { start = parsed.Date; }

            if (!string.IsNullOrEmpty(project.EndDate) && DateTime.TryParse(project.EndDate, out parsed)) { end = parsed.Date; }
            if (end == DateTime.MinValue) { end = start; }
            return start != DateTime.MinValue;
        }

        private static bool SameContent(CopyItem item)
        {
            try { return HashUtil.ComputeCrc32(item.TargetPath) == HashUtil.ComputeCrc32(item.Source.FullPath); }
            catch (Exception) { return false; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) { File.Delete(path); } } catch (Exception) { }
        }

        private static string MakeBackupName(string path)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(path) + "_旧_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                          + Path.GetExtension(path);
            return Path.Combine(directory, name);
        }
    }
}
