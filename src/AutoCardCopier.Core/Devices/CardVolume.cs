using System;
using System.Collections.Generic;
using System.IO;
using AutoCardCopier.Core.Infrastructure;

namespace AutoCardCopier.Core.Devices
{
    /// <summary>
    /// 一张被识别为"相机存储卡"的卷。
    /// 注意：CFexpress 与 XQD 读卡器常被 Windows 报告为固定磁盘，
    /// 因此是否属于卡片由 CardDetector 通过多重条件判定，而不是只看驱动器类型。
    /// </summary>
    public sealed class CardVolume
    {
        /// <summary>根路径（以反斜杠结尾）。</summary>
        public string RootPath { get; set; }

        /// <summary>盘符，例如 E:。</summary>
        public string DriveLetter { get; set; }

        /// <summary>卷标。</summary>
        public string VolumeLabel { get; set; }

        /// <summary>文件系统名称。</summary>
        public string FileSystem { get; set; }

        /// <summary>驱动器类型。</summary>
        public DriveType DriveType { get; set; }

        /// <summary>总容量。</summary>
        public long TotalBytes { get; set; }

        /// <summary>可用容量。</summary>
        public long FreeBytes { get; set; }

        /// <summary>卡片指纹。</summary>
        public CardIdentity Identity { get; set; }

        /// <summary>卡上识别到的素材根目录（例如卡根下的 DCIM）；没有则为空。</summary>
        public string MediaRootPath { get { return FirstMediaRootPath(); } }

        /// <summary>
        /// 卡上识别到的**全部**素材根目录（绝对路径）。
        /// 一张卡可能同时有 DCIM（照片）与 PRIVATE、AVCHD（视频）；
        /// 早期版本只记录第一个目录，导致视频目录整段被跳过，因此必须保留全部。
        /// </summary>
        public IList<string> MediaRootPaths { get; set; }

        /// <summary>推荐的扫描入口：始终是卡片根目录，由扫描器决定遍历哪些子目录。</summary>
        public string ScanRootPath { get { return RootPath; } }

        /// <summary>识别到的特征目录名称列表（DCIM、M4ROOT 等）。</summary>
        public IList<string> MediaFolderNames { get; set; }

        /// <summary>取第一个素材根（兼容旧调用）。</summary>
        private string FirstMediaRootPath()
        {
            if (MediaRootPaths != null && MediaRootPaths.Count > 0) { return MediaRootPaths[0]; }
            return RootPath;
        }

        /// <summary>识别为卡片的原因（用于界面解释与日志）。</summary>
        public string DetectionReason { get; set; }

        /// <summary>卡片是否已就绪可用。</summary>
        public bool IsReady { get; set; }

        /// <summary>界面显示的标题，例如 EOS_DIGITAL（E: 256.0 GB）。</summary>
        public string DisplayName
        {
            get
            {
                string label = string.IsNullOrWhiteSpace(VolumeLabel) ? "未命名卡" : VolumeLabel.Trim();
                return label + "（" + DriveLetter + " " + Humanize.Bytes(TotalBytes) + "）";
            }
        }

        /// <summary>可用空间占比（0 至 1）。</summary>
        public double FreeRatio
        {
            get { return TotalBytes <= 0 ? 0 : (double)FreeBytes / TotalBytes; }
        }
    }
}
