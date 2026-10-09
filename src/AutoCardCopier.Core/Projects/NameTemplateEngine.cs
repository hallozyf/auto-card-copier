using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AutoCardCopier.Core.Infrastructure;

namespace AutoCardCopier.Core.Projects
{
    /// <summary>
    /// 拍摄项目上下文：生成目录名与项目元数据所需的全部字段。
    /// </summary>
    public sealed class ProjectContext
    {
        /// <summary>拍摄日期（用于日期占位符）。</summary>
        public DateTime ShootDate { get; set; }

        /// <summary>拍摄类型（婚礼、人像写真……）。</summary>
        public string ShootType { get; set; }

        /// <summary>拍摄地点。</summary>
        public string Location { get; set; }

        /// <summary>摄影师。</summary>
        public string Photographer { get; set; }

        /// <summary>相机型号。</summary>
        public string CameraModel { get; set; }

        /// <summary>卡片卷标。</summary>
        public string CardLabel { get; set; }

        /// <summary>序号（用于目录重名时的占位符）。</summary>
        public string Sequence { get; set; }

        /// <summary>创建一份带兜底值的上下文，避免占位符展开出空目录名。</summary>
        public static ProjectContext CreateDefault()
        {
            return new ProjectContext
            {
                ShootDate = DateTime.Now,
                ShootType = "其他",
                Location = "未标注地点",
                Photographer = string.Empty,
                CameraModel = string.Empty,
                CardLabel = string.Empty,
                Sequence = string.Empty
            };
        }
    }

    /// <summary>
    /// 命名模板引擎：把形如 {yyyy}/{yyyy-MM-dd}_{type}_{location} 的模板展开为真实的目录相对路径。
    /// 设计要点：
    /// 1. 未识别的占位符原样保留，便于用户发现写错的地方；
    /// 2. 每一段都做非法字符清洗与长度限制；
    /// 3. 展开结果的空段会被自动丢弃，避免出现多余目录层级。
    /// </summary>
    public static class NameTemplateEngine
    {
        /// <summary>默认模板。</summary>
        public const string DefaultTemplate = "{yyyy}/{yyyy-MM-dd}_{type}_{location}";

        /// <summary>单个路径段的最大长度。</summary>
        public const int MaxSegmentLength = 80;

        /// <summary>展开模板，返回使用正斜杠分隔的相对路径。</summary>
        public static string Expand(string template, ProjectContext context)
        {
            if (string.IsNullOrWhiteSpace(template)) { template = DefaultTemplate; }
            if (context == null) { context = ProjectContext.CreateDefault(); }

            string expanded = ReplaceTokens(template, context);

            // 按目录分隔符切段，逐段清洗后再拼回去
            string[] rawSegments = expanded.Replace('\\', '/').Split('/');
            var segments = new List<string>();
            foreach (string raw in rawSegments)
            {
                string cleaned = SanitizeSegment(raw);
                if (!string.IsNullOrEmpty(cleaned)) { segments.Add(cleaned); }
            }

            if (segments.Count == 0)
            {
                segments.Add(SanitizeSegment(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            return string.Join("/", segments.ToArray());
        }

        /// <summary>把展开后的相对路径转换为 Windows 相对路径（使用反斜杠）。</summary>
        public static string ToWindowsRelativePath(string expandedPath)
        {
            if (string.IsNullOrEmpty(expandedPath)) { return expandedPath; }
            return expandedPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        }

        /// <summary>清洗单个路径段。</summary>
        public static string SanitizeSegment(string segment)
        {
            return PathUtil.SanitizeName(segment, MaxSegmentLength);
        }

        /// <summary>逐个替换占位符。</summary>
        private static string ReplaceTokens(string template, ProjectContext context)
        {
            var builder = new StringBuilder(template.Length + 32);
            int index = 0;
            while (index < template.Length)
            {
                char ch = template[index];
                if (ch != '{')
                {
                    builder.Append(ch);
                    index++;
                    continue;
                }

                int end = template.IndexOf('}', index + 1);
                if (end < 0)
                {
                    // 没有闭合的占位符：原样保留
                    builder.Append(template.Substring(index));
                    break;
                }

                string token = template.Substring(index + 1, end - index - 1);
                string value = ResolveToken(token, context);
                builder.Append(value ?? template.Substring(index, end - index + 1));
                index = end + 1;
            }
            return builder.ToString();
        }

        /// <summary>解析单个占位符；返回 null 表示无法识别，需要原样保留。</summary>
        private static string ResolveToken(string token, ProjectContext context)
        {
            string key = token.Trim();
            if (key.Length == 0) { return null; }

            switch (key.ToLowerInvariant())
            {
                case "yyyy": return context.ShootDate.ToString("yyyy", CultureInfo.InvariantCulture);
                case "yy": return context.ShootDate.ToString("yy", CultureInfo.InvariantCulture);
                case "mm": return context.ShootDate.ToString("MM", CultureInfo.InvariantCulture);
                case "month": return context.ShootDate.ToString("MM", CultureInfo.InvariantCulture);
                case "dd": return context.ShootDate.ToString("dd", CultureInfo.InvariantCulture);
                case "yyyy-mm-dd": return context.ShootDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "hh": return context.ShootDate.ToString("HH", CultureInfo.InvariantCulture);
                case "mm2": return context.ShootDate.ToString("mm", CultureInfo.InvariantCulture);
                case "hhmm": return context.ShootDate.ToString("HHmm", CultureInfo.InvariantCulture);
                case "type": return context.ShootType;
                case "location": return context.Location;
                case "photographer": return context.Photographer;
                case "camera": return context.CameraModel;
                case "card": return context.CardLabel;
                case "seq": return context.Sequence;
                default: return null;
            }
        }
    }
}
