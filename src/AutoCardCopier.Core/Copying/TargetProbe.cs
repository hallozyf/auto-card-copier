using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>目标探测结果。</summary>
    public sealed class TargetProbeResult
    {
        /// <summary>目标根目录。</summary>
        public string Root { get; set; }

        /// <summary>目录是否存在且可访问。</summary>
        public bool Exists { get; set; }

        /// <summary>是否可写（成功写入并删除探针文件）。</summary>
        public bool Writable { get; set; }

        /// <summary>总容量（读不到时为 0）。</summary>
        public long TotalBytes { get; set; }

        /// <summary>可用空间（读不到时为 0）。</summary>
        public long FreeBytes { get; set; }

        /// <summary>是否因为超时被判定为不可用。</summary>
        public bool TimedOut { get; set; }

        /// <summary>结果说明（面向界面）。</summary>
        public string Message { get; set; }

        /// <summary>是否可用。</summary>
        public bool Available { get { return Exists && Writable && !TimedOut; } }
    }

    /// <summary>
    /// 目标可用性探测（BUG-002 修复的关键组件）。
    ///
    /// 背景：v0.2.0 在界面线程上对目标（可能是 NAS）做同步文件系统访问，
    /// 数百次 SMB 往返导致窗口“未响应”。
    ///
    /// 本类保证：
    /// 1. 探测逻辑跑在**专用后台线程**上，调用方 await 结果，界面线程永不阻塞；
    /// 2. 超过超时时间立即返回“目标响应超时”，即使底层 I/O 仍被卡住；
    /// 3. 卡住的线程是后台线程，不会阻止程序退出，也不会占用线程池线程。
    /// </summary>
    public static class TargetProbe
    {
        /// <summary>默认超时时间（毫秒）。</summary>
        public const int DefaultTimeoutMs = 3000;

        /// <summary>探针文件名（写入后立即删除）。</summary>
        private const string ProbeFileName = ".acccopier-write-probe";

        /// <summary>
        /// 异步探测目标是否可用；到达超时时间即返回超时结果。
        /// </summary>
        public static async Task<TargetProbeResult> CheckAsync(string root, int timeoutMs = DefaultTimeoutMs,
            CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return new TargetProbeResult { Root = root, Message = "目标目录为空" };
            }
            if (timeoutMs <= 0) { timeoutMs = DefaultTimeoutMs; }

            var completion = new TaskCompletionSource<TargetProbeResult>();

            // 专用后台线程：即使底层 I/O 卡住也不会影响界面与线程池
            var thread = new Thread(delegate ()
            {
                TargetProbeResult result;
                try { result = CheckCore(root); }
                catch (Exception ex)
                {
                    result = new TargetProbeResult { Root = root, Message = "目标探测失败：" + ex.Message };
                }
                completion.TrySetResult(result);
            });
            thread.IsBackground = true;
            thread.Name = "TargetProbe";
            thread.Start();

            Task timeoutTask = Task.Delay(timeoutMs, token);
            Task finished = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);

            if (finished == completion.Task)
            {
                return await completion.Task.ConfigureAwait(false);
            }

            // 超时：立即返回，让界面有机会提示用户；后台线程稍后会自行结束
            token.ThrowIfCancellationRequested();
            return new TargetProbeResult
            {
                Root = root,
                TimedOut = true,
                Message = string.Format("目标响应超时（超过 {0} 秒）：{1}", Math.Round(timeoutMs / 1000.0, 1), root)
            };
        }

        /// <summary>实际的探测逻辑（只在后台线程上执行）。</summary>
        private static TargetProbeResult CheckCore(string root)
        {
            var result = new TargetProbeResult { Root = root };

            try
            {
                result.Exists = Directory.Exists(root);
            }
            catch (Exception)
            {
                result.Exists = false;
            }

            if (!result.Exists)
            {
                result.Message = "目标目录不存在或不可访问：" + root;
                return result;
            }

            try
            {
                string probePath = Path.Combine(root, ProbeFileName);
                File.WriteAllText(probePath, "ok");
                File.Delete(probePath);
                result.Writable = true;
            }
            catch (Exception ex)
            {
                result.Writable = false;
                result.Message = "目标目录不可写：" + ex.Message;
                return result;
            }

            try
            {
                string volumeRoot = Path.GetPathRoot(Path.GetFullPath(root));
                if (!string.IsNullOrEmpty(volumeRoot))
                {
                    var drive = new DriveInfo(volumeRoot);
                    if (drive.IsReady)
                    {
                        result.TotalBytes = drive.TotalSize;
                        result.FreeBytes = drive.AvailableFreeSpace;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到容量不影响“可用”判定
            }

            result.Message = "目标可用";
            return result;
        }
    }
}
