using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Formatting
{
    /// <summary>格式化结果。</summary>
    public sealed class FormatResult
    {
        /// <summary>是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>结果说明。</summary>
        public string Message { get; set; }

        /// <summary>格式化后的文件系统名称。</summary>
        public string FileSystem { get; set; }

        /// <summary>格式化后的可用空间。</summary>
        public long FreeBytes { get; set; }

        /// <summary>format.com 的原始输出（便于排查失败原因）。</summary>
        public string Output { get; set; }
    }

    /// <summary>
    /// 卡片格式化与清空服务。
    /// 安全策略：所有调用都必须先通过 DiskSafetyInspector 的护栏检查，本类内部再检查一次。
    /// </summary>
    public sealed class CardFormatter
    {
        /// <summary>format.com 的超时时间。</summary>
        private static readonly TimeSpan FormatTimeout = TimeSpan.FromSeconds(180);

        private readonly ILog _log;

        public CardFormatter(ILog log)
        {
            _log = log ?? NullLog.Instance;
        }

        /// <summary>
        /// 对卡片执行快速格式化。
        /// </summary>
        /// <param name="driveRoot">盘符根路径。</param>
        /// <param name="option">文件系统选择。</param>
        /// <param name="label">卷标；为空表示沿用原卷标。</param>
        /// <param name="hasVerifiedCopy">是否已完成拷贝 + 校验。</param>
        public FormatResult Format(string driveRoot, FormatFileSystem option, string label, bool hasVerifiedCopy)
        {
            DiskSafetyReport safety = DiskSafetyInspector.Inspect(driveRoot, hasVerifiedCopy, _log);
            if (!safety.CanFormat)
            {
                _log.Warn("格式化被安全护栏拒绝：" + safety.Reason);
                return new FormatResult { Success = false, Message = safety.Reason };
            }

            string driveLetter = safety.DriveLetter.TrimEnd(':');
            VolumeInfo before = VolumeApi.Read(driveRoot, _log);
            string fileSystem = ResolveFileSystem(option, before);
            string volumeLabel = string.IsNullOrWhiteSpace(label)
                ? (before == null ? string.Empty : before.Label)
                : label.Trim();

            if (string.Equals(fileSystem, "FAT32", StringComparison.OrdinalIgnoreCase)
                && before != null && before.TotalBytes > 32L * 1024 * 1024 * 1024)
            {
                _log.Warn("卡片容量大于 32 GB 却选择 FAT32，单个 4 GB 以上的视频文件将无法写入");
            }

            string arguments = string.Format(CultureInfo.InvariantCulture,
                "{0}: /FS:{1} /Q /Y{2}",
                driveLetter, fileSystem,
                string.IsNullOrWhiteSpace(volumeLabel) ? string.Empty : " /V:" + volumeLabel);

            _log.Info("开始格式化：" + driveLetter + " 文件系统 " + fileSystem + " 卷标 " + volumeLabel);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "format.com"),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                string output;
                int exitCode;
                using (Process process = Process.Start(startInfo))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit((int)FormatTimeout.TotalMilliseconds))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return new FormatResult { Success = false, Message = "格式化超时（超过 180 秒），请检查卡片是否被其他程序占用" };
                    }
                    exitCode = process.ExitCode;
                    output = stdout + (string.IsNullOrEmpty(stderr) ? string.Empty : "\n" + stderr);
                }

                if (exitCode != 0)
                {
                    string reason = ExplainFailure(output);
                    _log.Error("格式化失败（退出码 " + exitCode + "）：" + reason + "\n" + output);
                    return new FormatResult { Success = false, Message = reason, Output = output };
                }

                // 复核：重新读取卷信息确认格式化结果
                Thread.Sleep(1500);
                VolumeInfo after = VolumeApi.Read(driveRoot, _log);
                if (after == null || after.TotalBytes <= 0)
                {
                    return new FormatResult { Success = false, Message = "格式化命令已执行，但无法重新读取卡片信息，请手动确认", Output = output };
                }

                var result = new FormatResult
                {
                    Success = true,
                    FileSystem = after.FileSystem,
                    FreeBytes = after.FreeBytes,
                    Output = output,
                    Message = string.Format("格式化完成：{0}，可用空间 {1}", after.FileSystem, Humanize.Bytes(after.FreeBytes))
                };
                _log.Info(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                _log.Error("格式化过程异常", ex);
                return new FormatResult { Success = false, Message = "格式化失败：" + ex.Message };
            }
        }

        /// <summary>
        /// 清空卡片素材文件（保留目录结构与文件系统）。
        /// 用于不愿意格式化的场景；同样要求已经完成拷贝与校验。
        /// </summary>
        public FormatResult EraseFiles(string mediaRootPath, bool hasVerifiedCopy, IProgress<int> progress, CancellationToken token)
        {
            if (!hasVerifiedCopy)
            {
                return new FormatResult { Success = false, Message = "本次尚未完成“拷贝 + 校验通过”，出于安全考虑不允许清空卡片" };
            }
            if (string.IsNullOrWhiteSpace(mediaRootPath) || !Directory.Exists(PathUtil.Extend(mediaRootPath)))
            {
                return new FormatResult { Success = false, Message = "卡片素材目录不存在：" + mediaRootPath };
            }

            int deleted = 0;
            int failed = 0;
            try
            {
                foreach (string file in Directory.GetFiles(PathUtil.Extend(mediaRootPath), "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        deleted++;
                        if (progress != null && deleted % 50 == 0) { progress.Report(deleted); }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _log.Warn("删除文件失败：" + file, ex);
                    }
                }

                if (progress != null) { progress.Report(deleted); }
                string message = string.Format("已清空卡片：删除 {0} 个文件{1}", deleted,
                    failed > 0 ? "，失败 " + failed + " 个" : string.Empty);
                _log.Info(message);
                return new FormatResult { Success = failed == 0, Message = message };
            }
            catch (OperationCanceledException)
            {
                return new FormatResult { Success = false, Message = "已取消清空操作（已删除部分文件）" };
            }
            catch (Exception ex)
            {
                _log.Error("清空卡片失败", ex);
                return new FormatResult { Success = false, Message = "清空卡片失败：" + ex.Message };
            }
        }

        /// <summary>把 format.com 的输出翻译成用户能看懂的原因。</summary>
        private static string ExplainFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) { return "格式化失败（format.com 未返回信息）"; }
            string text = output.ToLowerInvariant();

            if (text.Contains("cannot lock") || text.Contains("being used") || text.Contains("in use"))
            {
                return "格式化失败：卡片正在被其他程序使用，请关闭资源管理器窗口或相机软件后重试";
            }
            if (text.Contains("write protect") || text.Contains("write-protected"))
            {
                return "格式化失败：卡片处于写保护状态（检查卡套上的写保护开关）";
            }
            if (text.Contains("unsupported") || text.Contains("not supported"))
            {
                return "格式化失败：卡片不支持所选的文件系统，请改用 exFAT";
            }
            if (text.Contains("access is denied") || text.Contains("access denied"))
            {
                return "格式化失败：拒绝访问，请确认卡片未被占用";
            }
            return "格式化失败：" + output.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        /// <summary>决定最终使用的文件系统。</summary>
        private static string ResolveFileSystem(FormatFileSystem option, VolumeInfo before)
        {
            switch (option)
            {
                case FormatFileSystem.ExFat: return "exFAT";
                case FormatFileSystem.Fat32: return "FAT32";
                default:
                    // 沿用原文件系统；读不到时按容量给出合理默认值
                    if (before != null && !string.IsNullOrWhiteSpace(before.FileSystem))
                    {
                        return before.FileSystem;
                    }
                    return (before != null && before.TotalBytes > 32L * 1024 * 1024 * 1024) ? "exFAT" : "FAT32";
            }
        }
    }
}
