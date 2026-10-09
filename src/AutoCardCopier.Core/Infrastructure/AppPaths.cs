using System;
using System.IO;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// 程序运行目录规划：数据目录、日志目录、项目库目录等。
    /// 之所以放在 Core 而不是界面层，是因为服务层也需要知道这些位置。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>程序名称（用于目录命名）。</summary>
        public const string AppFolderName = "AutoCardCopier";

        /// <summary>默认数据目录（%APPDATA% 下）。</summary>
        public static string DefaultDataDirectory
        {
            get
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(roaming, AppFolderName);
            }
        }

        /// <summary>程序所在目录。</summary>
        public static string ExecutableDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
        }

        /// <summary>便携模式的数据目录（程序目录下的 data）。</summary>
        public static string PortableDataDirectory
        {
            get { return Path.Combine(ExecutableDirectory, "data"); }
        }

        /// <summary>
        /// 决定实际使用的数据目录。
        /// 顺序：设置中指定 → 默认 %APPDATA% → 程序目录下的 data（%APPDATA% 不可写时）。
        /// </summary>
        public static string ResolveDataDirectory(AppSettings settings, ILog log)
        {
            var candidates = new System.Collections.Generic.List<string>();
            if (settings != null && !string.IsNullOrWhiteSpace(settings.DataDirectory))
            {
                candidates.Add(settings.DataDirectory);
            }
            candidates.Add(DefaultDataDirectory);
            candidates.Add(PortableDataDirectory);

            foreach (string candidate in candidates)
            {
                if (IsWritable(candidate))
                {
                    if (log != null) { log.Info("数据目录：" + candidate); }
                    return candidate;
                }
            }

            // 全部候选都不可写时退回系统临时目录，保证程序仍能启动
            string fallback = Path.Combine(Path.GetTempPath(), AppFolderName);
            if (log != null) { log.Warn("所有候选数据目录都不可写，回退到临时目录：" + fallback); }
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        /// <summary>日志目录。</summary>
        public static string LogDirectory(string dataDirectory) { return Path.Combine(dataDirectory, "logs"); }

        /// <summary>任务流水日志目录。</summary>
        public static string TaskLogDirectory(string dataDirectory) { return Path.Combine(LogDirectory(dataDirectory), "tasks"); }

        /// <summary>项目库目录。</summary>
        public static string ProjectsDirectory(string dataDirectory) { return Path.Combine(dataDirectory, "projects"); }

        /// <summary>设置文件路径。</summary>
        public static string SettingsFile(string dataDirectory) { return Path.Combine(dataDirectory, "settings.json"); }

        /// <summary>项目库索引文件路径。</summary>
        public static string IndexFile(string dataDirectory) { return Path.Combine(dataDirectory, "index.json"); }

        /// <summary>预设文件路径（常用地点与拍摄类型）。</summary>
        public static string PresetsFile(string dataDirectory) { return Path.Combine(dataDirectory, "presets.json"); }

        /// <summary>
        /// 判断目录是否可写：尝试创建目录并写入一个探测文件后立即删除。
        /// </summary>
        public static bool IsWritable(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) { return false; }
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
