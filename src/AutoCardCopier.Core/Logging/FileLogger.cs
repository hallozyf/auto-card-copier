using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AutoCardCopier.Core.Logging
{
    /// <summary>
    /// 按天滚动的文件日志。
    /// 设计要点：
    /// 1. 写日志失败绝不抛出异常（日志不能反过来影响主流程）；
    /// 2. 单日文件超过上限时自动切换到 日期.序号.log；
    /// 3. 启动时可清理超过保留天数的历史日志。
    /// </summary>
    public sealed class FileLogger : ILog, IDisposable
    {
        private readonly string _directory;
        private readonly string _filePrefix;
        private readonly long _maxBytesPerFile;
        private readonly object _gate = new object();
        private readonly bool _includeDebug;

        private StreamWriter _writer;
        private string _currentDate;
        private int _sequence;
        private bool _disposed;

        /// <param name="directory">日志目录。</param>
        /// <param name="filePrefix">文件名前缀，可为空。</param>
        /// <param name="maxBytesPerFile">单个日志文件大小上限，默认 10 MB。</param>
        /// <param name="includeDebug">是否写入 Debug 级别。</param>
        public FileLogger(string directory, string filePrefix = null, long maxBytesPerFile = 10 * 1024 * 1024, bool includeDebug = false)
        {
            if (string.IsNullOrWhiteSpace(directory)) { throw new ArgumentException("日志目录不能为空", "directory"); }
            _directory = directory;
            _filePrefix = string.IsNullOrEmpty(filePrefix) ? string.Empty : filePrefix + "-";
            _maxBytesPerFile = maxBytesPerFile;
            _includeDebug = includeDebug;
            System.IO.Directory.CreateDirectory(_directory);
        }

        /// <summary>当前日志目录。</summary>
        public string DirectoryPath { get { return _directory; } }

        public void Write(LogLevel level, string message, Exception error = null)
        {
            if (level == LogLevel.Debug && !_includeDebug) { return; }

            var line = new StringBuilder();
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(LevelText(level)).Append("] ")
                .Append(message ?? string.Empty);
            if (error != null)
            {
                line.Append(" | ").Append(error.GetType().Name).Append(": ").Append(error.Message);
                if (!string.IsNullOrEmpty(error.StackTrace))
                {
                    line.Append(Environment.NewLine).Append(error.StackTrace);
                }
            }

            lock (_gate)
            {
                if (_disposed) { return; }
                try
                {
                    EnsureWriter(Encoding.UTF8.GetByteCount(line.ToString()));
                    _writer.WriteLine(line.ToString());
                    _writer.Flush();
                }
                catch (Exception)
                {
                    // 磁盘满、权限不足等情况下静默放弃本条日志
                }
            }
        }

        /// <summary>清理超过保留天数的日志文件。</summary>
        public void CleanupOldFiles(int retentionDays)
        {
            if (retentionDays <= 0) { return; }
            DateTime threshold = DateTime.Now.Date.AddDays(-retentionDays);
            try
            {
                foreach (string file in System.IO.Directory.GetFiles(_directory, "*.log"))
                {
                    if (File.GetLastWriteTime(file) < threshold)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception)
            {
                // 清理失败不影响主流程
            }
        }

        /// <summary>列出当前目录中的日志文件。</summary>
        public IList<string> ListFiles()
        {
            var result = new List<string>();
            try
            {
                result.AddRange(System.IO.Directory.GetFiles(_directory, "*.log"));
            }
            catch (Exception)
            {
            }
            return result;
        }

        private void EnsureWriter(long pendingBytes)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            bool needNewFile = _writer == null || _currentDate != today;
            if (!needNewFile && _writer.BaseStream.Length + pendingBytes > _maxBytesPerFile)
            {
                // 超过单文件上限：换一个序号文件
                _sequence++;
                needNewFile = true;
            }

            if (!needNewFile) { return; }

            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }

            if (_currentDate != today)
            {
                _currentDate = today;
                _sequence = 0;
            }

            string fileName = _sequence == 0
                ? string.Format("{0}{1}.log", _filePrefix, today)
                : string.Format("{0}{1}.{2}.log", _filePrefix, today, _sequence);
            string path = Path.Combine(_directory, fileName);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _writer.AutoFlush = false;
        }

        private static string LevelText(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DEBUG";
                case LogLevel.Info: return "INFO";
                case LogLevel.Warn: return "WARN";
                case LogLevel.Error: return "ERROR";
                default: return level.ToString().ToUpperInvariant();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                if (_writer != null)
                {
                    try { _writer.Dispose(); } catch (Exception) { }
                    _writer = null;
                }
            }
        }
    }
}
