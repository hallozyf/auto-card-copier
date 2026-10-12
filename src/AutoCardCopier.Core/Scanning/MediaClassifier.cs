using System;
using System.Collections.Generic;
using System.IO;

namespace AutoCardCopier.Core.Scanning
{
    /// <summary>素材类型。</summary>
    public enum MediaKind
    {
        /// <summary>照片（含 RAW 与普通图片）。</summary>
        Photo = 0,
        /// <summary>视频。</summary>
        Video = 1,
        /// <summary>其他（旁挂文件、音频、未知格式）。</summary>
        Other = 2
    }

    /// <summary>
    /// 素材分类器：按扩展名判断文件类型，并决定它在目标目录中的归属子目录。
    /// 扩展名清单集中在这里，新增相机格式时只改这一处。
    /// </summary>
    public static class MediaClassifier
    {
        /// <summary>照片扩展名。</summary>
        private static readonly HashSet<string> PhotoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cr3", ".cr2", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".dng",
            ".orf", ".rw2", ".pef", ".raw", ".rwl", ".3fr", ".iiq", ".x3f", ".mrw",
            ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".tif", ".tiff", ".png", ".bmp", ".webp", ".gpr"
        };

        /// <summary>RAW（需要单独分目录的）扩展名。</summary>
        private static readonly HashSet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cr3", ".cr2", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".dng",
            ".orf", ".rw2", ".pef", ".raw", ".rwl", ".3fr", ".iiq", ".x3f", ".mrw", ".gpr"
        };

        /// <summary>视频扩展名。</summary>
        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".mxf", ".braw", ".r3d", ".mts", ".m2ts", ".avi", ".mkv",
            ".mpg", ".mpeg", ".wmv", ".insv", ".lrv", ".crm", ".m4v", ".3gp", ".vob", ".mod",
            // 补充：HDV / JVC / DV / 流媒体等常见容器（老式摄像机与部分运动相机）
            ".ts", ".m2t", ".m2p", ".tod", ".dv", ".flv", ".webm", ".asf", ".divx"
        };

        /// <summary>系统垃圾文件（始终忽略）。</summary>
        private static readonly HashSet<string> JunkFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Thumbs.db", "desktop.ini", ".DS_Store", "ehthumbs.db"
        };

        /// <summary>按扩展名分类。</summary>
        public static MediaKind Classify(string fileNameOrExtension)
        {
            string extension = GetExtension(fileNameOrExtension);
            if (PhotoExtensions.Contains(extension)) { return MediaKind.Photo; }
            if (VideoExtensions.Contains(extension)) { return MediaKind.Video; }
            return MediaKind.Other;
        }

        /// <summary>是否为 RAW 文件。</summary>
        public static bool IsRaw(string fileNameOrExtension)
        {
            return RawExtensions.Contains(GetExtension(fileNameOrExtension));
        }

        /// <summary>是否为系统垃圾文件。</summary>
        public static bool IsJunkFile(string fileName)
        {
            return !string.IsNullOrEmpty(fileName) && JunkFileNames.Contains(Path.GetFileName(fileName));
        }

        /// <summary>
        /// 计算素材在项目目录下的相对子目录（使用正斜杠分隔，便于跨平台写法）；
        /// 不需要分目录时返回空字符串。
        /// </summary>
        public static string ResolveSubFolder(MediaKind kind, string fileName, Settings.SubFolderMode mode)
        {
            if (mode == Settings.SubFolderMode.None) { return string.Empty; }

            string typeFolder = TypeFolderName(kind);
            if (mode == Settings.SubFolderMode.ByMediaType) { return typeFolder; }

            // ByMediaTypeAndFormat：照片再按 RAW 与 JPG 分开
            if (kind == MediaKind.Photo)
            {
                return typeFolder + "/" + (IsRaw(fileName) ? "RAW" : "JPG");
            }
            return typeFolder;
        }

        /// <summary>类型目录名称。</summary>
        public static string TypeFolderName(MediaKind kind)
        {
            switch (kind)
            {
                case MediaKind.Photo: return "照片";
                case MediaKind.Video: return "视频";
                default: return "其他";
            }
        }

        /// <summary>统一取小写扩展名（带点）。</summary>
        private static string GetExtension(string fileNameOrExtension)
        {
            if (string.IsNullOrEmpty(fileNameOrExtension)) { return string.Empty; }
            string value = fileNameOrExtension;
            int slash = value.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) { value = value.Substring(slash + 1); }
            int dot = value.LastIndexOf('.');
            if (dot < 0) { return string.Empty; }
            return value.Substring(dot).ToLowerInvariant();
        }
    }
}
