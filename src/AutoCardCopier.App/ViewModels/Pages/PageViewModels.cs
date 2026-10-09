using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using AutoCardCopier.Core.Infrastructure;

namespace AutoCardCopier.App.ViewModels.Pages
{
    /// <summary>页面视图模型基类：统一提供标题与说明文字。</summary>
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

    /// <summary>卡片页：M2 里程碑实现卡片识别与扫描。</summary>
    public sealed class CardsViewModel : PageViewModelBase
    {
        public CardsViewModel(AppServices services)
            : base(services, "卡片", "插入内存卡后，这里会显示卡片信息、素材统计与拷贝入口。")
        {
            StatusText = "正在等待卡片接入…（卡片识别功能将在 M2 里程碑完成）";
        }

        /// <summary>当前状态提示。</summary>
        public string StatusText { get; private set; }
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
            Items = new ObservableCollection<KeyValuePair<string, string>>();
            Items.Add(new KeyValuePair<string, string>("程序版本", AppServices.VersionText));
            Items.Add(new KeyValuePair<string, string>("数据目录", services.DataDirectory));
            Items.Add(new KeyValuePair<string, string>("日志目录", AppPaths.LogDirectory(services.DataDirectory)));
            Items.Add(new KeyValuePair<string, string>("配置文件", services.SettingsStore.SettingsPath));
            Items.Add(new KeyValuePair<string, string>("运行框架", FrameworkDescription()));
            Items.Add(new KeyValuePair<string, string>("系统架构", System.Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
            Items.Add(new KeyValuePair<string, string>("操作系统", System.Environment.OSVersion.VersionString));
            Items.Add(new KeyValuePair<string, string>("当前目录", Directory.GetCurrentDirectory()));
        }

        /// <summary>环境信息条目。</summary>
        public ObservableCollection<KeyValuePair<string, string>> Items { get; private set; }

        private static string FrameworkDescription()
        {
            return ".NET Framework " + System.Environment.Version;
        }
    }
}
