using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
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
        public CardItemViewModel(CardVolume card)
        {
            Card = card;
        }

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
    /// 卡片页视图模型：负责卡片列表刷新、素材扫描与扫描结果展示。
    /// 拷贝相关操作在 M3 里程碑接入。
    /// </summary>
    public sealed class CardsViewModel : PageViewModelBase
    {
        private readonly ILog _log;
        private CardItemViewModel _selectedCard;
        private string _statusText = "正在检测存储卡…";
        private bool _isBusy;
        private string _progressText = string.Empty;
        private ScanResult _scanResult;

        public CardsViewModel(AppServices services)
            : base(services, "卡片", "插入内存卡后自动识别并扫描素材，确认信息后即可开始拷贝。")
        {
            _log = services.Logger;
            Cards = new ObservableCollection<CardItemViewModel>();

            RefreshCommand = new RelayCommand(delegate { RefreshCards(); });
            ScanCommand = new AsyncRelayCommand(ScanSelectedCardAsync, delegate { return !IsBusy && SelectedCard != null; });

            services.CardDetector.CardsChanged += OnCardsChanged;
            services.CardDetector.Start();
            RefreshCards();
        }

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
                    RaisePropertyChanged("TargetHintText");
                    ScanCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>是否已选中卡片。</summary>
        public bool HasSelectedCard { get { return _selectedCard != null; } }

        /// <summary>是否有任务正在执行（扫描中）。</summary>
        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaisePropertyChanged("IsIdle");
                    ScanCommand.RaiseCanExecuteChanged();
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

        /// <summary>刷新列表命令。</summary>
        public RelayCommand RefreshCommand { get; private set; }

        /// <summary>扫描命令。</summary>
        public AsyncRelayCommand ScanCommand { get; private set; }

        // ---------- 扫描结果展示 ----------

        /// <summary>是否有扫描结果。</summary>
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
                DateTime start = _scanResult.EarliestUtc.Value.ToLocalTime();
                DateTime end = _scanResult.LatestUtc.Value.ToLocalTime();
                return TimeUtil.Display(start) + " ~ " + TimeUtil.Display(end);
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

        public string ContentSummaryText
        {
            get { return _scanResult == null ? "-" : _scanResult.ContentSummary; }
        }

        public string SkippedText
        {
            get { return _scanResult == null ? "-" : Humanize.Count(_scanResult.SkippedCount) + " 个（0 字节或系统文件）"; }
        }

        /// <summary>当前扫描结果（供后续拷贝流程使用）。</summary>
        public ScanResult ScanResult { get { return _scanResult; } }

        /// <summary>目标目录提示（M3 中会替换为真实路径预览）。</summary>
        public string TargetHintText
        {
            get
            {
                if (SelectedCard == null) { return "请先选择一张存储卡"; }
                string root = Services.Settings.PrimaryTargetRoot;
                if (string.IsNullOrWhiteSpace(root)) { return "尚未设置素材目标目录（可在设置页指定）"; }
                return "素材将拷贝到：" + root;
            }
        }

        /// <summary>卡片列表变化：切回界面线程更新绑定。</summary>
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
            foreach (CardVolume card in cards)
            {
                Cards.Add(new CardItemViewModel(card));
            }

            // 尽量保留原来的选择
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
            else if (StatusText.StartsWith("尚未检测到", StringComparison.Ordinal) || StatusText.StartsWith("正在检测", StringComparison.Ordinal))
            {
                StatusText = "已检测到 " + Cards.Count + " 张存储卡。点击“扫描素材”开始统计。";
            }
            else if (autoScanNewCard && restored != null && ScanCommand.CanExecute(null))
            {
                // 新插入的卡片自动扫描一次，减少一次点击
                ScanCommand.Execute(null);
            }
        }

        /// <summary>扫描当前选中的卡片。</summary>
        private async Task ScanSelectedCardAsync()
        {
            CardItemViewModel item = SelectedCard;
            if (item == null) { return; }

            IsBusy = true;
            ProgressText = "正在扫描素材…";
            StatusText = "正在扫描 " + item.DisplayName + " 的素材…";

            try
            {
                string root = item.Card.MediaRootPath;
                if (string.IsNullOrEmpty(root)) { root = item.Card.RootPath; }

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
                RaiseScanResultChanged();
                StatusText = string.Format("扫描完成：{0}，共 {1}。", result.ContentSummary,
                    Humanize.Bytes(result.TotalBytes));
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
            }
        }

        /// <summary>统一通知扫描结果相关属性已变化。</summary>
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
        }
    }

    /// <summary>项目库页：M4、M5 里程碑实现。</summary>
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

    /// <summary>历史页：M4 里程碑实现。</summary>
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

    /// <summary>设置页：M6 里程碑实现完整设置项。</summary>
    public sealed class SettingsViewModel : PageViewModelBase
    {
        public SettingsViewModel(AppServices services)
            : base(services, "设置", "目标目录、命名模板、拷贝与校验策略、界面主题等。")
        {
            StatusText = "完整设置项将在 M6 里程碑开放；当前数据目录与配置文件位置如下。";
            SettingsPath = Services.SettingsStore.SettingsPath;
            DataDirectory = Services.DataDirectory;
        }

        /// <summary>当前状态提示。</summary>
        public string StatusText { get; private set; }

        /// <summary>配置文件路径。</summary>
        public string SettingsPath { get; private set; }

        /// <summary>数据目录。</summary>
        public string DataDirectory { get; private set; }
    }

    /// <summary>关于页：显示版本与运行环境信息，便于排查问题。</summary>
    public sealed class AboutViewModel : PageViewModelBase
    {
        public AboutViewModel(AppServices services)
            : base(services, "关于", "运行环境信息，反馈问题时请附上这一页的内容。")
        {
            Items = new ObservableCollection<System.Collections.Generic.KeyValuePair<string, string>>();
            Items.Add(new System.Collections.Generic.KeyValuePair<string, string>("程序版本", AppServices.VersionText));
            Items.Add(new System.Collections.Generic.KeyValuePair<string, string>("数据目录", services.DataDirectory));
            Items.Add(new System.Collections.Generic.KeyValuePair<string, string>("日志目录", AppPaths.LogDirectory(services.DataDirectory)));
            Items.Add(new System.Collections.Generic.KeyValuePair<string, string>("配置文件", services.SettingsStore.SettingsPath));
            Items.Add(new System.Collections.Generic.KeyValuePair<string, string>("运行框架", ".NET Framework " + Environment.Version));
            Items.Add(new System.Collections.Generic.KeyValuePair<string, string>("系统架构", Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
            Items.Add(new System.Collections.Generic.KeyValuePair<string, string>("操作系统", Environment.OSVersion.VersionString));
        }

        /// <summary>环境信息条目。</summary>
        public ObservableCollection<System.Collections.Generic.KeyValuePair<string, string>> Items { get; private set; }
    }
}
