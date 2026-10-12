using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Scanning;

namespace AutoCardCopier.Core.History
{
    /// <summary>计划中单个文件的状态。</summary>
    public enum TaskItemState
    {
        /// <summary>尚未处理。</summary>
        Pending = 0,
        /// <summary>已成功写入。</summary>
        Done = 1,
        /// <summary>写入失败。</summary>
        Failed = 2
    }

    /// <summary>计划中的单个文件。</summary>
    [DataContract(Name = "item")]
    public sealed class TaskPlanItem
    {
        [DataMember(Name = "source", Order = 0)] public string SourcePath { get; set; }
        [DataMember(Name = "target", Order = 1)] public string TargetPath { get; set; }
        [DataMember(Name = "length", Order = 2)] public long Length { get; set; }
        [DataMember(Name = "lastWriteUtc", Order = 3)] public string LastWriteUtc { get; set; }
        [DataMember(Name = "kind", Order = 4)] public string Kind { get; set; }
        [DataMember(Name = "state", Order = 5)] public string StateText { get; set; }
        [DataMember(Name = "error", Order = 6)] public string Error { get; set; }

        /// <summary>状态枚举。</summary>
        public TaskItemState State
        {
            get
            {
                TaskItemState parsed;
                return Enum.TryParse(StateText, out parsed) ? parsed : TaskItemState.Pending;
            }
            set { StateText = value.ToString(); }
        }
    }

    /// <summary>一个目标的计划内容。</summary>
    [DataContract(Name = "target")]
    public sealed class TaskPlanTarget
    {
        public TaskPlanTarget() { Items = new List<TaskPlanItem>(); }

        [DataMember(Name = "root", Order = 0)] public string Root { get; set; }
        [DataMember(Name = "isPrimary", Order = 1)] public bool IsPrimary { get; set; }
        [DataMember(Name = "projectDirectory", Order = 2)] public string ProjectDirectory { get; set; }
        [DataMember(Name = "projectRelativePath", Order = 3)] public string ProjectRelativePath { get; set; }
        [DataMember(Name = "projectName", Order = 4)] public string ProjectName { get; set; }
        [DataMember(Name = "items", Order = 5)] public List<TaskPlanItem> Items { get; set; }

        /// <summary>已完成数量。</summary>
        public int DoneCount
        {
            get
            {
                int count = 0;
                foreach (TaskPlanItem item in Items) { if (item.State == TaskItemState.Done) { count++; } }
                return count;
            }
        }

        /// <summary>待处理数量。</summary>
        public int PendingCount
        {
            get
            {
                int count = 0;
                foreach (TaskPlanItem item in Items) { if (item.State != TaskItemState.Done) { count++; } }
                return count;
            }
        }
    }

    /// <summary>
    /// 任务计划文件：用于精确断点续传。
    /// 与 v0.1.0 的"跳过已存在且大小一致的文件"相比，这里额外记录了
    /// 每个文件的目标路径与完成状态，因此续传时不必重新扫描卡片，也不会重复校验已完成文件。
    /// </summary>
    [DataContract(Name = "taskPlan")]
    public sealed class TaskPlanFile
    {
        public TaskPlanFile() { Targets = new List<TaskPlanTarget>(); }

        [DataMember(Name = "schemaVersion", Order = 0)] public int SchemaVersion { get; set; }
        [DataMember(Name = "taskId", Order = 1)] public string TaskId { get; set; }
        [DataMember(Name = "projectId", Order = 2)] public string ProjectId { get; set; }
        [DataMember(Name = "projectName", Order = 3)] public string ProjectName { get; set; }
        [DataMember(Name = "sourceRoot", Order = 4)] public string SourceRoot { get; set; }
        [DataMember(Name = "location", Order = 5)] public string Location { get; set; }
        [DataMember(Name = "shootType", Order = 6)] public string ShootType { get; set; }
        [DataMember(Name = "shootDate", Order = 7)] public string ShootDate { get; set; }
        [DataMember(Name = "photographer", Order = 8)] public string Photographer { get; set; }
        [DataMember(Name = "cameraModel", Order = 9)] public string CameraModel { get; set; }
        [DataMember(Name = "cardFingerprint", Order = 10)] public string CardFingerprint { get; set; }
        [DataMember(Name = "cardLabel", Order = 11)] public string CardLabel { get; set; }
        [DataMember(Name = "createdAt", Order = 12)] public string CreatedAt { get; set; }
        [DataMember(Name = "updatedAt", Order = 13)] public string UpdatedAt { get; set; }
        [DataMember(Name = "targets", Order = 14)] public List<TaskPlanTarget> Targets { get; set; }

        /// <summary>全部目标的待处理文件总数。</summary>
        public int TotalPendingCount
        {
            get
            {
                int count = 0;
                foreach (TaskPlanTarget target in Targets) { count += target.PendingCount; }
                return count;
            }
        }

        /// <summary>全部目标的文件总数。</summary>
        public int TotalItemCount
        {
            get
            {
                int count = 0;
                foreach (TaskPlanTarget target in Targets) { count += target.Items.Count; }
                return count;
            }
        }

        /// <summary>源卡片当前是否仍然可用。</summary>
        public bool IsSourceAvailable()
        {
            return !string.IsNullOrEmpty(SourceRoot) && Directory.Exists(SourceRoot);
        }
    }

    /// <summary>计划文件的读写。</summary>
    public sealed class TaskPlanStore
    {
        private readonly string _directory;
        private readonly ILog _log;

        public TaskPlanStore(string directory, ILog log)
        {
            _directory = directory;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>计划文件目录。</summary>
        public string DirectoryPath { get { return _directory; } }

        /// <summary>根据拷贝计划生成计划文件。</summary>
        public static TaskPlanFile FromPlans(string taskId, string projectId, string sourceRoot,
            ProjectContextLike context, IList<CopyPlan> plans)
        {
            var file = new TaskPlanFile
            {
                SchemaVersion = 1,
                TaskId = taskId,
                ProjectId = projectId,
                SourceRoot = sourceRoot,
                CreatedAt = TimeUtil.ToIso(DateTime.Now),
                UpdatedAt = TimeUtil.ToIso(DateTime.Now)
            };

            if (context != null)
            {
                file.ProjectName = context.ProjectName;
                file.Location = context.Location;
                file.ShootType = context.ShootType;
                file.ShootDate = context.ShootDate;
                file.Photographer = context.Photographer;
                file.CameraModel = context.CameraModel;
                file.CardFingerprint = context.CardFingerprint;
                file.CardLabel = context.CardLabel;
            }

            for (int i = 0; i < plans.Count; i++)
            {
                CopyPlan plan = plans[i];
                var target = new TaskPlanTarget
                {
                    Root = plan.TargetRoot,
                    IsPrimary = i == 0,
                    ProjectDirectory = plan.ProjectDirectory,
                    ProjectRelativePath = plan.ProjectRelativePath,
                    ProjectName = plan.ProjectName
                };

                foreach (CopyItem item in plan.Items)
                {
                    target.Items.Add(new TaskPlanItem
                    {
                        SourcePath = item.Source.FullPath,
                        TargetPath = item.TargetPath,
                        Length = item.Length,
                        LastWriteUtc = TimeUtil.ToIsoUtcPrecise(item.Source.LastWriteTimeUtc),
                        Kind = item.Kind.ToString(),
                        State = item.Action == CopyAction.Skip ? TaskItemState.Done : TaskItemState.Pending
                    });
                }
                file.Targets.Add(target);
            }

            if (string.IsNullOrEmpty(file.ProjectName) && plans.Count > 0) { file.ProjectName = plans[0].ProjectName; }
            return file;
        }

        /// <summary>把一次拷贝结果回写到计划文件（标记已完成与失败）。</summary>
        public static void ApplyResult(TaskPlanFile file, string targetRoot, CopyResult copyResult)
        {
            if (file == null || copyResult == null) { return; }

            var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (copyResult.CompletedTargetPaths != null)
            {
                foreach (string path in copyResult.CompletedTargetPaths) { completed.Add(path); }
            }
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (copyResult.FailedTargetPaths != null)
            {
                foreach (string path in copyResult.FailedTargetPaths) { failed.Add(path); }
            }

            foreach (TaskPlanTarget target in file.Targets)
            {
                if (!string.IsNullOrEmpty(targetRoot)
                    && !string.Equals(target.Root, targetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (TaskPlanItem item in target.Items)
                {
                    if (completed.Contains(item.TargetPath)) { item.State = TaskItemState.Done; item.Error = null; }
                    else if (failed.Contains(item.TargetPath)) { item.State = TaskItemState.Failed; }
                }
            }
            file.UpdatedAt = TimeUtil.ToIso(DateTime.Now);
        }

        /// <summary>
        /// 把计划文件还原成可继续执行的拷贝计划：
        /// 已完成（Done）的文件直接标记为跳过，Pending/Failed 的继续拷贝。
        /// </summary>
        public static IList<CopyPlan> ToPlans(TaskPlanFile file, ILog log)
        {
            var plans = new List<CopyPlan>();
            if (file == null) { return plans; }

            foreach (TaskPlanTarget target in file.Targets)
            {
                var plan = new CopyPlan
                {
                    SourceRoot = file.SourceRoot,
                    TargetRoot = target.Root,
                    ProjectDirectory = target.ProjectDirectory,
                    ProjectRelativePath = target.ProjectRelativePath,
                    ProjectName = target.ProjectName
                };

                foreach (TaskPlanItem item in target.Items)
                {
                    MediaKind kind;
                    if (!Enum.TryParse(item.Kind, out kind)) { kind = MediaKind.Other; }

                    var source = new ScannedFile
                    {
                        FullPath = item.SourcePath,
                        RelativePath = Path.GetFileName(item.SourcePath),
                        Length = item.Length,
                        LastWriteTimeUtc = TimeUtil.ParseIsoUtc(item.LastWriteUtc, DateTime.UtcNow),
                        Kind = kind
                    };

                    var planItem = new CopyItem
                    {
                        Source = source,
                        TargetPath = item.TargetPath,
                        Action = CopyAction.Copy
                    };

                    if (item.State == TaskItemState.Done)
                    {
                        planItem.Action = CopyAction.Skip;
                        planItem.SkipReason = "断点续传：该文件已在上一轮完成";
                    }
                    plan.Items.Add(planItem);
                }

                plan.Warnings.Add("本计划由断点续传文件还原（任务 " + file.TaskId + "）");
                plans.Add(plan);
            }
            return plans;
        }

        /// <summary>保存计划文件。</summary>
        public OperationResult Save(TaskPlanFile file)
        {
            if (file == null) { return OperationResult.Fail("计划为空"); }
            try
            {
                file.UpdatedAt = TimeUtil.ToIso(DateTime.Now);
                JsonStore.WriteAllTextAtomic(FilePath(file.TaskId), JsonStore.Serialize(file));
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                _log.Warn("保存任务计划失败：" + file.TaskId, ex);
                return OperationResult.Fail("保存任务计划失败：" + ex.Message, ex);
            }
        }

        /// <summary>读取计划文件。</summary>
        public TaskPlanFile Load(string taskId)
        {
            try
            {
                string path = FilePath(taskId);
                if (!File.Exists(PathUtil.Extend(path))) { return null; }
                return JsonStore.Deserialize<TaskPlanFile>(JsonStore.ReadAllText(path));
            }
            catch (Exception ex)
            {
                _log.Warn("读取任务计划失败：" + taskId, ex);
                return null;
            }
        }

        /// <summary>删除计划文件（任务完成后调用，避免下次误提示续传）。</summary>
        public void Delete(string taskId)
        {
            try
            {
                string path = FilePath(taskId);
                if (File.Exists(PathUtil.Extend(path))) { File.Delete(PathUtil.Extend(path)); }
            }
            catch (Exception ex)
            {
                _log.Warn("删除任务计划失败：" + taskId, ex);
            }
        }

        private string FilePath(string taskId)
        {
            return Path.Combine(_directory, PathUtil.SanitizeName(taskId, 64) + ".plan.json");
        }
    }

    /// <summary>
    /// 生成计划文件所需的上下文快照。
    /// 单独定义是为了让 Core.History 不依赖 UI 层，也不与 Projects.ProjectContext 耦合过多。
    /// </summary>
    public sealed class ProjectContextLike
    {
        public string ProjectName { get; set; }
        public string ShootDate { get; set; }
        public string Location { get; set; }
        public string ShootType { get; set; }
        public string Photographer { get; set; }
        public string CameraModel { get; set; }
        public string CardFingerprint { get; set; }
        public string CardLabel { get; set; }
    }
}
