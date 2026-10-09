using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>校验进度。</summary>
    public sealed class VerifyProgress
    {
        public int CompletedFiles { get; set; }
        public int TotalFiles { get; set; }
        public long VerifiedBytes { get; set; }
        public long TotalBytes { get; set; }
        public string CurrentFile { get; set; }
    }

    /// <summary>单个文件的校验失败信息。</summary>
    public sealed class FileVerifyFailure
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>校验报告。</summary>
    public sealed class VerifyReport
    {
        public VerifyReport()
        {
            Failures = new List<FileVerifyFailure>();
        }

        /// <summary>本次使用的校验档位。</summary>
        public VerifyLevel Level { get; set; }

        /// <summary>通过的文件数。</summary>
        public int PassedFiles { get; set; }

        /// <summary>失败的文件数。</summary>
        public int FailedFiles { get; set; }

        /// <summary>被跳过的文件数。</summary>
        public int SkippedFiles { get; set; }

        /// <summary>已校验字节数。</summary>
        public long VerifiedBytes { get; set; }

        /// <summary>耗时。</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>失败明细。</summary>
        public IList<FileVerifyFailure> Failures { get; set; }

        /// <summary>是否全部通过。</summary>
        public bool AllPassed { get { return FailedFiles == 0; } }

        /// <summary>面向界面的结论文本。</summary>
        public string Summary
        {
            get
            {
                if (Level == VerifyLevel.None) { return "未启用校验"; }
                if (AllPassed) { return string.Format("校验通过：{0} 个文件全部一致", PassedFiles); }
                return string.Format("校验发现 {0} 个文件不一致（共校验 {1} 个）", FailedFiles, PassedFiles + FailedFiles);
            }
        }
    }

    /// <summary>
    /// 校验服务：按档位比较源文件与目标文件是否一致。
    /// 设计要点：
    /// 1. 先比长度，再按档位比内容，避免不必要的读盘；
    /// 2. 顺序读取（不并行），因为随机读会明显拉低机械盘与网络盘吞吐；
    /// 3. 单个文件失败不影响整体流程，全部记入失败清单供用户重拷。
    /// </summary>
    public static class VerifyService
    {
        /// <summary>执行校验。</summary>
        public static VerifyReport Verify(CopyPlan plan, AppSettings settings,
            IProgress<VerifyProgress> progress, CancellationToken token, ILog log)
        {
            var report = new VerifyReport();
            if (plan == null) { return report; }
            settings = settings ?? AppSettings.CreateDefault();
            log = log ?? NullLog.Instance;

            report.Level = settings.VerifyLevel;
            if (settings.VerifyLevel == VerifyLevel.None)
            {
                report.SkippedFiles = plan.Items.Count;
                return report;
            }

            var stopwatch = Stopwatch.StartNew();
            int completed = 0;
            long verifiedBytes = 0;
            DateTime lastReport = DateTime.MinValue;

            foreach (CopyItem item in plan.Items)
            {
                token.ThrowIfCancellationRequested();
                completed++;

                // 视频文件可选跳过哈希校验（体积大、耗时长）
                bool isVideo = item.Kind == Scanning.MediaKind.Video;
                if (isVideo && !settings.VerifyVideo && settings.VerifyLevel != VerifyLevel.SizeOnly)
                {
                    report.SkippedFiles++;
                    continue;
                }

                try
                {
                    string detail;
                    bool passed = VerifyItem(item, settings.VerifyLevel, out detail);
                    if (passed)
                    {
                        report.PassedFiles++;
                        verifiedBytes += item.Length;
                    }
                    else
                    {
                        report.FailedFiles++;
                        report.Failures.Add(new FileVerifyFailure
                        {
                            SourcePath = item.Source.FullPath,
                            TargetPath = item.TargetPath,
                            Detail = detail
                        });
                        log.Warn("校验不一致：" + item.TargetPath + "（" + detail + "）");
                    }
                }
                catch (Exception ex)
                {
                    report.FailedFiles++;
                    report.Failures.Add(new FileVerifyFailure
                    {
                        SourcePath = item.Source.FullPath,
                        TargetPath = item.TargetPath,
                        Detail = "校验异常：" + ex.Message
                    });
                    log.Warn("校验异常：" + item.TargetPath, ex);
                }

                if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 200)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new VerifyProgress
                    {
                        CompletedFiles = completed,
                        TotalFiles = plan.Items.Count,
                        VerifiedBytes = verifiedBytes,
                        TotalBytes = plan.TotalBytes,
                        CurrentFile = System.IO.Path.GetFileName(item.TargetPath)
                    });
                }
            }

            stopwatch.Stop();
            report.Duration = stopwatch.Elapsed;
            report.VerifiedBytes = verifiedBytes;

            if (progress != null)
            {
                progress.Report(new VerifyProgress
                {
                    CompletedFiles = completed,
                    TotalFiles = plan.Items.Count,
                    VerifiedBytes = verifiedBytes,
                    TotalBytes = plan.TotalBytes,
                    CurrentFile = string.Empty
                });
            }

            log.Info("校验完成：" + report.Summary + "，耗时 " + Humanize.Duration(report.Duration));
            return report;
        }

        /// <summary>校验单个文件。</summary>
        private static bool VerifyItem(CopyItem item, VerifyLevel level, out string detail)
        {
            detail = string.Empty;
            string source = PathUtil.Extend(item.Source.FullPath);
            string target = PathUtil.Extend(item.TargetPath);

            if (!File.Exists(target))
            {
                detail = "目标文件不存在";
                return false;
            }

            var sourceInfo = new FileInfo(source);
            var targetInfo = new FileInfo(target);

            if (sourceInfo.Length != targetInfo.Length)
            {
                detail = string.Format("大小不一致：源 {0}，目标 {1}", sourceInfo.Length, targetInfo.Length);
                return false;
            }

            if (level == VerifyLevel.SizeOnly) { return true; }

            string sourceHash = HashUtil.ComputeByLevel(item.Source.FullPath, level);
            string targetHash = HashUtil.ComputeByLevel(item.TargetPath, level);
            if (!string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase))
            {
                detail = "内容哈希不一致";
                return false;
            }
            return true;
        }
    }
}
