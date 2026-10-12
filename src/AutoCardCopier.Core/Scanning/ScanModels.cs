using System;
using System.Collections.Generic;

namespace AutoCardCopier.Core.Scanning
{
    /// <summary>扫描到的单个素材文件。</summary>
    public sealed class ScannedFile
    {
        /// <summary>源文件完整路径。</summary>
        public string FullPath { get; set; }

        /// <summary>相对于卡片素材根的路径（用于在目标目录中重建结构）。</summary>
        public string RelativePath { get; set; }

        /// <summary>字节数。</summary>
        public long Length { get; set; }

        /// <summary>最后写入时间（UTC）。</summary>
        public DateTime LastWriteTimeUtc { get; set; }

        /// <summary>素材类型。</summary>
        public MediaKind Kind { get; set; }
    }

    /// <summary>扫描进度。</summary>
    public sealed class ScanProgress
    {
        /// <summary>当前阶段说明（枚举素材 / 识别机型等）。</summary>
        public string Phase { get; set; }

        /// <summary>已扫描文件数。</summary>
        public int FilesScanned { get; set; }

        /// <summary>已扫描字节数。</summary>
        public long BytesScanned { get; set; }

        /// <summary>当前正在处理的条目。</summary>
        public string CurrentItem { get; set; }
    }

    /// <summary>一次扫描的完整结果。</summary>
    public sealed class ScanResult
    {
        public ScanResult()
        {
            Files = new List<ScannedFile>();
            ScannedRoots = new List<string>();
        }

        /// <summary>被扫描的卡片根目录。</summary>
        public string RootPath { get; set; }

        /// <summary>本次实际遍历的顶层目录（用于界面显示与日志留痕，便于排查"漏扫"）。</summary>
        public IList<string> ScannedRoots { get; set; }

        /// <summary>全部素材文件（不含被忽略的文件）。</summary>
        public IList<ScannedFile> Files { get; set; }

        /// <summary>照片数量。</summary>
        public int PhotoCount { get; set; }

        /// <summary>视频数量。</summary>
        public int VideoCount { get; set; }

        /// <summary>其他文件数量。</summary>
        public int OtherCount { get; set; }

        /// <summary>照片总字节。</summary>
        public long PhotoBytes { get; set; }

        /// <summary>视频总字节。</summary>
        public long VideoBytes { get; set; }

        /// <summary>其他文件总字节。</summary>
        public long OtherBytes { get; set; }

        /// <summary>被跳过的文件数量（0 字节文件、系统垃圾文件、无法访问的文件）。</summary>
        public int SkippedCount { get; set; }

        /// <summary>素材最早时间（UTC）。</summary>
        public DateTime? EarliestUtc { get; set; }

        /// <summary>素材最晚时间（UTC）。</summary>
        public DateTime? LatestUtc { get; set; }

        /// <summary>识别到的相机型号（可能为空）。</summary>
        public string CameraModel { get; set; }

        /// <summary>扫描耗时。</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>素材总数量。</summary>
        public int TotalCount { get { return Files.Count; } }

        /// <summary>素材总字节。</summary>
        public long TotalBytes { get { return PhotoBytes + VideoBytes + OtherBytes; } }

        /// <summary>是否包含视频。</summary>
        public bool HasVideo { get { return VideoCount > 0; } }

        /// <summary>是否包含照片。</summary>
        public bool HasPhoto { get { return PhotoCount > 0; } }

        /// <summary>本次拍摄的类型判断文本（照片 / 视频 / 照片 + 视频）。</summary>
        public string ContentSummary
        {
            get
            {
                if (HasPhoto && HasVideo) { return "照片 + 视频"; }
                if (HasVideo) { return "视频"; }
                if (HasPhoto) { return "照片"; }
                return "未发现素材";
            }
        }
    }
}
