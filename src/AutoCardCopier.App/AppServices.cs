using System;
using System.Windows;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.Devices;
using AutoCardCopier.Core.History;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;
using Microsoft.Win32;

namespace AutoCardCopier.App
{
    /// <summary>
    /// 应用级服务容器。
    /// 项目规模不大，直接用这个轻量容器手动组装依赖，避免引入第三方 DI 框架。
    /// </summary>
    public sealed class AppServices
    {
        private AppServices() { }

        /// <summary>当前设置。</summary>
        public AppSettings Settings { get; private set; }

        /// <summary>设置存取器。</summary>
        public SettingsStore SettingsStore { get; private set; }

        /// <summary>文件日志。</summary>
        public FileLogger Logger { get; private set; }

        /// <summary>数据目录（配置、项目库、日志的根）。</summary>
        public string DataDirectory { get; private set; }

        /// <summary>事件总线。</summary>
        public EventBus Events { get; private set; }

        /// <summary>卡片检测器。</summary>
        public CardDetector CardDetector { get; private set; }

        /// <summary>素材扫描器。</summary>
        public MediaScanner Scanner { get; private set; }

        /// <summary>拷贝计划生成器。</summary>
        public CopyPlanBuilder PlanBuilder { get; private set; }

        /// <summary>并行拷贝引擎。</summary>
        public CopyEngine CopyEngine { get; private set; }

        /// <summary>清单与项目元数据写入器。</summary>
        public ManifestWriter ManifestWriter { get; private set; }

        /// <summary>预设（常用地点与拍摄类型）。</summary>
        public PresetsStore Presets { get; private set; }

        /// <summary>本地项目库。</summary>
        public ProjectLibrary ProjectLibrary { get; private set; }

        /// <summary>任务历史。</summary>
        public TaskHistoryStore TaskHistory { get; private set; }

        /// <summary>程序版本号。</summary>
        public static string VersionText
        {
            get
            {
                Version version = typeof(AppServices).Assembly.GetName().Version;
                return string.Format("{0}.{1}.{2}", version.Major, version.Minor, version.Build);
            }
        }

        /// <summary>
        /// 创建服务容器：先确定数据目录，再加载设置、初始化日志。
        /// 顺序很重要——日志目录依赖数据目录，而设置本身又需要从数据目录读取。
        /// </summary>
        public static AppServices Create()
        {
            var services = new AppServices();
            services.Events = new EventBus();

            // 第一步：用默认路径尝试读取设置（此时还不知道最终数据目录）
            string bootstrapDirectory = AppPaths.DefaultDataDirectory;
            var bootstrapLog = NullLog.Instance;
            var bootstrapStore = new SettingsStore(AppPaths.SettingsFile(bootstrapDirectory), bootstrapLog);
            AppSettings probe = bootstrapStore.Load();

            // 第二步：确定真正可用的数据目录
            services.DataDirectory = AppPaths.ResolveDataDirectory(probe, NullLog.Instance);

            // 第三步：正式加载设置与日志
            services.Logger = new FileLogger(AppPaths.LogDirectory(services.DataDirectory), "app", includeDebug: true);
            services.SettingsStore = new SettingsStore(AppPaths.SettingsFile(services.DataDirectory), services.Logger);
            services.Settings = services.SettingsStore.Load();
            services.Settings.Normalize(services.Logger);
            services.Logger.Info("程序启动，版本 " + VersionText + "，数据目录 " + services.DataDirectory);
            services.Logger.CleanupOldFiles(services.Settings.LogRetentionDays);

            // 第四步：创建领域服务
            services.Scanner = new MediaScanner(services.Logger);
            services.CardDetector = new CardDetector(services.Logger, pollSeconds: 3);
            services.PlanBuilder = new CopyPlanBuilder(services.Logger);
            services.CopyEngine = new CopyEngine(services.Logger);
            services.ManifestWriter = new ManifestWriter(services.Logger);
            services.Presets = new PresetsStore(AppPaths.PresetsFile(services.DataDirectory), services.Logger);
            services.ProjectLibrary = new ProjectLibrary(AppPaths.ProjectsDirectory(services.DataDirectory), services.Logger);
            services.TaskHistory = new TaskHistoryStore(AppPaths.HistoryFile(services.DataDirectory), services.Logger);

            return services;
        }

        /// <summary>保存设置。</summary>
        public OperationResult SaveSettings()
        {
            return SettingsStore.Save(Settings);
        }

        /// <summary>按主题模式切换配色资源字典。</summary>
        public void ApplyTheme(ThemeMode mode)
        {
            bool dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && IsSystemDarkTheme());
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml", UriKind.Relative)
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > 0) { merged[0] = dictionary; }
            else { merged.Add(dictionary); }
        }

        /// <summary>读取注册表判断系统是否使用深色主题。</summary>
        private static bool IsSystemDarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AppsUseLightTheme");
                        if (value is int) { return (int)value == 0; }
                    }
                }
            }
            catch (Exception)
            {
                // 读不到就按浅色处理
            }
            return false;
        }

        /// <summary>释放资源。</summary>
        public void Shutdown()
        {
            if (CardDetector != null)
            {
                CardDetector.Dispose();
                CardDetector = null;
            }
            if (Logger != null)
            {
                Logger.Info("程序退出");
                Logger.Dispose();
                Logger = null;
            }
        }
    }
}
