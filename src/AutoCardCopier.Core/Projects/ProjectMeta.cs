using System;
using System.Globalization;
using System.Runtime.Serialization;
using AutoCardCopier.Core.Infrastructure;

namespace AutoCardCopier.Core.Projects
{
    /// <summary>
    /// 项目元数据：写入项目目录下的 _project.json，用来自描述"这次拍摄是什么"。
    /// 采用扁平字段结构，便于人工阅读、手工修正，也便于从 NAS 直接重建本地项目库。
    /// 时间统一使用 ISO 8601 字符串存储（见 TimeUtil）。
    /// </summary>
    [DataContract(Name = "project")]
    public sealed class ProjectMeta
    {
        /// <summary>起始日期（yyyy-MM-dd）；单日项目与 ShootDate 相同（F14-D）。</summary>
        [DataMember(Name = "startDate", Order = 50)]
        public string StartDate { get; set; }

        /// <summary>终止日期（yyyy-MM-dd）；单日项目与 ShootDate 相同。</summary>
        [DataMember(Name = "endDate", Order = 51)]
        public string EndDate { get; set; }

        /// <summary>来源：create（新建）/ import（历史导入）/ append（追加）/ migrate（旧文件迁移）。</summary>
        [DataMember(Name = "source", Order = 52)]
        public string Source { get; set; }

        /// <summary>最近一次校验时间（ISO）。</summary>
        [DataMember(Name = "lastVerifiedAt", Order = 53)]
        public string LastVerifiedAt { get; set; }

        /// <summary>导入布局：flat（合并放一起）/ byday（一天一个文件夹）。</summary>
        [DataMember(Name = "importLayout", Order = 54)]
        public string ImportLayout { get; set; }

        /// <summary>是否跨多天项目。</summary>
        public bool IsMultiDay
        {
            get
            {
                return !string.IsNullOrEmpty(StartDate) && !string.IsNullOrEmpty(EndDate)
                       && string.CompareOrdinal(StartDate, EndDate) < 0;
            }
        }

        [DataMember(Name = "schemaVersion", Order = 0)]
        public int SchemaVersion { get; set; }

        /// <summary>项目唯一标识。</summary>
        [DataMember(Name = "id", Order = 1)]
        public string Id { get; set; }

        /// <summary>项目显示名（通常等于项目目录名）。</summary>
        [DataMember(Name = "name", Order = 2)]
        public string Name { get; set; }

        /// <summary>拍摄日期（yyyy-MM-dd）。</summary>
        [DataMember(Name = "shootDate", Order = 3)]
        public string ShootDate { get; set; }

        /// <summary>素材最早时间。</summary>
        [DataMember(Name = "shootTimeStart", Order = 4)]
        public string ShootTimeStart { get; set; }

        /// <summary>素材最晚时间。</summary>
        [DataMember(Name = "shootTimeEnd", Order = 5)]
        public string ShootTimeEnd { get; set; }

        /// <summary>拍摄地点。</summary>
        [DataMember(Name = "location", Order = 6)]
        public string Location { get; set; }

        /// <summary>拍摄类型。</summary>
        [DataMember(Name = "shootType", Order = 7)]
        public string ShootType { get; set; }

        /// <summary>摄影师。</summary>
        [DataMember(Name = "photographer", Order = 8)]
        public string Photographer { get; set; }

        /// <summary>相机型号（自动识别或手工填写）。</summary>
        [DataMember(Name = "cameraModel", Order = 9)]
        public string CameraModel { get; set; }

        /// <summary>卡片卷标。</summary>
        [DataMember(Name = "cardLabel", Order = 10)]
        public string CardLabel { get; set; }

        /// <summary>卡片指纹（用于防重复导入与格式化护栏）。</summary>
        [DataMember(Name = "cardFingerprint", Order = 11)]
        public string CardFingerprint { get; set; }

        /// <summary>素材落盘路径（完整路径）。</summary>
        [DataMember(Name = "targetPath", Order = 12)]
        public string TargetPath { get; set; }

        /// <summary>素材落盘相对路径（相对目标根，便于换机器后定位）。</summary>
        [DataMember(Name = "relativePath", Order = 13)]
        public string RelativePath { get; set; }

        [DataMember(Name = "photoCount", Order = 20)] public int PhotoCount { get; set; }
        [DataMember(Name = "photoBytes", Order = 21)] public long PhotoBytes { get; set; }
        [DataMember(Name = "videoCount", Order = 22)] public int VideoCount { get; set; }
        [DataMember(Name = "videoBytes", Order = 23)] public long VideoBytes { get; set; }
        [DataMember(Name = "otherCount", Order = 24)] public int OtherCount { get; set; }
        [DataMember(Name = "otherBytes", Order = 25)] public long OtherBytes { get; set; }
        [DataMember(Name = "totalCount", Order = 26)] public int TotalCount { get; set; }
        [DataMember(Name = "totalBytes", Order = 27)] public long TotalBytes { get; set; }

        /// <summary>校验档位。</summary>
        [DataMember(Name = "verifyLevel", Order = 30)]
        public string VerifyLevel { get; set; }

        /// <summary>校验结果（通过 / 失败 / 未校验）。</summary>
        [DataMember(Name = "verifyStatus", Order = 31)]
        public string VerifyStatus { get; set; }

        /// <summary>备注。</summary>
        [DataMember(Name = "note", Order = 32)]
        public string Note { get; set; }

        /// <summary>创建时间。</summary>
        [DataMember(Name = "createdAt", Order = 40)]
        public string CreatedAt { get; set; }

        /// <summary>更新时间。</summary>
        [DataMember(Name = "updatedAt", Order = 41)]
        public string UpdatedAt { get; set; }

        /// <summary>拍摄日期的解析结果（解析失败返回当天）。</summary>
        public DateTime GetShootDate()
        {
            return TimeUtil.ParseIso(ShootDate, DateTime.Now.Date);
        }

        /// <summary>基于上下文的草稿。</summary>
        public static ProjectMeta CreateDraft(string id, string name, ProjectContext context)
        {
            string now = TimeUtil.ToIso(DateTime.Now);
            return new ProjectMeta
            {
                SchemaVersion = 1,
                Id = id,
                Name = name,
                ShootDate = TimeUtil.ToIso(context.ShootDate),
                Location = context.Location,
                ShootType = context.ShootType,
                Photographer = context.Photographer,
                CameraModel = context.CameraModel,
                CardLabel = context.CardLabel,
                CreatedAt = now,
                UpdatedAt = now,
                VerifyLevel = string.Empty,
                VerifyStatus = "未校验",
                Note = string.Empty
            };
        }

        /// <summary>生成一个新的项目标识。</summary>
        public static string NewId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                   Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
