using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.Projects
{
    /// <summary>
    /// 用户预设：常用拍摄类型与常用地点。
    /// 保存到数据目录下的 presets.json，用于减少每次拷卡的输入量。
    /// </summary>
    [DataContract(Name = "presets")]
    public sealed class Presets
    {
        /// <summary>拍摄类型候选（可按用户习惯增删）。</summary>
        [DataMember(Name = "shootTypes", Order = 0)]
        public string[] ShootTypes { get; set; }

        /// <summary>最近使用过的地点（最新在前）。</summary>
        [DataMember(Name = "locations", Order = 1)]
        public string[] Locations { get; set; }

        /// <summary>默认拍摄类型。</summary>
        [DataMember(Name = "defaultShootType", Order = 2)]
        public string DefaultShootType { get; set; }

        /// <summary>最近使用地点的保留数量。</summary>
        public const int MaxLocations = 30;

        /// <summary>内置默认预设。</summary>
        public static Presets CreateDefault()
        {
            return new Presets
            {
                ShootTypes = new[]
                {
                    "人像写真", "婚纱婚礼", "活动会议", "商业产品", "风光旅行",
                    "体育运动", "新闻纪实", "视频摄制", "航拍", "其他"
                },
                Locations = new string[0],
                DefaultShootType = "人像写真"
            };
        }

        /// <summary>把设置补全为可用状态。</summary>
        public void Normalize()
        {
            if (ShootTypes == null || ShootTypes.Length == 0) { ShootTypes = CreateDefault().ShootTypes; }
            if (Locations == null) { Locations = new string[0]; }
            if (string.IsNullOrWhiteSpace(DefaultShootType)) { DefaultShootType = ShootTypes[0]; }
        }
    }

    /// <summary>预设的读写。</summary>
    public sealed class PresetsStore
    {
        private readonly string _path;
        private readonly ILog _log;
        private readonly object _gate = new object();
        private Presets _cached;

        public PresetsStore(string path, ILog log)
        {
            _path = path;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>配置文件路径。</summary>
        public string Path { get { return _path; } }

        /// <summary>读取预设（带缓存）。</summary>
        public Presets Load()
        {
            lock (_gate)
            {
                if (_cached != null) { return _cached; }
                try
                {
                    if (System.IO.File.Exists(_path))
                    {
                        _cached = JsonStore.Deserialize<Presets>(JsonStore.ReadAllText(_path));
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("读取预设失败，使用默认预设", ex);
                }
                if (_cached == null) { _cached = Presets.CreateDefault(); }
                _cached.Normalize();
                return _cached;
            }
        }

        /// <summary>保存预设。</summary>
        public OperationResult Save(Presets presets)
        {
            if (presets == null) { return OperationResult.Fail("预设为空"); }
            lock (_gate)
            {
                try
                {
                    presets.Normalize();
                    JsonStore.WriteAllTextAtomic(_path, JsonStore.Serialize(presets));
                    _cached = presets;
                    return OperationResult.Ok("预设已保存");
                }
                catch (Exception ex)
                {
                    _log.Warn("保存预设失败", ex);
                    return OperationResult.Fail("保存预设失败：" + ex.Message, ex);
                }
            }
        }

        /// <summary>记住一个地点（去重并置顶，最多保留 MaxLocations 个）。</summary>
        public void RememberLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) { return; }
            Presets presets = Load();
            var list = new List<string>();
            list.Add(location.Trim());
            foreach (string item in presets.Locations)
            {
                if (!string.Equals(item, location.Trim(), StringComparison.OrdinalIgnoreCase)) { list.Add(item); }
            }
            if (list.Count > Presets.MaxLocations) { list.RemoveRange(Presets.MaxLocations, list.Count - Presets.MaxLocations); }
            presets.Locations = list.ToArray();
            Save(presets);
        }
    }
}
