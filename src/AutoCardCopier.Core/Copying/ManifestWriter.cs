using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>校验清单中的单个文件条目。</summary>
    [DataContract(Name = "file")]
    public sealed class ManifestFileEntry
    {
        [DataMember(Name = "relativePath", Order = 0)] public string RelativePath { get; set; }
        [DataMember(Name = "length", Order = 1)] public long Length { get; set; }
        [DataMember(Name = "lastWriteUtc", Order = 2)] public string LastWriteUtc { get; set; }
        [DataMember(Name = "mediaKind", Order = 3)] public string MediaKind { get; set; }
        [DataMember(Name = "hash", Order = 4)] public string Hash { get; set; }
        [DataMember(Name = "verified", Order = 5)] public bool Verified { get; set; }
    }

    /// <summary>清单中的源卡信息。</summary>
    [DataContract(Name = "source")]
    public sealed class ManifestSource
    {
        [DataMember(Name = "rootPath", Order = 0)] public string RootPath { get; set; }
        [DataMember(Name = "volumeLabel", Order = 1)] public string VolumeLabel { get; set; }
        [DataMember(Name = "fingerprint", Order = 2)] public string Fingerprint { get; set; }
        [DataMember(Name = "cameraModel", Order = 3)] public string CameraModel { get; set; }
    }

    /// <summary>清单汇总信息。</summary>
    [DataContract(Name = "summary")]
    public sealed class ManifestSummary
    {
        [DataMember(Name = "fileCount", Order = 0)] public int FileCount { get; set; }
        [DataMember(Name = "totalBytes", Order = 1)] public long TotalBytes { get; set; }
        [DataMember(Name = "photoCount", Order = 2)] public int PhotoCount { get; set; }
        [DataMember(Name = "videoCount", Order = 3)] public int VideoCount { get; set; }
        [DataMember(Name = "otherCount", Order = 4)] public int OtherCount { get; set; }
        [DataMember(Name = "copiedFiles", Order = 5)] public int CopiedFiles { get; set; }
        [DataMember(Name = "failedFiles", Order = 6)] public int FailedFiles { get; set; }
        [DataMember(Name = "skippedFiles", Order = 7)] public int SkippedFiles { get; set; }
        [DataMember(Name = "durationSeconds", Order = 8)] public double DurationSeconds { get; set; }
        [DataMember(Name = "averageBytesPerSecond", Order = 9)] public double AverageBytesPerSecond { get; set; }
        [DataMember(Name = "verifyPassed", Order = 10)] public int VerifyPassed { get; set; }
        [DataMember(Name = "verifyFailed", Order = 11)] public int VerifyFailed { get; set; }
    }

    /// <summary>完整的校验清单（_manifest.json）。</summary>
    [DataContract(Name = "manifest")]
    public sealed class ManifestModel
    {
        public ManifestModel()
        {
            Files = new List<ManifestFileEntry>();
        }

        [DataMember(Name = "schemaVersion", Order = 0)] public int SchemaVersion { get; set; }
        [DataMember(Name = "projectId", Order = 1)] public string ProjectId { get; set; }
        [DataMember(Name = "projectName", Order = 2)] public string ProjectName { get; set; }
        [DataMember(Name = "generatedAt", Order = 3)] public string GeneratedAt { get; set; }
        [DataMember(Name = "verifyLevel", Order = 4)] public string VerifyLevel { get; set; }
        [DataMember(Name = "source", Order = 5)] public ManifestSource Source { get; set; }
        [DataMember(Name = "summary", Order = 6)] public ManifestSummary Summary { get; set; }
        [DataMember(Name = "files", Order = 7)] public List<ManifestFileEntry> Files { get; set; }
    }

    /// <summary>
    /// 清单写入器：把一次拷卡的结果落成项目目录中的三个自描述文件。
    /// - _manifest.json：机器可读的完整校验清单
    /// - _project.json：跨机器可读的项目元数据
    /// - _copy.log：人类可读的本次任务摘要
    /// </summary>
    public sealed class ManifestWriter
    {
        /// <summary>清单文件名。</summary>
        public const string ManifestFileName = "_manifest.json";

        /// <summary>项目元数据文件名。</summary>
        public const string ProjectFileName = "_project.json";

        /// <summary>任务摘要文件名。</summary>
        public const string CopyLogFileName = "_copy.log";

        private readonly ILog _log;
        private readonly string _dataDirectory;

        /// <summary>以应用数据目录为清单存放位置（F14-C）。</summary>
        public ManifestWriter(string dataDirectory, ILog log)
        {
            _log = log ?? NullLog.Instance;
            _dataDirectory = dataDirectory;
        }

        /// <summary>兼容旧调用：清单放入默认应用数据目录。</summary>
        public ManifestWriter(ILog log) : this(null, log) { }

        private string DataDirectory
        {
            get
            {
                return string.IsNullOrEmpty(_dataDirectory)
                    ? AutoCardCopier.Core.Infrastructure.AppPaths.DefaultDataDirectory
                    : _dataDirectory;
            }
        }

        /// <summary>
        /// 生成清单与项目元数据并写入项目目录。
        /// </summary>
        public OperationResult WriteProjectFiles(CopyPlan plan, ScanResult scan, ProjectContext context,
            AppSettings settings, CopyResult copyResult, VerifyReport verifyReport,
            CardIdentity cardIdentity, string projectId, ProjectLayout layout = ProjectLayout.Flat,
            DateTime? rangeStart = null, DateTime? rangeEnd = null, string source = "create")
        {
            if (plan == null) { return OperationResult.Fail("计划为空"); }

            try
            {
                ManifestModel manifest = BuildManifest(plan, scan, context, settings, copyResult, verifyReport, cardIdentity, projectId);
                ProjectMeta meta = BuildProjectMeta(plan, scan, context, verifyReport, cardIdentity, projectId,
                    layout, rangeStart, rangeEnd, source);

                string copyLog = BuildCopyLog(plan, copyResult, verifyReport, cardIdentity);

                // 默认：清单与日志存放到应用数据目录，素材目录保持纯净（F14-C）
                string manifestDirectory = AutoCardCopier.Core.Infrastructure.AppPaths.ManifestsDirectory(DataDirectory);
                string safeId = string.IsNullOrWhiteSpace(projectId) ? "unknown" : projectId;
                JsonStore.WriteAllTextAtomic(Path.Combine(manifestDirectory, safeId + ".manifest.json"), JsonStore.Serialize(manifest));
                JsonStore.WriteAllTextAtomic(Path.Combine(manifestDirectory, safeId + ".copylog.txt"), copyLog);

                // 可选：同时在素材目录写入自描述文件，便于脱离软件后仍可自解释
                bool selfDescribing = settings != null && settings.WriteSelfDescribingFiles;
                if (selfDescribing)
                {
                    JsonStore.WriteAllTextAtomic(Path.Combine(plan.ProjectDirectory, ManifestFileName), JsonStore.Serialize(manifest));
                    JsonStore.WriteAllTextAtomic(Path.Combine(plan.ProjectDirectory, ProjectFileName), JsonStore.Serialize(meta));
                    JsonStore.WriteAllTextAtomic(Path.Combine(plan.ProjectDirectory, CopyLogFileName), copyLog);
                }

                _log.Info(string.Format("已写入校验清单：{0}（素材目录{1}）", manifestDirectory,
                    selfDescribing ? "同时写入自描述文件" : "不写入任何文件"));
                return OperationResult.Ok(selfDescribing ? "已写入校验清单与自描述文件" : "已写入校验清单（素材目录保持纯净）");
            }
            catch (Exception ex)
            {
                _log.Error("写入项目文件失败", ex);
                return OperationResult.Fail("写入项目文件失败：" + ex.Message, ex);
            }
        }

        /// <summary>构造清单模型（同时用于导出 CSV）。</summary>
        public ManifestModel BuildManifest(CopyPlan plan, ScanResult scan, ProjectContext context,
            AppSettings settings, CopyResult copyResult, VerifyReport verifyReport,
            CardIdentity cardIdentity, string projectId)
        {
            var manifest = new ManifestModel
            {
                SchemaVersion = 1,
                ProjectId = projectId,
                ProjectName = plan.ProjectName,
                GeneratedAt = TimeUtil.ToIso(DateTime.Now),
                VerifyLevel = (verifyReport == null ? VerifyLevel.None : verifyReport.Level).ToString(),
                Source = new ManifestSource
                {
                    RootPath = plan.SourceRoot,
                    VolumeLabel = cardIdentity == null ? string.Empty : cardIdentity.Label,
                    Fingerprint = cardIdentity == null ? string.Empty : cardIdentity.Fingerprint,
                    CameraModel = scan == null ? (context == null ? string.Empty : context.CameraModel) : scan.CameraModel
                },
                Summary = new ManifestSummary
                {
                    FileCount = plan.TotalCount,
                    TotalBytes = plan.TotalBytes,
                    PhotoCount = scan == null ? 0 : scan.PhotoCount,
                    VideoCount = scan == null ? 0 : scan.VideoCount,
                    OtherCount = scan == null ? 0 : scan.OtherCount,
                    CopiedFiles = copyResult == null ? 0 : copyResult.CopiedFiles,
                    FailedFiles = copyResult == null ? 0 : copyResult.FailedFiles,
                    SkippedFiles = copyResult == null ? 0 : copyResult.SkippedFiles,
                    DurationSeconds = copyResult == null ? 0 : copyResult.Duration.TotalSeconds,
                    AverageBytesPerSecond = copyResult == null ? 0 : copyResult.AverageBytesPerSecond,
                    VerifyPassed = verifyReport == null ? 0 : verifyReport.PassedFiles,
                    VerifyFailed = verifyReport == null ? 0 : verifyReport.FailedFiles
                }
            };

            var failedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (verifyReport != null)
            {
                foreach (FileVerifyFailure failure in verifyReport.Failures) { failedTargets.Add(failure.TargetPath); }
            }

            foreach (CopyItem item in plan.Items)
            {
                VerifyLevel level = verifyReport == null ? VerifyLevel.None : verifyReport.Level;
                string hash = level == VerifyLevel.None || level == VerifyLevel.SizeOnly
                    ? string.Empty
                    : HashUtil.ComputeByLevel(item.TargetPath, level);

                manifest.Files.Add(new ManifestFileEntry
                {
                    RelativePath = MakeRelative(plan.TargetRoot, item.TargetPath),
                    Length = item.Length,
                    LastWriteUtc = TimeUtil.ToIsoUtc(item.Source.LastWriteTimeUtc),
                    MediaKind = item.Kind.ToString(),
                    Hash = hash,
                    Verified = !failedTargets.Contains(item.TargetPath)
                });
            }

            return manifest;
        }

        /// <summary>构造项目元数据。</summary>
        public ProjectMeta BuildProjectMeta(CopyPlan plan, ScanResult scan, ProjectContext context,
            VerifyReport verifyReport, CardIdentity cardIdentity, string projectId,
            ProjectLayout layout = ProjectLayout.Flat, DateTime? rangeStart = null, DateTime? rangeEnd = null,
            string source = "create")
        {
            // 统计与时间范围都以"本项目实际包含的文件"为准：
            // 多日分组时整张卡的统计不能代表某一个项目（F13/F14-D）。
            int photoCount = 0, videoCount = 0, otherCount = 0;
            long photoBytes = 0, videoBytes = 0, otherBytes = 0;
            DateTime? earliest = null, latest = null;

            if (plan != null)
            {
                foreach (CopyItem item in plan.Items)
                {
                    ScannedFile file = item.Source;
                    if (file == null) { continue; }
                    if (file.Kind == MediaKind.Photo) { photoCount++; photoBytes += file.Length; }
                    else if (file.Kind == MediaKind.Video) { videoCount++; videoBytes += file.Length; }
                    else { otherCount++; otherBytes += file.Length; }

                    if (file.ShootTime.HasValue)
                    {
                        DateTime moment = file.ShootTime.Value;
                        if (!earliest.HasValue || moment < earliest.Value) { earliest = moment; }
                        if (!latest.HasValue || moment > latest.Value) { latest = moment; }
                    }
                }
            }

            DateTime fallbackStart = scan != null && scan.EarliestUtc.HasValue
                ? scan.EarliestUtc.Value.ToLocalTime()
                : context.ShootDate;
            DateTime fallbackEnd = scan != null && scan.LatestUtc.HasValue
                ? scan.LatestUtc.Value.ToLocalTime()
                : context.ShootDate;

            DateTime rangeStartDate = (rangeStart ?? earliest ?? fallbackStart).Date;
            DateTime rangeEndDate = (rangeEnd ?? latest ?? fallbackEnd).Date;
            if (rangeEndDate < rangeStartDate) { rangeEndDate = rangeStartDate; }

            DateTime shootStart = earliest ?? rangeStartDate;
            DateTime shootEnd = latest ?? rangeEndDate;

            return new ProjectMeta
            {
                SchemaVersion = 1,
                Id = projectId,
                Name = plan.ProjectName,
                ShootDate = TimeUtil.DisplayDate(rangeStartDate),
                ShootTimeStart = TimeUtil.ToIso(shootStart),
                ShootTimeEnd = TimeUtil.ToIso(shootEnd),
                StartDate = TimeUtil.DisplayDate(rangeStartDate),
                EndDate = TimeUtil.DisplayDate(rangeEndDate),
                Source = string.IsNullOrEmpty(source) ? "create" : source,
                ImportLayout = layout == ProjectLayout.ByDay ? "byday" : "flat",
                LastVerifiedAt = verifyReport != null && verifyReport.AllPassed
                    ? TimeUtil.ToIsoUtcPrecise(DateTime.Now)
                    : null,
                Location = context.Location,
                ShootType = context.ShootType,
                Photographer = context.Photographer,
                CameraModel = scan != null && !string.IsNullOrEmpty(scan.CameraModel) ? scan.CameraModel : context.CameraModel,
                CardLabel = cardIdentity == null ? context.CardLabel : cardIdentity.Label,
                CardFingerprint = cardIdentity == null ? string.Empty : cardIdentity.Fingerprint,
                TargetPath = plan.ProjectDirectory,
                RelativePath = plan.ProjectRelativePath,
                PhotoCount = plan == null ? 0 : photoCount,
                PhotoBytes = photoBytes,
                VideoCount = videoCount,
                VideoBytes = videoBytes,
                OtherCount = otherCount,
                OtherBytes = otherBytes,
                TotalCount = plan == null ? 0 : plan.TotalCount,
                TotalBytes = plan == null ? 0 : plan.TotalBytes,
                VerifyLevel = (verifyReport == null ? VerifyLevel.None : verifyReport.Level).ToString(),
                VerifyStatus = verifyReport == null ? "未校验" : (verifyReport.AllPassed ? "校验通过" : "校验失败"),
                Note = string.Empty,
                CreatedAt = TimeUtil.ToIso(DateTime.Now),
                UpdatedAt = TimeUtil.ToIso(DateTime.Now)
            };
        }

        /// <summary>生成人类可读的任务摘要。</summary>
        private static string BuildCopyLog(CopyPlan plan, CopyResult copyResult, VerifyReport verifyReport, CardIdentity cardIdentity)
        {
            var builder = new StringBuilder();
            builder.AppendLine("自动拷卡助手 · 本次拷卡摘要");
            builder.AppendLine("生成时间：" + TimeUtil.Display(DateTime.Now));
            builder.AppendLine("项目名称：" + plan.ProjectName);
            builder.AppendLine("项目目录：" + plan.ProjectDirectory);
            builder.AppendLine("源卡：" + (cardIdentity == null ? plan.SourceRoot : cardIdentity.Display));
            builder.AppendLine("文件数量：" + plan.TotalCount + "（拷贝 " +
                (copyResult == null ? 0 : copyResult.CopiedFiles) + "，跳过 " +
                (copyResult == null ? 0 : copyResult.SkippedFiles) + "，失败 " +
                (copyResult == null ? 0 : copyResult.FailedFiles) + "）");
            builder.AppendLine("数据总量：" + Humanize.Bytes(plan.TotalBytes));
            if (copyResult != null)
            {
                builder.AppendLine("拷贝耗时：" + Humanize.Duration(copyResult.Duration) +
                                   "，平均速度 " + Humanize.Speed(copyResult.AverageBytesPerSecond));
            }
            if (verifyReport != null)
            {
                builder.AppendLine("校验档位：" + verifyReport.Level);
                builder.AppendLine("校验结果：" + verifyReport.Summary + "，耗时 " + Humanize.Duration(verifyReport.Duration));
                foreach (FileVerifyFailure failure in verifyReport.Failures)
                {
                    builder.AppendLine("  校验失败：" + failure.TargetPath + "（" + failure.Detail + "）");
                }
            }
            if (copyResult != null && copyResult.Failures.Count > 0)
            {
                builder.AppendLine("拷贝失败明细：");
                foreach (CopyFailure failure in copyResult.Failures)
                {
                    builder.AppendLine("  " + failure.SourcePath + " → " + failure.Error);
                }
            }
            if (plan.Warnings.Count > 0)
            {
                builder.AppendLine("提示：");
                foreach (string warning in plan.Warnings) { builder.AppendLine("  " + warning); }
            }
            return builder.ToString();
        }

        /// <summary>导出 CSV（UTF-8 带 BOM，保证 Excel 打开不乱码）。</summary>
        public static OperationResult ExportCsv(ManifestModel manifest, string csvPath)
        {
            if (manifest == null) { return OperationResult.Fail("清单为空"); }
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("相对路径,字节数,类型,最后修改(UTC),哈希,校验状态");
                foreach (ManifestFileEntry entry in manifest.Files)
                {
                    builder.Append(Csv(entry.RelativePath)).Append(',')
                        .Append(entry.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(Csv(entry.MediaKind)).Append(',')
                        .Append(Csv(entry.LastWriteUtc)).Append(',')
                        .Append(Csv(entry.Hash)).Append(',')
                        .Append(entry.Verified ? "通过" : "失败")
                        .AppendLine();
                }

                string extended = PathUtil.Extend(csvPath);
                string directory = Path.GetDirectoryName(extended);
                if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
                File.WriteAllText(extended, builder.ToString(), new UTF8Encoding(true));
                return OperationResult.Ok("已导出 CSV：" + csvPath);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail("导出 CSV 失败：" + ex.Message, ex);
            }
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) { return string.Empty; }
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) { return value; }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string MakeRelative(string root, string fullPath)
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
