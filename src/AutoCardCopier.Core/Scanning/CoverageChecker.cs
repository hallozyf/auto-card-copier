using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Scanning
{
    /// <summary>扫描覆盖率报告。</summary>
    public sealed class CoverageReport
    {
        public CoverageReport() { Samples = new List<string>(); }

        /// <summary>卡上按扫描规则应当被处理的媒体文件总数（照片 + 视频）。</summary>
        public int TotalMediaFiles { get; set; }

        /// <summary>未被本次拷贝覆盖的媒体文件数量。</summary>
        public int UncoveredMediaFiles { get; set; }

        /// <summary>未覆盖文件的示例（用于界面提示，最多 5 条）。</summary>
        public IList<string> Samples { get; set; }

        /// <summary>是否全部覆盖。</summary>
        public bool FullyCovered { get { return UncoveredMediaFiles == 0; } }

        private string _messageOverride;

        /// <summary>面向用户的说明（可被显式覆盖，例如卡片目录不存在时）。</summary>
        public string Message
        {
            get
            {
                if (!string.IsNullOrEmpty(_messageOverride)) { return _messageOverride; }
                if (FullyCovered) { return string.Format("卡上 {0} 个素材已全部纳入本次拷贝", TotalMediaFiles); }
                return string.Format("卡上还有 {0} 个素材（共 {1} 个）未被本次拷贝覆盖", UncoveredMediaFiles, TotalMediaFiles);
            }
            set { _messageOverride = value; }
        }
    }

    /// <summary>
    /// 扫描覆盖率检查（BUG-001 的机制性兜底）。
    ///
    /// 背景：只要"扫描范围"判断出错，就会出现"漏拷 + 校验却通过"的组合，
    /// 用户随后格式化卡片就会造成永久数据丢失。
    /// 因此在解锁格式化之前，重新枚举一次卡片，确认卡上不存在"未被本次拷贝覆盖"的素材文件。
    /// </summary>
    public static class CoverageChecker
    {
        /// <summary>示例文件最多保留的条数。</summary>
        private const int MaxSamples = 5;

        /// <summary>
        /// 检查卡片上的素材是否都已被本次拷贝覆盖。
        /// </summary>
        /// <param name="cardRootPath">卡片根目录。</param>
        /// <param name="settings">设置（决定扫描范围与忽略规则）。</param>
        /// <param name="copiedSourcePaths">本次拷贝计划中的源文件完整路径集合。</param>
        /// <param name="log">日志。</param>
        /// <param name="token">取消标记。</param>
        public static CoverageReport Check(string cardRootPath, AppSettings settings,
            IEnumerable<string> copiedSourcePaths, ILog log, CancellationToken token)
        {
            log = log ?? NullLog.Instance;
            var report = new CoverageReport();

            if (string.IsNullOrWhiteSpace(cardRootPath) || !Directory.Exists(cardRootPath))
            {
                report.Message = "卡片目录不存在，无法检查覆盖率";
                return report;
            }

            var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (copiedSourcePaths != null)
            {
                foreach (string path in copiedSourcePaths)
                {
                    if (!string.IsNullOrEmpty(path)) { copied.Add(path); }
                }
            }

            IList<string> roots = MediaScanner.ResolveScanRoots(cardRootPath, settings ?? AppSettings.CreateDefault());
            var pending = new Stack<string>();
            for (int i = roots.Count - 1; i >= 0; i--) { pending.Push(roots[i]); }

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();

                string[] subDirectories;
                string[] files;
                if (!MediaScanner.TryEnumerateDirectory(directory, out subDirectories, out files)) { continue; }

                foreach (string sub in subDirectories) { pending.Push(sub); }

                foreach (string file in files)
                {
                    token.ThrowIfCancellationRequested();

                    string name = Path.GetFileName(file);
                    if (MediaClassifier.IsJunkFile(name)) { continue; }

                    MediaKind kind = MediaClassifier.Classify(name);
                    if (kind == MediaKind.Other) { continue; }

                    long length;
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists) { continue; }
                        length = info.Length;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (length == 0) { continue; }

                    report.TotalMediaFiles++;
                    if (copied.Contains(file)) { continue; }

                    report.UncoveredMediaFiles++;
                    if (report.Samples.Count < MaxSamples) { report.Samples.Add(file); }
                }
            }

            if (!report.FullyCovered)
            {
                log.Warn("覆盖率检查未通过：" + report.Message);
            }
            return report;
        }
    }
}
