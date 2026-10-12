using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Scanning
{
    /// <summary>
    /// 卡片素材扫描器：只读遍历卡上的素材目录，统计数量、体积与时间范围，并尝试识别相机型号。
    /// 设计要点：
    /// 1. 全程只读，绝不修改卡上任何内容；
    /// 2. 单个目录或文件出错只跳过并计数，不中断整体扫描；
    /// 3. 支持取消，并在扫描过程中按节流上报进度。
    /// </summary>
    public sealed class MediaScanner
    {
        /// <summary>已知的相机素材根目录名（不区分大小写）。</summary>
        private static readonly string[] MediaRootCandidates =
        {
            "DCIM", "M4ROOT", "AVCHD", "XDROOT", "PRIVATE", "BMD",
            // 补充：佳能 XF 的 CONTENTS、老机型 MP_ROOT、以及常见的自建素材目录
            "CONTENTS", "MP_ROOT", "VIDEO", "MOVIE", "CLIP"
        };

        private readonly ILog _log;

        public MediaScanner(ILog log)
        {
            _log = log ?? NullLog.Instance;
        }

        /// <summary>
        /// 扫描指定卡片根目录。
        /// </summary>
        /// <param name="cardRootPath">卡片根目录（例如 E: 加反斜杠）。</param>
        /// <param name="settings">设置（用于忽略目录等）。</param>
        /// <param name="progress">进度回调，可为 null。</param>
        /// <param name="token">取消标记。</param>
        public ScanResult Scan(string cardRootPath, AppSettings settings, IProgress<ScanProgress> progress, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(cardRootPath)) { throw new ArgumentException("卡片路径不能为空", "cardRootPath"); }
            settings = settings ?? AppSettings.CreateDefault();

            var stopwatch = Stopwatch.StartNew();
            var result = new ScanResult { RootPath = cardRootPath };
            var ignoreSet = BuildIgnoreSet(settings);

            IList<string> roots = ResolveScanRoots(cardRootPath, settings);
            result.ScannedRoots = new List<string>(roots);
            _log.Info("扫描范围（" + roots.Count + " 个顶层目录）：" + string.Join("、", roots));
            var pending = new Stack<string>();
            // 逆序压栈，保证扫描顺序与目录顺序一致（更符合直觉，也便于观察进度）
            for (int i = roots.Count - 1; i >= 0; i--) { pending.Push(roots[i]); }

            var reporter = new ProgressThrottle(progress, 200);
            int filesScanned = 0;
            long bytesScanned = 0;

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();

                string[] subDirectories;
                string[] files;
                if (!TryEnumerateDirectory(directory, out subDirectories, out files))
                {
                    result.SkippedCount++;
                    continue;
                }

                foreach (string sub in subDirectories)
                {
                    string name = Path.GetFileName(sub);
                    if (name != null && ignoreSet.Contains(name)) { continue; }
                    pending.Push(sub);
                }

                foreach (string file in files)
                {
                    token.ThrowIfCancellationRequested();

                    string fileName = Path.GetFileName(file);
                    if (settings.SkipNonMediaFiles && MediaClassifier.IsJunkFile(fileName))
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(file);
                        if (!info.Exists) { result.SkippedCount++; continue; }
                    }
                    catch (Exception)
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    // 0 字节文件通常是相机或系统产生的占位文件，不参与拷贝
                    if (info.Length == 0)
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    MediaKind kind = MediaClassifier.Classify(fileName);
                    var scanned = new ScannedFile
                    {
                        FullPath = file,
                        RelativePath = MakeRelative(cardRootPath, file),
                        Length = info.Length,
                        LastWriteTimeUtc = info.LastWriteTimeUtc,
                        Kind = kind
                    };
                    result.Files.Add(scanned);

                    filesScanned++;
                    bytesScanned += info.Length;
                    Accumulate(result, scanned);
                    reporter.Report(new ScanProgress
                    {
                        Phase = "扫描素材",
                        FilesScanned = filesScanned,
                        BytesScanned = bytesScanned,
                        CurrentItem = fileName
                    });
                }
            }

            reporter.Flush(new ScanProgress
            {
                Phase = "扫描素材",
                FilesScanned = filesScanned,
                BytesScanned = bytesScanned,
                CurrentItem = string.Empty
            });

            result.CameraModel = TryDetectCameraModel(result, token, reporter, filesScanned, bytesScanned);
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            _log.Info(string.Format("扫描完成：{0} 个文件，{1}，相机型号 {2}，跳过 {3} 个",
                result.TotalCount, Humanize.Bytes(result.TotalBytes),
                string.IsNullOrEmpty(result.CameraModel) ? "未知" : result.CameraModel,
                result.SkippedCount));

            return result;
        }

        /// <summary>
        /// 决定从哪些目录开始扫描。
        ///
        /// 修复 BUG-001：早期实现"命中第一个特征目录就不再扫描其它目录"，
        /// 导致"照片在 DCIM、视频在 PRIVATE/AVCHD/CONTENTS"的卡漏扫全部视频。
        /// 现在改为：特征目录全部纳入 + （可选）卡根下的其它非忽略目录也纳入；
        /// 一个特征目录都没有时才直接扫描卡根。
        /// </summary>
        public static IList<string> ResolveScanRoots(string cardRootPath, AppSettings settings)
        {
            var roots = new List<string>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string candidate in MediaRootCandidates)
            {
                string path = Path.Combine(cardRootPath, candidate);
                try
                {
                    if (Directory.Exists(path) && added.Add(path)) { roots.Add(path); }
                }
                catch (Exception)
                {
                }
            }

            // 扫描卡根下的其它目录：相机可能把素材放在非标准目录里，漏扫意味着素材不会被备份
            if (settings == null || settings.ScanWholeCardRoot)
            {
                var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ignored.Add("System Volume Information");
                ignored.Add("$RECYCLE.BIN");
                ignored.Add(".Trashes");
                ignored.Add(".Spotlight-V100");
                ignored.Add(".fseventsd");
                if (settings != null && settings.IgnoredDirectories != null)
                {
                    foreach (string name in settings.IgnoredDirectories)
                    {
                        if (!string.IsNullOrWhiteSpace(name)) { ignored.Add(name.Trim()); }
                    }
                }

                try
                {
                    foreach (string directory in Directory.GetDirectories(cardRootPath))
                    {
                        string name = Path.GetFileName(directory);
                        if (string.IsNullOrEmpty(name) || ignored.Contains(name)) { continue; }
                        if (added.Add(directory)) { roots.Add(directory); }
                    }
                }
                catch (Exception)
                {
                    // 卡根枚举失败时不影响已找到的特征目录
                }
            }

            if (roots.Count == 0) { roots.Add(cardRootPath); }
            return roots;
        }

        /// <summary>枚举目录内容，遇到权限或 IO 异常时返回 false。</summary>
        public static bool TryEnumerateDirectory(string directory, out string[] subDirectories, out string[] files)
        {
            subDirectories = new string[0];
            files = new string[0];
            try
            {
                subDirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static HashSet<string> BuildIgnoreSet(AppSettings settings)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add("System Volume Information");
            set.Add("$RECYCLE.BIN");
            set.Add(".Trashes");
            if (settings.IgnoredDirectories != null)
            {
                foreach (string name in settings.IgnoredDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(name)) { set.Add(name.Trim()); }
                }
            }
            return set;
        }

        /// <summary>计算相对路径；失败时退化为文件名。</summary>
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
            return Path.GetFileName(fullPath);
        }

        private static void Accumulate(ScanResult result, ScannedFile file)
        {
            switch (file.Kind)
            {
                case MediaKind.Photo:
                    result.PhotoCount++;
                    result.PhotoBytes += file.Length;
                    break;
                case MediaKind.Video:
                    result.VideoCount++;
                    result.VideoBytes += file.Length;
                    break;
                default:
                    result.OtherCount++;
                    result.OtherBytes += file.Length;
                    break;
            }

            // 时间范围只统计真正的素材，避免旁挂文件干扰"拍摄时间"的判断
            if (file.Kind == MediaKind.Other) { return; }
            if (!result.EarliestUtc.HasValue || file.LastWriteTimeUtc < result.EarliestUtc.Value)
            {
                result.EarliestUtc = file.LastWriteTimeUtc;
            }
            if (!result.LatestUtc.HasValue || file.LastWriteTimeUtc > result.LatestUtc.Value)
            {
                result.LatestUtc = file.LastWriteTimeUtc;
            }
        }

        /// <summary>从少量照片文件中识别相机型号；识别不到返回空字符串。</summary>
        private static string TryDetectCameraModel(ScanResult result, CancellationToken token,
            ProgressThrottle reporter, int filesScanned, long bytesScanned)
        {
            int inspected = 0;
            foreach (ScannedFile file in result.Files)
            {
                if (file.Kind != MediaKind.Photo) { continue; }
                if (inspected >= 20 || token.IsCancellationRequested) { break; }
                inspected++;

                string model = ExifReader.TryReadCameraModel(file.FullPath);
                if (!string.IsNullOrWhiteSpace(model)) { return model; }
            }
            return string.Empty;
        }

        /// <summary>
        /// 进度节流器：避免高频进度回调把界面线程压垮。
        /// </summary>
        private sealed class ProgressThrottle
        {
            private readonly IProgress<ScanProgress> _progress;
            private readonly int _intervalMs;
            private DateTime _lastReport = DateTime.MinValue;

            public ProgressThrottle(IProgress<ScanProgress> progress, int intervalMs)
            {
                _progress = progress;
                _intervalMs = intervalMs;
            }

            public void Report(ScanProgress value)
            {
                if (_progress == null) { return; }
                if ((DateTime.UtcNow - _lastReport).TotalMilliseconds < _intervalMs) { return; }
                _lastReport = DateTime.UtcNow;
                _progress.Report(value);
            }

            public void Flush(ScanProgress value)
            {
                if (_progress == null) { return; }
                _progress.Report(value);
            }
        }
    }
}
