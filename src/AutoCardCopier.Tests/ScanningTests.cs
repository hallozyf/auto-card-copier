using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Tests
{
    /// <summary>素材分类、扫描与 EXIF 解析的单元测试。</summary>
    public static class ScanningTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("分类器识别常见相机扩展名", () =>
            {
                Assert.Equal(MediaKind.Photo, MediaClassifier.Classify("IMG_0001.CR3"));
                Assert.Equal(MediaKind.Photo, MediaClassifier.Classify("DSC_0001.NEF"));
                Assert.Equal(MediaKind.Photo, MediaClassifier.Classify("photo.jpeg"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("MVI_0001.MP4"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("clip.mov"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("A001.BRAW"));
                Assert.Equal(MediaKind.Other, MediaClassifier.Classify("sidecar.xmp"));
                Assert.Equal(MediaKind.Other, MediaClassifier.Classify("noextension"));
            });

            yield return Case("分类器识别 RAW 与垃圾文件", () =>
            {
                Assert.True(MediaClassifier.IsRaw("IMG_0001.CR3"));
                Assert.True(MediaClassifier.IsRaw(".DNG"));
                Assert.False(MediaClassifier.IsRaw("IMG_0001.JPG"));
                Assert.True(MediaClassifier.IsJunkFile("Thumbs.db"));
                Assert.True(MediaClassifier.IsJunkFile("C:\\DCIM\\100CANON\\.DS_Store"));
                Assert.False(MediaClassifier.IsJunkFile("IMG_0001.CR3"));
            });

            yield return Case("子目录规划符合设置", () =>
            {
                Assert.Equal(string.Empty, MediaClassifier.ResolveSubFolder(MediaKind.Photo, "a.cr3", SubFolderMode.None));
                Assert.Equal("照片", MediaClassifier.ResolveSubFolder(MediaKind.Photo, "a.cr3", SubFolderMode.ByMediaType));
                Assert.Equal("照片/RAW", MediaClassifier.ResolveSubFolder(MediaKind.Photo, "a.cr3", SubFolderMode.ByMediaTypeAndFormat));
                Assert.Equal("照片/JPG", MediaClassifier.ResolveSubFolder(MediaKind.Photo, "a.jpg", SubFolderMode.ByMediaTypeAndFormat));
                Assert.Equal("视频", MediaClassifier.ResolveSubFolder(MediaKind.Video, "a.mp4", SubFolderMode.ByMediaTypeAndFormat));
                Assert.Equal("其他", MediaClassifier.ResolveSubFolder(MediaKind.Other, "a.xmp", SubFolderMode.ByMediaTypeAndFormat));
            });

            yield return Case("扫描模拟卡片统计正确", () =>
            {
                using (var temp = new TempDirectory("scan"))
                {
                    FixtureFactory.CreateSimulatedCard(temp.Path, photoCount: 5, videoCount: 2, otherCount: 1);
                    // 旁挂文件放在素材目录内，扫描器才会收录
                    FixtureFactory.WriteFile(temp.Combine("DCIM", "100CANON", "IMG_0001.XMP"), 128);

                    var scanner = new MediaScanner(NullLog.Instance);
                    ScanResult result = scanner.Scan(temp.Path, AppSettings.CreateDefault(), null, CancellationToken.None);

                    // 修复 BUG-001 后，卡根下的 MISC 目录也会被扫描（不再只盯着特征目录），
                    // 因此"其他文件"由 1 个变为 2 个（DCIM 内的 XMP 与 MISC 内的 XMP）
                    Assert.Equal(5, result.PhotoCount);
                    Assert.Equal(2, result.VideoCount);
                    Assert.Equal(2, result.OtherCount);
                    Assert.Equal(9, result.TotalCount);
                    Assert.Equal(5 * 2048L + 2 * 8192L + 256L, result.TotalBytes);
                    Assert.True(result.SkippedCount >= 1, "0 字节文件应被跳过，实际跳过 " + result.SkippedCount);
                    Assert.True(result.HasPhoto && result.HasVideo);
                    Assert.Equal("照片 + 视频", result.ContentSummary);
                }
            });

            yield return Case("扫描器记录素材时间范围", () =>
            {
                using (var temp = new TempDirectory("scan"))
                {
                    FixtureFactory.CreateSimulatedCard(temp.Path, photoCount: 2, videoCount: 0, otherCount: 0);
                    var early = new DateTime(2026, 3, 10, 9, 12, 0, DateTimeKind.Utc);
                    var late = new DateTime(2026, 3, 10, 18, 40, 0, DateTimeKind.Utc);

                    // 注意：夹具里还有一个 0 字节的 EMPTY.CR3 会被扫描器跳过，这里只设置真实照片的时间
                    File.SetLastWriteTimeUtc(temp.Combine("DCIM", "100CANON", "IMG_0001.CR3"), early);
                    File.SetLastWriteTimeUtc(temp.Combine("DCIM", "101CANON", "IMG_0002.CR3"), late);

                    var scanner = new MediaScanner(NullLog.Instance);
                    ScanResult result = scanner.Scan(temp.Path, AppSettings.CreateDefault(), null, CancellationToken.None);

                    Assert.Equal(early, result.EarliestUtc.Value);
                    Assert.Equal(late, result.LatestUtc.Value);
                }
            });

            yield return Case("没有特征目录时扫描卡根", () =>
            {
                using (var temp = new TempDirectory("scan"))
                {
                    FixtureFactory.CreateManyFiles(temp.Combine("random"), 3, 256);
                    FixtureFactory.WriteFile(temp.Combine("random", "clip.mp4"), 4096);

                    var scanner = new MediaScanner(NullLog.Instance);
                    ScanResult result = scanner.Scan(temp.Path, AppSettings.CreateDefault(), null, CancellationToken.None);

                    Assert.Equal(0, result.PhotoCount);
                    Assert.Equal(1, result.VideoCount);
                    Assert.Equal(3, result.OtherCount);
                }
            });

            yield return Case("扫描可以被取消", () =>
            {
                using (var temp = new TempDirectory("scan"))
                {
                    FixtureFactory.CreateManyFiles(temp.Combine("DCIM", "100CANON"), 50, 128);
                    var tokenSource = new CancellationTokenSource();
                    tokenSource.Cancel();

                    var scanner = new MediaScanner(NullLog.Instance);
                    Assert.Throws<OperationCanceledException>(() =>
                        scanner.Scan(temp.Path, AppSettings.CreateDefault(), null, tokenSource.Token));
                }
            });

            yield return Case("扫描进度回调被触发", () =>
            {
                using (var temp = new TempDirectory("scan"))
                {
                    FixtureFactory.CreateManyFiles(temp.Combine("DCIM", "100CANON"), 30, 128);
                    var reports = new List<ScanProgress>();
                    var progress = new ProgressCollector(reports);

                    var scanner = new MediaScanner(NullLog.Instance);
                    scanner.Scan(temp.Path, AppSettings.CreateDefault(), progress, CancellationToken.None);

                    Assert.True(reports.Count > 0, "应当至少上报一次进度");
                    Assert.Equal(30, reports[reports.Count - 1].FilesScanned);
                }
            });

            yield return Case("EXIF 解析相机型号（TIFF 结构）", () =>
            {
                byte[] buffer = BuildTiffWithModel("Canon", "EOS R5");
                Assert.Equal("Canon EOS R5", ExifReader.Parse(buffer, buffer.Length));
            });

            yield return Case("EXIF 解析相机型号（JPEG APP1 结构）", () =>
            {
                byte[] tiff = BuildTiffWithModel("NIKON CORPORATION", "NIKON Z 7");
                var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10 };
                jpeg.AddRange(System.Text.Encoding.ASCII.GetBytes("Exif"));
                jpeg.Add(0);
                jpeg.Add(0);
                jpeg.AddRange(tiff);
                byte[] buffer = jpeg.ToArray();

                Assert.Equal("NIKON CORPORATION NIKON Z 7", ExifReader.Parse(buffer, buffer.Length));
            });

            yield return Case("EXIF 解析对垃圾数据返回空值", () =>
            {
                var random = new byte[512];
                for (int i = 0; i < random.Length; i++) { random[i] = (byte)(i * 7 % 251); }
                Assert.Equal(string.Empty, ExifReader.Parse(random, random.Length));
                Assert.Equal(string.Empty, ExifReader.TryReadCameraModel("不存在的文件.jpg"));
            });
        }

        /// <summary>构造一个最小的小端 TIFF 结构，包含 Make 与 Model 两个 ASCII 标签。</summary>
        private static byte[] BuildTiffWithModel(string make, string model)
        {
            byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes(make + "\0");
            byte[] modelBytes = System.Text.Encoding.ASCII.GetBytes(model + "\0");

            const int ifdOffset = 8;
            const int entryCount = 2;
            int entriesStart = ifdOffset + 2;
            int nextIfdOffsetPosition = entriesStart + entryCount * 12;
            int dataStart = nextIfdOffsetPosition + 4;
            int makeOffset = dataStart;
            int modelOffset = dataStart + makeBytes.Length;

            var buffer = new List<byte>();
            buffer.AddRange(new byte[] { 0x49, 0x49, 0x2A, 0x00 });          // II* 标记
            buffer.AddRange(BitConverter.GetBytes((uint)ifdOffset));           // IFD0 偏移
            buffer.AddRange(BitConverter.GetBytes((ushort)entryCount));        // 条目数

            buffer.AddRange(BuildEntry(0x010F, makeBytes.Length, (uint)makeOffset));
            buffer.AddRange(BuildEntry(0x0110, modelBytes.Length, (uint)modelOffset));

            buffer.AddRange(BitConverter.GetBytes((uint)0));                   // 无下一个 IFD
            buffer.AddRange(makeBytes);
            buffer.AddRange(modelBytes);
            return buffer.ToArray();
        }

        private static byte[] BuildEntry(int tag, int count, uint valueOffset)
        {
            var entry = new List<byte>();
            entry.AddRange(BitConverter.GetBytes((ushort)tag));
            entry.AddRange(BitConverter.GetBytes((ushort)2));      // 类型 2 = ASCII
            entry.AddRange(BitConverter.GetBytes((uint)count));
            entry.AddRange(BitConverter.GetBytes(valueOffset));
            return entry.ToArray();
        }

        /// <summary>把 IProgress 回调收集到列表中的测试辅助类。</summary>
        private sealed class ProgressCollector : IProgress<ScanProgress>
        {
            private readonly List<ScanProgress> _target;
            public ProgressCollector(List<ScanProgress> target) { _target = target; }

            public void Report(ScanProgress value)
            {
                lock (_target) { _target.Add(value); }
            }
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
