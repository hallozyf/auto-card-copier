using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>拷贝进度快照。</summary>
    public sealed class CopyProgress
    {
        public int CompletedFiles { get; set; }
        public int TotalFiles { get; set; }
        public long CompletedBytes { get; set; }
        public long TotalBytes { get; set; }
        public string CurrentFile { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan Remaining { get; set; }
        public bool IsPaused { get; set; }

        /// <summary>当前写入的目标（多目标备份时用于区分主目标与副目标）。</summary>
        public string TargetLabel { get; set; }

        /// <summary>当前目标序号（从 1 开始）与目标总数。</summary>
        public int TargetIndex { get; set; }
        public int TargetCount { get; set; }

        /// <summary>完成比例（0 至 1）。</summary>
        public double Ratio
        {
            get { return TotalBytes <= 0 ? 0 : Math.Min(1.0, (double)CompletedBytes / TotalBytes); }
        }
    }

    /// <summary>单个文件的失败信息。</summary>
    public sealed class CopyFailure
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string Error { get; set; }
    }

    /// <summary>拷贝结果。</summary>
    public sealed class CopyResult
    {
        public CopyResult()
        {
            Failures = new List<CopyFailure>();
            CompletedTargetPaths = new List<string>();
            FailedTargetPaths = new List<string>();
        }

        public int CopiedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int FailedFiles { get; set; }
        public long CopiedBytes { get; set; }
        public TimeSpan Duration { get; set; }
        public IList<CopyFailure> Failures { get; set; }

        /// <summary>成功写入的目标文件完整路径（用于精确断点续传的进度记录）。</summary>
        public IList<string> CompletedTargetPaths { get; private set; }

        /// <summary>写入失败的目标文件完整路径。</summary>
        public IList<string> FailedTargetPaths { get; private set; }

        /// <summary>初始化逐文件路径集合。</summary>
        public void EnsurePathCollections()
        {
            if (CompletedTargetPaths == null)
            {
                CompletedTargetPaths = new System.Collections.Generic.List<string>();
            }
            if (FailedTargetPaths == null)
            {
                FailedTargetPaths = new System.Collections.Generic.List<string>();
            }
        }
        public bool Cancelled { get; set; }

        /// <summary>是否全部成功。</summary>
        public bool AllSucceeded { get { return FailedFiles == 0 && !Cancelled; } }

        /// <summary>平均速度（字节每秒）。</summary>
        public double AverageBytesPerSecond
        {
            get { return Duration.TotalSeconds <= 0 ? 0 : CopiedBytes / Duration.TotalSeconds; }
        }
    }

    /// <summary>
    /// 并行拷贝引擎。
    /// 核心保证：
    /// 1. 目标目录永远不会出现半截文件——先写临时文件，校验长度后再原子改名；
    /// 2. 取消时清理临时文件；
    /// 3. 文件时间戳按源文件设置，保证"拍摄时间"信息不丢失；
    /// 4. 单文件失败自动重试，最终失败记入失败清单但不影响其他文件。
    /// </summary>
    public sealed class CopyEngine
    {
        private const string TempExtension = ".acctmp";
        private const int MaxRetryPerFile = 3;

        private readonly ILog _log;

        public CopyEngine(ILog log)
        {
            _log = log ?? NullLog.Instance;
        }

        /// <summary>
        /// 执行拷贝计划。
        /// </summary>
        public async Task<CopyResult> RunAsync(CopyPlan plan, AppSettings settings,
            IProgress<CopyProgress> progress, PauseToken pause, CancellationToken token)
        {
            if (plan == null) { throw new ArgumentNullException("plan"); }
            settings = settings ?? AppSettings.CreateDefault();
            pause = pause ?? new PauseToken();

            var result = new CopyResult();
            IList<CopyItem> pending = plan.GetPendingItems();
            result.SkippedFiles = plan.TotalCount - pending.Count;

            var stopwatch = Stopwatch.StartNew();
            int completedFiles = 0;
            long completedBytes = 0;
            long lastBytes = 0;
            var lastTick = DateTime.UtcNow;
            double speed = 0;
            var progressLock = new object();

            Action<string, long> report = delegate (string currentFile, long bytesNow)
            {
                if (progress == null) { return; }
                lock (progressLock)
                {
                    DateTime now = DateTime.UtcNow;
                    double seconds = (now - lastTick).TotalSeconds;
                    if (seconds >= 0.5)
                    {
                        speed = (bytesNow - lastBytes) / seconds;
                        lastBytes = bytesNow;
                        lastTick = now;
                    }

                    double remainingSeconds = speed > 1
                        ? (plan.PendingBytes - bytesNow) / speed
                        : double.NaN;

                    progress.Report(new CopyProgress
                    {
                        CompletedFiles = Volatile.Read(ref completedFiles),
                        TotalFiles = pending.Count,
                        CompletedBytes = bytesNow,
                        TotalBytes = plan.PendingBytes,
                        CurrentFile = currentFile,
                        BytesPerSecond = speed,
                        Elapsed = stopwatch.Elapsed,
                        Remaining = double.IsNaN(remainingSeconds) ? TimeSpan.Zero : TimeSpan.FromSeconds(remainingSeconds),
                        IsPaused = pause.IsPaused
                    });
                }
            };

            try
            {
                Directory.CreateDirectory(PathUtil.Extend(plan.ProjectDirectory));

                int parallelism = Math.Max(1, Math.Min(8, settings.MaxParallelism));
                using (var gate = new SemaphoreSlim(parallelism))
                {
                    var tasks = new List<Task>();
                    foreach (CopyItem item in pending)
                    {
                        token.ThrowIfCancellationRequested();
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        await pause.WaitWhilePausedAsync(token).ConfigureAwait(false);

                        CopyItem captured = item;
                        tasks.Add(Task.Run(delegate
                        {
                            try
                            {
                                CopyOneFile(captured, settings, token, delegate (long delta)
                                {
                                    long now = Interlocked.Add(ref completedBytes, delta);
                                    report(Path.GetFileName(captured.Source.FullPath), now);
                                });
                                lock (result.CompletedTargetPaths) { result.CompletedTargetPaths.Add(captured.TargetPath); }
                                Interlocked.Increment(ref completedFiles);
                                report(Path.GetFileName(captured.Source.FullPath), Interlocked.Read(ref completedBytes));
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                lock (result.Failures)
                                {
                                    result.Failures.Add(new CopyFailure
                                    {
                                        SourcePath = captured.Source.FullPath,
                                        TargetPath = captured.TargetPath,
                                        Error = ex.Message
                                    });
                                    result.FailedTargetPaths.Add(captured.TargetPath);
                                }
                                _log.Error("拷贝失败：" + captured.Source.FullPath, ex);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }, token));
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                _log.Warn("拷贝任务已取消，已写入的文件保留，临时文件已清理");
            }

            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            result.FailedFiles = result.Failures.Count;
            result.CopiedFiles = pending.Count - result.FailedFiles;
            result.CopiedBytes = Interlocked.Read(ref completedBytes);

            _log.Info(string.Format("拷贝结束：成功 {0} 个，失败 {1} 个，跳过 {2} 个，耗时 {3}，平均 {4}",
                result.CopiedFiles, result.FailedFiles, result.SkippedFiles,
                Humanize.Duration(result.Duration), Humanize.Speed(result.AverageBytesPerSecond)));

            return result;
        }

        /// <summary>拷贝单个文件（带重试）。</summary>
        private void CopyOneFile(CopyItem item, AppSettings settings, CancellationToken token, Action<long> onBytes)
        {
            Exception lastError = null;
            for (int attempt = 1; attempt <= MaxRetryPerFile; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    CopyWithTempFile(item, settings, token, onBytes);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _log.Warn(string.Format("第 {0} 次拷贝失败：{1}", attempt, item.Source.FullPath), ex);
                    if (attempt < MaxRetryPerFile)
                    {
                        // 指数退避：1 秒、3 秒、9 秒
                        int delaySeconds = (int)Math.Pow(3, attempt - 1);
                        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(delaySeconds));
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            throw lastError ?? new IOException("拷贝失败");
        }

        /// <summary>
        /// 通过"临时文件 + 原子改名"写入目标，保证目标目录不出现半截文件。
        /// </summary>
        private void CopyWithTempFile(CopyItem item, AppSettings settings, CancellationToken token, Action<long> onBytes)
        {
            string targetDirectory = Path.GetDirectoryName(item.TargetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(PathUtil.Extend(targetDirectory));
            }

            string tempPath = item.TargetPath + TempExtension;
            string extendedSource = PathUtil.Extend(item.Source.FullPath);
            string extendedTemp = PathUtil.Extend(tempPath);

            try
            {
                int bufferSize = Math.Max(64, settings.BufferSizeKb) * 1024;
                using (var source = new FileStream(extendedSource, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan))
                using (var target = new FileStream(extendedTemp, FileMode.Create, FileAccess.Write,
                           FileShare.None, bufferSize, FileOptions.SequentialScan))
                {
                    var buffer = new byte[bufferSize];
                    long written = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, read);
                        written += read;
                        if (onBytes != null) { onBytes(read); }
                    }
                    target.Flush(true);

                    if (written != item.Length)
                    {
                        throw new IOException(string.Format("写入字节数不符：期望 {0}，实际 {1}", item.Length, written));
                    }
                }

                // 校验临时文件长度后原子改名
                var tempInfo = new FileInfo(extendedTemp);
                if (tempInfo.Length != item.Length)
                {
                    throw new IOException("临时文件长度校验失败");
                }

                string extendedTarget = PathUtil.Extend(item.TargetPath);
                if (File.Exists(extendedTarget)) { File.Delete(extendedTarget); }
                File.Move(extendedTemp, extendedTarget);

                if (settings.PreserveTimestamps)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(extendedTarget, item.Source.LastWriteTimeUtc);
                        File.SetCreationTimeUtc(extendedTarget, item.Source.LastWriteTimeUtc);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("设置文件时间戳失败：" + item.TargetPath, ex);
                    }
                }
            }
            catch (Exception)
            {
                // 任何失败都清理临时文件，绝不在目标目录留下半截文件
                TryDeleteTemp(extendedTemp);
                throw;
            }
        }

        private void TryDeleteTemp(string extendedTempPath)
        {
            try
            {
                if (File.Exists(extendedTempPath)) { File.Delete(extendedTempPath); }
            }
            catch (Exception ex)
            {
                _log.Warn("清理临时文件失败：" + extendedTempPath, ex);
            }
        }
    }
}
