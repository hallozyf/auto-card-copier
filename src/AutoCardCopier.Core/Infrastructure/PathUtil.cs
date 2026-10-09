using System;
using System.IO;
using System.Text;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// 路径工具：长路径处理、网络路径判断、路径拼接与包含关系判断。
    /// 摄影素材常出现超过 260 字符的路径，必须统一走这里的扩展前缀逻辑。
    /// </summary>
    public static class PathUtil
    {
        /// <summary>Windows 长路径前缀。</summary>
        public const string LongPathPrefix = @"\\?\";

        /// <summary>UNC 长路径前缀。</summary>
        public const string UncPathPrefix = @"\\?\UNC\";

        /// <summary>判断是否为 UNC 网络路径（如两个反斜杠开头的服务器路径）。</summary>
        public static bool IsUnc(string path)
        {
            return !string.IsNullOrEmpty(path)
                   && path.StartsWith(@"\\", StringComparison.Ordinal)
                   && !path.StartsWith(LongPathPrefix, StringComparison.Ordinal);
        }

        /// <summary>判断是否为网络路径（UNC 或映射到网络位置的盘符暂不在此判断）。</summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { return false; }
            if (IsUnc(path)) { return true; }
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) { return false; }
                return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (Exception)
            {
                // 无法判断时按本地路径处理，调用方另有安全护栏
                return false;
            }
        }

        /// <summary>
        /// 给绝对路径加上长路径前缀，使其可以突破 260 字符限制。
        /// 已经是扩展路径时原样返回；相对路径无法转换，返回原值。
        /// </summary>
        public static string Extend(string path)
        {
            if (string.IsNullOrEmpty(path)) { return path; }
            if (path.StartsWith(LongPathPrefix, StringComparison.Ordinal)) { return path; }
            if (!Path.IsPathRooted(path)) { return path; }

            string normalized = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // UNC：\\server\share\dir 转为 \\?\UNC\server\share\dir
                return UncPathPrefix + normalized.Substring(2);
            }
            return LongPathPrefix + normalized;
        }

        /// <summary>去掉长路径前缀，得到普通路径（用于显示与记录）。</summary>
        public static string Unextend(string path)
        {
            if (string.IsNullOrEmpty(path)) { return path; }
            if (path.StartsWith(UncPathPrefix, StringComparison.Ordinal))
            {
                return @"\\" + path.Substring(UncPathPrefix.Length);
            }
            if (path.StartsWith(LongPathPrefix, StringComparison.Ordinal))
            {
                return path.Substring(LongPathPrefix.Length);
            }
            return path;
        }

        /// <summary>保证目录路径以反斜杠结尾（用于"是否为子路径"的判断）。</summary>
        public static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path)) { return path; }
            return path.EndsWith(@"\", StringComparison.Ordinal) ? path : path + @"\";
        }

        /// <summary>
        /// 判断 child 是否位于 parent 目录之下（忽略大小写，按完整路径比较）。
        /// 用于"目标目录不能是源目录的子目录"等安全校验。
        /// </summary>
        public static bool IsSubPathOf(string parent, string child)
        {
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child)) { return false; }
            try
            {
                string p = EnsureTrailingSeparator(Path.GetFullPath(Unextend(parent)));
                string c = Path.GetFullPath(Unextend(child));
                return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>安全拼接目录与子项，自动处理多余的分隔符。</summary>
        public static string Combine(string directory, string name)
        {
            if (string.IsNullOrEmpty(directory)) { return name; }
            if (string.IsNullOrEmpty(name)) { return directory; }
            return directory.TrimEnd('\\', '/') + @"\" + name.TrimStart('\\', '/');
        }

        /// <summary>
        /// 生成一个在指定目录下不冲突的文件名（追加 _2、_3 …）。
        /// 已存在同名目录或文件时向后寻找可用的名字。
        /// </summary>
        public static string MakeUniqueName(string directory, string baseName)
        {
            string candidate = baseName;
            int index = 2;
            while (Exists(directory, candidate))
            {
                candidate = string.Format("{0}_{1}", baseName, index);
                index++;
                if (index > 9999) { break; }
            }
            return candidate;
        }

        private static bool Exists(string directory, string name)
        {
            string full = Combine(directory, name);
            return Directory.Exists(Extend(full)) || File.Exists(Extend(full));
        }

        /// <summary>
        /// 把任意字符串清洗成合法的 Windows 文件名片段：
        /// 替换非法字符、去掉控制字符与首尾空格及点。
        /// </summary>
        public static string SanitizeName(string name, int maxLength = 100)
        {
            if (string.IsNullOrEmpty(name)) { return string.Empty; }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (char ch in name)
            {
                if (char.IsControl(ch)) { continue; }
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            string result = builder.ToString().Trim().Trim('.');
            if (result.Length > maxLength)
            {
                result = result.Substring(0, maxLength).Trim().Trim('.');
            }
            return result;
        }
    }
}
