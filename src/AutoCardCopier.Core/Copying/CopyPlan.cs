using System;
using System.Collections.Generic;
using AutoCardCopier.Core.Scanning;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>单个文件的拷贝动作。</summary>
    public enum CopyAction
    {
        /// <summary>需要拷贝。</summary>
        Copy = 0,
        /// <summary>跳过（目标已存在且符合策略）。</summary>
        Skip = 1
    }

    /// <summary>拷贝计划中的一项。</summary>
    public sealed class CopyItem
    {
        /// <summary>源文件。</summary>
        public ScannedFile Source { get; set; }

        /// <summary>目标完整路径。</summary>
        public string TargetPath { get; set; }

        /// <summary>本次要执行的动作。</summary>
        public CopyAction Action { get; set; }

        /// <summary>跳过原因（用于界面与清单）。</summary>
        public string SkipReason { get; set; }

        /// <summary>素材类型。</summary>
        public MediaKind Kind { get { return Source == null ? MediaKind.Other : Source.Kind; } }

        /// <summary>字节数。</summary>
        public long Length { get { return Source == null ? 0 : Source.Length; } }
    }

    /// <summary>
    /// 拷贝计划：一次拷卡的完整文件清单与目标位置。
    /// 计划在执行前生成，既用于空间预检，也用于断点续传与清单生成。
    /// </summary>
    public sealed class CopyPlan
    {
        public CopyPlan()
        {
            Items = new List<CopyItem>();
            Warnings = new List<string>();
        }

        /// <summary>源卡素材根目录。</summary>
        public string SourceRoot { get; set; }

        /// <summary>目标根目录。</summary>
        public string TargetRoot { get; set; }

        /// <summary>项目目录（完整路径）。</summary>
        public string ProjectDirectory { get; set; }

        /// <summary>项目目录名（不含父级）。</summary>
        public string ProjectName { get; set; }

        /// <summary>项目相对路径（使用反斜杠）。</summary>
        public string ProjectRelativePath { get; set; }

        /// <summary>全部计划项。</summary>
        public IList<CopyItem> Items { get; set; }

        /// <summary>计划生成过程中的提示（例如目录已存在并自动改名）。</summary>
        public IList<string> Warnings { get; set; }

        /// <summary>需要拷贝的文件数。</summary>
        public int PendingCount
        {
            get
            {
                int count = 0;
                foreach (CopyItem item in Items) { if (item.Action == CopyAction.Copy) { count++; } }
                return count;
            }
        }

        /// <summary>需要拷贝的总字节数。</summary>
        public long PendingBytes
        {
            get
            {
                long total = 0;
                foreach (CopyItem item in Items) { if (item.Action == CopyAction.Copy) { total += item.Length; } }
                return total;
            }
        }

        /// <summary>计划中的文件总数。</summary>
        public int TotalCount { get { return Items.Count; } }

        /// <summary>计划中的总字节数。</summary>
        public long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (CopyItem item in Items) { total += item.Length; }
                return total;
            }
        }

        /// <summary>需要拷贝的项（用于执行阶段）。</summary>
        public IList<CopyItem> GetPendingItems()
        {
            var pending = new List<CopyItem>();
            foreach (CopyItem item in Items)
            {
                if (item.Action == CopyAction.Copy) { pending.Add(item); }
            }
            return pending;
        }
    }

    /// <summary>计划预检结果。</summary>
    public sealed class PlanPreflight
    {
        /// <summary>是否可以继续。</summary>
        public bool CanProceed { get; set; }

        /// <summary>需要写入的字节数。</summary>
        public long RequiredBytes { get; set; }

        /// <summary>目标可用字节数。</summary>
        public long AvailableBytes { get; set; }

        /// <summary>检查说明。</summary>
        public string Message { get; set; }
    }
}
