using System;
using System.Collections.Generic;
using System.IO;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Scanning;

namespace AutoCardCopier.Core.Projects
{
    /// <summary>扫描到的一个候选项目（导入前供界面预览与勾选）。</summary>
    public sealed class LibraryCandidate
    {
        /// <summary>项目目录完整路径。</summary>
        public string Path { get; set; }

        /// <summary>目录名（默认项目名）。</summary>
        public string Name { get; set; }

        /// <summary>相对扫描根的路径。</summary>
        public string RelativePath { get; set; }

        /// <summary>拍摄起始日期（yyyy-MM-dd）。</summary>
        public string StartDate { get; set; }

        /// <summary>拍摄终止日期（yyyy-MM-dd）。</summary>
        public string EndDate { get; set; }

        public int PhotoCount { get; set; }
        public int VideoCount { get; set; }
        public int OtherCount { get; set; }
        public long TotalBytes { get; set; }

        /// <summary>是否已在项目库中（用于跳过重复导入）。</summary>
        public bool IsImported { get; set; }

        /// <summary>已存在的项目 Id（若已导入）。</summary>
        public string ExistingProjectId { get; set; }

        public int TotalCount { get { return PhotoCount + VideoCount + OtherCount; } }

        /// <summary>日期范围显示文本。</summary>
        public string DisplayRange
        {
            get
            {
                if (string.IsNullOrEmpty(StartDate)) { return "无法判定日期"; }
                return StartDate == EndDate ? StartDate : StartDate + " ~ " + EndDate;
            }
        }
    }

    /// <summary>
    /// 历史素材库导入（F14-A）。
    ///
    /// 已批准的识别规则：**包含素材的最上层目录算一个项目**，其子目录合并进同一个项目。
    /// 日期范围用文件修改时间估算；精确模式下会用最早的若干个文件读 EXIF 校正。
    /// </summary>
    public static class LibraryImporter
    {
        /// <summary>精确模式下用于校正日期的样本文件数。</summary>
        private const int SampleFiles = 4;

        /// <summary>
        /// 扫描一个素材根目录，返回候选项目列表（不写入任何数据）。
        /// </summary>
        /// <param name="root">素材根目录。</param>
        /// <param name="maxDepth">向下扫描的层数（默认 3）。</param>
        /// <param name="library">用于判断项目是否已导入（可为 null）。</param>
        /// <param name="log">日志。</param>
        /// <param name="precise">是否用 EXIF 校正日期。</param>
        public static IList<LibraryCandidate> Scan(string root, int maxDepth, ProjectLibrary library,
            ILog log, bool precise = false)
        {
            var candidates = new List<LibraryCandidate>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { return candidates; }
            if (maxDepth <= 0) { maxDepth = 3; }

            // 1. 找出"直接包含素材"的目录
            var withMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allDirectories = new List<string>();
            CollectDirectories(root, 0, maxDepth, withMedia, allDirectories);

            // 2. 项目边界：跳过"透明目录"（年份、日期、照片/视频、DCIM 等结构目录），折叠到真正的项目目录
            var tops = new List<string>();
            var subtreeCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in allDirectories)
            {
                if (string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!HasMediaInSubtree(directory, withMedia, subtreeCache)) { continue; }

                // 从当前目录向上折叠：只要当前目录本身是"结构目录"（年份/日期/照片/视频…）就上移一级
                string boundary = directory;
                while (IsTransparentDirectory(Path.GetFileName(boundary)))
                {
                    // 只有当前节点确实位于扫描根之下时才允许上移（可一路折叠到根）
                    if (!PathUtil.IsSubPathOf(root, boundary)) { break; }
                    string up = Path.GetDirectoryName(boundary);
                    if (string.IsNullOrEmpty(up)) { break; }
                    boundary = up;
                }

                // 折叠到扫描根时：只有根目录自己直接含素材才当作一个项目
                if (string.Equals(boundary.TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    if (!withMedia.Contains(boundary)) { continue; }
                }
                if (!tops.Contains(boundary)) { tops.Add(boundary); }
            }

            // 3. 统计每个项目的素材与日期范围
            var existing = new Dictionary<string, ProjectMeta>(StringComparer.OrdinalIgnoreCase);
            if (library != null)
            {
                foreach (ProjectMeta project in library.LoadAll())
                {
                    if (!string.IsNullOrEmpty(project.TargetPath)) { existing[project.TargetPath] = project; }
                }
            }

            foreach (string directory in tops)
            {
                LibraryCandidate candidate = Summarize(directory, root, precise, log);
                if (candidate == null || candidate.TotalCount == 0) { continue; }

                ProjectMeta known;
                if (existing.TryGetValue(candidate.Path, out known))
                {
                    candidate.IsImported = true;
                    candidate.ExistingProjectId = known.Id;
                }
                candidates.Add(candidate);
            }

            candidates.Sort(delegate (LibraryCandidate a, LibraryCandidate b)
            {
                return string.CompareOrdinal(a.StartDate ?? "", b.StartDate ?? "");
            });
            if (log != null)
            {
                log.Info(string.Format("历史素材扫描完成：根目录 {0}，识别 {1} 个项目", root, candidates.Count));
            }
            return candidates;
        }

        /// <summary>把选中的候选项目写入项目库。</summary>
        /// <param name="source">来源标识：import（历史导入）/ migrate（旧文件迁移）。</param>
        public static int Import(IList<LibraryCandidate> candidates, ProjectLibrary library, ILog log,
            string source = "import", string location = "", string shootType = "")
        {
            if (candidates == null || library == null) { return 0; }

            int imported = 0;
            DateTime now = DateTime.Now;
            foreach (LibraryCandidate candidate in candidates)
            {
                if (candidate == null || candidate.IsImported) { continue; }

                string name = string.IsNullOrWhiteSpace(candidate.Name) ? Path.GetFileName(candidate.Path) : candidate.Name;
                var meta = new ProjectMeta
                {
                    Id = ProjectMeta.NewId(),
                    Name = name,
                    ShootDate = candidate.StartDate,
                    StartDate = candidate.StartDate,
                    EndDate = candidate.EndDate,
                    ShootTimeStart = candidate.StartDate,
                    ShootTimeEnd = candidate.EndDate,
                    Location = location ?? string.Empty,
                    ShootType = shootType ?? string.Empty,
                    TargetPath = candidate.Path,
                    RelativePath = candidate.RelativePath,
                    PhotoCount = candidate.PhotoCount,
                    VideoCount = candidate.VideoCount,
                    OtherCount = candidate.OtherCount,
                    TotalCount = candidate.TotalCount,
                    TotalBytes = candidate.TotalBytes,
                    Source = source,
                    ImportLayout = "flat",
                    Note = "由历史素材扫描建立",
                    CreatedAt = TimeUtil.ToIsoUtcPrecise(now),
                    UpdatedAt = TimeUtil.ToIsoUtcPrecise(now)
                };

                OperationResult saved = library.Save(meta);
                if (saved.Success)
                {
                    candidate.IsImported = true;
                    candidate.ExistingProjectId = meta.Id;
                    imported++;
                }
                else if (log != null)
                {
                    log.Warn("导入项目失败：" + candidate.Path + "：" + saved.Message);
                }
            }
            if (log != null) { log.Info("历史素材导入完成：" + imported + " 个项目"); }
            return imported;
        }

        /// <summary>递归收集目录清单，并标记哪些目录直接包含素材。</summary>
        private static void CollectDirectories(string directory, int depth, int maxDepth,
            HashSet<string> withMedia, List<string> all)
        {
            all.Add(directory);
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (IsMediaFile(file)) { withMedia.Add(directory); break; }
                }
            }
            catch (Exception) { }

            if (depth >= maxDepth) { return; }
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".")) { continue; }
                    CollectDirectories(sub, depth + 1, maxDepth, withMedia, all);
                }
            }
            catch (Exception) { }
        }

        /// <summary>结构目录名（不是项目边界，会折叠到上一级）。</summary>
        private static readonly string[] StructuralNames = new string[]
        {
            "照片", "相片", "图片", "视频", "影片", "其他", "其它",
            "photos", "photo", "pictures", "images", "videos", "video", "movies", "clips", "clip", "other",
            "dcim", "private", "contents", "mp_root", "100canon", "100msdcf", "raw", "jpg", "mov", "mp4"
        };

        /// <summary>是否为结构目录：年份、日期、照片/视频、相机目录等（折叠到上一级）。</summary>
        private static bool IsTransparentDirectory(string name)
        {
            if (string.IsNullOrEmpty(name)) { return true; }
            string lower = name.Trim().ToLowerInvariant();

            if (lower.Length == 4 && IsDigits(lower) && lower[0] == '2') { return true; }
            if (lower.Length == 10 && lower[4] == '-' && lower[7] == '-') { return true; }
            if (lower.Length == 8 && IsDigits(lower)) { return true; }

            foreach (string structural in StructuralNames)
            {
                if (string.Equals(lower, structural, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static bool IsDigits(string text)
        {
            foreach (char c in text) { if (c < '0' || c > '9') { return false; } }
            return text.Length > 0;
        }

        /// <summary>子树内是否含素材（带缓存，避免重复遍历）。</summary>
        private static bool HasMediaInSubtree(string directory, HashSet<string> withMedia, Dictionary<string, bool> cache)
        {
            bool cached;
            if (cache.TryGetValue(directory, out cached)) { return cached; }

            bool result = withMedia.Contains(directory);
            if (!result)
            {
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(directory))
                    {
                        if (HasMediaInSubtree(sub, withMedia, cache)) { result = true; break; }
                    }
                }
                catch (Exception) { }
            }
            cache[directory] = result;
            return result;
        }

        /// <summary>计算相对路径（容错：不在根下时返回目录名）。</summary>
        private static string ToRelative(string root, string directory)
        {
            try
            {
                string normalizedRoot = PathUtil.EnsureTrailingSeparator(Path.GetFullPath(root));
                string full = Path.GetFullPath(directory);
                if (full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return full.Substring(normalizedRoot.Length);
                }
                return Path.GetFileName(full);
            }
            catch (Exception)
            {
                return Path.GetFileName(directory);
            }
        }

        /// <summary>是否为照片或视频。</summary>
        private static bool IsMediaFile(string fullPath)
        {
            MediaKind kind = MediaClassifier.Classify(Path.GetFileName(fullPath));
            return kind == MediaKind.Photo || kind == MediaKind.Video;
        }

        /// <summary>汇总一个项目目录的素材数量、字节数与日期范围。</summary>
        private static LibraryCandidate Summarize(string directory, string root, bool precise, ILog log)
        {
            var candidate = new LibraryCandidate
            {
                Path = directory,
                Name = Path.GetFileName(directory),
                RelativePath = ToRelative(root, directory)
            };

            DateTime earliest = DateTime.MaxValue;
            DateTime latest = DateTime.MinValue;
            var samples = new List<string>();

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    MediaKind kind = MediaClassifier.Classify(Path.GetFileName(file));
                    if (kind == MediaKind.Other) { continue; }

                    long length = 0;
                    DateTime modified = DateTime.MinValue;
                    try
                    {
                        var info = new FileInfo(file);
                        length = info.Length;
                        modified = info.LastWriteTime;
                    }
                    catch (Exception) { continue; }

                    if (kind == MediaKind.Photo) { candidate.PhotoCount++; }
                    else { candidate.VideoCount++; }
                    candidate.TotalBytes += length;

                    if (modified < earliest) { earliest = modified; }
                    if (modified > latest) { latest = modified; }
                    if (precise && samples.Count < SampleFiles) { samples.Add(file); }
                }
            }
            catch (Exception ex)
            {
                if (log != null) { log.Warn("统计目录失败：" + directory + "：" + ex.Message); }
                return null;
            }

            if (candidate.TotalCount == 0) { return null; }

            // 精确模式下用少量样本读真实拍摄时间，避免文件时间偏差
            if (precise)
            {
                foreach (string sample in samples)
                {
                    try
                    {
                        ShootTimeResult read = MediaTimestampReader.Read(sample,
                            MediaClassifier.Classify(Path.GetFileName(sample)), false);
                        if (!read.Time.HasValue) { continue; }
                        if (read.Time.Value < earliest) { earliest = read.Time.Value; }
                        if (read.Time.Value > latest) { latest = read.Time.Value; }
                    }
                    catch (Exception) { }
                }
            }

            candidate.StartDate = earliest == DateTime.MaxValue ? null : earliest.ToString("yyyy-MM-dd");
            candidate.EndDate = latest == DateTime.MinValue ? null : latest.ToString("yyyy-MM-dd");
            return candidate;
        }
    }
}
