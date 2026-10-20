using System;
using System.Collections.Generic;
using System.IO;

namespace AutoCardCopier.Core.Projects
{
    /// <summary>清理结果（F14-C）：先预览，再执行。</summary>
    public sealed class CleanupReport
    {
        public CleanupReport()
        {
            Samples = new List<string>();
            Failures = new List<string>();
        }

        /// <summary>是否为"只统计不删除"的预览。</summary>
        public bool DryRun { get; set; }

        /// <summary>扫描到的旧元数据文件数量。</summary>
        public int Found { get; set; }

        /// <summary>实际删除成功的数量（预览时为 0）。</summary>
        public int Deleted { get; set; }

        /// <summary>被扫描过的目录数量。</summary>
        public int Directories { get; set; }

        /// <summary>示例文件路径（最多 20 条，避免结果区被刷屏）。</summary>
        public IList<string> Samples { get; private set; }

        /// <summary>删除失败的条目（权限、占用等）。</summary>
        public IList<string> Failures { get; private set; }

        /// <summary>给界面展示的一句话结论。</summary>
        public string Summary
        {
            get
            {
                if (DryRun)
                {
                    return Found == 0
                        ? "没有发现旧版元数据文件"
                        : string.Format("发现 {0} 个旧版元数据文件（共扫描 {1} 个目录）", Found, Directories);
                }
                return string.Format("已删除 {0} 个旧版元数据文件{1}",
                    Deleted, Failures.Count > 0 ? "，" + Failures.Count + " 个失败" : string.Empty);
            }
        }
    }

    /// <summary>
    /// 旧版元数据清理器（F14-C）。
    ///
    /// 只处理软件自己曾经写入的三个文件名，其它文件一律不碰：
    /// _manifest.json、_project.json、_copy.log。
    /// 新版默认把这些数据保存在应用数据目录，素材目录保持纯净；
    /// 本工具用于清理升级前留下的旧文件。
    /// </summary>
    public static class LegacyMetadataCleaner
    {
        /// <summary>允许清理的文件名（小写比较，避免误删用户自己的 JSON）。</summary>
        public static readonly string[] LegacyFileNames = { "_manifest.json", "_project.json", "_copy.log" };

        /// <summary>预览：只统计不删除。</summary>
        public static CleanupReport Preview(string rootDirectory)
        {
            return Run(rootDirectory, true);
        }

        /// <summary>执行删除（调用方必须先让用户二次确认）。</summary>
        public static CleanupReport Clean(string rootDirectory)
        {
            return Run(rootDirectory, false);
        }

        private static CleanupReport Run(string rootDirectory, bool dryRun)
        {
            var report = new CleanupReport { DryRun = dryRun };
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                report.Failures.Add("目录不存在：" + rootDirectory);
                return report;
            }

            var pending = new Stack<string>();
            pending.Push(rootDirectory);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                report.Directories++;

                string[] files;
                try { files = Directory.GetFiles(current); }
                catch (Exception ex)
                {
                    report.Failures.Add(current + "（无法读取：" + ex.Message + "）");
                    continue;
                }

                foreach (string file in files)
                {
                    if (!IsLegacyFileName(Path.GetFileName(file))) { continue; }
                    report.Found++;
                    if (report.Samples.Count < 20) { report.Samples.Add(file); }
                    if (dryRun) { continue; }

                    try
                    {
                        File.Delete(file);
                        report.Deleted++;
                    }
                    catch (Exception ex)
                    {
                        report.Failures.Add(file + "（删除失败：" + ex.Message + "）");
                    }
                }

                string[] children;
                try { children = Directory.GetDirectories(current); }
                catch (Exception ex)
                {
                    report.Failures.Add(current + "（无法列出子目录：" + ex.Message + "）");
                    continue;
                }

                foreach (string child in children)
                {
                    // 跳过符号链接/联接点，避免在异常链接上绕死或误删素材库外部内容
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) { continue; }
                    }
                    catch (Exception) { continue; }
                    pending.Push(child);
                }
            }

            return report;
        }

        private static bool IsLegacyFileName(string fileName)
        {
            foreach (string name in LegacyFileNames)
            {
                if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }
    }
}
