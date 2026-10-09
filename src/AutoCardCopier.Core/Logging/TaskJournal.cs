using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoCardCopier.Core.Logging
{
    /// <summary>
    /// 任务流水日志：每个任务一个 JSONL 文件，一行一个事件。
    /// 用于事后回溯"这次拷卡到底发生了什么"，也用于断点续传时读取任务状态。
    /// </summary>
    public sealed class TaskJournal : IDisposable
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private bool _disposed;

        /// <param name="directory">流水日志目录（通常为 日志目录 下的 tasks 子目录）。</param>
        /// <param name="taskId">任务标识。</param>
        public TaskJournal(string directory, string taskId)
        {
            if (string.IsNullOrWhiteSpace(directory)) { throw new ArgumentException("目录不能为空", "directory"); }
            if (string.IsNullOrWhiteSpace(taskId)) { throw new ArgumentException("任务标识不能为空", "taskId"); }
            System.IO.Directory.CreateDirectory(directory);
            _path = System.IO.Path.Combine(directory, taskId + ".jsonl");
        }

        /// <summary>流水文件路径。</summary>
        public string FilePath { get { return _path; } }

        /// <summary>追加一个事件。</summary>
        public void Append(string eventType, params KeyValuePair<string, object>[] data)
        {
            var pairs = new List<KeyValuePair<string, object>>();
            pairs.Add(new KeyValuePair<string, object>("time", Infrastructure.TimeUtil.ToIso(DateTime.Now)));
            pairs.Add(new KeyValuePair<string, object>("event", eventType));
            if (data != null) { pairs.AddRange(data); }

            string line = Infrastructure.SimpleJson.WriteLine(pairs);
            lock (_gate)
            {
                if (_disposed) { return; }
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // 流水写入失败不应影响任务本身
                }
            }
        }

        /// <summary>读取本任务的全部事件行（用于续传与排查）。</summary>
        public IList<string> ReadAllLines()
        {
            var result = new List<string>();
            try
            {
                if (File.Exists(_path)) { result.AddRange(File.ReadAllLines(_path, Encoding.UTF8)); }
            }
            catch (Exception)
            {
            }
            return result;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
