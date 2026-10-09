using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoCardCopier.Core.Interop
{
    /// <summary>
    /// Win32 互操作入口。
    /// 说明：只声明本项目实际用到的少量 API，避免引入庞大的互操作库。
    /// </summary>
    public static class NativeMethods
    {
        /// <summary>设备变化消息（窗口消息）。</summary>
        public const int WM_DEVICECHANGE = 0x0219;

        /// <summary>设备到达（插入）。</summary>
        public const int DBT_DEVICEARRIVAL = 0x8000;

        /// <summary>设备移除（拔出）。</summary>
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        /// <summary>卷类型变化。</summary>
        public const int DBT_DEVTYP_VOLUME = 0x0002;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            int fileSystemNameSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        /// <summary>
        /// 读取卷信息（卷标、卷序列号、文件系统名称）。
        /// 失败时返回 false，调用方需要容忍读不到信息的设备。
        /// </summary>
        public static bool TryGetVolumeInformation(string rootPath, out string label, out uint serialNumber, out string fileSystemName)
        {
            label = string.Empty;
            fileSystemName = string.Empty;
            serialNumber = 0;

            try
            {
                var labelBuffer = new StringBuilder(261);
                var fsBuffer = new StringBuilder(261);
                uint componentLength;
                uint flags;

                bool ok = GetVolumeInformation(rootPath, labelBuffer, labelBuffer.Capacity,
                    out serialNumber, out componentLength, out flags, fsBuffer, fsBuffer.Capacity);
                if (!ok) { return false; }

                label = labelBuffer.ToString();
                fileSystemName = fsBuffer.ToString();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>读取卷容量信息（可用字节、总字节）。</summary>
        public static bool TryGetDiskFreeSpace(string rootPath, out long totalBytes, out long freeBytes)
        {
            totalBytes = 0;
            freeBytes = 0;
            try
            {
                ulong available;
                ulong total;
                ulong totalFree;
                if (!GetDiskFreeSpaceEx(rootPath, out available, out total, out totalFree)) { return false; }
                totalBytes = (long)Math.Min(total, long.MaxValue);
                freeBytes = (long)Math.Min(available, long.MaxValue);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
