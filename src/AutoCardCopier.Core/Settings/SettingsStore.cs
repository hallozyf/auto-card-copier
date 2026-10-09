using System;
using System.IO;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.Settings
{
    /// <summary>
    /// 设置的加载与保存：负责默认值、损坏自愈与原子写入。
    /// 配置文件损坏时不会让程序启动失败，而是备份坏文件并重建默认配置。
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly ILog _log;

        /// <param name="settingsPath">settings.json 的完整路径。</param>
        /// <param name="log">日志（可为 NullLog）。</param>
        public SettingsStore(string settingsPath, ILog log)
        {
            if (string.IsNullOrWhiteSpace(settingsPath)) { throw new ArgumentException("路径不能为空", "settingsPath"); }
            SettingsPath = settingsPath;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>配置文件路径。</summary>
        public string SettingsPath { get; private set; }

        /// <summary>
        /// 读取设置。文件不存在时返回默认值；
        /// 文件损坏时把原文件改名为 .bad 备份，并返回默认值。
        /// </summary>
        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    _log.Info("未找到配置文件，使用默认设置：" + SettingsPath);
                    return AppSettings.CreateDefault();
                }

                string json = JsonStore.ReadAllText(SettingsPath);
                AppSettings settings = JsonStore.Deserialize<AppSettings>(json);
                if (settings == null) { throw new InvalidDataException("反序列化结果为空"); }
                settings.Normalize(_log);
                return settings;
            }
            catch (Exception ex)
            {
                _log.Error("配置文件损坏，已回退默认设置", ex);
                TryBackupBrokenFile();
                return AppSettings.CreateDefault();
            }
        }

        /// <summary>保存设置（原子写入）。</summary>
        public OperationResult Save(AppSettings settings)
        {
            if (settings == null) { return OperationResult.Fail("设置为空"); }
            try
            {
                settings.Normalize(_log);
                string json = JsonStore.Serialize(settings);
                JsonStore.WriteAllTextAtomic(SettingsPath, json);
                return OperationResult.Ok("设置已保存");
            }
            catch (Exception ex)
            {
                _log.Error("保存设置失败", ex);
                return OperationResult.Fail("保存设置失败：" + ex.Message, ex);
            }
        }

        private void TryBackupBrokenFile()
        {
            try
            {
                if (!File.Exists(SettingsPath)) { return; }
                string backup = SettingsPath + ".bad";
                if (File.Exists(backup)) { File.Delete(backup); }
                File.Move(SettingsPath, backup);
                _log.Warn("原配置文件已备份为 " + backup);
            }
            catch (Exception ex)
            {
                _log.Warn("备份损坏的配置文件失败", ex);
            }
        }
    }
}
