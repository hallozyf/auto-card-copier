using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.History
{
    /// <summary>任务状态。</summary>
    public enum TaskStatus
    {
        /// <summary>进行中（若程序启动时仍看到该状态，说明上次任务被异常中断）。</summary>
        Running = 0,
        /// <summary>成功。</summary>
        Succeeded = 1,
        /// <summary>部分成功（有文件失败）。</summary>
        Partial = 2,
        /// <summary>失败。</summary>
        Failed = 3,
        /// <summary>被用户取消。</summary>
        Cancelled = 4
    }

    /// <summary>一次拷卡任务的记录。</summary>
    [DataContract(Name = "task")]
    public sealed class TaskRecord
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "status", Order = 1)] public string StatusText { get; set; }
        [DataMember(Name = "startedAt", Order = 2)] public string StartedAt { get; set; }
        [DataMember(Name = "finishedAt", Order = 3)] public string FinishedAt { get; set; }
        [DataMember(Name = "cardLabel", Order = 4)] public string CardLabel { get; set; }
        [DataMember(Name = "cardFingerprint", Order = 5)] public string CardFingerprint { get; set; }
        [DataMember(Name = "projectName", Order = 6)] public string ProjectName { get; set; }
        [DataMember(Name = "targetPath", Order = 7)] public string TargetPath { get; set; }
        [DataMember(Name = "fileCount", Order = 8)] public int FileCount { get; set; }
        [DataMember(Name = "totalBytes", Order = 9)] public long TotalBytes { get; set; }
        [DataMember(Name = "copiedFiles", Order = 10)] public int CopiedFiles { get; set; }
        [DataMember(Name = "failedFiles", Order = 11)] public int FailedFiles { get; set; }
        [DataMember(Name = "skippedFiles", Order = 12)] public int SkippedFiles { get; set; }
        [DataMember(Name = "durationSeconds", Order = 13)] public double DurationSeconds { get; set; }
        [DataMember(Name = "averageBytesPerSecond", Order = 14)] public double AverageBytesPerSecond { get; set; }
        [DataMember(Name = "verifyLevel", Order = 15)] public string VerifyLevel { get; set; }
        [DataMember(Name = "verifyStatus", Order = 16)] public string VerifyStatus { get; set; }
        [DataMember(Name = "message", Order = 17)] public string Message { get; set; }
        [DataMember(Name = "logFile", Order = 18)] public string LogFile { get; set; }

        /// <summary>状态枚举。</summary>
        public TaskStatus Status
        {
            get
            {
                TaskStatus parsed;
                return Enum.TryParse(StatusText, out parsed) ? parsed : TaskStatus.Failed;
            }
        }

        /// <summary>面向界面的结果文本。</summary>
        public string StatusDisplay
        {
            get
            {
                switch (Status)
                {
                    case TaskStatus.Succeeded: return "成功";
                    case TaskStatus.Partial: return "部分成功";
                    case TaskStatus.Cancelled: return "已取消";
                    case TaskStatus.Running: return "进行中";
                    default: return "失败";
                }
            }
        }
    }

    /// <summary>任务历史文档（单文件存储，便于备份与人工查看）。</summary>
    [DataContract(Name = "taskHistory")]
    public sealed class TaskHistoryDocument
    {
        public TaskHistoryDocument() { Tasks = new List<TaskRecord>(); }

        [DataMember(Name = "schemaVersion", Order = 0)] public int SchemaVersion { get; set; }
        [DataMember(Name = "tasks", Order = 1)] public List<TaskRecord> Tasks { get; set; }
    }

    /// <summary>
    /// 任务历史存储：记录每一次拷卡的结果，支持查询与"异常中断检测"。
    /// 说明：选择"整份文档重写"而不是追加 JSONL，是为了让历史文件始终是合法 JSON，
    /// 便于用户直接查看与备份；历史条数上限 2000，超出后丢弃最早的记录。
    /// </summary>
    public sealed class TaskHistoryStore
    {
        private const int MaxRecords = 2000;

        private readonly string _path;
        private readonly ILog _log;
        private readonly object _gate = new object();
        private TaskHistoryDocument _document;

        public TaskHistoryStore(string path, ILog log)
        {
            _path = path;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>历史文件路径。</summary>
        public string FilePath { get { return _path; } }

        /// <summary>读取全部记录（按开始时间倒序）。</summary>
        public IList<TaskRecord> LoadAll()
        {
            lock (_gate)
            {
                TaskHistoryDocument document = EnsureLoaded();
                var list = new List<TaskRecord>(document.Tasks);
                list.Sort(delegate (TaskRecord a, TaskRecord b)
                {
                    return string.CompareOrdinal(b.StartedAt, a.StartedAt);
                });
                return list;
            }
        }

        /// <summary>新增一条记录并保存。</summary>
        public OperationResult Add(TaskRecord record)
        {
            if (record == null) { return OperationResult.Fail("记录为空"); }
            lock (_gate)
            {
                TaskHistoryDocument document = EnsureLoaded();
                document.Tasks.Add(record);
                if (document.Tasks.Count > MaxRecords)
                {
                    document.Tasks.RemoveRange(0, document.Tasks.Count - MaxRecords);
                }
                return SaveDocument(document);
            }
        }

        /// <summary>更新已有记录（按 Id 匹配）；找不到则新增。</summary>
        public OperationResult Upsert(TaskRecord record)
        {
            if (record == null) { return OperationResult.Fail("记录为空"); }
            lock (_gate)
            {
                TaskHistoryDocument document = EnsureLoaded();
                int index = document.Tasks.FindIndex(delegate (TaskRecord item) { return item.Id == record.Id; });
                if (index >= 0) { document.Tasks[index] = record; }
                else { document.Tasks.Add(record); }
                return SaveDocument(document);
            }
        }

        /// <summary>
        /// 找出上次运行中被中断的任务（状态仍为"进行中"），并把它们标记为"失败（已中断）"。
        /// 启动时调用一次即可。
        /// </summary>
        public IList<TaskRecord> TakeInterruptedTasks()
        {
            lock (_gate)
            {
                TaskHistoryDocument document = EnsureLoaded();
                var interrupted = new List<TaskRecord>();
                bool changed = false;

                foreach (TaskRecord task in document.Tasks)
                {
                    if (task.Status != TaskStatus.Running) { continue; }
                    interrupted.Add(task);
                    task.StatusText = TaskStatus.Failed.ToString();
                    task.Message = "任务被中断（程序退出或断电），已拷贝的文件仍然保留，可重新开始并自动跳过已完成文件";
                    task.FinishedAt = TimeUtil.ToIso(DateTime.Now);
                    changed = true;
                }

                if (changed) { SaveDocument(document); }
                return interrupted;
            }
        }

        private TaskHistoryDocument EnsureLoaded()
        {
            if (_document != null) { return _document; }
            try
            {
                if (File.Exists(_path))
                {
                    _document = JsonStore.Deserialize<TaskHistoryDocument>(JsonStore.ReadAllText(_path));
                }
            }
            catch (Exception ex)
            {
                _log.Warn("读取任务历史失败，将重新开始记录", ex);
            }
            if (_document == null)
            {
                _document = new TaskHistoryDocument { SchemaVersion = 1 };
            }
            if (_document.Tasks == null) { _document.Tasks = new List<TaskRecord>(); }
            return _document;
        }

        private OperationResult SaveDocument(TaskHistoryDocument document)
        {
            try
            {
                document.SchemaVersion = 1;
                JsonStore.WriteAllTextAtomic(_path, JsonStore.Serialize(document));
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                _log.Warn("保存任务历史失败", ex);
                return OperationResult.Fail("保存任务历史失败：" + ex.Message, ex);
            }
        }
    }
}
