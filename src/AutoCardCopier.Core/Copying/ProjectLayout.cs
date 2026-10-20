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
            string projectDirectory = (plan.ProjectDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);

            foreach (CopyItem item in plan.Items)
            {
                if (item.Source == null || !item.Source.ShootTime.HasValue) { continue; }

                string date = item.Source.ShootTime.Value.ToString("yyyy-MM-dd");

                // 类型子目录：只有目标文件原本就在项目目录的下一级子目录里才沿用，
                // 否则（"不分类"模式，文件直接躺在项目目录下）按素材类型命名，避免把项目名当类型。
                string type = string.Empty;
                string fileDirectory = Path.GetDirectoryName(item.TargetPath) ?? string.Empty;
                if (!string.Equals(fileDirectory.TrimEnd(Path.DirectorySeparatorChar), projectDirectory,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    type = Path.GetFileName(fileDirectory);
                }
                if (string.IsNullOrEmpty(type)) { type = ResolveKindFolder(item.Source.Kind); }

                item.TargetPath = Path.Combine(plan.ProjectDirectory, date, type, Path.GetFileName(item.TargetPath));
            }

            plan.Warnings.Add("布局：一天一个文件夹（项目/日期/类型）");
        }

        /// <summary>按素材类型给出中文子目录名（未知时间/其它类型也不猜，统一归“其他”）。</summary>
        private static string ResolveKindFolder(MediaKind kind)
        {
            if (kind == MediaKind.Video) { return "视频"; }
            if (kind == MediaKind.Photo) { return "照片"; }
            return "其他";
        }
    }
}
