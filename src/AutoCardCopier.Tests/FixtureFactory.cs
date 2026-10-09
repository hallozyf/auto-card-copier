using System;
using System.IO;
using System.Text;

namespace AutoCardCopier.Tests
{
    /// <summary>
    /// 测试用的临时目录封装：自动创建与清理，保证测试不在工作区留下垃圾文件。
    /// </summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "acctest-" + prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path);
        }

        /// <summary>目录完整路径。</summary>
        public string Path { get; private set; }

        /// <summary>在目录下拼接子路径。</summary>
        public string Combine(params string[] parts)
        {
            string result = Path;
            foreach (string part in parts) { result = System.IO.Path.Combine(result, part); }
            return result;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) { Directory.Delete(Path, true); }
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }

    /// <summary>
    /// 夹具工厂：生成"模拟存储卡"目录结构，供扫描、拷贝、校验等测试复用。
    /// 结构参考真实相机卡：DCIM 下的编号目录、照片、视频、旁挂文件与系统垃圾目录。
    /// </summary>
    public static class FixtureFactory
    {
        /// <summary>写入指定大小的文件（内容为可预测的字节，便于校验比对）。</summary>
        public static void WriteFile(string path, long size, byte seed = 7)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[64 * 1024];
                long remaining = size;
                byte value = seed;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(buffer.Length, remaining);
                    for (int i = 0; i < chunk; i++)
                    {
                        buffer[i] = (byte)(value + i);
                    }
                    stream.Write(buffer, 0, chunk);
                    remaining -= chunk;
                    value = (byte)(value + 31);
                }
            }
        }

        /// <summary>
        /// 生成一张模拟卡片：包含照片、视频、其他文件、0 字节文件与系统目录。
        /// </summary>
        /// <param name="root">卡片根目录（调用方提供，通常是 TempDirectory）。</param>
        /// <param name="photoCount">照片数量。</param>
        /// <param name="videoCount">视频数量。</param>
        /// <param name="otherCount">旁挂文件数量。</param>
        /// <param name="photoSize">单张照片字节数。</param>
        /// <param name="videoSize">单个视频字节数。</param>
        public static void CreateSimulatedCard(string root, int photoCount = 5, int videoCount = 2,
            int otherCount = 1, long photoSize = 2048, long videoSize = 8192)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(root, "DCIM", "100CANON"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "DCIM", "101CANON"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "MISC"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "System Volume Information"));

            for (int i = 0; i < photoCount; i++)
            {
                string folder = (i % 2 == 0) ? "100CANON" : "101CANON";
                WriteFile(System.IO.Path.Combine(root, "DCIM", folder,
                    string.Format("IMG_{0:0000}.CR3", i + 1)), photoSize, (byte)(i + 1));
            }

            for (int i = 0; i < videoCount; i++)
            {
                WriteFile(System.IO.Path.Combine(root, "DCIM", "100CANON",
                    string.Format("MVI_{0:0000}.MP4", i + 1)), videoSize, (byte)(100 + i));
            }

            for (int i = 0; i < otherCount; i++)
            {
                WriteFile(System.IO.Path.Combine(root, "MISC",
                    string.Format("sidecar_{0}.XMP", i + 1)), 128, (byte)(200 + i));
            }

            // 0 字节文件与系统目录中的文件：用于验证"被正确忽略或单独统计"
            WriteFile(System.IO.Path.Combine(root, "DCIM", "100CANON", "EMPTY.CR3"), 0);
            WriteFile(System.IO.Path.Combine(root, "System Volume Information", "indexerVolumeGuid"), 16);
        }

        /// <summary>生成一个包含指定数量文件的目录（用于大目录与性能测试）。</summary>
        public static void CreateManyFiles(string root, int count, long sizeEach = 128)
        {
            Directory.CreateDirectory(root);
            for (int i = 0; i < count; i++)
            {
                WriteFile(System.IO.Path.Combine(root, string.Format("file_{0:00000}.bin", i)), sizeEach, (byte)i);
            }
        }

        /// <summary>生成一个中文与 emoji 混用的目录名，用于验证 Unicode 处理。</summary>
        public static string ChineseAndEmojiName()
        {
            return "婚礼拍摄_上海外滩_🎉";
        }

        /// <summary>统计目录下的文件数与总字节数（忽略指定目录名）。</summary>
        public static void MeasureDirectory(string root, out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            if (!Directory.Exists(root)) { return; }
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                totalBytes += new FileInfo(file).Length;
            }
        }
    }
}
