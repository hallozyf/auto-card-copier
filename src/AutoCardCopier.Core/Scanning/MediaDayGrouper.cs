using System;
using System.Collections.Generic;
using AutoCardCopier.Core.Infrastructure;

namespace AutoCardCopier.Core.Scanning
{
    /// <summary>
    /// 一个拍摄日期分组（对应一个待创建的项目）。
    /// </summary>
    public sealed class MediaDayGroup
    {
        public MediaDayGroup()
        {
            Files = new List<ScannedFile>();
        }

        /// <summary>分组日期；无法确定时间时为 null（未知日期组）。</summary>
        public DateTime? Date { get; set; }

        /// <summary>组内最早拍摄时间。</summary>
        public DateTime Earliest { get; set; }

        /// <summary>组内最晚拍摄时间。</summary>
        public DateTime Latest { get; set; }

        /// <summary>组内文件（按拍摄时间排序）。</summary>
        public IList<ScannedFile> Files { get; private set; }

        public int PhotoCount { get; set; }
        public int VideoCount { get; set; }
        public int OtherCount { get; set; }
        public long PhotoBytes { get; set; }
        public long VideoBytes { get; set; }
        public long OtherBytes { get; set; }

        /// <summary>文件总数。</summary>
        public int TotalCount { get { return Files.Count; } }

        /// <summary>总字节数。</summary>
        public long TotalBytes { get { return PhotoBytes + VideoBytes + OtherBytes; } }

        /// <summary>日期显示文本。</summary>
        public string DisplayDate
        {
            get { return Date.HasValue ? Date.Value.ToString("yyyy-MM-dd") : "未知日期"; }
        }

        /// <summary>时间范围显示文本。</summary>
        public string DisplayRange
        {
            get
            {
                if (!Date.HasValue) { return "需要人工确认"; }
                return Earliest.ToString("MM-dd HH:mm") + " ~ " + Latest.ToString("MM-dd HH:mm");
            }
        }

        /// <summary>是否包含未知时间的文件。</summary>
        public bool IsUnknownDate { get { return !Date.HasValue; } }
    }

    /// <summary>
    /// 按拍摄日期分组（F13-B）。
    /// 说明：需求方已确认**不提供日界设置**，一律按自然日（00:00 分界）分组。
    /// </summary>
    public static class MediaDayGrouper
    {
        /// <summary>
        /// 把扫描到的素材按“拍摄日期”分桶。
        /// 未知时间的文件单独成一组（不混入任何日期，避免素材被放进错误项目）。
        /// </summary>
        public static IList<MediaDayGroup> Group(IEnumerable<ScannedFile> files)
        {
            var byDate = new Dictionary<DateTime, MediaDayGroup>();
            MediaDayGroup unknown = null;

            foreach (ScannedFile file in files)
            {
                MediaDayGroup group;
                if (file.ShootTime.HasValue)
                {
                    DateTime day = file.ShootTime.Value.Date;
                    if (!byDate.TryGetValue(day, out group))
                    {
                        group = new MediaDayGroup { Date = day, Earliest = file.ShootTime.Value, Latest = file.ShootTime.Value };
                        byDate[day] = group;
                    }
                    if (file.ShootTime.Value < group.Earliest) { group.Earliest = file.ShootTime.Value; }
                    if (file.ShootTime.Value > group.Latest) { group.Latest = file.ShootTime.Value; }
                }
                else
                {
                    if (unknown == null) { unknown = new MediaDayGroup { Date = null }; }
                    group = unknown;
                }

                group.Files.Add(file);
                Accumulate(group, file);
            }

            var result = new List<MediaDayGroup>(byDate.Values);
            result.Sort(delegate (MediaDayGroup a, MediaDayGroup b)
            {
                return Nullable.Compare(a.Date, b.Date);
            });
            if (unknown != null) { result.Add(unknown); }

            foreach (MediaDayGroup group in result)
            {
                SortFilesByTime(group);
            }
            return result;
        }

        /// <summary>统计组内的分类计数与字节数。</summary>
        private static void Accumulate(MediaDayGroup group, ScannedFile file)
        {
            switch (file.Kind)
            {
                case MediaKind.Photo:
                    group.PhotoCount++;
                    group.PhotoBytes += file.Length;
                    break;
                case MediaKind.Video:
                    group.VideoCount++;
                    group.VideoBytes += file.Length;
                    break;
                default:
                    group.OtherCount++;
                    group.OtherBytes += file.Length;
                    break;
            }
        }

        /// <summary>组内按拍摄时间升序排列，方便预览与拷贝顺序稳定。</summary>
        private static void SortFilesByTime(MediaDayGroup group)
        {
            var sorted = new List<ScannedFile>(group.Files);
            sorted.Sort(delegate (ScannedFile a, ScannedFile b)
            {
                DateTime left = a.ShootTime ?? DateTime.MaxValue;
                DateTime right = b.ShootTime ?? DateTime.MaxValue;
                int compared = left.CompareTo(right);
                if (compared != 0) { return compared; }
                return string.CompareOrdinal(a.FullPath, b.FullPath);
            });

            group.Files.Clear();
            foreach (ScannedFile file in sorted) { group.Files.Add(file); }
        }

        /// <summary>分组结果的摘要文本（用于界面与日志）。</summary>
        public static string Describe(IList<MediaDayGroup> groups)
        {
            if (groups == null || groups.Count == 0) { return "没有素材"; }
            int unknownCount = 0;
            foreach (MediaDayGroup group in groups) { if (group.IsUnknownDate) { unknownCount++; } }

            string text = string.Format("共 {0} 个拍摄日期", groups.Count - unknownCount);
            if (unknownCount > 0) { text += "，另有 1 组时间未知"; }
            return text;
        }
    }
}
