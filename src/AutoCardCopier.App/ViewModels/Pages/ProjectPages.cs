using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AutoCardCopier.Core.History;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Projects;

namespace AutoCardCopier.App.ViewModels.Pages
{
    /// <summary>项目列表中的一项。</summary>
    public sealed class ProjectItemViewModel : ViewModelBase
    {
        public ProjectItemViewModel(ProjectMeta meta) { Meta = meta; }

        /// <summary>项目元数据。</summary>
        public ProjectMeta Meta { get; private set; }

        public string Name { get { return Meta.Name; } }

        /// <summary>日期展示：跨天项目显示"起 ~ 止"，单日项目显示拍摄日期（F14-D）。</summary>
        public string DateText
        {
            get
            {
                if (Meta.IsMultiDay) { return Meta.StartDate + " ~ " + Meta.EndDate; }
                return Meta.ShootDate;
            }
        }

        /// <summary>项目跨度说明。</summary>
        public string SpanText { get { return Meta.IsMultiDay ? "跨天项目" : "单日项目"; } }

        /// <summary>导入布局说明。</summary>
        public string LayoutText
        {
            get
            {
                return string.Equals(Meta.ImportLayout, "byday", StringComparison.OrdinalIgnoreCase)
                    ? "一天一个文件夹" : "合并放一起";
            }
        }

        /// <summary>来源说明（新建 / 历史导入 / 追加）。</summary>
        public string SourceText
        {
            get
            {
                if (string.Equals(Meta.Source, "import", StringComparison.OrdinalIgnoreCase)) { return "历史导入"; }
                if (string.Equals(Meta.Source, "append", StringComparison.OrdinalIgnoreCase)) { return "追加"; }
                return "新建";
            }
        }
        public string LocationText { get { return string.IsNullOrEmpty(Meta.Location) ? "未标注" : Meta.Location; } }
        public string TypeText { get { return string.IsNullOrEmpty(Meta.ShootType) ? "其他" : Meta.ShootType; } }
        public string CountText { get { return Humanize.Count(Meta.TotalCount) + " 个文件"; } }
        public string SizeText { get { return Humanize.Bytes(Meta.TotalBytes); } }
        public string VerifyText { get { return string.IsNullOrEmpty(Meta.VerifyStatus) ? "未记录" : Meta.VerifyStatus; } }
        public string CameraText { get { return string.IsNullOrEmpty(Meta.CameraModel) ? "-" : Meta.CameraModel; } }
        public string PathText { get { return Meta.TargetPath; } }
        public bool VerifyPassed { get { return Meta.VerifyStatus == "校验通过"; } }
    }

    /// <summary>
    /// 项目库页视图模型：检索、统计、打开目录、编辑项目信息、从服务器重建。
    /// </summary>
    public sealed class ProjectsViewModel : PageViewModelBase
    {
        private RelayCommand _importLibraryCommand;

        /// <summary>
        /// 导入已有素材目录（F14-A）：选择目录 → 扫描识别 → 确认后入库。
        /// 懒初始化，避免改动既有构造函数。
        /// </summary>
        public RelayCommand ImportLibraryCommand
        {
            get { return _importLibraryCommand ?? (_importLibraryCommand = new RelayCommand(ImportLibrary)); }
        }

        private void ImportLibrary()
        {
            string root;
            try
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "选择要导入的素材库根目录（已存在的旧素材文件夹）";
                    dialog.ShowNewFolderButton = false;
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) { return; }
                    root = dialog.SelectedPath;
                }
            }
            catch (Exception ex)
            {
                StatusText = "选择目录失败：" + ex.Message;
                return;
            }

            StatusText = "正在扫描目录，请稍候…";
            try
            {
                System.Collections.Generic.IList<AutoCardCopier.Core.Projects.LibraryCandidate> found =
                    AutoCardCopier.Core.Projects.LibraryImporter.Scan(root, 3, Services.ProjectLibrary, Services.Logger, false);

                if (found.Count == 0)
                {
                    StatusText = "该目录下没有识别到素材项目。";
                    return;
                }

                int pending = 0;
                var summary = new System.Text.StringBuilder();
                summary.AppendLine("识别到 " + found.Count + " 个项目（已入库的会跳过）：");
                summary.AppendLine();
                foreach (AutoCardCopier.Core.Projects.LibraryCandidate item in found)
                {
                    if (item.IsImported)
                    {
                        summary.AppendLine("· " + item.Name + "　" + item.DisplayRange + "　" + item.TotalCount + " 个文件（已存在，跳过）");
                        continue;
                    }
                    pending++;
                    summary.AppendLine("· " + item.Name + "　" + item.DisplayRange + "　" + item.TotalCount + " 个文件　" + item.RelativePath);
                }
                summary.AppendLine();
                summary.AppendLine("将导入 " + pending + " 个项目。是否继续？");

                if (pending == 0)
                {
                    MessageBox.Show(summary.ToString(), "没有需要导入的项目", MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusText = "全部项目已存在，无需导入。";
                    return;
                }

                MessageBoxResult confirm = MessageBox.Show(summary.ToString(), "确认导入",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.OK) { StatusText = "已取消导入。"; return; }

                int imported = AutoCardCopier.Core.Projects.LibraryImporter.Import(found, Services.ProjectLibrary,
                    Services.Logger, "import");
                StatusText = string.Format("已导入 {0} 个项目（共识别 {1} 个）。刷新后可在列表中查看。", imported, found.Count);
            }
            catch (Exception ex)
            {
                // 失败原因直接显示在状态栏，不再写日志（Logger 的写入口由具体实现提供）
                StatusText = "导入失败：" + ex.Message;
            }
        }

        private string _keyword = string.Empty;
        private string _selectedType = "全部类型";
        private string _statusText = string.Empty;
        private string _summaryText = string.Empty;
        private ProjectItemViewModel _selectedProject;
        private string _editLocation = string.Empty;
        private string _editType = string.Empty;
        private string _editNote = string.Empty;

        public ProjectsViewModel(AppServices services)
            : base(services, "项目库", "按拍摄时间、地点、类型检索历史项目；每条记录都对应服务器上的一个素材目录。")
        {
            Projects = new ObservableCollection<ProjectItemViewModel>();
            TypeOptions = new ObservableCollection<string>();
            TypeOptions.Add("全部类型");
            foreach (string type in services.Presets.Load().ShootTypes) { TypeOptions.Add(type); }

            SearchCommand = new RelayCommand(Reload);
            ClearFilterCommand = new RelayCommand(ClearFilter);
            OpenFolderCommand = new RelayCommand(OpenFolder);
            CopyPathCommand = new RelayCommand(CopyPath);
            DeleteCommand = new RelayCommand(DeleteSelected);
            RebuildCommand = new RelayCommand(RebuildFromServer);
            SaveEditCommand = new RelayCommand(SaveEdit);

            Reload();
        }

        /// <summary>项目列表。</summary>
        public ObservableCollection<ProjectItemViewModel> Projects { get; private set; }

        /// <summary>拍摄类型筛选候选项。</summary>
        public ObservableCollection<string> TypeOptions { get; private set; }

        /// <summary>关键词。</summary>
        public string Keyword
        {
            get { return _keyword; }
            set { SetProperty(ref _keyword, value); }
        }

        /// <summary>当前选择的类型筛选。</summary>
        public string SelectedType
        {
            get { return _selectedType; }
            set { if (SetProperty(ref _selectedType, value)) { Reload(); } }
        }

        /// <summary>状态提示。</summary>
        public string StatusText
        {
            get { return _statusText; }
            private set { SetProperty(ref _statusText, value); }
        }

        /// <summary>统计摘要。</summary>
        public string SummaryText
        {
            get { return _summaryText; }
            private set { SetProperty(ref _summaryText, value); }
        }

        /// <summary>当前选中的项目。</summary>
        public ProjectItemViewModel SelectedProject
        {
            get { return _selectedProject; }
            set
            {
                if (SetProperty(ref _selectedProject, value))
                {
                    RaisePropertyChanged("HasSelection");
                    EditLocation = value == null ? string.Empty : value.Meta.Location;
                    EditType = value == null ? string.Empty : value.Meta.ShootType;
                    EditNote = value == null ? string.Empty : value.Meta.Note;
                }
            }
        }

        /// <summary>是否有选中项。</summary>
        public bool HasSelection { get { return _selectedProject != null; } }

        public string EditLocation { get { return _editLocation; } set { SetProperty(ref _editLocation, value); } }
        public string EditType { get { return _editType; } set { SetProperty(ref _editType, value); } }
        public string EditNote { get { return _editNote; } set { SetProperty(ref _editNote, value); } }

        public RelayCommand SearchCommand { get; private set; }
        public RelayCommand ClearFilterCommand { get; private set; }
        public RelayCommand OpenFolderCommand { get; private set; }
        public RelayCommand CopyPathCommand { get; private set; }
        public RelayCommand DeleteCommand { get; private set; }
        public RelayCommand RebuildCommand { get; private set; }
        public RelayCommand SaveEditCommand { get; private set; }

        /// <summary>按当前条件重新加载列表。</summary>
        public void Reload()
        {
            try
            {
                var filter = new ProjectFilter { Keyword = Keyword };
                if (!string.IsNullOrEmpty(SelectedType) && SelectedType != "全部类型")
                {
                    filter.ShootType = SelectedType;
                }

                System.Collections.Generic.IList<ProjectMeta> result = Services.ProjectLibrary.Search(filter);

                Projects.Clear();
                long totalBytes = 0;
                int totalFiles = 0;
                foreach (ProjectMeta meta in result)
                {
                    Projects.Add(new ProjectItemViewModel(meta));
                    totalBytes += meta.TotalBytes;
                    totalFiles += meta.TotalCount;
                }

                SummaryText = string.Format("共 {0} 个项目，{1} 个文件，合计 {2}",
                    Humanize.Count(Projects.Count), Humanize.Count(totalFiles), Humanize.Bytes(totalBytes));
                StatusText = Projects.Count == 0
                    ? "项目库为空。完成一次拷贝后会自动生成项目记录；也可以点击“从服务器重建”。"
                    : "选择项目可查看详情、打开目录或修改拍摄信息。";
                SelectedProject = Projects.Count > 0 ? Projects[0] : null;
            }
            catch (Exception ex)
            {
                StatusText = "读取项目库失败：" + ex.Message;
            }
        }

        private void ClearFilter()
        {
            Keyword = string.Empty;
            _selectedType = "全部类型";
            RaisePropertyChanged("SelectedType");
            Reload();
        }

        private void OpenFolder()
        {
            if (SelectedProject == null) { return; }
            OpenInExplorer(SelectedProject.Meta.TargetPath);
        }

        private void CopyPath()
        {
            if (SelectedProject == null) { return; }
            try
            {
                Clipboard.SetText(SelectedProject.Meta.TargetPath ?? string.Empty);
                StatusText = "已复制路径到剪贴板。";
            }
            catch (Exception ex)
            {
                StatusText = "复制失败：" + ex.Message;
            }
        }

        private void DeleteSelected()
        {
            if (SelectedProject == null) { return; }
            MessageBoxResult answer = MessageBox.Show(
                "确定要从项目库中删除这条记录吗？\n\n注意：只会删除本地的项目记录，服务器上的素材文件不会被删除。",
                "删除项目记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) { return; }

            OperationResult result = Services.ProjectLibrary.Delete(SelectedProject.Meta.Id);
            StatusText = result.Message;
            Reload();
        }

        private void RebuildFromServer()
        {
            string root = Services.Settings.PrimaryTargetRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                StatusText = "请先在设置页填写素材目标根目录。";
                return;
            }

            OperationResult<int> result = Services.ProjectLibrary.RebuildFromServer(root);
            StatusText = result.Message;
            Reload();
        }

        private void SaveEdit()
        {
            if (SelectedProject == null) { return; }
            ProjectMeta meta = SelectedProject.Meta;
            meta.Location = (EditLocation ?? string.Empty).Trim();
            meta.ShootType = (EditType ?? string.Empty).Trim();
            meta.Note = EditNote ?? string.Empty;

            OperationResult result = Services.ProjectLibrary.Update(meta);
            StatusText = result.Success ? "项目信息已更新，并同步写入项目目录。" : result.Message;
            Reload();
        }

        private void OpenInExplorer(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    StatusText = "目录不存在：" + path;
                    return;
                }
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                StatusText = "打开目录失败：" + ex.Message;
            }
        }
    }

    /// <summary>历史列表中的一项。</summary>
    public sealed class TaskItemViewModel
    {
        public TaskItemViewModel(TaskRecord record) { Record = record; }

        /// <summary>任务记录。</summary>
        public TaskRecord Record { get; private set; }

        public string TimeText { get { return TimeUtil.ParseIso(Record.StartedAt, DateTime.Now).ToString("yyyy-MM-dd HH:mm"); } }
        public string ProjectName { get { return string.IsNullOrEmpty(Record.ProjectName) ? "(未命名)" : Record.ProjectName; } }
        public string StatusDisplay { get { return Record.StatusDisplay; } }
        public string ProgressText
        {
            get
            {
                return string.Format("{0} / {1} 个文件 · {2} · 失败 {3}",
                    Humanize.Count(Record.CopiedFiles), Humanize.Count(Record.FileCount),
                    Humanize.Bytes(Record.TotalBytes), Humanize.Count(Record.FailedFiles));
            }
        }
        public string SpeedText
        {
            get
            {
                if (Record.DurationSeconds <= 0) { return "-"; }
                return Humanize.Duration(TimeSpan.FromSeconds(Record.DurationSeconds)) + " · " +
                       Humanize.Speed(Record.AverageBytesPerSecond);
            }
        }
        public string VerifyText { get { return Record.VerifyStatus ?? "-"; } }
        public string MessageText { get { return Record.Message; } }
        public string TargetPath { get { return Record.TargetPath; } }
        public bool IsFailed { get { return Record.Status == TaskStatus.Failed || Record.Status == TaskStatus.Partial; } }
    }

    /// <summary>历史页视图模型。</summary>
    public sealed class HistoryViewModel : PageViewModelBase
    {
        private string _statusText = string.Empty;
        private string _summaryText = string.Empty;
        private TaskItemViewModel _selectedTask;

        public HistoryViewModel(AppServices services)
            : base(services, "历史", "每一次拷卡的结果、耗时、速度与校验状态；失败任务可打开目录或日志排查。")
        {
            Tasks = new ObservableCollection<TaskItemViewModel>();
            RefreshCommand = new RelayCommand(Reload);
            OpenFolderCommand = new RelayCommand(OpenFolder);
            OpenLogCommand = new RelayCommand(OpenLog);
            Reload();
        }

        /// <summary>任务列表（最新在前）。</summary>
        public ObservableCollection<TaskItemViewModel> Tasks { get; private set; }

        /// <summary>选中的任务。</summary>
        public TaskItemViewModel SelectedTask
        {
            get { return _selectedTask; }
            set { if (SetProperty(ref _selectedTask, value)) { RaisePropertyChanged("HasSelection"); } }
        }

        /// <summary>是否有选中项。</summary>
        public bool HasSelection { get { return _selectedTask != null; } }

        /// <summary>状态提示。</summary>
        public string StatusText
        {
            get { return _statusText; }
            private set { SetProperty(ref _statusText, value); }
        }

        /// <summary>统计摘要。</summary>
        public string SummaryText
        {
            get { return _summaryText; }
            private set { SetProperty(ref _summaryText, value); }
        }

        public RelayCommand RefreshCommand { get; private set; }
        public RelayCommand OpenFolderCommand { get; private set; }
        public RelayCommand OpenLogCommand { get; private set; }

        /// <summary>重新加载历史。</summary>
        public void Reload()
        {
            try
            {
                System.Collections.Generic.IList<TaskRecord> records = Services.TaskHistory.LoadAll();
                Tasks.Clear();
                int failed = 0;
                long totalBytes = 0;
                foreach (TaskRecord record in records)
                {
                    Tasks.Add(new TaskItemViewModel(record));
                    if (record.Status == TaskStatus.Failed || record.Status == TaskStatus.Partial) { failed++; }
                    totalBytes += record.TotalBytes;
                }

                SummaryText = string.Format("共 {0} 次任务，其中异常 {1} 次，累计 {2}",
                    Humanize.Count(Tasks.Count), Humanize.Count(failed), Humanize.Bytes(totalBytes));
                StatusText = Tasks.Count == 0 ? "还没有任务记录。" : "选择一条任务可打开目标目录或日志。";
                SelectedTask = Tasks.Count > 0 ? Tasks[0] : null;
            }
            catch (Exception ex)
            {
                StatusText = "读取任务历史失败：" + ex.Message;
            }
        }

        private void OpenFolder()
        {
            if (SelectedTask == null) { return; }
            string path = SelectedTask.TargetPath;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    StatusText = "目录不存在：" + path;
                    return;
                }
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                StatusText = "打开目录失败：" + ex.Message;
            }
        }

        private void OpenLog()
        {
            try
            {
                string directory = AppPaths.TaskLogDirectory(Services.DataDirectory);
                Directory.CreateDirectory(directory);
                Process.Start("explorer.exe", "\"" + directory + "\"");
            }
            catch (Exception ex)
            {
                StatusText = "打开日志目录失败：" + ex.Message;
            }
        }
    }
}
