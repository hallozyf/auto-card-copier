using System.Collections.Generic;
using System.IO;
using AutoCardCopier.Core.Scanning;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>项目目录布局（F14-D）。</summary>
    public enum ProjectLayout
    {
        /// <summary>合并放一起：项目/照片、项目/视频。</summary>
        Flat = 0,

        /// <summary>一天一个文件夹：项目/日期/照片。</summary>
        ByDay = 1
    }

    /// <summary>同名文件的处理动作（F14-B，由用户决定）。</summary>
    public enum ConflictAction
    {
        Skip = 0,
        Overwrite = 1,
        Replace = 2,
        KeepBoth = 3
    }

    /// <summary>冲突决策来源：界面弹窗实现，测试注入固定策略。</summary>
    public interface IConflictResolver
    {
        ConflictAction Resolve(ConflictContext context);
    }

    /// <summary>一次冲突的上下文。</summary>
    public sealed class ConflictContext
    {
        public string TargetPath { get; set; }
        public string FileName { get; set; }
        public long ExistingLength { get; set; }
        public long SourceLength { get; set; }
    }

    /// <summary>目录布局工具（F14-D）：把已生成的计划改写为"一天一个文件夹"。</summary>
    public static class LayoutHelper
    {
        /// <summary>
        /// 把计划中的目标路径改写为 项目/日期/类型/文件。
        /// 无拍摄时间的文件保持原路径（无法判断日期，不猜测）。
        /// </summary>
        public static void ApplyByDay(CopyPlan plan)
        {
            if (plan == null) { return; }
            foreach (CopyItem item in plan.Items)
            {
                if (item.Source == null || !item.Source.ShootTime.HasValue) { continue; }

                string date = item.Source.ShootTime.Value.ToString("yyyy-MM-dd");
                string type = Path.GetFileName(Path.GetDirectoryName(item.TargetPath) ?? string.Empty);
                if (string.IsNullOrEmpty(type)) { type = item.Source.Kind == MediaKind.Video ? "视频" : "照片"; }

                item.TargetPath = Path.Combine(plan.ProjectDirectory, date, type, Path.GetFileName(item.TargetPath));
            }
            foreach (string warning in new List<string>())
            {
                plan.Warnings.Add(warning);
            }
            plan.Warnings.Add("布局：一天一个文件夹（项目/日期/类型）");
        }
    }
}
