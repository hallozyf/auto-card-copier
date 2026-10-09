using System;
using System.IO;
using AutoCardCopier.Core.Interop;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.Devices
{
    /// <summary>读取到的卷信息。</summary>
    public sealed class VolumeInfo
    {
        /// <summary>根路径，例如 C: 加反斜杠。</summary>
        public string RootPath { get; set; }

        /// <summary>驱动器类型。</summary>
        public DriveType DriveType { get; set; }

        /// <summary>卷标。</summary>
        public string Label { get; set; }

        /// <summary>文件系统名称（exFAT、FAT32、NTFS 等）。</summary>
        public string FileSystem { get; set; }

        /// <summary>卷序列号（用于生成卡片指纹）。</summary>
        public uint SerialNumber { get; set; }

        /// <summary>总容量（字节）。</summary>
        public long TotalBytes { get; set; }

        /// <summary>可用容量（字节）。</summary>
        public long FreeBytes { get; set; }
    }

    /// <summary>
    /// 卷信息读取封装：把 Win32 调用与 DriveInfo 组合成一份可直接使用的数据。
    /// </summary>
    public static class VolumeApi
    {
        /// <summary>读取指定盘符根路径的卷信息；读取失败时返回 null。</summary>
        public static VolumeInfo Read(string rootPath, ILog log = null)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) { return null; }

            try
            {
                var info = new VolumeInfo
                {
                    RootPath = rootPath,
                    Label = string.Empty,
                    FileSystem = string.Empty
                };

                uint serial;
                string label;
                string fileSystem;
                if (NativeMethods.TryGetVolumeInformation(rootPath, out label, out serial, out fileSystem))
                {
                    info.Label = label ?? string.Empty;
                    info.SerialNumber = serial;
                    info.FileSystem = fileSystem ?? string.Empty;
                }

                long total;
                long free;
                if (NativeMethods.TryGetDiskFreeSpace(rootPath, out total, out free))
                {
                    info.TotalBytes = total;
                    info.FreeBytes = free;
                }

                try
                {
                    info.DriveType = new DriveInfo(rootPath).DriveType;
                }
                catch (Exception)
                {
                    info.DriveType = DriveType.Unknown;
                }

                return info;
            }
            catch (Exception ex)
            {
                if (log != null) { log.Warn("读取卷信息失败：" + rootPath, ex); }
                return null;
            }
        }

        /// <summary>枚举当前系统中所有已就绪的卷（跳过光驱与未插入介质的驱动器）。</summary>
        public static System.Collections.Generic.IList<VolumeInfo> EnumerateVolumes(ILog log = null)
        {
            var result = new System.Collections.Generic.List<VolumeInfo>();
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex)
            {
                if (log != null) { log.Warn("枚举驱动器失败", ex); }
                return result;
            }

            foreach (DriveInfo drive in drives)
            {
                try
                {
                    if (!drive.IsReady) { continue; }
                    if (drive.DriveType == DriveType.CDRom || drive.DriveType == DriveType.NoRootDirectory) { continue; }

                    VolumeInfo info = Read(drive.RootDirectory.FullName, log);
                    if (info != null) { result.Add(info); }
                }
                catch (Exception ex)
                {
                    if (log != null) { log.Warn("读取驱动器失败：" + drive.Name, ex); }
                }
            }
            return result;
        }
    }
}
