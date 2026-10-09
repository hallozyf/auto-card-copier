using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// JSON 读写工具：统一使用 DataContractJsonSerializer（.NET 自带，无第三方依赖），
    /// 写入时通过 SimpleJson.Pretty 加上缩进，保证文件可以直接用记事本查看与手工修复。
    /// </summary>
    public static class JsonStore
    {
        /// <summary>把对象序列化为可读 JSON 文本。</summary>
        public static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                string compact = Encoding.UTF8.GetString(stream.ToArray());
                return SimpleJson.Pretty(compact);
            }
        }

        /// <summary>反序列化 JSON 文本；失败时抛出异常，由调用方决定如何降级。</summary>
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) { throw new ArgumentException("内容为空", "json"); }
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        /// <summary>
        /// 原子写入：先写到同目录的 .tmp 文件再改名覆盖，
        /// 避免写入过程中断电或崩溃导致目标文件变成半截内容。
        /// </summary>
        public static void WriteAllTextAtomic(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                // File.Replace 需要目标存在且支持备份，这里用删除加移动保证兼容性
                File.Delete(path);
            }
            File.Move(temp, path);
        }

        /// <summary>读取文本文件（UTF-8，允许 BOM）。</summary>
        public static string ReadAllText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }
}
