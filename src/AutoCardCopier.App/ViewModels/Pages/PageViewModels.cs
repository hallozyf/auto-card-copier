using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

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

            RefreshCommand = new RelayCommand(RefreshCards);
            ScanCommand = new AsyncRelayCommand(ScanSelectedCardAsync, delegate { return !IsBusy && SelectedCard != null; });
            StartCopyCommand = new AsyncRelayCommand(StartCopyAsync, delegate { return !IsBusy && CanStartCopy; });
            CancelCopyCommand = new RelayCommand(CancelCopy, delegate { return IsCopying; });
            PauseResumeCommand = new RelayCommand(TogglePause, delegate { return IsCopying; });

            services.CardDetector.CardsChanged += OnCardsChanged;
            services.CardDetector.Start();
            RefreshCards();
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

        // ---------- 命令 ----------

        public RelayCommand RefreshCommand { get; private set; }
        public AsyncRelayCommand ScanCommand { get; private set; }
        public AsyncRelayCommand StartCopyCommand { get; private set; }
        public RelayCommand CancelCopyCommand { get; private set; }
        public RelayCommand PauseResumeCommand { get; private set; }

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
                string root = string.IsNullOrEmpty(item.Card.MediaRootPath) ? item.Card.RootPath : item.Card.MediaRootPath;
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
            RaisePropertyChanged("TargetPreviewText");
            RaisePropertyChanged("CanStartCopy");
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
                // 1. 生成计划
                StatusText = "正在生成拷贝计划…";
                var planResult = Services.PlanBuilder.Build(scan, settings, context, settings.PrimaryTargetRoot);
                if (!planResult.Success)
                {
                    StatusText = planResult.Message;
                    return;
                }
                CopyPlan plan = planResult.Value;

                // 2. 预检
                PlanPreflight preflight = Services.PlanBuilder.Preflight(plan);
                if (!preflight.CanProceed)
                {
                    StatusText = preflight.Message;
                    return;
                }
                foreach (string warning in plan.Warnings) { failures.Add("提示：" + warning); }

                // 3. 拷贝
                _cancellation = new CancellationTokenSource();
                _pauseToken.Resume();
                IsPaused = false;
                StatusText = "正在拷贝 " + Humanize.Count(plan.PendingCount) + " 个文件（" + Humanize.Bytes(plan.PendingBytes) + "）…";

                var copyProgress = new Progress<CopyProgress>(delegate (CopyProgress value)
                {
                    CopyPercent = value.Ratio * 100;
                    CopySpeedText = Humanize.Speed(value.BytesPerSecond);
                    CopyEtaText = value.Remaining > TimeSpan.Zero ? "剩余 " + Humanize.Duration(value.Remaining) : "计算中…";
                    CopyCurrentFile = value.CurrentFile;
                    ProgressText = string.Format("{0} / {1} 个文件 · {2} / {3} · {4}",
                        Humanize.Count(value.CompletedFiles), Humanize.Count(value.TotalFiles),
                        Humanize.Bytes(value.CompletedBytes), Humanize.Bytes(value.TotalBytes), CopySpeedText);
                });

                CopyResult copyResult = await Services.CopyEngine.RunAsync(
                    plan, settings, copyProgress, _pauseToken, _cancellation.Token);

                foreach (CopyFailure failure in copyResult.Failures)
                {
                    failures.Add("拷贝失败：" + failure.SourcePath + "（" + failure.Error + "）");
                }

                if (copyResult.Cancelled)
                {
                    StatusText = "任务已取消。已拷贝的文件保留在目标目录，可重新扫描后再次拷贝（会自动跳过已完成的文件）。";
                    ResultSummary = "任务已取消";
                    HasResult = true;
                    return;
                }

                // 4. 校验
                StatusText = "正在校验数据完整性（" + settings.VerifyLevel + "）…";
                var verifyProgress = new Progress<VerifyProgress>(delegate (VerifyProgress value)
                {
                    ProgressText = string.Format("校验中 {0} / {1}", Humanize.Count(value.CompletedFiles), Humanize.Count(value.TotalFiles));
                });

                VerifyReport report = await Task.Run(delegate
                {
                    return VerifyService.Verify(plan, settings, verifyProgress, CancellationToken.None, _log);
                });

                foreach (FileVerifyFailure failure in report.Failures)
                {
                    failures.Add("校验失败：" + failure.TargetPath + "（" + failure.Detail + "）");
                }

                // 5. 写入清单与项目元数据
                string projectId = ProjectMeta.NewId();
                var writeResult = Services.ManifestWriter.WriteProjectFiles(
                    plan, scan, context, settings, copyResult, report, item.Card.Identity, projectId);
                if (!writeResult.Success) { failures.Add("清单写入失败：" + writeResult.Message); }

                // 6. 记住地点，便于下次快速选择
                Services.Presets.RememberLocation(context.Location);
                if (!Locations.Contains(context.Location)) { Locations.Insert(0, context.Location); }

                LastVerifiedCardFingerprint = report.AllPassed && report.Level != VerifyLevel.None
                    ? item.Card.Identity.Fingerprint
                    : null;

                ResultSummary = string.Format("{0}；拷贝 {1} 个文件（{2}），耗时 {3}，平均 {4}；{5}",
                    report.AllPassed ? "✅ 拷贝与校验全部通过" : "⚠️ 校验存在问题",
                    Humanize.Count(copyResult.CopiedFiles), Humanize.Bytes(copyResult.CopiedBytes),
                    Humanize.Duration(copyResult.Duration), Humanize.Speed(copyResult.AverageBytesPerSecond),
                    report.Summary);
                HasResult = true;
                StatusText = "项目目录：" + plan.ProjectDirectory;
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

    /// <summary>项目库页：M4 里程碑接入数据。</summary>
    public sealed class ProjectsViewModel : PageViewModelBase
    {
        public ProjectsViewModel(AppServices services)
            : base(services, "项目库", "按拍摄时间、地点、类型检索历史项目。")
        {
            StatusText = "项目库将在 M4 里程碑接入数据。";
        }

        /// <summary>当前状态提示。</summary>
        public string StatusText { get; private set; }
    }

    /// <summary>历史页：M4 里程碑接入数据。</summary>
    public sealed class HistoryViewModel : PageViewModelBase
    {
        public HistoryViewModel(AppServices services)
            : base(services, "历史", "查看每一次拷卡任务的结果、耗时与失败明细。")
        {
            StatusText = "任务历史将在 M4 里程碑接入数据。";
        }

        /// <summary>当前状态提示。</summary>
        public string StatusText { get; private set; }
    }

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
        private bool _autoFormatAfterVerify;
        private int _autoFormatCountdownSec;
        private ThemeMode _theme;
        private bool _minimizeToTray;
        private bool _autoStart;
        private int _logRetentionDays;
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
            _autoFormatAfterVerify = settings.AutoFormatAfterVerify;
            _autoFormatCountdownSec = settings.AutoFormatCountdownSec;
            _theme = settings.Theme;
            _minimizeToTray = settings.MinimizeToTray;
            _autoStart = settings.AutoStart;
            _logRetentionDays = settings.LogRetentionDays;

            SaveCommand = new RelayCommand(Save);
            BrowseTargetCommand = new RelayCommand(BrowseTarget);
            OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
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
            settings.AutoFormatAfterVerify = AutoFormatAfterVerify;
            settings.AutoFormatCountdownSec = AutoFormatCountdownSec;
            settings.Theme = SelectedTheme;
            settings.MinimizeToTray = MinimizeToTray;
            settings.AutoStart = AutoStart;
            settings.LogRetentionDays = LogRetentionDays;

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

        private void OpenDataFolder()
        {
            try
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
