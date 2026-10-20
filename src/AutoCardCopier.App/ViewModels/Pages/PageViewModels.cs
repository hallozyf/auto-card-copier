using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Formatting;
using AutoCardCopier.Core.History;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;
// 与 System.Threading.Tasks.TaskStatus 同名，这里显式指向领域枚举
using TaskStatus = AutoCardCopier.Core.History.TaskStatus;

namespace AutoCardCopier.App.ViewModels.Pages
{
    /// <summary>页面视图模型基类：统一提供标题、说明与服务容器。</summary>
    public abstract class PageViewModelBase : ViewModelBase
    {
        protected PageViewModelBase(AppServices services, string title, string description)
        {
            Services = services;
            Title = title;
            Description = description;
        }

        /// <summary>服务容器。</summary>
        protected AppServices Services { get; private set; }

        /// <summary>页面标题。</summary>
        public string Title { get; private set; }

        /// <summary>页面说明。</summary>
        public string Description { get; private set; }
    }

    /// <summary>
    /// 一个拍摄日期分组的界面项（F13-C）：可编辑项目名、地点、类型，并实时预览目标目录。
    /// </summary>
    public sealed class MediaGroupItemViewModel : ViewModelBase
    {
        private readonly string _targetRoot;
        private readonly string _parentRelative;
        private string _projectName;
        private string _location;
        private string _shootType;

        public MediaGroupItemViewModel(MediaDayGroup group, ProjectContext context, string defaultName,
            string targetRoot, string parentRelative)
        {
            Group = group;
            Context = context;
            _targetRoot = targetRoot;
            _parentRelative = parentRelative;
            _location = context.Location;
            _shootType = context.ShootType;
            _projectName = defaultName;
        }

        /// <summary>对应的扫描分组。</summary>
        public MediaDayGroup Group { get; private set; }

        /// <summary>项目上下文（拷贝时使用）。</summary>
        public ProjectContext Context { get; private set; }

        /// <summary>本项目包含的文件。</summary>
        public IList<ScannedFile> Files { get { return Group.Files; } }

        public string DateText { get { return Group.DisplayDate; } }
        public string RangeText { get { return Group.DisplayRange; } }
        public string SizeText { get { return Humanize.Bytes(Group.TotalBytes); } }
        public string CountText
        {
            get
            {
                return string.Format("照片 {0} · 视频 {1} · 其他 {2}",
                    Humanize.Count(Group.PhotoCount), Humanize.Count(Group.VideoCount), Humanize.Count(Group.OtherCount));
            }
        }

        /// <summary>项目名（可编辑）；同时用于生成目录名。</summary>
        public string ProjectName
        {
            get { return _projectName; }
            set
            {
                if (SetProperty(ref _projectName, value))
                {
                    RaisePropertyChanged("TargetPreviewText");
                    RaisePropertyChanged("RelativeDirectory");
                }
            }
        }

        /// <summary>本项目的相对目录（父级 + 项目名）。</summary>
        public string RelativeDirectory
        {
            get
            {
                string name = PathUtil.SanitizeName(_projectName, 100);
                if (string.IsNullOrEmpty(_parentRelative)) { return name; }
                return System.IO.Path.Combine(_parentRelative, name);
            }
        }

        /// <summary>拍摄地点（可编辑）。</summary>
        public string Location
        {
            get { return _location; }
            set { if (SetProperty(ref _location, value)) { Context.Location = value; } }
        }

        /// <summary>拍摄类型（可编辑）。</summary>
        public string ShootType
        {
            get { return _shootType; }
            set { if (SetProperty(ref _shootType, value)) { Context.ShootType = value; } }
        }

        /// <summary>目标目录预览。</summary>
        public string TargetPreviewText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_targetRoot)) { return "尚未设置素材目标目录"; }
                return System.IO.Path.Combine(_targetRoot, RelativeDirectory);
            }
        }

        /// <summary>把统一填写的地点/类型写入本分组。</summary>
        public void ApplyBulk(string location, string shootType)
        {
            if (!string.IsNullOrEmpty(location)) { Location = location; }
            if (!string.IsNullOrEmpty(shootType)) { ShootType = shootType; }
        }
    }

    /// <summary>卡片列表中的一项。</summary>
    public sealed class CardItemViewModel : ViewModelBase
    {
        public CardItemViewModel(CardVolume card) { Card = card; }

        /// <summary>卡片数据。</summary>
        public CardVolume Card { get; private set; }

        public string DisplayName { get { return Card.DisplayName; } }
        public string DriveLetter { get { return Card.DriveLetter; } }
        public string FileSystem { get { return string.IsNullOrEmpty(Card.FileSystem) ? "未知文件系统" : Card.FileSystem; } }
        public string CapacityText
        {
            get { return Humanize.Bytes(Card.TotalBytes) + "（可用 " + Humanize.Bytes(Card.FreeBytes) + "）"; }
        }
        public string MediaFoldersText
        {
            get
            {
                if (Card.MediaFolderNames == null || Card.MediaFolderNames.Count == 0) { return "未发现标准素材目录"; }
                return "素材目录：" + string.Join("、", Card.MediaFolderNames);
            }
        }
        public string DetectionReason { get { return Card.DetectionReason; } }
    }

    /// <summary>
    /// 卡片页视图模型：卡片识别、素材扫描、拍摄信息填写、拷贝与校验的完整流程。
    /// </summary>
    public sealed class CardsViewModel : PageViewModelBase
    {
        private readonly ILog _log;
        private readonly PauseToken _pauseToken = new PauseToken();
        private CancellationTokenSource _cancellation;
        private CardItemViewModel _selectedCard;
        private string _statusText = "正在检测存储卡…";
        private bool _isBusy;
        private string _progressText = string.Empty;
        private ScanResult _scanResult;

        private string _location = string.Empty;
        private string _shootType;
        private string _photographer = string.Empty;
        private string _note = string.Empty;

        private double _copyPercent;
        private string _copySpeedText = string.Empty;
        private string _copyEtaText = string.Empty;
        private string _copyCurrentFile = string.Empty;
        private bool _isCopying;
        private bool _isPaused;
        private string _resultSummary = string.Empty;
        private bool _hasResult;

        public CardsViewModel(AppServices services)
            : base(services, "卡片", "插入内存卡后自动识别并扫描素材，填写拍摄信息后即可开始拷贝。")
        {
            _log = services.Logger;
            Cards = new ObservableCollection<CardItemViewModel>();
            Failures = new ObservableCollection<string>();
            ShootTypes = new ObservableCollection<string>(services.Presets.Load().ShootTypes);
            Locations = new ObservableCollection<string>(services.Presets.Load().Locations);
            _shootType = services.Presets.Load().DefaultShootType;

            Groups = new ObservableCollection<MediaGroupItemViewModel>();
            ApplyBulkCommand = new RelayCommand(ApplyBulkToAllGroups);
            AppendCandidates = new ObservableCollection<ProjectMeta>();
            StartAppendCommand = new AsyncRelayCommand(StartAppendAsync, CanStartAppend);
            RefreshCommand = new RelayCommand(RefreshCards);
            ScanCommand = new AsyncRelayCommand(ScanSelectedCardAsync, delegate { return !IsBusy && SelectedCard != null; });
            StartCopyCommand = new AsyncRelayCommand(StartCopyAsync, delegate { return !IsBusy && CanStartCopy; });
            ResumeCommand = new AsyncRelayCommand(ResumeAsync, delegate { return !IsBusy && !IsCopying && CanResume; });
            CancelCopyCommand = new RelayCommand(CancelCopy, delegate { return IsCopying; });
            PauseResumeCommand = new RelayCommand(TogglePause, delegate { return IsCopying; });
            FormatCardCommand = new RelayCommand(FormatCard, delegate { return CanFormatCard; });
            EraseCardCommand = new RelayCommand(EraseCard, delegate { return CanFormatCard; });

            services.CardDetector.CardsChanged += OnCardsChanged;
            services.CardDetector.Start();
            RefreshCards();
            DetectInterruptedTasks();
        }

        // ---------- 卡片列表 ----------

        /// <summary>卡片列表。</summary>
        public ObservableCollection<CardItemViewModel> Cards { get; private set; }

        /// <summary>当前选中的卡片。</summary>
        public CardItemViewModel SelectedCard
        {
            get { return _selectedCard; }
            set
            {
                if (SetProperty(ref _selectedCard, value))
                {
                    RaisePropertyChanged("HasSelectedCard");
                    RaisePropertyChanged("CardInfoText");
                    ScanCommand.RaiseCanExecuteChanged();
                    StartCopyCommand.RaiseCanExecuteChanged();
                    RefreshFormatState();
                }
            }
        }

        /// <summary>是否已选中卡片。</summary>
        public bool HasSelectedCard { get { return _selectedCard != null; } }

        /// <summary>卡片附加说明。</summary>
        public string CardInfoText
        {
            get
            {
                if (SelectedCard == null) { return "尚未选择卡片"; }
                return SelectedCard.DetectionReason + " · " + SelectedCard.MediaFoldersText;
            }
        }

        // ---------- 拍摄信息 ----------

        /// <summary>拍摄地点。</summary>
        public string Location
        {
            get { return _location; }
            set { if (SetProperty(ref _location, value)) { RaisePropertyChanged("TargetPreviewText"); StartCopyCommand.RaiseCanExecuteChanged(); } }
        }

        /// <summary>拍摄类型。</summary>
        public string ShootType
        {
            get { return _shootType; }
            set { if (SetProperty(ref _shootType, value)) { RaisePropertyChanged("TargetPreviewText"); StartCopyCommand.RaiseCanExecuteChanged(); } }
        }

        /// <summary>摄影师。</summary>
        public string Photographer
        {
            get { return _photographer; }
            set { SetProperty(ref _photographer, value); }
        }

        /// <summary>备注。</summary>
        public string Note
        {
            get { return _note; }
            set { SetProperty(ref _note, value); }
        }

        /// <summary>拍摄类型候选。</summary>
        public ObservableCollection<string> ShootTypes { get; private set; }

        /// <summary>历史地点候选。</summary>
        public ObservableCollection<string> Locations { get; private set; }

        /// <summary>目标目录预览。这是自动化建目录的"所见即所得"提示。</summary>
        public string TargetPreviewText
        {
            get
            {
                string root = Services.Settings.PrimaryTargetRoot;
                if (string.IsNullOrWhiteSpace(root)) { return "尚未设置素材目标目录，请先到设置页填写。"; }

                var context = new ProjectContext
                {
                    ShootDate = _scanResult != null && _scanResult.EarliestUtc.HasValue
                        ? _scanResult.EarliestUtc.Value.ToLocalTime()
                        : DateTime.Now,
                    ShootType = ShootType,
                    Location = Location
                };
                string relative = NameTemplateEngine.ToWindowsRelativePath(
                    NameTemplateEngine.Expand(Services.Settings.NameTemplate, context));
                return System.IO.Path.Combine(root, relative);
            }
        }

        // ---------- 状态与进度 ----------

        /// <summary>是否忙碌（扫描或拷贝中）。</summary>
        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaisePropertyChanged("IsIdle");
                    ScanCommand.RaiseCanExecuteChanged();
                    StartCopyCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>是否空闲。</summary>
        public bool IsIdle { get { return !_isBusy; } }

        /// <summary>状态提示。</summary>
        public string StatusText
        {
            get { return _statusText; }
            private set { SetProperty(ref _statusText, value); }
        }

        /// <summary>进度文本。</summary>
        public string ProgressText
        {
            get { return _progressText; }
            private set { SetProperty(ref _progressText, value); }
        }

        // ---------- 拷贝进度 ----------

        /// <summary>拷贝进度百分比（0 至 100）。</summary>
        public double CopyPercent
        {
            get { return _copyPercent; }
            private set { SetProperty(ref _copyPercent, value); }
        }

        /// <summary>实时速度文本。</summary>
        public string CopySpeedText
        {
            get { return _copySpeedText; }
            private set { SetProperty(ref _copySpeedText, value); }
        }

        /// <summary>剩余时间文本。</summary>
        public string CopyEtaText
        {
            get { return _copyEtaText; }
            private set { SetProperty(ref _copyEtaText, value); }
        }

        /// <summary>当前文件。</summary>
        public string CopyCurrentFile
        {
            get { return _copyCurrentFile; }
            private set { SetProperty(ref _copyCurrentFile, value); }
        }

        /// <summary>是否正在拷贝。</summary>
        public bool IsCopying
        {
            get { return _isCopying; }
            private set
            {
                if (SetProperty(ref _isCopying, value))
                {
                    RaisePropertyChanged("IsNotCopying");
                    CancelCopyCommand.RaiseCanExecuteChanged();
                    PauseResumeCommand.RaiseCanExecuteChanged();
                    StartCopyCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>是否未在拷贝。</summary>
        public bool IsNotCopying { get { return !_isCopying; } }

        /// <summary>是否暂停中。</summary>
        public bool IsPaused
        {
            get { return _isPaused; }
            private set
            {
                if (SetProperty(ref _isPaused, value)) { RaisePropertyChanged("PauseButtonText"); }
            }
        }

        /// <summary>暂停按钮文本。</summary>
        public string PauseButtonText { get { return _isPaused ? "继续" : "暂停"; } }

        /// <summary>结果摘要。</summary>
        public string ResultSummary
        {
            get { return _resultSummary; }
            private set { SetProperty(ref _resultSummary, value); }
        }

        /// <summary>是否有结果。</summary>
        public bool HasResult
        {
            get { return _hasResult; }
            private set { SetProperty(ref _hasResult, value); }
        }

        /// <summary>失败清单（拷贝或校验失败的文件）。</summary>
        public ObservableCollection<string> Failures { get; private set; }

        // ---------- 扫描结果展示 ----------

        public bool HasScanResult { get { return _scanResult != null; } }

        public string PhotoText
        {
            get { return _scanResult == null ? "-" : Humanize.Count(_scanResult.PhotoCount) + " 个 / " + Humanize.Bytes(_scanResult.PhotoBytes); }
        }

        public string VideoText
        {
            get { return _scanResult == null ? "-" : Humanize.Count(_scanResult.VideoCount) + " 个 / " + Humanize.Bytes(_scanResult.VideoBytes); }
        }

        public string OtherText
        {
            get { return _scanResult == null ? "-" : Humanize.Count(_scanResult.OtherCount) + " 个 / " + Humanize.Bytes(_scanResult.OtherBytes); }
        }

        public string TotalText
        {
            get { return _scanResult == null ? "-" : Humanize.Count(_scanResult.TotalCount) + " 个 / " + Humanize.Bytes(_scanResult.TotalBytes); }
        }

        public string TimeRangeText
        {
            get
            {
                if (_scanResult == null || !_scanResult.EarliestUtc.HasValue || !_scanResult.LatestUtc.HasValue) { return "-"; }
                return TimeUtil.Display(_scanResult.EarliestUtc.Value.ToLocalTime()) + " ~ " +
                       TimeUtil.Display(_scanResult.LatestUtc.Value.ToLocalTime());
            }
        }

        public string CameraModelText
        {
            get
            {
                if (_scanResult == null) { return "-"; }
                return string.IsNullOrEmpty(_scanResult.CameraModel) ? "未知" : _scanResult.CameraModel;
            }
        }

        public string ContentSummaryText { get { return _scanResult == null ? "-" : _scanResult.ContentSummary; } }

        public string SkippedText
        {
            get { return _scanResult == null ? "-" : Humanize.Count(_scanResult.SkippedCount) + " 个（0 字节或系统文件）"; }
        }

        /// <summary>是否可以开始拷贝。</summary>
        public bool CanStartCopy
        {
            get
            {
                return SelectedCard != null
                       && _scanResult != null
                       && _scanResult.TotalCount > 0
                       && !string.IsNullOrWhiteSpace(Services.Settings.PrimaryTargetRoot)
                       && !IsCopying;
            }
        }

        /// <summary>最近一次校验通过的卡片指纹（供后续格式化护栏使用）。</summary>
        public string LastVerifiedCardFingerprint { get; private set; }

        /// <summary>最近一次扫描覆盖率检查结果。</summary>
        private CoverageReport _lastCoverage;

        // ---------- 任务历史与续传提示 ----------

        private string _currentTaskId;
        private CopyPlan _currentPlan;
        private IList<CopyPlan> _currentPlans;
        private BackupResult _currentBackup;
        private string _currentProjectId;
        private TaskPlanFile _planFile;
        private CopyResult _currentCopyResult;
        private VerifyReport _currentVerifyReport;
        private ProjectContext _currentContext;
        private CardItemViewModel _currentCardItem;

        /// <summary>上次被中断任务的提示文本（为空表示没有）。</summary>
        public string ResumeHintText { get; private set; }

        /// <summary>是否存在中断提示。</summary>
        public bool HasResumeHint { get { return !string.IsNullOrEmpty(ResumeHintText); } }

        // ---------- 多日分组（F13） ----------

        private string _bulkLocation = string.Empty;
        private string _bulkShootType = string.Empty;
        private string _groupSummaryText = string.Empty;

        /// <summary>按拍摄日期分组的项目列表（单日卡只有一项）。</summary>
        public ObservableCollection<MediaGroupItemViewModel> Groups { get; private set; }

        /// <summary>是否检测到多个拍摄日期（决定是否显示分组表格）。</summary>
        public bool HasMultipleGroups { get { return Groups.Count > 1; } }

        /// <summary>分组摘要文本。</summary>
        public string GroupSummaryText
        {
            get { return _groupSummaryText; }
            private set { SetProperty(ref _groupSummaryText, value); }
        }

        /// <summary>统一填写的地点（一键应用到全部分组）。</summary>
        public string BulkLocation
        {
            get { return _bulkLocation; }
            set { SetProperty(ref _bulkLocation, value); }
        }

        /// <summary>统一填写的拍摄类型（一键应用到全部分组）。</summary>
        public string BulkShootType
        {
            get { return _bulkShootType; }
            set { SetProperty(ref _bulkShootType, value); }
        }

        /// <summary>把统一填写的地点与类型应用到全部分组。</summary>
        public RelayCommand ApplyBulkCommand { get; private set; }

        // ---------- 合并为一个项目（F14-D） ----------

        private bool _mergeMode;
        private string _mergeProjectName = string.Empty;
        private string _mergeLocation = string.Empty;
        private string _mergeShootType = string.Empty;
        private ProjectLayout _mergeLayout = ProjectLayout.Flat;
        private DateTime _mergeStartDate = DateTime.Now.Date;
        private DateTime _mergeEndDate = DateTime.Now.Date;
        private IList<KeyValuePair<DateTime, string>> _mergeDateOptions =
            new List<KeyValuePair<DateTime, string>>();

        /// <summary>按天拆分（默认，每天一个项目）。</summary>
        public bool IsSplitByDayMode
        {
            get { return !_mergeMode; }
            set { if (value) { SetMergeMode(false); } }
        }

        /// <summary>合并为一个项目（所有天的素材进同一项目）。</summary>
        public bool IsMergeMode
        {
            get { return _mergeMode; }
            set { if (value) { SetMergeMode(true); } }
        }

        private void SetMergeMode(bool merge)
        {
            if (_mergeMode == merge) { return; }
            _mergeMode = merge;
            RaisePropertyChanged("IsMergeMode");
            RaisePropertyChanged("IsSplitByDayMode");
            RaisePropertyChanged("ShowMergePanel");
            RaisePropertyChanged("MergePreviewText");
        }

        /// <summary>合并模式下显示项目名/日期范围/布局面板。</summary>
        public bool ShowMergePanel { get { return _mergeMode; } }

        /// <summary>合并后的项目名（可编辑）。</summary>
        public string MergeProjectName
        {
            get { return _mergeProjectName; }
            set { if (SetProperty(ref _mergeProjectName, value)) { RaisePropertyChanged("MergePreviewText"); } }
        }

        /// <summary>合并项目的地点。</summary>
        public string MergeLocation
        {
            get { return _mergeLocation; }
            set { SetProperty(ref _mergeLocation, value); }
        }

        /// <summary>合并项目的拍摄类型。</summary>
        public string MergeShootType
        {
            get { return _mergeShootType; }
            set { SetProperty(ref _mergeShootType, value); }
        }

        /// <summary>合并项目的起始日期（默认取扫描到的最早日期）。</summary>
        public DateTime MergeStartDate
        {
            get { return _mergeStartDate; }
            set
            {
                if (SetProperty(ref _mergeStartDate, value)) { RaisePropertyChanged("MergePreviewText"); }
            }
        }

        /// <summary>合并项目的终止日期（默认取扫描到的最晚日期）。</summary>
        public DateTime MergeEndDate
        {
            get { return _mergeEndDate; }
            set
            {
                if (SetProperty(ref _mergeEndDate, value)) { RaisePropertyChanged("MergePreviewText"); }
            }
        }

        /// <summary>可选的起止日期（来自卡上实际存在的拍摄日期，避免手填出错）。</summary>
        public IList<KeyValuePair<DateTime, string>> MergeDateOptions
        {
            get { return _mergeDateOptions; }
            private set { SetProperty(ref _mergeDateOptions, value); }
        }

        /// <summary>布局一：合并放一起（项目/照片、项目/视频）。</summary>
        public bool IsFlatLayout
        {
            get { return _mergeLayout == ProjectLayout.Flat; }
            set { if (value) { SetMergeLayout(ProjectLayout.Flat); } }
        }

        /// <summary>布局二：一天一个文件夹（项目/日期/照片）。</summary>
        public bool IsByDayLayout
        {
            get { return _mergeLayout == ProjectLayout.ByDay; }
            set { if (value) { SetMergeLayout(ProjectLayout.ByDay); } }
        }

        private void SetMergeLayout(ProjectLayout layout)
        {
            if (_mergeLayout == layout) { return; }
            _mergeLayout = layout;
            RaisePropertyChanged("IsFlatLayout");
            RaisePropertyChanged("IsByDayLayout");
            RaisePropertyChanged("MergePreviewText");
        }

        /// <summary>合并项目将要创建的目录预览（含布局提示）。</summary>
        public string MergePreviewText
        {
            get
            {
                string root = Services.Settings.PrimaryTargetRoot;
                if (string.IsNullOrWhiteSpace(root)) { return "尚未设置素材目标目录，请先到设置页填写。"; }

                string name = PathUtil.SanitizeName(_mergeProjectName, 100);
                if (string.IsNullOrEmpty(name)) { return "请填写项目名"; }

                string relative = string.IsNullOrEmpty(ResolveMergeParent())
                    ? name
                    : System.IO.Path.Combine(ResolveMergeParent(), name);
                string preview = System.IO.Path.Combine(root, relative);
                return _mergeLayout == ProjectLayout.ByDay
                    ? preview + System.IO.Path.DirectorySeparatorChar + "日期" +
                      System.IO.Path.DirectorySeparatorChar + "照片|视频|其他"
                    : preview + System.IO.Path.DirectorySeparatorChar + "照片|视频|其他";
            }
        }

        // ---------- 命令 ----------

        public RelayCommand RefreshCommand { get; private set; }
        public AsyncRelayCommand ScanCommand { get; private set; }
        public AsyncRelayCommand StartCopyCommand { get; private set; }

        /// <summary>继续上次被中断的任务（F12 精确断点续传）。</summary>
        public AsyncRelayCommand ResumeCommand { get; private set; }
        public RelayCommand CancelCopyCommand { get; private set; }
        public RelayCommand PauseResumeCommand { get; private set; }

        // ---------- 格式化与清空 ----------

        public RelayCommand FormatCardCommand { get; private set; }
        public RelayCommand EraseCardCommand { get; private set; }

        /// <summary>
        /// 是否可以对该卡片执行格式化或清空。
        /// 三个前提缺一不可：
        /// 1. 是"本次拷贝并校验通过的那一张卡"；
        /// 2. 卡上不存在"未被本次拷贝覆盖"的素材（BUG-001 的机制性兜底）；
        /// 3. 当前没有正在进行的任务。
        /// </summary>
        public bool CanFormatCard
        {
            get
            {
                if (SelectedCard == null || IsCopying || IsBusy) { return false; }
                if (LastVerifiedCardFingerprint == null
                    || LastVerifiedCardFingerprint != SelectedCard.Card.Identity.Fingerprint) { return false; }
                return _lastCoverage == null || _lastCoverage.FullyCovered;
            }
        }

        /// <summary>格式化区域的提示文本。</summary>
        public string FormatHintText
        {
            get
            {
                if (SelectedCard == null) { return "请先选择一张存储卡。"; }
                if (CanFormatCard) { return "本次拷贝与校验已完成，且卡上素材已全部覆盖，可以安全格式化这张卡片。"; }
                if (_lastCoverage != null && !_lastCoverage.FullyCovered)
                {
                    return "已暂停格式化解锁：" + _lastCoverage.Message + "。请重新扫描并完成这些素材的拷贝。";
                }
                return "仅当本次拷贝并校验通过、且卡上素材全部被覆盖后，才可以对这张卡片执行格式化或清空。";
            }
        }

        /// <summary>扫描范围文本（显示本次实际遍历的顶层目录，便于发现漏扫）。</summary>
        public string ScanScopeText
        {
            get
            {
                if (_scanResult == null || _scanResult.ScannedRoots == null || _scanResult.ScannedRoots.Count == 0)
                {
                    return "-";
                }
                return string.Join("、" + System.Environment.NewLine, _scanResult.ScannedRoots);
            }
        }

        /// <summary>覆盖率检查结果文本。</summary>
        public string CoverageText
        {
            get { return _lastCoverage == null ? "-" : _lastCoverage.Message; }
        }

        private void RefreshFormatState()
        {
            RaisePropertyChanged("CanFormatCard");
            RaisePropertyChanged("FormatHintText");
            RaisePropertyChanged("CoverageText");
            if (FormatCardCommand != null) { FormatCardCommand.RaiseCanExecuteChanged(); }
            if (EraseCardCommand != null) { EraseCardCommand.RaiseCanExecuteChanged(); }
        }

        /// <summary>格式化卡片（带完整的护栏检查与二次确认）。</summary>
        private void FormatCard()
        {
            CardItemViewModel item = SelectedCard;
            if (item == null || !CanFormatCard) { return; }

            bool verified = true; // CanFormatCard 已经保证校验通过
            DiskSafetyReport safety = DiskSafetyInspector.Inspect(item.Card.RootPath, verified, _log);
            if (!safety.CanFormat)
            {
                StatusText = "无法格式化：" + safety.Reason;
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "即将格式化卡片：" + item.DisplayName + "\n\n" +
                "· 本次拷贝与校验已经完成\n" +
                "· 卡片上的所有数据都会被清空，且无法恢复\n" +
                "· 格式化文件系统：" + Services.Settings.FormatFileSystem + "\n\n" +
                "确定要继续吗？",
                "确认格式化卡片", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) { StatusText = "已取消格式化，卡片数据未被改动。"; return; }

            StatusText = "正在格式化卡片，请勿拔出…";
            var formatter = new CardFormatter(_log);
            FormatResult result = formatter.Format(item.Card.RootPath, Services.Settings.FormatFileSystem,
                item.Card.VolumeLabel, verified, _lastCoverage == null ? 0 : _lastCoverage.UncoveredMediaFiles);

            StatusText = result.Message;
            Services.Notify(result.Success ? "卡片格式化完成" : "卡片格式化失败", result.Message);
            RecordMaintenanceTask("格式化卡片 " + item.DisplayName, item, result);
            if (result.Success) { LastVerifiedCardFingerprint = null; }
            RefreshFormatState();
            RefreshCards();
        }

        /// <summary>清空卡片素材文件（保留文件系统）。</summary>
        private void EraseCard()
        {
            CardItemViewModel item = SelectedCard;
            if (item == null || !CanFormatCard) { return; }

            MessageBoxResult answer = MessageBox.Show(
                "即将清空卡片上的素材文件：" + item.DisplayName + "\n\n" +
                "· 只删除素材文件，保留目录结构与文件系统\n" +
                "· 删除后无法恢复\n\n确定要继续吗？",
                "确认清空卡片", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) { StatusText = "已取消清空操作。"; return; }

            // 修复 BUG-001：清空要覆盖全部素材根（照片与视频可能分处不同顶层目录）
            var roots = new List<string>();
            if (item.Card.MediaRootPaths != null && item.Card.MediaRootPaths.Count > 0)
            {
                foreach (string path in item.Card.MediaRootPaths) { roots.Add(path); }
            }
            if (roots.Count == 0) { roots.Add(item.Card.RootPath); }

            StatusText = "正在清空卡片…";
            var formatter = new CardFormatter(_log);
            FormatResult result = null;
            foreach (string root in roots)
            {
                FormatResult one = formatter.EraseFiles(root, true, null, CancellationToken.None);
                if (result == null) { result = one; }
                else if (!one.Success) { result = one; }
            }
            if (result == null) { result = new FormatResult { Success = false, Message = "没有可清空的素材目录" }; }

            StatusText = result.Message;
            Services.Notify(result.Success ? "卡片已清空" : "清空卡片失败", result.Message);
            RecordMaintenanceTask("清空卡片 " + item.DisplayName, item, result);
            RefreshFormatState();
        }

        /// <summary>把格式化/清空操作也记入任务历史，保证每一次写操作都可追溯。</summary>
        private void RecordMaintenanceTask(string actionName, CardItemViewModel item, FormatResult result)
        {
            try
            {
                Services.TaskHistory.Add(new TaskRecord
                {
                    Id = ProjectMeta.NewId(),
                    StatusText = (result.Success ? TaskStatus.Succeeded : TaskStatus.Failed).ToString(),
                    StartedAt = TimeUtil.ToIso(DateTime.Now),
                    FinishedAt = TimeUtil.ToIso(DateTime.Now),
                    ProjectName = actionName,
                    TargetPath = item.Card.RootPath,
                    CardLabel = item.Card.VolumeLabel,
                    CardFingerprint = item.Card.Identity.Fingerprint,
                    Message = result.Message,
                    VerifyStatus = "—"
                });
            }
            catch (Exception ex)
            {
                _log.Warn("记录维护任务失败", ex);
            }
        }

        // ---------- 卡片变化 ----------

        private void OnCardsChanged(object sender, CardsChangedEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                ApplyCards(e.Cards, autoScanNewCard: true);
            }));
        }

        /// <summary>主动刷新卡片列表。</summary>
        public void RefreshCards()
        {
            ApplyCards(Services.CardDetector.Cards, autoScanNewCard: false);
        }

        private void ApplyCards(System.Collections.Generic.IList<CardVolume> cards, bool autoScanNewCard)
        {
            string previousFingerprint = SelectedCard == null ? null : SelectedCard.Card.Identity.Fingerprint;

            Cards.Clear();
            foreach (CardVolume card in cards) { Cards.Add(new CardItemViewModel(card)); }

            CardItemViewModel restored = null;
            if (previousFingerprint != null)
            {
                foreach (CardItemViewModel item in Cards)
                {
                    if (item.Card.Identity.Fingerprint == previousFingerprint) { restored = item; break; }
                }
            }
            if (restored == null && Cards.Count > 0) { restored = Cards[0]; }
            SelectedCard = restored;

            if (Cards.Count == 0)
            {
                StatusText = "尚未检测到存储卡，请插入内存卡或读卡器。";
                _scanResult = null;
                RaiseScanResultChanged();
            }
            else if (autoScanNewCard && restored != null && ScanCommand.CanExecute(null))
            {
                StatusText = "检测到新卡片，正在自动扫描素材…";
                ScanCommand.Execute(null);
            }
        }

        // ---------- 扫描 ----------

        private async Task ScanSelectedCardAsync()
        {
            CardItemViewModel item = SelectedCard;
            if (item == null) { return; }

            IsBusy = true;
            ProgressText = "正在扫描素材…";
            StatusText = "正在扫描 " + item.DisplayName + " 的素材…";

            try
            {
                // 修复 BUG-001：始终从卡根开始扫描，由扫描器遍历全部特征目录与其它目录，
                // 不再只盯着“第一个特征目录”（否则视频在 PRIVATE/AVCHD 时会被整段跳过）
                string root = item.Card.ScanRootPath;
                var progress = new Progress<ScanProgress>(delegate (ScanProgress value)
                {
                    ProgressText = string.Format("{0}：{1} 个文件，{2}", value.Phase,
                        Humanize.Count(value.FilesScanned), Humanize.Bytes(value.BytesScanned));
                });

                AppSettings settings = Services.Settings;
                ScanResult result = await Task.Run(delegate
                {
                    return Services.Scanner.Scan(root, settings, progress, CancellationToken.None);
                });

                _scanResult = result;
                RebuildGroups(result);
                RefreshAppendCandidates(result);
                if (string.IsNullOrEmpty(Location) && !string.IsNullOrEmpty(result.CameraModel))
                {
                    // 没有输入地点时不影响流程，这里只是记录机型到上下文
                }
                RaiseScanResultChanged();
                StatusText = string.Format("扫描完成：{0}，共 {1}。填写拍摄信息后即可开始拷贝。", result.ContentSummary, Humanize.Bytes(result.TotalBytes));
            }
            catch (Exception ex)
            {
                _log.Error("扫描素材失败", ex);
                StatusText = "扫描失败：" + ex.Message;
            }
            finally
            {
                ProgressText = string.Empty;
                IsBusy = false;
                StartCopyCommand.RaiseCanExecuteChanged();
            }
        }

        private void RaiseScanResultChanged()
        {
            RaisePropertyChanged("HasScanResult");
            RaisePropertyChanged("PhotoText");
            RaisePropertyChanged("VideoText");
            RaisePropertyChanged("OtherText");
            RaisePropertyChanged("TotalText");
            RaisePropertyChanged("TimeRangeText");
            RaisePropertyChanged("CameraModelText");
            RaisePropertyChanged("ContentSummaryText");
            RaisePropertyChanged("SkippedText");
            RaisePropertyChanged("ScanScopeText");
            RaisePropertyChanged("CoverageText");
            RaisePropertyChanged("TargetPreviewText");
            RaisePropertyChanged("CanStartCopy");
        }

        /// <summary>
        /// 启动时检查上次是否有被中断的任务。
        /// 有的话给出提示：已拷贝的文件会保留，重新开始时按策略自动跳过，从而近似实现断点续传。
        /// </summary>
        private void DetectInterruptedTasks()
        {
            try
            {
                System.Collections.Generic.IList<TaskRecord> interrupted = Services.TaskHistory.TakeInterruptedTasks();
                if (interrupted.Count == 0) { ResumeHintText = string.Empty; }
                else
                {
                    TaskRecord last = interrupted[interrupted.Count - 1];
                    _planFile = Services.PlanStore.Load(last.Id);
                    if (_planFile != null && _planFile.IsSourceAvailable())
                    {
                        ResumeHintText = string.Format(
                            "上次任务（{0}）未完成：还剩 {1} 个文件未拷贝。插回原卡后点“继续上次任务”即可直接补拷，不需要重新扫描。",
                            last.ProjectName, Humanize.Count(_planFile.TotalPendingCount));
                    }
                    else
                    {
                        ResumeHintText = "上次有任务未完成：" + last.ProjectName +
                                         "；重新扫描后再次拷贝会自动跳过已完成的文件。";
                    }
                    StatusText = ResumeHintText;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("检查中断任务失败", ex);
            }
            RaisePropertyChanged("ResumeHintText");
            RaisePropertyChanged("HasResumeHint");
            RaiseResumeState();
        }

        /// <summary>把多个执行单元的拷贝结果聚合成一份（用于任务历史展示）。</summary>
        private static CopyResult AggregateCopy(BackupResult backup)
        {
            var aggregate = new CopyResult();
            aggregate.EnsurePathCollections();
            if (backup == null) { return aggregate; }

            foreach (BackupUnit unit in backup.Units)
            {
                if (unit.Copy == null) { continue; }
                aggregate.CopiedFiles += unit.Copy.CopiedFiles;
                aggregate.SkippedFiles += unit.Copy.SkippedFiles;
                aggregate.FailedFiles += unit.Copy.FailedFiles;
                aggregate.CopiedBytes += unit.Copy.CopiedBytes;
                if (unit.Copy.Duration > aggregate.Duration) { aggregate.Duration = unit.Copy.Duration; }
                foreach (CopyFailure failure in unit.Copy.Failures) { aggregate.Failures.Add(failure); }
                foreach (string path in unit.Copy.CompletedTargetPaths) { aggregate.CompletedTargetPaths.Add(path); }
                foreach (string path in unit.Copy.SkippedTargetPaths) { aggregate.SkippedTargetPaths.Add(path); }
                foreach (string path in unit.Copy.FailedTargetPaths) { aggregate.FailedTargetPaths.Add(path); }
            }
            return aggregate;
        }

        /// <summary>把多个执行单元的校验结果聚合成一份。</summary>
        private static VerifyReport AggregateVerify(BackupResult backup)
        {
            var aggregate = new VerifyReport();
            if (backup == null) { return aggregate; }

            foreach (BackupUnit unit in backup.Units)
            {
                if (unit.Verify == null) { continue; }
                aggregate.Level = unit.Verify.Level;
                aggregate.PassedFiles += unit.Verify.PassedFiles;
                aggregate.FailedFiles += unit.Verify.FailedFiles;
                aggregate.SkippedFiles += unit.Verify.SkippedFiles;
                aggregate.VerifiedBytes += unit.Verify.VerifiedBytes;
                foreach (FileVerifyFailure failure in unit.Verify.Failures) { aggregate.Failures.Add(failure); }
            }
            return aggregate;
        }

        /// <summary>任务开始时写入一条"进行中"记录。</summary>
        private void BeginTaskRecord(CopyPlan plan, CardItemViewModel cardItem, ProjectContext context)
        {
            _currentTaskId = ProjectMeta.NewId();
            _currentProjectId = ProjectMeta.NewId();
            _currentPlan = plan;
            _currentContext = context;
            _currentCardItem = cardItem;
            _currentCopyResult = null;
            _currentVerifyReport = null;

            Services.TaskHistory.Upsert(new TaskRecord
            {
                Id = _currentTaskId,
                StatusText = TaskStatus.Running.ToString(),
                StartedAt = TimeUtil.ToIso(DateTime.Now),
                ProjectName = plan.ProjectName,
                TargetPath = plan.ProjectDirectory,
                FileCount = plan.TotalCount,
                TotalBytes = plan.TotalBytes,
                CardLabel = cardItem == null ? string.Empty : cardItem.Card.VolumeLabel,
                CardFingerprint = cardItem == null ? string.Empty : cardItem.Card.Identity.Fingerprint,
                VerifyLevel = Services.Settings.VerifyLevel.ToString()
            });
        }

        /// <summary>任务结束时补全记录，并把项目写入本地项目库。</summary>
        private void FinishTaskRecord()
        {
            if (_currentTaskId == null || _currentPlan == null) { return; }
            try
            {
                CopyResult copy = _currentCopyResult;
                VerifyReport verify = _currentVerifyReport;

                TaskStatus status;
                string message;
                if (copy == null)
                {
                    status = TaskStatus.Failed;
                    message = "任务未完成（计划或预检阶段中止）";
                }
                else if (copy.Cancelled)
                {
                    status = TaskStatus.Cancelled;
                    message = "用户取消";
                }
                else if (copy.FailedFiles > 0 || (verify != null && !verify.AllPassed))
                {
                    status = TaskStatus.Partial;
                    message = string.Format("拷贝失败 {0} 个，校验失败 {1} 个",
                        copy.FailedFiles, verify == null ? 0 : verify.FailedFiles);
                }
                else
                {
                    status = TaskStatus.Succeeded;
                    message = verify == null ? "拷贝完成" : verify.Summary;
                }

                Services.TaskHistory.Upsert(new TaskRecord
                {
                    Id = _currentTaskId,
                    StatusText = status.ToString(),
                    StartedAt = TimeUtil.ToIso(DateTime.Now),
                    FinishedAt = TimeUtil.ToIso(DateTime.Now),
                    ProjectName = _currentPlan.ProjectName,
                    TargetPath = _currentPlan.ProjectDirectory,
                    FileCount = _currentPlan.TotalCount,
                    TotalBytes = _currentPlan.TotalBytes,
                    CopiedFiles = copy == null ? 0 : copy.CopiedFiles,
                    FailedFiles = copy == null ? 0 : copy.FailedFiles,
                    SkippedFiles = copy == null ? 0 : copy.SkippedFiles,
                    DurationSeconds = copy == null ? 0 : copy.Duration.TotalSeconds,
                    AverageBytesPerSecond = copy == null ? 0 : copy.AverageBytesPerSecond,
                    CardLabel = _currentCardItem == null ? string.Empty : _currentCardItem.Card.VolumeLabel,
                    CardFingerprint = _currentCardItem == null ? string.Empty : _currentCardItem.Card.Identity.Fingerprint,
                    VerifyLevel = verify == null ? Services.Settings.VerifyLevel.ToString() : verify.Level.ToString(),
                    VerifyStatus = verify == null ? "未校验" : (verify.AllPassed ? "校验通过" : "校验失败"),
                    Message = message,
                    LogFile = AppPaths.TaskLogDirectory(Services.DataDirectory)
                });

                // 只有真正产生素材时才写入项目库，避免失败任务污染项目列表
                if (status == TaskStatus.Succeeded || status == TaskStatus.Partial)
                {
                    ProjectMeta meta = Services.ManifestWriter.BuildProjectMeta(
                        _currentPlan, _scanResult, _currentContext, verify, _currentCardItem == null ? null : _currentCardItem.Card.Identity, _currentTaskId);
                    Services.ProjectLibrary.Save(meta);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("写入任务历史失败", ex);
            }
        }

        // ---------- 追加到已有项目（F14-B） ----------

        /// <summary>可按日期匹配的候选项目（扫描完成后刷新）。</summary>
        public ObservableCollection<ProjectMeta> AppendCandidates { get; private set; }

        private ProjectMeta _selectedAppendProject;

        /// <summary>选中的追加目标项目。</summary>
        public ProjectMeta SelectedAppendProject
        {
            get { return _selectedAppendProject; }
            set
            {
                if (SetProperty(ref _selectedAppendProject, value))
                {
                    RaisePropertyChanged("AppendRangeText");
                    StartAppendCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>选中项目的日期范围提示。</summary>
        public string AppendRangeText
        {
            get
            {
                if (_selectedAppendProject == null) { return "请选择要追加的项目"; }
                DateTime start, end;
                if (!AppendService.TryResolveRange(_selectedAppendProject, out start, out end)) { return "该项目缺少日期信息"; }
                return start == end
                    ? "项目日期：" + start.ToString("yyyy-MM-dd")
                    : "项目日期范围：" + start.ToString("yyyy-MM-dd") + " ~ " + end.ToString("yyyy-MM-dd");
            }
        }

        /// <summary>开始追加。</summary>
        public AsyncRelayCommand StartAppendCommand { get; private set; }

        private bool CanStartAppend()
        {
            return _scanResult != null && _selectedAppendProject != null && !IsBusy;
        }

        /// <summary>扫描完成后刷新候选项目（按拍摄日期是否落在项目范围内筛选）。</summary>
        private void RefreshAppendCandidates(ScanResult scan)
        {
            AppendCandidates.Clear();
            SelectedAppendProject = null;
            if (scan == null || scan.Files.Count == 0) { return; }

            DateTime earliest = DateTime.MaxValue;
            DateTime latest = DateTime.MinValue;
            foreach (ScannedFile file in scan.Files)
            {
                if (!file.ShootTime.HasValue) { continue; }
                if (file.ShootTime.Value < earliest) { earliest = file.ShootTime.Value; }
                if (file.ShootTime.Value > latest) { latest = file.ShootTime.Value; }
            }
            if (earliest == DateTime.MaxValue) { return; }

            foreach (ProjectMeta project in Services.ProjectLibrary.LoadAll())
            {
                DateTime start, end;
                if (!AppendService.TryResolveRange(project, out start, out end)) { continue; }
                // 只有日期范围能覆盖本次素材（或与本次重叠）的项目才列为候选
                if (latest.Date < start || earliest.Date > end) { continue; }
                AppendCandidates.Add(project);
            }
            if (AppendCandidates.Count > 0) { SelectedAppendProject = AppendCandidates[0]; }
        }

        /// <summary>执行追加（日期不匹配时整批拒绝，不写入任何文件）。</summary>
        private async Task StartAppendAsync()
        {
            if (_scanResult == null || _selectedAppendProject == null) { return; }

            ProjectMeta project = _selectedAppendProject;
            int conflictCount = 0;
            foreach (ScannedFile file in _scanResult.Files) { conflictCount++; }

            using (_cancellation = new CancellationTokenSource())
            {
                IsBusy = true;
                HasResult = true;
                ResultSummary = string.Empty;
                StatusText = string.Format("正在向项目“{0}”追加素材…", project.Name);

                var progress = new Progress<CopyProgress>(delegate (CopyProgress value)
                {
                    if (value.TotalBytes > 0)
                    {
                        StatusText = string.Format("追加中：{0}/{1}（{2}）", Humanize.Count(value.CompletedFiles),
                            Humanize.Count(value.TotalFiles), Humanize.Bytes(value.CompletedBytes));
                    }
                });

                try
                {
                    var resolver = new UiConflictResolver(Application.Current.MainWindow, conflictCount);
                    var request = new AppendRequest
                    {
                        Project = project,
                        Files = _scanResult.Files,
                        Layout = string.Equals(project.ImportLayout, "byday", StringComparison.OrdinalIgnoreCase)
                            ? ProjectLayout.ByDay : ProjectLayout.Flat,
                        CardFingerprint = SelectedCard == null ? string.Empty : SelectedCard.Card.Identity.Fingerprint
                    };

                    AppendResult result = await Services.Append.RunAsync(request, Services.Settings,
                        resolver, progress, _pauseToken, _cancellation.Token);

                    if (!result.Success && result.OutOfRangeFiles.Count > 0)
                    {
                        var lines = new List<string>();
                        foreach (string name in result.OutOfRangeFiles) { lines.Add("· " + name); }
                        StatusText = result.Message;
                        ResultSummary = result.Message + Environment.NewLine + string.Join(Environment.NewLine, lines.ToArray());
                        return;
                    }

                    StatusText = result.Message;
                    ResultSummary = string.Format("{0}{1}（项目：{2}，范围 {3}）",
                        result.Success ? "✅ " : "⚠️ ", result.Message, project.Name, result.RangeText);

                    if (result.Success)
                    {
                        // 更新项目记录：文件数与校验时间
                        project.TotalCount = project.TotalCount + result.AddedFiles;
                        project.Source = "append";
                        project.LastVerifiedAt = TimeUtil.ToIsoUtcPrecise(DateTime.Now);
                        project.UpdatedAt = project.LastVerifiedAt;
                        Services.ProjectLibrary.Save(project);
                    }
                }
                catch (Exception ex)
                {
                    StatusText = "追加失败：" + ex.Message;
                    ResultSummary = "追加失败：" + ex.Message;
                }
                finally
                {
                    IsBusy = false;
                    StartAppendCommand.RaiseCanExecuteChanged();
                }
            }
        }

        // ---------- 多日分组（F13-B/C） ----------

        /// <summary>扫描完成后按拍摄日期分组，生成待命名的项目列表。</summary>
        private void RebuildGroups(ScanResult scan)
        {
            Groups.Clear();

            if (scan == null || scan.Groups == null || scan.Groups.Count == 0)
            {
                GroupSummaryText = string.Empty;
                MergeDateOptions = new List<KeyValuePair<DateTime, string>>();
                RaisePropertyChanged("HasMultipleGroups");
                return;
            }

            string targetRoot = Services.Settings.PrimaryTargetRoot;
            string template = Services.Settings.NameTemplate;

            foreach (MediaDayGroup group in scan.Groups)
            {
                DateTime shootDate = group.Date
                    ?? (scan.EarliestUtc.HasValue ? scan.EarliestUtc.Value.ToLocalTime().Date : DateTime.Now.Date);

                var context = new ProjectContext
                {
                    ShootDate = shootDate,
                    ShootType = string.IsNullOrWhiteSpace(ShootType) ? "其他" : ShootType.Trim(),
                    Location = string.IsNullOrWhiteSpace(Location) ? "未标注地点" : Location.Trim(),
                    Photographer = Photographer ?? string.Empty,
                    CameraModel = scan.CameraModel,
                    CardLabel = SelectedCard == null ? string.Empty : SelectedCard.Card.VolumeLabel
                };

                Groups.Add(new MediaGroupItemViewModel(group, context,
                    BuildDefaultProjectName(template, context), targetRoot, BuildParentRelative(template, context)));
            }

            InitializeMergeDefaults(scan);

            GroupSummaryText = string.Format("检测到 {0}，共 {1} / {2}",
                MediaDayGrouper.Describe(scan.Groups), Humanize.Count(scan.TotalCount), Humanize.Bytes(scan.TotalBytes));
            if (scan.UnknownTimeCount > 0)
            {
                GroupSummaryText += string.Format("；其中 {0} 个文件时间未知，请为“未知日期”组确认项目名",
                    Humanize.Count(scan.UnknownTimeCount));
            }
            RaisePropertyChanged("HasMultipleGroups");
        }

        /// <summary>模板展开后取最后一段作为默认项目名。</summary>
        private static string BuildDefaultProjectName(string template, ProjectContext context)
        {
            string relative = NameTemplateEngine.ToWindowsRelativePath(NameTemplateEngine.Expand(template, context));
            int index = relative.LastIndexOf(System.IO.Path.DirectorySeparatorChar);
            return PathUtil.SanitizeName(index >= 0 ? relative.Substring(index + 1) : relative, 100);
        }

        /// <summary>模板展开后取父级相对路径（除最后一段）。</summary>
        private static string BuildParentRelative(string template, ProjectContext context)
        {
            string relative = NameTemplateEngine.ToWindowsRelativePath(NameTemplateEngine.Expand(template, context));
            int index = relative.LastIndexOf(System.IO.Path.DirectorySeparatorChar);
            return index >= 0 ? relative.Substring(0, index) : string.Empty;
        }

        /// <summary>把统一填写的地点与类型应用到全部分组。</summary>
        private void ApplyBulkToAllGroups()
        {
            foreach (MediaGroupItemViewModel item in Groups) { item.ApplyBulk(BulkLocation, BulkShootType); }
            StatusText = string.Format("已把地点与类型应用到 {0} 个分组。", Groups.Count);
        }

        // ---------- 合并为一个项目（F14-D） ----------

        /// <summary>
        /// 扫描后重置合并模式的默认值：日期范围取卡上最早与最晚日期，项目名按命名模板生成。
        /// 每次重新扫描都回到"按天拆分"，避免上一次的选择被误用到另一张卡。
        /// </summary>
        private void InitializeMergeDefaults(ScanResult scan)
        {
            var dates = new List<DateTime>();
            if (scan != null && scan.Groups != null)
            {
                foreach (MediaDayGroup group in scan.Groups)
                {
                    if (!group.Date.HasValue) { continue; }
                    DateTime day = group.Date.Value.Date;
                    if (!dates.Contains(day)) { dates.Add(day); }
                }
            }
            dates.Sort();
            if (dates.Count == 0) { dates.Add(DateTime.Now.Date); }

            var options = new List<KeyValuePair<DateTime, string>>();
            foreach (DateTime day in dates)
            {
                options.Add(new KeyValuePair<DateTime, string>(day, day.ToString("yyyy-MM-dd")));
            }
            MergeDateOptions = options;
            _mergeStartDate = dates[0];
            _mergeEndDate = dates[dates.Count - 1];
            RaisePropertyChanged("MergeStartDate");
            RaisePropertyChanged("MergeEndDate");

            // 项目名/地点/类型沿用拍摄信息表单，用户仍可在合并面板里单独修改
            var context = new ProjectContext
            {
                ShootDate = _mergeStartDate,
                ShootType = string.IsNullOrWhiteSpace(ShootType) ? "其他" : ShootType.Trim(),
                Location = string.IsNullOrWhiteSpace(Location) ? "未标注地点" : Location.Trim()
            };
            MergeProjectName = BuildDefaultProjectName(Services.Settings.NameTemplate, context);
            MergeLocation = context.Location;
            MergeShootType = context.ShootType;

            // 新一次扫描回到"按天拆分"
            _mergeMode = false;
            RaisePropertyChanged("IsMergeMode");
            RaisePropertyChanged("IsSplitByDayMode");
            RaisePropertyChanged("ShowMergePanel");
            RaisePropertyChanged("MergePreviewText");
        }

        /// <summary>合并项目的父级相对路径（命名模板里的年份等目录），按当前起始日期计算。</summary>
        private string ResolveMergeParent()
        {
            var context = new ProjectContext
            {
                ShootDate = _mergeStartDate.Date,
                ShootType = string.IsNullOrWhiteSpace(_mergeShootType) ? "其他" : _mergeShootType.Trim(),
                Location = string.IsNullOrWhiteSpace(_mergeLocation) ? "未标注地点" : _mergeLocation.Trim()
            };
            return BuildParentRelative(Services.Settings.NameTemplate, context);
        }

        /// <summary>构造"合并为一个项目"的拷贝请求。</summary>
        private ProjectGroupRequest BuildMergedRequest(ScanResult scan)
        {
            string shootType = string.IsNullOrWhiteSpace(_mergeShootType) ? "其他" : _mergeShootType.Trim();
            string location = string.IsNullOrWhiteSpace(_mergeLocation) ? "未标注地点" : _mergeLocation.Trim();

            var context = new ProjectContext
            {
                ShootDate = _mergeStartDate.Date,
                ShootType = shootType,
                Location = location,
                Photographer = Photographer ?? string.Empty,
                CameraModel = scan.CameraModel,
                CardLabel = SelectedCard == null ? string.Empty : SelectedCard.Card.VolumeLabel
            };

            string name = PathUtil.SanitizeName(_mergeProjectName, 100);
            string parent = BuildParentRelative(Services.Settings.NameTemplate, context);
            return new ProjectGroupRequest
            {
                Context = context,
                Files = scan.Files,
                RelativeDirectory = string.IsNullOrEmpty(parent) ? name : System.IO.Path.Combine(parent, name),
                DisplayName = name,
                Layout = _mergeLayout,
                RangeStart = _mergeStartDate.Date,
                RangeEnd = _mergeEndDate.Date,
                Source = "create"
            };
        }

        /// <summary>合并模式的校验：项目名不能为空、日期范围必须覆盖本次全部素材。</summary>
        private string ValidateMergedGroup()
        {
            if (string.IsNullOrWhiteSpace(PathUtil.SanitizeName(_mergeProjectName, 100)))
            {
                return "合并项目的项目名不能为空。";
            }
            if (_mergeEndDate.Date < _mergeStartDate.Date)
            {
                return "终止日期不能早于起始日期，请重新选择。";
            }

            int outside = 0;
            string sample = null;
            foreach (ScannedFile file in _scanResult.Files)
            {
                if (!file.ShootTime.HasValue) { continue; }
                DateTime day = file.ShootTime.Value.Date;
                if (day >= _mergeStartDate.Date && day <= _mergeEndDate.Date) { continue; }
                outside++;
                if (sample == null)
                {
                    sample = System.IO.Path.GetFileName(file.FullPath) + "（" + day.ToString("yyyy-MM-dd") + "）";
                }
            }

            if (outside > 0)
            {
                return string.Format(
                    "有 {0} 个素材的拍摄日期不在 {1} ~ {2} 内（例如 {3}）。请调整日期范围，或改用“按天拆分”。",
                    outside, _mergeStartDate.ToString("yyyy-MM-dd"), _mergeEndDate.ToString("yyyy-MM-dd"), sample);
            }
            return null;
        }

        /// <summary>
        /// 构造拷贝请求：多日时按分组表格生成多个项目；单日时沿用拍摄信息表单，行为与旧版本一致。
        /// </summary>
        private IList<ProjectGroupRequest> BuildGroupRequests(ScanResult scan)
        {
            var requests = new List<ProjectGroupRequest>();

            if (Groups.Count > 1)
            {
                // 合并为一个项目（F14-D）：所有天的素材进同一项目，日期范围与布局由用户选择
                if (IsMergeMode)
                {
                    requests.Add(BuildMergedRequest(scan));
                    return requests;
                }

                foreach (MediaGroupItemViewModel item in Groups)
                {
                    string name = PathUtil.SanitizeName(item.ProjectName, 100);
                    requests.Add(new ProjectGroupRequest
                    {
                        Context = item.Context,
                        Files = item.Files,
                        RelativeDirectory = item.RelativeDirectory,
                        DisplayName = string.IsNullOrEmpty(name) ? item.DateText : name
                    });
                }
                return requests;
            }

            var context = new ProjectContext
            {
                ShootDate = scan.EarliestUtc.HasValue ? scan.EarliestUtc.Value.ToLocalTime() : DateTime.Now,
                ShootType = string.IsNullOrWhiteSpace(ShootType) ? "其他" : ShootType.Trim(),
                Location = string.IsNullOrWhiteSpace(Location) ? "未标注地点" : Location.Trim(),
                Photographer = Photographer ?? string.Empty,
                CameraModel = scan.CameraModel,
                CardLabel = SelectedCard == null ? string.Empty : SelectedCard.Card.VolumeLabel
            };
            string template = Services.Settings.NameTemplate;
            requests.Add(new ProjectGroupRequest
            {
                Context = context,
                Files = scan.Files,
                RelativeDirectory = NameTemplateEngine.ToWindowsRelativePath(NameTemplateEngine.Expand(template, context)),
                DisplayName = BuildDefaultProjectName(template, context)
            });
            return requests;
        }

        /// <summary>多项目时的名称校验（空名与重名都不允许开始拷贝）。</summary>
        private string ValidateGroups()
        {
            if (Groups.Count <= 1) { return null; }
            if (IsMergeMode) { return ValidateMergedGroup(); }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MediaGroupItemViewModel item in Groups)
            {
                string name = PathUtil.SanitizeName(item.ProjectName, 100);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return string.Format("{0} 的项目名为空，请填写后再开始拷贝。", item.DateText);
                }
                if (!seen.Add(item.RelativeDirectory))
                {
                    return "存在同名项目目录：" + item.RelativeDirectory + "，请修改项目名。";
                }
            }
            return null;
        }

        // ---------- 断点续传（F12） ----------

        /// <summary>是否存在可继续的任务。</summary>
        public bool CanResume
        {
            get { return _planFile != null && _planFile.IsSourceAvailable(); }
        }

        /// <summary>通知续传相关属性变化。</summary>
        private void RaiseResumeState()
        {
            RaisePropertyChanged("CanResume");
            RaisePropertyChanged("ResumeHintText");
            if (ResumeCommand != null) { ResumeCommand.RaiseCanExecuteChanged(); }
        }

        /// <summary>写入或更新断点续传计划文件。</summary>
        private void SavePlanFile(IList<CopyPlan> plans, CardItemViewModel cardItem, ProjectContext context)
        {
            try
            {
                if (_currentTaskId == null || plans == null || plans.Count == 0) { return; }

                if (_planFile == null)
                {
                    _planFile = TaskPlanStore.FromPlans(_currentTaskId, _currentProjectId,
                        cardItem == null ? string.Empty : cardItem.Card.ScanRootPath,
                        new ProjectContextLike
                        {
                            ProjectName = plans[0].ProjectName,
                            ShootDate = TimeUtil.ToIso(context.ShootDate),
                            Location = context.Location,
                            ShootType = context.ShootType,
                            Photographer = context.Photographer,
                            CameraModel = context.CameraModel,
                            CardFingerprint = cardItem == null ? string.Empty : cardItem.Card.Identity.Fingerprint,
                            CardLabel = cardItem == null ? string.Empty : cardItem.Card.VolumeLabel
                        }, plans);
                }

                if (_currentBackup != null)
                {
                    foreach (BackupUnit eachUnit in _currentBackup.Units)
                    {
                        if (eachUnit.Copy != null) { TaskPlanStore.ApplyResult(_planFile, eachUnit.Root, eachUnit.Copy); }
                    }
                }

                Services.PlanStore.Save(_planFile);
            }
            catch (Exception ex)
            {
                _log.Warn("保存续传计划失败", ex);
            }
        }

        /// <summary>
        /// 继续上次被中断的任务：只补拷未完成的文件，然后重新校验。
        /// 与重新扫描再拷贝的区别在于：这里直接使用落盘的计划文件，不必重新扫描卡片，
        /// 也不会重复校验已经确认完成的文件。
        /// </summary>
        private async Task ResumeAsync()
        {
            TaskPlanFile file = _planFile;
            if (file == null) { StatusText = "没有可继续的任务。"; return; }
            if (!file.IsSourceAvailable())
            {
                StatusText = "源卡片未连接（" + file.SourceRoot + "），请插回原卡后再继续。";
                return;
            }

            IList<CopyPlan> plans = TaskPlanStore.ToPlans(file, _log);
            if (plans.Count == 0) { StatusText = "计划文件内容为空，无法继续。"; return; }

            CardItemViewModel cardItem = SelectedCard;
            var context = new ProjectContext
            {
                ShootDate = TimeUtil.ParseIso(file.ShootDate, DateTime.Now),
                ShootType = file.ShootType,
                Location = file.Location,
                Photographer = file.Photographer,
                CameraModel = file.CameraModel,
                CardLabel = file.CardLabel
            };

            IsBusy = true;
            IsCopying = true;
            HasResult = false;
            Failures.Clear();
            ResultSummary = string.Empty;

            _currentPlans = plans;
            _currentTaskId = file.TaskId;
            _currentProjectId = file.ProjectId;
            _currentCardItem = cardItem;
            _currentContext = context;
            _currentCopyResult = null;
            _currentVerifyReport = null;
            _currentBackup = null;

            var failures = new List<string>();
            try
            {
                int pending = 0;
                foreach (CopyPlan each in plans) { pending += each.PendingCount; }
                StatusText = pending > 0
                    ? string.Format("正在继续上次任务，需补拷 {0} 个文件…", Humanize.Count(pending))
                    : "上次任务的文件均已完成，正在重新校验…";

                _cancellation = new CancellationTokenSource();
                _pauseToken.Resume();
                IsPaused = false;

                var copyProgress = new Progress<CopyProgress>(delegate (CopyProgress value)
                {
                    CopyPercent = value.Ratio * 100;
                    CopySpeedText = Humanize.Speed(value.BytesPerSecond);
                    CopyEtaText = value.Remaining > TimeSpan.Zero ? "剩余 " + Humanize.Duration(value.Remaining) : "计算中…";
                    CopyCurrentFile = value.CurrentFile;
                    ProgressText = string.Format("{0}{1} / {2} 个文件 · {3} / {4} · {5}",
                        value.TargetCount > 1 ? "[" + value.TargetIndex + "/" + value.TargetCount + "] " : string.Empty,
                        Humanize.Count(value.CompletedFiles), Humanize.Count(value.TotalFiles),
                        Humanize.Bytes(value.CompletedBytes), Humanize.Bytes(value.TotalBytes), CopySpeedText);
                });

                var resumePlanSet = new BackupPlanSet();
                foreach (CopyPlan restoredPlan in plans)
                {
                    resumePlanSet.Units.Add(new BackupUnit
                    {
                        Group = new ProjectGroupRequest
                        {
                            Context = context,
                            Files = new List<ScannedFile>(),
                            RelativeDirectory = restoredPlan.ProjectRelativePath,
                            DisplayName = restoredPlan.ProjectName
                        },
                        Root = restoredPlan.TargetRoot,
                        IsPrimaryTarget = resumePlanSet.Units.Count == 0,
                        ProjectIndex = 0,
                        ProjectCount = 1,
                        Plan = restoredPlan
                    });
                }

                BackupResult backup = await Services.Backup.RunAsync(
                    resumePlanSet, null, Services.Settings, copyProgress, _pauseToken, _cancellation.Token);
                _currentBackup = backup;

                CopyResult copyResult = AggregateCopy(backup);
                VerifyReport report = AggregateVerify(backup);
                _currentCopyResult = copyResult;
                _currentVerifyReport = report;

                foreach (BackupUnit eachUnit in backup.Units)
                {
                    if (!eachUnit.Succeeded) { failures.Add("未完成：" + eachUnit.DisplayName + " —— " + eachUnit.Message); }
                }

                SavePlanFile(plans, cardItem, context);

                if (backup.Cancelled)
                {
                    StatusText = "已取消继续任务，已完成的文件保留在目标目录。";
                    ResultSummary = "任务已取消";
                    HasResult = true;
                    return;
                }

                string projectId = _currentProjectId ?? ProjectMeta.NewId();
                _currentProjectId = projectId;
                foreach (BackupUnit eachUnit in backup.Units)
                {
                    if (eachUnit.Plan == null) { continue; }
                    var writeResult = Services.ManifestWriter.WriteProjectFiles(
                        eachUnit.Plan, null, context, Services.Settings, eachUnit.Copy, eachUnit.Verify,
                        cardItem == null ? null : cardItem.Card.Identity, projectId);
                    if (!writeResult.Success) { failures.Add("清单写入失败（" + eachUnit.DisplayName + "）：" + writeResult.Message); }
                }

                StatusText = "正在检查卡上是否还有未被拷贝的素材…";
                CoverageReport coverage = await Task.Run(delegate
                {
                    var copiedPaths = new List<string>();
                    foreach (CopyPlan each in plans)
                    {
                        foreach (CopyItem planItem in each.Items)
                        {
                            if (!copiedPaths.Contains(planItem.Source.FullPath)) { copiedPaths.Add(planItem.Source.FullPath); }
                        }
                    }
                    return CoverageChecker.Check(file.SourceRoot, Services.Settings, copiedPaths, _log, CancellationToken.None);
                });
                _lastCoverage = coverage;
                if (!coverage.FullyCovered) { failures.Add("⚠️ " + coverage.Message); }

                bool allOk = backup.AllSucceeded && coverage.FullyCovered;
                LastVerifiedCardFingerprint = allOk && report.Level != VerifyLevel.None && cardItem != null
                    ? cardItem.Card.Identity.Fingerprint
                    : null;

                if (allOk)
                {
                    Services.PlanStore.Delete(file.TaskId);
                    _planFile = null;
                    ResumeHintText = string.Empty;
                }

                ResultSummary = string.Format("{0}；本轮补拷 {1} 个文件（{2}）；{3}；{4}",
                    allOk ? "✅ 续传后拷贝与校验全部通过" : "⚠️ 续传后仍存在问题",
                    Humanize.Count(copyResult.CopiedFiles), Humanize.Bytes(copyResult.CopiedBytes),
                    report.Summary, coverage.Message);
                HasResult = true;
                StatusText = plans[0].ProjectDirectory;
                RefreshFormatState();
            }
            catch (Exception ex)
            {
                _log.Error("继续任务失败", ex);
                StatusText = "继续任务失败：" + ex.Message;
                failures.Add("异常：" + ex.Message);
                HasResult = true;
                ResultSummary = "继续任务失败";
            }
            finally
            {
                IsCopying = false;
                IsBusy = false;
                IsPaused = false;
                CopyCurrentFile = string.Empty;
                CopySpeedText = string.Empty;
                CopyEtaText = string.Empty;
                ProgressText = string.Empty;
                foreach (string line in failures) { Failures.Add(line); }
                RaiseResumeState();
                StartCopyCommand.RaiseCanExecuteChanged();
            }
        }

        // ---------- 拷贝 ----------

        private void CancelCopy()
        {
            if (_cancellation != null && !_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
                StatusText = "正在取消任务，已拷贝的文件会保留…";
            }
        }

        private void TogglePause()
        {
            if (_pauseToken.IsPaused)
            {
                _pauseToken.Resume();
                IsPaused = false;
            }
            else
            {
                _pauseToken.Pause();
                IsPaused = true;
            }
        }

        /// <summary>执行完整流程：规划 → 预检 → 拷贝 → 校验 → 写入清单。</summary>
        private async Task StartCopyAsync()
        {
            CardItemViewModel item = SelectedCard;
            ScanResult scan = _scanResult;
            if (item == null || scan == null) { return; }

            AppSettings settings = Services.Settings;
            var context = new ProjectContext
            {
                ShootDate = scan.EarliestUtc.HasValue ? scan.EarliestUtc.Value.ToLocalTime() : DateTime.Now,
                ShootType = string.IsNullOrWhiteSpace(ShootType) ? "其他" : ShootType.Trim(),
                Location = string.IsNullOrWhiteSpace(Location) ? "未标注地点" : Location.Trim(),
                Photographer = Photographer == null ? string.Empty : Photographer.Trim(),
                CameraModel = scan.CameraModel,
                CardLabel = item.Card.VolumeLabel
            };

            IsBusy = true;
            IsCopying = true;
            HasResult = false;
            Failures.Clear();
            ResultSummary = string.Empty;

            var failures = new List<string>();
            try
            {
                // 1. 校验分组名称、探测目标并按项目生成计划（全程 await，界面线程不阻塞）
                string validationError = ValidateGroups();
                if (validationError != null)
                {
                    StatusText = validationError;
                    HasResult = true;
                    ResultSummary = validationError;
                    return;
                }

                IList<ProjectGroupRequest> requests = BuildGroupRequests(scan);
                StatusText = string.Format("正在检查目标目录并生成 {0} 个项目的拷贝计划…", requests.Count);
                var planSetResult = await Services.Backup.BuildPlansAsync(
                    requests, scan.RootPath, settings, TargetProbe.DefaultTimeoutMs, CancellationToken.None);
                if (!planSetResult.Success)
                {
                    StatusText = planSetResult.Message;
                    return;
                }
                BackupPlanSet planSet = planSetResult.Value;
                IList<BackupUnit> units = planSet.Units;
                var plans = new List<CopyPlan>();
                foreach (BackupUnit eachUnit in units) { plans.Add(eachUnit.Plan); }
                _currentPlans = plans;
                CopyPlan plan = units[0].Plan;
                _currentContext = requests[0].Context;

                // 不可用目标（离线硬盘、超时的网络路径）如实列出，稍后汇总为“部分成功”
                foreach (TargetProbeResult unavailable in planSet.UnavailableTargets)
                {
                    failures.Add("目标不可用：" + unavailable.Root + " —— " + unavailable.Message);
                }

                // 2. 预检（单目标时直接拦截；多目标时由协调器逐个检查，副目标失败不影响主目标）
                // 空间与可写性由协调器按执行单元检查（目标可用性已在计划阶段探测过）
                foreach (BackupUnit eachUnit in units)
                {
                    foreach (string warning in eachUnit.Plan.Warnings) { failures.Add("提示：" + warning); }
                }

                // 任务开始即落一条记录，并写入断点续传计划文件（F12）
                BeginTaskRecord(plan, item, context);
                SavePlanFile(plans, item, context);

                // 3. 拷贝 + 4. 校验（多目标串行执行）
                _cancellation = new CancellationTokenSource();
                _pauseToken.Resume();
                IsPaused = false;
                StatusText = string.Format("正在拷贝 {0} 个文件（{1}），共 {2} 个目标…",
                    Humanize.Count(plan.PendingCount), Humanize.Bytes(plan.PendingBytes), plans.Count);

                var copyProgress = new Progress<CopyProgress>(delegate (CopyProgress value)
                {
                    CopyPercent = value.Ratio * 100;
                    CopySpeedText = Humanize.Speed(value.BytesPerSecond);
                    CopyEtaText = value.Remaining > TimeSpan.Zero ? "剩余 " + Humanize.Duration(value.Remaining) : "计算中…";
                    CopyCurrentFile = value.CurrentFile;
                    ProgressText = string.Format("{0}{1} / {2} 个文件 · {3} / {4} · {5}",
                        value.TargetCount > 1 ? "[" + value.TargetIndex + "/" + value.TargetCount + "] " : string.Empty,
                        Humanize.Count(value.CompletedFiles), Humanize.Count(value.TotalFiles),
                        Humanize.Bytes(value.CompletedBytes), Humanize.Bytes(value.TotalBytes), CopySpeedText);
                });

                BackupResult backup = await Services.Backup.RunAsync(
                    planSet, scan, settings, copyProgress, _pauseToken, _cancellation.Token);
                _currentBackup = backup;

                CopyResult copyResult = AggregateCopy(backup);
                VerifyReport report = AggregateVerify(backup);
                _currentCopyResult = copyResult;
                _currentVerifyReport = report;

                // 每个目标的失败与校验问题都要如实上报
                foreach (BackupUnit eachUnit in backup.Units)
                {
                    if (!eachUnit.Succeeded) { failures.Add("未完成：" + eachUnit.DisplayName + " —— " + eachUnit.Message); }
                    IList<CopyFailure> unitFailures = eachUnit.Copy == null ? new List<CopyFailure>() : eachUnit.Copy.Failures;
                    foreach (CopyFailure failure in unitFailures)
                    {
                        failures.Add("拷贝失败：" + failure.SourcePath + "（" + failure.Error + "）");
                    }
                    if (eachUnit.Verify != null)
                    {
                        foreach (FileVerifyFailure failure in eachUnit.Verify.Failures)
                        {
                            failures.Add("校验失败：" + failure.TargetPath + "（" + failure.Detail + "）");
                        }
                    }
                }

                // 记录本轮完成情况，便于断电/退出后续传
                SavePlanFile(plans, item, context);

                if (backup.Cancelled)
                {
                    StatusText = "任务已取消。已拷贝的文件保留在目标目录；下次启动可以直接“继续上次任务”，软件只会补拷未完成的文件。";
                    ResultSummary = "任务已取消";
                    HasResult = true;
                    return;
                }

                // 5. 写入清单与项目元数据（每个目标各写一份，保证任意一块盘都能自描述）
                var projectIds = new Dictionary<int, string>();
                var savedProjects = new HashSet<int>();
                foreach (BackupUnit eachUnit in backup.Units)
                {
                    if (eachUnit.Plan == null || eachUnit.Group == null) { continue; }

                    string projectId;
                    if (!projectIds.TryGetValue(eachUnit.ProjectIndex, out projectId))
                    {
                        projectId = ProjectMeta.NewId();
                        projectIds[eachUnit.ProjectIndex] = projectId;
                    }
                    _currentProjectId = projectId;

                    var writeResult = Services.ManifestWriter.WriteProjectFiles(
                        eachUnit.Plan, scan, eachUnit.Group.Context, settings,
                        eachUnit.Copy, eachUnit.Verify, item.Card.Identity, projectId,
                        eachUnit.Group.Layout, eachUnit.Group.RangeStart, eachUnit.Group.RangeEnd,
                        eachUnit.Group.Source);
                    if (!writeResult.Success)
                    {
                        failures.Add("清单写入失败（" + eachUnit.DisplayName + "）：" + writeResult.Message);
                    }

                    // 每个项目只在本地项目库写一条（取主目标单元）
                    if (eachUnit.IsPrimaryTarget && savedProjects.Add(eachUnit.ProjectIndex))
                    {
                        ProjectMeta meta = Services.ManifestWriter.BuildProjectMeta(
                            eachUnit.Plan, scan, eachUnit.Group.Context, eachUnit.Verify, item.Card.Identity, projectId,
                            eachUnit.Group.Layout, eachUnit.Group.RangeStart, eachUnit.Group.RangeEnd,
                            eachUnit.Group.Source);
                        Services.ProjectLibrary.Save(meta);
                    }
                }

                // 6. 记住地点，便于下次快速选择
                Services.Presets.RememberLocation(context.Location);
                if (!Locations.Contains(context.Location)) { Locations.Insert(0, context.Location); }

                // 6.5 覆盖率检查（BUG-001 兜底）：确认卡上不存在"本次没有纳入拷贝"的素材
                StatusText = "正在检查卡上是否还有未被拷贝的素材…";
                CoverageReport coverage = await Task.Run(delegate
                {
                    var copiedPaths = new List<string>();
                    foreach (BackupUnit eachUnit in units)
                    {
                        foreach (CopyItem planItem in eachUnit.Plan.Items)
                        {
                            if (!copiedPaths.Contains(planItem.Source.FullPath)) { copiedPaths.Add(planItem.Source.FullPath); }
                        }
                    }
                    return CoverageChecker.Check(item.Card.ScanRootPath, settings, copiedPaths, _log, CancellationToken.None);
                });
                _lastCoverage = coverage;
                if (!coverage.FullyCovered)
                {
                    failures.Add("⚠️ " + coverage.Message + "；已锁定格式化，请重新扫描后再次拷贝。");
                    foreach (string sample in coverage.Samples) { failures.Add("    未拷贝：" + sample); }
                }

                bool allTargetsOk = backup.AllSucceeded;
                LastVerifiedCardFingerprint = allTargetsOk && report.Level != VerifyLevel.None && coverage.FullyCovered
                    ? item.Card.Identity.Fingerprint
                    : null;

                // 任务全部完成：删除计划文件，避免下次误提示续传
                if (allTargetsOk && coverage.FullyCovered && _currentTaskId != null)
                {
                    Services.PlanStore.Delete(_currentTaskId);
                }

                ResultSummary = string.Format("{0}；{1}；拷贝 {2} 个文件（{3}），跳过 {4} 个；覆盖检查：{5}",
                    allTargetsOk ? "✅ 拷贝与校验全部通过" : "⚠️ 存在未完成的目标或校验问题",
                    backup.Summary,
                    Humanize.Count(backup.TotalCopiedFiles), Humanize.Bytes(backup.TotalCopiedBytes),
                    Humanize.Count(backup.TotalSkippedFiles),
                    coverage.Message);
                HasResult = true;
                StatusText = "项目目录：" + plan.ProjectDirectory;
                RefreshFormatState();
                Services.Notify(report.AllPassed ? "拷贝完成，校验通过" : "拷贝完成，但校验存在问题",
                    string.Format("{0}：{1} 个文件，{2}，耗时 {3}", plan.ProjectName,
                        Humanize.Count(copyResult.CopiedFiles), Humanize.Bytes(copyResult.CopiedBytes),
                        Humanize.Duration(copyResult.Duration)));
            }
            catch (Exception ex)
            {
                _log.Error("拷贝流程失败", ex);
                StatusText = "拷贝失败：" + ex.Message;
                failures.Add("异常：" + ex.Message);
                HasResult = true;
                ResultSummary = "任务失败";
            }
            finally
            {
                FinishTaskRecord();
                IsCopying = false;
                IsBusy = false;
                IsPaused = false;
                CopyCurrentFile = string.Empty;
                CopySpeedText = string.Empty;
                CopyEtaText = string.Empty;
                ProgressText = string.Empty;
                foreach (string line in failures) { Failures.Add(line); }
                StartCopyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // 项目库页与历史页的视图模型位于 ProjectPages.cs

    /// <summary>设置页视图模型：可编辑全部常用设置并保存。</summary>
    public sealed class SettingsViewModel : PageViewModelBase
    {
        private string _primaryTargetRoot;
        private string _secondaryTargetRoot;
        private string _nameTemplate;
        private SubFolderMode _subFolderMode;
        private ConflictPolicy _conflictPolicy;
        private int _maxParallelism;
        private VerifyLevel _verifyLevel;
        private bool _verifyVideo;
        private bool _scanWholeCardRoot;
        private bool _autoFormatAfterVerify;
        private int _autoFormatCountdownSec;
        private ThemeMode _theme;
        private bool _minimizeToTray;
        private bool _autoStart;
        private int _logRetentionDays;
        private bool _writeSelfDescribingFiles;
        private bool _isCleaningMetadata;
        private string _saveMessage = string.Empty;

        public SettingsViewModel(AppServices services)
            : base(services, "设置", "目标目录、命名模板、拷贝与校验策略、界面主题等。修改后点击保存生效。")
        {
            AppSettings settings = services.Settings;

            SubFolderModeOptions = new List<KeyValuePair<SubFolderMode, string>>
            {
                new KeyValuePair<SubFolderMode, string>(SubFolderMode.None, "不分类（全部放在项目根目录）"),
                new KeyValuePair<SubFolderMode, string>(SubFolderMode.ByMediaType, "按类型：照片 / 视频 / 其他"),
                new KeyValuePair<SubFolderMode, string>(SubFolderMode.ByMediaTypeAndFormat, "按类型与格式：照片再分 RAW / JPG")
            };
            ConflictPolicyOptions = new List<KeyValuePair<ConflictPolicy, string>>
            {
                new KeyValuePair<ConflictPolicy, string>(ConflictPolicy.AutoRenameDir, "自动追加序号（推荐）"),
                new KeyValuePair<ConflictPolicy, string>(ConflictPolicy.Merge, "合并到已有目录")
            };
            VerifyLevelOptions = new List<KeyValuePair<VerifyLevel, string>>
            {
                new KeyValuePair<VerifyLevel, string>(VerifyLevel.None, "不校验（最快）"),
                new KeyValuePair<VerifyLevel, string>(VerifyLevel.SizeOnly, "仅比较大小"),
                new KeyValuePair<VerifyLevel, string>(VerifyLevel.SizeAndSample, "大小 + 抽样哈希（推荐）"),
                new KeyValuePair<VerifyLevel, string>(VerifyLevel.Crc32, "CRC32 全量"),
                new KeyValuePair<VerifyLevel, string>(VerifyLevel.Sha256, "SHA-256 全量（最严格）")
            };
            ThemeOptions = new List<KeyValuePair<ThemeMode, string>>
            {
                new KeyValuePair<ThemeMode, string>(ThemeMode.Light, "浅色"),
                new KeyValuePair<ThemeMode, string>(ThemeMode.Dark, "深色"),
                new KeyValuePair<ThemeMode, string>(ThemeMode.System, "跟随系统")
            };
            ParallelismOptions = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 };

            _primaryTargetRoot = settings.PrimaryTargetRoot;
            _secondaryTargetRoot = settings.SecondaryTargetRoot;
            _nameTemplate = settings.NameTemplate;
            _subFolderMode = settings.SubFolderMode;
            _conflictPolicy = settings.ConflictPolicy;
            _maxParallelism = settings.MaxParallelism;
            _verifyLevel = settings.VerifyLevel;
            _verifyVideo = settings.VerifyVideo;
            _scanWholeCardRoot = settings.ScanWholeCardRoot;
            _autoFormatAfterVerify = settings.AutoFormatAfterVerify;
            _autoFormatCountdownSec = settings.AutoFormatCountdownSec;
            _theme = settings.Theme;
            _minimizeToTray = settings.MinimizeToTray;
            _autoStart = settings.AutoStart;
            _logRetentionDays = settings.LogRetentionDays;
            _writeSelfDescribingFiles = settings.WriteSelfDescribingFiles;

            SaveCommand = new RelayCommand(Save);
            BrowseTargetCommand = new RelayCommand(BrowseTarget);
            OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
            CleanLegacyMetadataCommand = new AsyncRelayCommand(
                CleanLegacyMetadataAsync, delegate { return !_isCleaningMetadata; });
        }

        public string PrimaryTargetRoot
        {
            get { return _primaryTargetRoot; }
            set { SetProperty(ref _primaryTargetRoot, value); }
        }

        public string SecondaryTargetRoot
        {
            get { return _secondaryTargetRoot; }
            set { SetProperty(ref _secondaryTargetRoot, value); }
        }

        public string NameTemplate
        {
            get { return _nameTemplate; }
            set { SetProperty(ref _nameTemplate, value); }
        }

        public SubFolderMode SelectedSubFolderMode
        {
            get { return _subFolderMode; }
            set { SetProperty(ref _subFolderMode, value); }
        }

        public ConflictPolicy SelectedConflictPolicy
        {
            get { return _conflictPolicy; }
            set { SetProperty(ref _conflictPolicy, value); }
        }

        public int MaxParallelism
        {
            get { return _maxParallelism; }
            set { SetProperty(ref _maxParallelism, value); }
        }

        public VerifyLevel SelectedVerifyLevel
        {
            get { return _verifyLevel; }
            set { SetProperty(ref _verifyLevel, value); }
        }

        public bool VerifyVideo
        {
            get { return _verifyVideo; }
            set { SetProperty(ref _verifyVideo, value); }
        }

        /// <summary>是否扫描卡根下的其它目录（修复 BUG-001 的开关，默认开启）。</summary>
        public bool ScanWholeCardRoot
        {
            get { return _scanWholeCardRoot; }
            set { SetProperty(ref _scanWholeCardRoot, value); }
        }

        public bool AutoFormatAfterVerify
        {
            get { return _autoFormatAfterVerify; }
            set { SetProperty(ref _autoFormatAfterVerify, value); }
        }

        public int AutoFormatCountdownSec
        {
            get { return _autoFormatCountdownSec; }
            set { SetProperty(ref _autoFormatCountdownSec, value); }
        }

        public ThemeMode SelectedTheme
        {
            get { return _theme; }
            set
            {
                if (SetProperty(ref _theme, value))
                {
                    // 主题即时预览，无需保存
                    Services.ApplyTheme(value);
                }
            }
        }

        public bool MinimizeToTray
        {
            get { return _minimizeToTray; }
            set { SetProperty(ref _minimizeToTray, value); }
        }

        public bool AutoStart
        {
            get { return _autoStart; }
            set { SetProperty(ref _autoStart, value); }
        }

        public int LogRetentionDays
        {
            get { return _logRetentionDays; }
            set { SetProperty(ref _logRetentionDays, value); }
        }

        /// <summary>保存结果提示。</summary>
        public string SaveMessage
        {
            get { return _saveMessage; }
            private set { SetProperty(ref _saveMessage, value); }
        }

        public IList<KeyValuePair<SubFolderMode, string>> SubFolderModeOptions { get; private set; }
        public IList<KeyValuePair<ConflictPolicy, string>> ConflictPolicyOptions { get; private set; }
        public IList<KeyValuePair<VerifyLevel, string>> VerifyLevelOptions { get; private set; }
        public IList<KeyValuePair<ThemeMode, string>> ThemeOptions { get; private set; }
        public IList<int> ParallelismOptions { get; private set; }

        /// <summary>数据目录（只读展示）。</summary>
        public string DataDirectory { get { return Services.DataDirectory; } }

        /// <summary>配置文件路径（只读展示）。</summary>
        public string SettingsPath { get { return Services.SettingsStore.SettingsPath; } }

        public RelayCommand SaveCommand { get; private set; }
        public RelayCommand BrowseTargetCommand { get; private set; }
        public RelayCommand OpenDataFolderCommand { get; private set; }

        /// <summary>清理旧版元数据（F14-C）：预览 → 二次确认 → 只删软件自己的三个文件名。</summary>
        public AsyncRelayCommand CleanLegacyMetadataCommand { get; private set; }

        /// <summary>是否在素材目录额外写自描述文件（默认关闭，素材目录保持纯净）。</summary>
        public bool WriteSelfDescribingFiles
        {
            get { return _writeSelfDescribingFiles; }
            set { SetProperty(ref _writeSelfDescribingFiles, value); }
        }

        private void Save()
        {
            AppSettings settings = Services.Settings;
            settings.PrimaryTargetRoot = (PrimaryTargetRoot ?? string.Empty).Trim();
            settings.SecondaryTargetRoot = (SecondaryTargetRoot ?? string.Empty).Trim();
            settings.NameTemplate = string.IsNullOrWhiteSpace(NameTemplate) ? NameTemplateEngine.DefaultTemplate : NameTemplate.Trim();
            settings.SubFolderMode = SelectedSubFolderMode;
            settings.ConflictPolicy = SelectedConflictPolicy;
            settings.MaxParallelism = MaxParallelism;
            settings.VerifyLevel = SelectedVerifyLevel;
            settings.VerifyVideo = VerifyVideo;
            settings.ScanWholeCardRoot = ScanWholeCardRoot;
            settings.AutoFormatAfterVerify = AutoFormatAfterVerify;
            settings.AutoFormatCountdownSec = AutoFormatCountdownSec;
            settings.Theme = SelectedTheme;
            settings.MinimizeToTray = MinimizeToTray;
            settings.AutoStart = AutoStart;
            settings.LogRetentionDays = LogRetentionDays;
            settings.WriteSelfDescribingFiles = WriteSelfDescribingFiles;

            OperationResult result = Services.SaveSettings();
            SaveMessage = result.Success ? "已保存（" + TimeUtil.Display(DateTime.Now) + "）" : result.Message;
        }

        private void BrowseTarget()
        {
            try
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "选择素材存放的目标根目录（可以是网络位置）";
                    dialog.ShowNewFolderButton = true;
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        PrimaryTargetRoot = dialog.SelectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                SaveMessage = "选择目录失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 清理旧版元数据（F14-C）：先选目录 → 后台扫描预览 → 弹窗二次确认 → 后台删除。
        /// 只删除软件自己写过的 _manifest.json / _project.json / _copy.log，不碰其它文件。
        /// </summary>
        private async Task CleanLegacyMetadataAsync()
        {
            string root;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择要清理旧版元数据的素材库根目录（只会删除 _manifest.json、_project.json、_copy.log）";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) { return; }
                root = dialog.SelectedPath;
            }

            _isCleaningMetadata = true;
            CleanLegacyMetadataCommand.RaiseCanExecuteChanged();
            try
            {
                SaveMessage = "正在扫描旧版元数据…";
                CleanupReport preview = await Task.Run(delegate { return LegacyMetadataCleaner.Preview(root); });
                if (preview.Found == 0)
                {
                    SaveMessage = "没有发现旧版元数据文件（" + root + "）";
                    return;
                }

                string message = string.Format(
                    "在所选目录下扫描到 {0} 个旧版元数据文件（共 {1} 个目录）。{2}{2}" +
                    "只会删除以下三个文件名：{2}_manifest.json、_project.json、_copy.log{2}{2}" +
                    "例如：{2}{3}{2}{2}确定删除吗？此操作不可撤销。",
                    preview.Found, preview.Directories, Environment.NewLine,
                    string.Join(Environment.NewLine, preview.Samples));

                MessageBoxResult answer = MessageBox.Show(message, "清理旧版元数据（二次确认）",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (answer != MessageBoxResult.OK)
                {
                    SaveMessage = "已取消清理，未删除任何文件。";
                    return;
                }

                SaveMessage = "正在删除旧版元数据…";
                CleanupReport cleaned = await Task.Run(delegate { return LegacyMetadataCleaner.Clean(root); });
                SaveMessage = cleaned.Summary +
                              (cleaned.Failures.Count > 0 ? "；首个失败：" + cleaned.Failures[0] : string.Empty);
            }
            catch (Exception ex)
            {
                SaveMessage = "清理失败：" + ex.Message;
            }
            finally
            {
                _isCleaningMetadata = false;
                CleanLegacyMetadataCommand.RaiseCanExecuteChanged();
            }
        }

        private void OpenDataFolder()
        {            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Services.DataDirectory + "\"");
            }
            catch (Exception ex)
            {
                SaveMessage = "打开目录失败：" + ex.Message;
            }
        }
    }

    /// <summary>关于页：显示版本与运行环境信息。</summary>
    public sealed class AboutViewModel : PageViewModelBase
    {
        public AboutViewModel(AppServices services)
            : base(services, "关于", "运行环境信息，反馈问题时请附上这一页的内容。")
        {
            Items = new ObservableCollection<KeyValuePair<string, string>>();
            Items.Add(new KeyValuePair<string, string>("程序版本", AppServices.VersionText));
            Items.Add(new KeyValuePair<string, string>("数据目录", services.DataDirectory));
            Items.Add(new KeyValuePair<string, string>("日志目录", AppPaths.LogDirectory(services.DataDirectory)));
            Items.Add(new KeyValuePair<string, string>("配置文件", services.SettingsStore.SettingsPath));
            Items.Add(new KeyValuePair<string, string>("运行框架", ".NET Framework " + Environment.Version));
            Items.Add(new KeyValuePair<string, string>("系统架构", Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
            Items.Add(new KeyValuePair<string, string>("操作系统", Environment.OSVersion.VersionString));
        }

        /// <summary>环境信息条目。</summary>
        public ObservableCollection<KeyValuePair<string, string>> Items { get; private set; }
    }
}
