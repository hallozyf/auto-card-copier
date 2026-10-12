using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.Devices
{
    /// <summary>卡片集合发生变化时的事件参数。</summary>
    public sealed class CardsChangedEventArgs : EventArgs
    {
        public CardsChangedEventArgs(IList<CardVolume> cards) { Cards = cards; }

        /// <summary>变化后的卡片列表快照。</summary>
        public IList<CardVolume> Cards { get; private set; }
    }

    /// <summary>
    /// 卡片检测器。
    /// 设计要点：
    /// 1. 判定规则要能识别被报告为"固定磁盘"的 CFexpress / XQD 读卡器；
    /// 2. 同时支持事件驱动（Windows 设备消息）与轮询兜底（部分读卡器不发消息）；
    /// 3. 事件触发后做防抖，避免一次插卡触发多次刷新。
    /// </summary>
    public sealed class CardDetector : IDisposable
    {
        /// <summary>常见的相机卡特征目录（不区分大小写）。</summary>
        private static readonly string[] MediaFolderCandidates =
        {
            "DCIM", "M4ROOT", "AVCHD", "XDROOT", "PRIVATE", "BMD", "CANON", "NIKON",
            // 补充：专业摄像机（佳能 XF 的 CONTENTS）、老机型（MP_ROOT）与自建素材目录
            "CONTENTS", "MP_ROOT", "VIDEO", "MOVIE", "CLIP"
        };

        /// <summary>常见相机卡卷标关键字，用于辅助判定。</summary>
        private static readonly string[] KnownCardLabels =
        {
            "EOS_DIGITAL", "NIKON", "SONY", "CANON", "FUJI", "PANASONIC", "LEICA",
            "NO NAME", "UNTITLED", "PHOTO", "CAMERA", "SD_CARD", "XQD", "CFEXPRESS"
        };

        private readonly ILog _log;
        private readonly int _pollSeconds;
        private readonly object _gate = new object();
        private Timer _pollTimer;
        private DateTime _lastRefresh = DateTime.MinValue;
        private IList<CardVolume> _cards = new List<CardVolume>();
        private bool _disposed;

        /// <param name="log">日志。</param>
        /// <param name="pollSeconds">轮询间隔（秒），0 表示关闭轮询。</param>
        public CardDetector(ILog log, int pollSeconds = 3)
        {
            _log = log ?? NullLog.Instance;
            _pollSeconds = pollSeconds;
        }

        /// <summary>卡片集合变化事件。</summary>
        public event EventHandler<CardsChangedEventArgs> CardsChanged;

        /// <summary>最近一次检测到的卡片列表。</summary>
        public IList<CardVolume> Cards
        {
            get { lock (_gate) { return new List<CardVolume>(_cards); } }
        }

        /// <summary>开始后台轮询。</summary>
        public void Start()
        {
            if (_pollSeconds <= 0) { return; }
            lock (_gate)
            {
                if (_pollTimer != null) { return; }
                _pollTimer = new Timer(OnPollTick, null,
                    TimeSpan.FromSeconds(_pollSeconds), TimeSpan.FromSeconds(_pollSeconds));
            }
            Refresh();
        }

        /// <summary>停止后台轮询。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_pollTimer != null)
                {
                    _pollTimer.Dispose();
                    _pollTimer = null;
                }
            }
        }

        /// <summary>
        /// 设备变化通知（由界面层的 WM_DEVICECHANGE 钩子调用）。
        /// 这里的防抖很关键：一次插卡 Windows 可能连发多条消息。
        /// </summary>
        public void NotifyDeviceChanged()
        {
            lock (_gate)
            {
                if ((DateTime.UtcNow - _lastRefresh).TotalMilliseconds < 800) { return; }
                _lastRefresh = DateTime.UtcNow;
            }

            // 稍作延迟，等待卷挂载与盘符分配完成
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(800);
                Refresh();
            });
        }

        /// <summary>立即重新检测卡片并触发变化事件。</summary>
        public void Refresh()
        {
            IList<CardVolume> cards;
            try
            {
                cards = DetectCards();
            }
            catch (Exception ex)
            {
                _log.Warn("卡片检测失败", ex);
                return;
            }

            bool changed = HasChanged(_cards, cards);
            lock (_gate) { _cards = cards; }

            if (changed)
            {
                _log.Info("检测到卡片变化，当前卡片数：" + cards.Count);
                EventHandler<CardsChangedEventArgs> handler = CardsChanged;
                if (handler != null) { handler(this, new CardsChangedEventArgs(cards)); }
            }
        }

        /// <summary>
        /// 执行一次完整的卡片枚举与判定。
        /// </summary>
        public IList<CardVolume> DetectCards()
        {
            var result = new List<CardVolume>();
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:";
            IList<VolumeInfo> volumes = VolumeApi.EnumerateVolumes(_log);

            foreach (VolumeInfo volume in volumes)
            {
                try
                {
                    // 网络驱动器与光驱绝不可能是内存卡
                    if (volume.DriveType == DriveType.Network || volume.DriveType == DriveType.CDRom) { continue; }

                    CardVolume card = BuildCard(volume, systemDrive);
                    if (card != null) { result.Add(card); }
                }
                catch (Exception ex)
                {
                    _log.Warn("判定卡片失败：" + volume.RootPath, ex);
                }
            }
            return result;
        }

        /// <summary>
        /// 把卷转换为卡片（不满足条件时返回 null）。
        /// 判定顺序：排除系统盘 → 找特征目录 → 卷标辅助判定 → 可移动介质兜底。
        /// </summary>
        private CardVolume BuildCard(VolumeInfo volume, string systemDrive)
        {
            string root = volume.RootPath;
            if (string.IsNullOrEmpty(root)) { return null; }

            // 系统盘永远不是卡片
            if (string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), systemDrive.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            IList<string> mediaFolders = FindMediaFolders(root);
            bool labelLooksLikeCard = LabelLooksLikeCard(volume.Label);
            bool isRemovable = volume.DriveType == DriveType.Removable;

            string reason = null;
            if (mediaFolders.Count > 0) { reason = "包含相机素材目录：" + string.Join("、", mediaFolders); }
            else if (labelLooksLikeCard) { reason = "卷标符合相机卡命名：" + volume.Label; }
            else if (isRemovable && volume.TotalBytes > 0) { reason = "可移动磁盘"; }
            else { return null; }

            var card = new CardVolume
            {
                RootPath = root,
                DriveLetter = root.TrimEnd(Path.DirectorySeparatorChar),
                VolumeLabel = volume.Label,
                FileSystem = volume.FileSystem,
                DriveType = volume.DriveType,
                TotalBytes = volume.TotalBytes,
                FreeBytes = volume.FreeBytes,
                Identity = CardIdentity.FromVolume(volume),
                MediaFolderNames = mediaFolders,
                MediaRootPaths = BuildMediaRootPaths(root, mediaFolders),
                DetectionReason = reason,
                IsReady = volume.TotalBytes > 0
            };
            return card;
        }

        /// <summary>把识别到的特征目录名转换成绝对路径列表。</summary>
        private static IList<string> BuildMediaRootPaths(string root, IList<string> folderNames)
        {
            var paths = new List<string>();
            if (folderNames != null)
            {
                foreach (string name in folderNames) { paths.Add(Infrastructure.PathUtil.Combine(root, name)); }
            }
            return paths;
        }

        /// <summary>在卡根目录下查找相机素材目录。</summary>
        private static IList<string> FindMediaFolders(string root)
        {
            var found = new List<string>();
            foreach (string name in MediaFolderCandidates)
            {
                try
                {
                    string path = Path.Combine(root, name);
                    if (Directory.Exists(path)) { found.Add(name); }
                }
                catch (Exception)
                {
                    // 单个目录探测失败不影响其他判断
                }
            }
            return found;
        }

        private static bool LabelLooksLikeCard(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) { return false; }
            string upper = label.Trim().ToUpperInvariant();
            foreach (string keyword in KnownCardLabels)
            {
                if (upper.IndexOf(keyword, StringComparison.Ordinal) >= 0) { return true; }
            }
            return false;
        }

        /// <summary>比较两次卡片列表是否一致（按指纹比较，忽略顺序）。</summary>
        private static bool HasChanged(IList<CardVolume> before, IList<CardVolume> after)
        {
            if (before == null || after == null) { return true; }
            if (before.Count != after.Count) { return true; }

            var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CardVolume card in before) { fingerprints.Add(card.Identity.Fingerprint); }
            foreach (CardVolume card in after)
            {
                if (!fingerprints.Contains(card.Identity.Fingerprint)) { return true; }
            }
            return false;
        }

        private void OnPollTick(object state)
        {
            if (_disposed) { return; }
            Refresh();
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }
    }
}
