using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.Formatting
{
    /// <summary>磁盘安全判定结果。</summary>
    public sealed class DiskSafetyReport
    {
        /// <summary>是否允许执行格式化。</summary>
        public bool CanFormat { get; set; }

        /// <summary>不允许时的原因（面向用户的中文说明）。</summary>
        public string Reason { get; set; }

        /// <summary>盘符（例如 E:）。</summary>
        public string DriveLetter { get; set; }

        /// <summary>驱动器类型。</summary>
        public DriveType DriveType { get; set; }

        /// <summary>物理磁盘序号（WMI 中的 Index）。</summary>
        public int DiskIndex { get; set; }

        /// <summary>物理磁盘接口类型（USB、SCSI、IDE、NVMe 等）。</summary>
        public string InterfaceType { get; set; }

        /// <summary>物理磁盘媒体类型。</summary>
        public string MediaType { get; set; }

        /// <summary>物理磁盘型号。</summary>
        public string Model { get; set; }

        /// <summary>判定说明（用于日志与界面提示）。</summary>
        public string Description
        {
            get
            {
                return string.Format("{0} · 物理磁盘 {1} · 接口 {2} · {3}",
                    DriveLetter, DiskIndex, InterfaceType, MediaType);
            }
        }
    }

    /// <summary>
    /// 磁盘安全判定：决定某个盘符是否允许被格式化。
    ///
    /// 这是整个软件里最危险的一步，因此护栏是硬编码、不可配置的：
    /// 1. 不能是系统盘；
    /// 2. 不能是网络驱动器、光驱、无根目录的卷；
    /// 3. 对应物理磁盘不能是系统盘所在磁盘；
    /// 4. 物理磁盘接口必须是 USB（外接），否则拒绝；
    /// 5. 必须存在"本次已成功拷贝并校验通过"的凭证（由调用方传入）。
    /// </summary>
    public static class DiskSafetyInspector
    {
        /// <summary>执行判定。</summary>
        /// <param name="driveRoot">盘符根路径，例如 E: 加反斜杠。</param>
        /// <param name="hasVerifiedCopy">本次是否已经完成"拷贝 + 校验通过"。</param>
        /// <param name="log">日志。</param>
        public static DiskSafetyReport Inspect(string driveRoot, bool hasVerifiedCopy, ILog log)
        {
            var report = new DiskSafetyReport
            {
                CanFormat = false,
                DriveLetter = driveRoot == null ? string.Empty : driveRoot.TrimEnd(Path.DirectorySeparatorChar),
                Reason = string.Empty
            };

            if (string.IsNullOrWhiteSpace(driveRoot))
            {
                report.Reason = "盘符为空";
                return report;
            }

            // 规则 1：系统盘
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:";
            if (string.Equals(report.DriveLetter, systemDrive.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                report.Reason = "不能对系统盘执行格式化";
                return report;
            }

            // 规则 2：驱动器类型
            try
            {
                var info = new DriveInfo(driveRoot);
                report.DriveType = info.DriveType;
                if (info.DriveType == DriveType.Network)
                {
                    report.Reason = "网络驱动器不支持格式化";
                    return report;
                }
                if (info.DriveType == DriveType.CDRom || info.DriveType == DriveType.NoRootDirectory)
                {
                    report.Reason = "该设备不是可格式化的存储卷";
                    return report;
                }
            }
            catch (Exception ex)
            {
                report.Reason = "无法读取驱动器信息：" + ex.Message;
                return report;
            }

            // 规则 3 与 4：物理磁盘信息
            try
            {
                string driveLetter = report.DriveLetter.TrimEnd(':') + ":";
                ManagementObject disk = FindPhysicalDisk(driveLetter);
                if (disk == null)
                {
                    report.Reason = "无法确定该盘符对应的物理磁盘（已拒绝操作）";
                    return report;
                }

                using (disk)
                {
                    report.DiskIndex = ToInt(disk["Index"]);
                    report.InterfaceType = Convert.ToString(disk["InterfaceType"]);
                    report.MediaType = Convert.ToString(disk["MediaType"]);
                    report.Model = Convert.ToString(disk["Model"]);
                }

                int systemDiskIndex = FindDiskIndexForDrive(systemDrive.TrimEnd(Path.DirectorySeparatorChar) + ":");
                if (systemDiskIndex >= 0 && systemDiskIndex == report.DiskIndex)
                {
                    report.Reason = "该磁盘是系统盘所在磁盘，禁止格式化";
                    return report;
                }

                if (!string.Equals(report.InterfaceType, "USB", StringComparison.OrdinalIgnoreCase))
                {
                    report.Reason = string.Format("该磁盘接口为 {0}，不是外接 USB 设备，禁止格式化", report.InterfaceType);
                    return report;
                }
            }
            catch (Exception ex)
            {
                if (log != null) { log.Warn("物理磁盘判定失败", ex); }
                report.Reason = "物理磁盘判定失败：" + ex.Message;
                return report;
            }

            // 规则 5：必须已经成功拷贝并校验通过
            if (!hasVerifiedCopy)
            {
                report.Reason = "本次尚未完成“拷贝 + 校验通过”，出于安全考虑不允许格式化";
                return report;
            }

            report.CanFormat = true;
            report.Reason = "安全检查通过：" + report.Description;
            return report;
        }

        /// <summary>通过 WMI 关联找到盘符对应的物理磁盘。</summary>
        private static ManagementObject FindPhysicalDisk(string driveLetter)
        {
            string partitionQuery = string.Format(
                "ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{0}'}} WHERE AssocClass=Win32_LogicalDiskToPartition",
                driveLetter.Replace("'", string.Empty));

            foreach (ManagementObject partition in new ManagementObjectSearcher(partitionQuery).Get())
            {
                using (partition)
                {
                    string diskQuery = string.Format(
                        "ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{0}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition",
                        Convert.ToString(partition["DeviceID"]).Replace("'", string.Empty));

                    foreach (ManagementObject disk in new ManagementObjectSearcher(diskQuery).Get())
                    {
                        return disk;
                    }
                }
            }
            return null;
        }

        /// <summary>找到某个盘符所在物理磁盘的序号；失败返回 -1。</summary>
        public static int FindDiskIndexForDrive(string driveLetter)
        {
            try
            {
                ManagementObject disk = FindPhysicalDisk(driveLetter);
                if (disk == null) { return -1; }
                using (disk) { return ToInt(disk["Index"]); }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>列出所有物理磁盘的简要信息（用于"关于"页或诊断）。</summary>
        public static IList<DiskSafetyReport> EnumeratePhysicalDisks(ILog log)
        {
            var list = new List<DiskSafetyReport>();
            try
            {
                foreach (ManagementObject disk in new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive").Get())
                {
                    using (disk)
                    {
                        list.Add(new DiskSafetyReport
                        {
                            DiskIndex = ToInt(disk["Index"]),
                            InterfaceType = Convert.ToString(disk["InterfaceType"]),
                            MediaType = Convert.ToString(disk["MediaType"]),
                            Model = Convert.ToString(disk["Model"]),
                            DriveLetter = string.Empty,
                            CanFormat = false,
                            Reason = "物理磁盘信息"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null) { log.Warn("枚举物理磁盘失败", ex); }
            }
            return list;
        }

        private static int ToInt(object value)
        {
            if (value == null) { return -1; }
            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : -1;
        }
    }
}
