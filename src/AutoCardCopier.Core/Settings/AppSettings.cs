using System;
using System.Runtime.Serialization;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.Settings
{
    /// <summary>目标目录的子目录组织方式。</summary>
    public enum SubFolderMode
    {
        /// <summary>所有素材放在项目根目录。</summary>
        None = 0,
        /// <summary>按媒体类型分为 照片 / 视频 / 其他。</summary>
        ByMediaType = 1,
        /// <summary>在按类型的基础上，照片再按 RAW 与 JPG 分开。</summary>
        ByMediaTypeAndFormat = 2
    }

    /// <summary>目标目录已存在时的处理策略。</summary>
    public enum ConflictPolicy
    {
        /// <summary>自动追加序号生成新目录。</summary>
        AutoRenameDir = 0,
        /// <summary>合并到已有目录。</summary>
        Merge = 1,
        /// <summary>询问用户。</summary>
        Ask = 2
    }

    /// <summary>已存在文件的拷贝策略（断点续传时使用）。</summary>
    public enum CopyMode
    {
        /// <summary>目标不存在或比源旧时才拷贝。</summary>
        CopyIfNewer = 0,
        /// <summary>总是重拷。</summary>
        Force = 1,
        /// <summary>目标存在且大小一致就跳过。</summary>
        Skip = 2
    }

    /// <summary>校验档位。</summary>
    public enum VerifyLevel
    {
        /// <summary>不校验。</summary>
        None = 0,
        /// <summary>只比较文件大小。</summary>
        SizeOnly = 1,
        /// <summary>比较大小并对全部文件计算 CRC32。</summary>
        Crc32 = 2,
        /// <summary>比较大小并对全部文件计算 SHA-256。</summary>
        Sha256 = 3,
        /// <summary>大小 + 抽样哈希（每文件首尾与中点各 1 MB）。</summary>
        SizeAndSample = 4
    }

    /// <summary>界面主题。</summary>
    public enum ThemeMode
    {
        Light = 0,
        Dark = 1,
        System = 2
    }

    /// <summary>格式化时使用的文件系统。</summary>
    public enum FormatFileSystem
    {
        /// <summary>沿用卡片原有文件系统。</summary>
        KeepOriginal = 0,
        ExFat = 1,
        Fat32 = 2
    }

    /// <summary>
    /// 软件设置。所有字段都必须在构造时给出默认值，
    /// 保证配置文件缺字段或损坏时仍能得到一份可用配置。
    /// </summary>
    [DataContract(Name = "settings")]
    public sealed class AppSettings
    {
        /// <summary>配置文件结构版本，便于将来做升级迁移。</summary>
        [DataMember(Name = "schemaVersion", Order = 0)]
        public int SchemaVersion { get; set; }

        // ---------- 目标 ----------

        /// <summary>主目标根目录（盘符或 UNC）。</summary>
        [DataMember(Name = "primaryTargetRoot", Order = 1)]
        public string PrimaryTargetRoot { get; set; }

        /// <summary>副目标根目录，为空表示不做多目标备份。</summary>
        [DataMember(Name = "secondaryTargetRoot", Order = 2)]
        public string SecondaryTargetRoot { get; set; }

        /// <summary>项目目录命名模板。</summary>
        [DataMember(Name = "nameTemplate", Order = 3)]
        public string NameTemplate { get; set; }

        /// <summary>子目录组织方式。</summary>
        [DataMember(Name = "subFolderMode", Order = 4)]
        public SubFolderMode SubFolderMode { get; set; }

        /// <summary>目录冲突策略。</summary>
        [DataMember(Name = "conflictPolicy", Order = 5)]
        public ConflictPolicy ConflictPolicy { get; set; }

        // ---------- 拷贝 ----------

        /// <summary>并行拷贝的文件数（1 至 8）。</summary>
        [DataMember(Name = "maxParallelism", Order = 10)]
        public int MaxParallelism { get; set; }

        /// <summary>读写缓冲区大小（KB）。</summary>
        [DataMember(Name = "bufferSizeKb", Order = 11)]
        public int BufferSizeKb { get; set; }

        /// <summary>已存在文件的处理策略。</summary>
        [DataMember(Name = "copyMode", Order = 12)]
        public CopyMode CopyMode { get; set; }

        /// <summary>是否跳过系统文件与缩略图缓存。</summary>
        [DataMember(Name = "skipNonMediaFiles", Order = 13)]
        public bool SkipNonMediaFiles { get; set; }

        /// <summary>是否保留原始文件时间戳。</summary>
        [DataMember(Name = "preserveTimestamps", Order = 14)]
        public bool PreserveTimestamps { get; set; }

        // ---------- 校验 ----------

        /// <summary>校验档位。</summary>
        [DataMember(Name = "verifyLevel", Order = 20)]
        public VerifyLevel VerifyLevel { get; set; }

        /// <summary>是否对视频文件做哈希校验（视频体积大，耗时明显）。</summary>
        [DataMember(Name = "verifyVideo", Order = 21)]
        public bool VerifyVideo { get; set; }

        // ---------- 卡片 ----------

        /// <summary>是否必须包含 DCIM 等特征目录才认定为卡片。</summary>
        [DataMember(Name = "requireDcimFolder", Order = 30)]
        public bool RequireDcimFolder { get; set; }

        /// <summary>需要忽略的目录名（不区分大小写）。</summary>
        [DataMember(Name = "ignoredDirectories", Order = 31)]
        public string[] IgnoredDirectories { get; set; }

        /// <summary>格式化时使用的文件系统。</summary>
        [DataMember(Name = "formatFileSystem", Order = 32)]
        public FormatFileSystem FormatFileSystem { get; set; }

        /// <summary>校验通过后是否自动格式化。</summary>
        [DataMember(Name = "autoFormatAfterVerify", Order = 33)]
        public bool AutoFormatAfterVerify { get; set; }

        /// <summary>自动格式化倒计时秒数。</summary>
        [DataMember(Name = "autoFormatCountdownSec", Order = 34)]
        public int AutoFormatCountdownSec { get; set; }

        /// <summary>
        /// 是否同时扫描卡根下的其它目录（特征目录与忽略目录之外的目录）。
        /// 默认开启：相机可能把视频放在非标准目录，漏扫会导致素材没有被拷贝。
        /// </summary>
        [DataMember(Name = "scanWholeCardRoot", Order = 35)]
        public bool ScanWholeCardRoot { get; set; }

        // ---------- 界面 ----------

        /// <summary>主题。</summary>
        [DataMember(Name = "theme", Order = 40)]
        public ThemeMode Theme { get; set; }

        /// <summary>关闭窗口时是否最小化到托盘。</summary>
        [DataMember(Name = "minimizeToTray", Order = 41)]
        public bool MinimizeToTray { get; set; }

        /// <summary>是否开机自启。</summary>
        [DataMember(Name = "autoStart", Order = 42)]
        public bool AutoStart { get; set; }

        /// <summary>窗口宽度（记忆上次大小）。</summary>
        [DataMember(Name = "windowWidth", Order = 43)]
        public double WindowWidth { get; set; }

        /// <summary>窗口高度。</summary>
        [DataMember(Name = "windowHeight", Order = 44)]
        public double WindowHeight { get; set; }

        /// <summary>窗口是否最大化。</summary>
        [DataMember(Name = "windowMaximized", Order = 45)]
        public bool WindowMaximized { get; set; }

        // ---------- 数据 ----------

        /// <summary>数据目录；为空表示使用默认的 %APPDATA% 目录。</summary>
        [DataMember(Name = "dataDirectory", Order = 50)]
        public string DataDirectory { get; set; }

        /// <summary>日志保留天数。</summary>
        [DataMember(Name = "logRetentionDays", Order = 51)]
        public int LogRetentionDays { get; set; }

        /// <summary>创建一份带默认值的设置。</summary>
        public static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                SchemaVersion = 1,
                PrimaryTargetRoot = string.Empty,
                SecondaryTargetRoot = string.Empty,
                NameTemplate = "{yyyy}/{yyyy-MM-dd}_{type}_{location}",
                SubFolderMode = SubFolderMode.ByMediaType,
                ConflictPolicy = ConflictPolicy.AutoRenameDir,
                MaxParallelism = 4,
                BufferSizeKb = 1024,
                CopyMode = CopyMode.CopyIfNewer,
                SkipNonMediaFiles = true,
                PreserveTimestamps = true,
                VerifyLevel = VerifyLevel.SizeAndSample,
                VerifyVideo = true,
                RequireDcimFolder = false,
                ScanWholeCardRoot = true,
                IgnoredDirectories = new[]
                {
                    "System Volume Information", "$RECYCLE.BIN", ".Trashes", ".Spotlight-V100", ".fseventsd"
                },
                FormatFileSystem = FormatFileSystem.KeepOriginal,
                AutoFormatAfterVerify = false,
                AutoFormatCountdownSec = 10,
                Theme = ThemeMode.Light,
                MinimizeToTray = true,
                AutoStart = false,
                WindowWidth = 1080,
                WindowHeight = 720,
                WindowMaximized = false,
                DataDirectory = string.Empty,
                LogRetentionDays = 90
            };
        }

        /// <summary>
        /// 把非法或越界的值修正回合法范围，避免手工编辑配置导致运行时异常。
        /// </summary>
        public void Normalize(Logging.ILog log = null)
        {
            if (SchemaVersion <= 0) { SchemaVersion = 1; }
            if (string.IsNullOrWhiteSpace(NameTemplate)) { NameTemplate = "{yyyy}/{yyyy-MM-dd}_{type}_{location}"; }
            MaxParallelism = Clamp(MaxParallelism, 1, 8);
            BufferSizeKb = Clamp(BufferSizeKb, 64, 16384);
            AutoFormatCountdownSec = Clamp(AutoFormatCountdownSec, 3, 120);
            LogRetentionDays = Clamp(LogRetentionDays, 7, 3650);
            WindowWidth = WindowWidth < 640 ? 1080 : WindowWidth;
            WindowHeight = WindowHeight < 480 ? 720 : WindowHeight;
            if (IgnoredDirectories == null || IgnoredDirectories.Length == 0)
            {
                IgnoredDirectories = new[]
                {
                    "System Volume Information", "$RECYCLE.BIN", ".Trashes", ".Spotlight-V100", ".fseventsd"
                };
            }
            if (PrimaryTargetRoot == null) { PrimaryTargetRoot = string.Empty; }
            if (SecondaryTargetRoot == null) { SecondaryTargetRoot = string.Empty; }
            if (DataDirectory == null) { DataDirectory = string.Empty; }

            if (log != null)
            {
                log.Debug("设置已校验并归一化");
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) { return min; }
            if (value > max) { return max; }
            return value;
        }
    }
}
