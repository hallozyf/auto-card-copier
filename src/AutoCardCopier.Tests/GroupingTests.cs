using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Tests
{
    /// <summary>多日素材自动分组（F13）的测试。</summary>
    public static class GroupingTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("F14-C 清单外置：素材目录保持纯净", StorageExternalization);

            yield return Case("F13-A 读取 EXIF 原始拍摄时间", () =>
            {
                using (var temp = new TempDirectory("exif"))
                {
                    var shootTime = new DateTime(2026, 3, 10, 15, 30, 12);
                    string path = temp.Combine("IMG_0001.JPG");
                    WriteJpegWithShootTime(path, shootTime);

                    var result = MediaTimestampReader.Read(path, MediaKind.Photo, false);
                    Assert.Equal(ShootTimeSource.ExifOriginal, result.Source);
                    Assert.Equal(shootTime, result.Time.Value);
                }
            });

            yield return Case("F13-A 无 EXIF 时回退文件时间", () =>
            {
                using (var temp = new TempDirectory("nodata"))
                {
                    string path = temp.Combine("IMG_0002.JPG");
                    FixtureFactory.WriteFile(path, 512);
                    var expected = new DateTime(2026, 3, 11, 9, 0, 0);
                    File.SetLastWriteTime(path, expected);

                    var result = MediaTimestampReader.Read(path, MediaKind.Photo, false);
                    Assert.Equal(ShootTimeSource.FileTime, result.Source);
                    Assert.Equal(expected, result.Time.Value);
                }
            });

            yield return Case("F13-A 读取 MP4 容器创建时间（mvhd）", () =>
            {
                using (var temp = new TempDirectory("mvhd"))
                {
                    var local = new DateTime(2026, 3, 12, 18, 20, 0);
                    string path = temp.Combine("C0001.MP4");
                    WriteMp4WithCreationTime(path, local);

                    var result = MediaTimestampReader.Read(path, MediaKind.Video, false);
                    Assert.Equal(ShootTimeSource.Container, result.Source);
                    Assert.True(Math.Abs((result.Time.Value - local).TotalMinutes) < 2,
                        "容器时间换算后应接近 " + local + "，实际 " + result.Time);
                }
            });

            yield return Case("F13-A 打开开关后视频使用文件时间", () =>
            {
                using (var temp = new TempDirectory("videoflag"))
                {
                    string path = temp.Combine("C0002.MP4");
                    WriteMp4WithCreationTime(path, new DateTime(2026, 3, 12, 18, 20, 0));
                    var fileTime = new DateTime(2026, 3, 20, 8, 0, 0);
                    File.SetLastWriteTime(path, fileTime);

                    var result = MediaTimestampReader.Read(path, MediaKind.Video, true);
                    Assert.Equal(ShootTimeSource.FileTime, result.Source);
                    Assert.Equal(fileTime, result.Time.Value);
                }
            });

            yield return Case("F13-B 按拍摄日期分组且计数守恒", () =>
            {
                var files = new List<ScannedFile>
                {
                    NewFile("a.jpg", new DateTime(2026, 3, 10, 9, 0, 0), 100),
                    NewFile("b.jpg", new DateTime(2026, 3, 10, 18, 0, 0), 200),
                    NewFile("c.mp4", new DateTime(2026, 3, 11, 10, 0, 0), 300),
                    NewFile("d.jpg", new DateTime(2026, 3, 12, 23, 59, 0), 400)
                };

                IList<MediaDayGroup> groups = MediaDayGrouper.Group(files);
                Assert.Equal(3, groups.Count);
                Assert.Equal(new DateTime(2026, 3, 10), groups[0].Date.Value);
                Assert.Equal(2, groups[0].TotalCount);
                Assert.Equal(300L, groups[0].TotalBytes);
                Assert.Equal(new DateTime(2026, 3, 11), groups[1].Date.Value);
                Assert.Equal(new DateTime(2026, 3, 12), groups[2].Date.Value);

                int total = 0;
                foreach (MediaDayGroup group in groups) { total += group.TotalCount; }
                Assert.Equal(files.Count, total);
            });

            yield return Case("F13-B 未知时间单独成组", () =>
            {
                var files = new List<ScannedFile>
                {
                    NewFile("a.jpg", new DateTime(2026, 3, 10, 9, 0, 0), 100),
                    NewFile("x.bin", (DateTime?)null, 50)
                };

                IList<MediaDayGroup> groups = MediaDayGrouper.Group(files);
                Assert.Equal(2, groups.Count);
                Assert.True(groups[1].IsUnknownDate);
                Assert.Equal(1, groups[1].TotalCount);
            });

            yield return Case("F13 扫描时按自然日分组（真实文件时间）", () =>
            {
                using (var card = new TempDirectory("card"))
                {
                    string folder = Path.Combine(card.Path, "DCIM", "100CANON");
                    Directory.CreateDirectory(folder);
                    string day1 = Path.Combine(folder, "IMG_0001.JPG");
                    string day2 = Path.Combine(folder, "IMG_0002.JPG");
                    string day3 = Path.Combine(folder, "IMG_0003.JPG");
                    FixtureFactory.WriteFile(day1, 256);
                    FixtureFactory.WriteFile(day2, 256);
                    FixtureFactory.WriteFile(day3, 256);
                    File.SetLastWriteTime(day1, new DateTime(2026, 3, 10, 10, 0, 0));
                    File.SetLastWriteTime(day2, new DateTime(2026, 3, 11, 11, 0, 0));
                    File.SetLastWriteTime(day3, new DateTime(2026, 3, 12, 12, 0, 0));

                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);

                    Assert.Equal(3, scan.DayCount);
                    Assert.Equal(3, scan.Groups.Count);
                    Assert.Equal(0, scan.UnknownTimeCount);
                }
            });

            yield return Case("F13-D 多项目拷贝：每个日期各自成目录并独立校验", () =>
            {
                using (var card = new TempDirectory("card"))
                using (var target = new TempDirectory("target"))
                {
                    string folder = Path.Combine(card.Path, "DCIM", "100CANON");
                    Directory.CreateDirectory(folder);
                    var days = new[] { new DateTime(2026, 3, 10), new DateTime(2026, 3, 11), new DateTime(2026, 3, 12) };
                    for (int i = 0; i < days.Length; i++)
                    {
                        string path = Path.Combine(folder, string.Format("IMG_{0:0000}.JPG", i + 1));
                        FixtureFactory.WriteFile(path, 1024);
                        File.SetLastWriteTime(path, days[i].AddHours(10 + i));
                    }

                    AppSettings settings = AppSettings.CreateDefault();
                    settings.ParallelProjects = 2;
                    settings.PrimaryTargetRoot = target.Path;
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);

                    var requests = new List<ProjectGroupRequest>();
                    foreach (MediaDayGroup group in scan.Groups)
                    {
                        var context = new ProjectContext
                        {
                            ShootDate = group.Date.Value,
                            ShootType = "人像写真",
                            Location = "百旺公园"
                        };
                        requests.Add(new ProjectGroupRequest
                        {
                            Context = context,
                            Files = group.Files,
                            RelativeDirectory = "2026\\" + group.DisplayDate + "_人像写真_百旺公园",
                            DisplayName = group.DisplayDate
                        });
                    }

                    var coordinator = new BackupCoordinator(new CopyPlanBuilder(NullLog.Instance), new CopyEngine(NullLog.Instance), NullLog.Instance);
                    OperationResult<BackupPlanSet> planResult = coordinator
                        .BuildPlansAsync(requests, scan.RootPath, settings).GetAwaiter().GetResult();
                    Assert.True(planResult.Success, planResult.Message);
                    BackupPlanSet planSet = planResult.Value;

                    Assert.Equal(3, planSet.Units.Count);

                    BackupResult result = coordinator.RunAsync(planSet, scan, settings, null, new PauseToken(), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.True(result.AllSucceeded, result.Summary);
                    Assert.Equal(3, result.Projects.Count);
                    Assert.Equal(3, result.TotalCopiedFiles);

                    foreach (ProjectOutcome project in result.Projects)
                    {
                        Assert.True(Directory.Exists(project.ProjectDirectory), "项目目录应当存在：" + project.ProjectDirectory);
                        int files;
                        long bytes;
                        FixtureFactory.MeasureDirectory(project.ProjectDirectory, out files, out bytes);
                        Assert.Equal(1, files);
                        Assert.Equal(1024L, bytes);
                    }
                }
            });
        }

        /// <summary>F14-C：校验清单写入应用目录，素材目录不留任何软件文件。</summary>
        public static void StorageExternalization()
        {
            using (var data = new TempDirectory("data"))
            using (var target = new TempDirectory("target"))
            {
                AppSettings settings = AppSettings.CreateDefault();
                settings.PrimaryTargetRoot = target.Path;
                settings.WriteSelfDescribingFiles = false;

                var log = NullLog.Instance;
                var writer = new AutoCardCopier.Core.Copying.ManifestWriter(data.Path, log);

                var plan = new CopyPlan
                {
                    TargetRoot = target.Path,
                    ProjectDirectory = Path.Combine(target.Path, "2026", "测试项目"),
                    ProjectRelativePath = "2026\\测试项目",
                    ProjectName = "测试项目"
                };
                Directory.CreateDirectory(plan.ProjectDirectory);
                plan.Items.Add(new CopyItem
                {
                    Source = new ScannedFile { FullPath = Path.Combine(plan.ProjectDirectory, "IMG_0001.JPG"), RelativePath = "IMG_0001.JPG", Length = 10, Kind = MediaKind.Photo, ShootTime = new DateTime(2026, 3, 10, 9, 0, 0) },
                    TargetPath = Path.Combine(plan.ProjectDirectory, "IMG_0001.JPG"),
                    Action = CopyAction.Copy
                });

                var copyResult = new CopyResult();
                copyResult.EnsurePathCollections();
                var report = new VerifyReport();

                var result = writer.WriteProjectFiles(plan, null, new ProjectContext { ShootDate = new DateTime(2026, 3, 10) },
                    settings, copyResult, report, null, "proj-test-001");
                Assert.True(result.Success, result.Message);

                // 素材目录里不应出现软件文件
                string[] leftovers = Directory.GetFiles(plan.ProjectDirectory);
                Assert.Equal(0, leftovers.Length);

                // 应用数据目录里应当有清单与日志
                string manifest = Path.Combine(data.Path, "manifests", "proj-test-001.manifest.json");
                Assert.True(File.Exists(manifest), "清单应写入应用数据目录：" + manifest);
                Assert.True(File.Exists(Path.Combine(data.Path, "manifests", "proj-test-001.copylog.txt")), "拷贝日志应写入应用数据目录");
            }
        }

        private static ScannedFile NewFile(string name, DateTime? shootTime, long length)
        {
            return new ScannedFile
            {
                FullPath = @"C:\card\" + name,
                RelativePath = name,
                Length = length,
                LastWriteTimeUtc = (shootTime ?? new DateTime(2026, 1, 1)).ToUniversalTime(),
                Kind = name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? MediaKind.Video : MediaKind.Photo,
                ShootTime = shootTime,
                ShootTimeSource = shootTime.HasValue ? ShootTimeSource.ExifOriginal : ShootTimeSource.Unknown
            };
        }

        /// <summary>构造一个含 EXIF DateTimeOriginal 的最小 JPEG 文件。</summary>
        private static void WriteJpegWithShootTime(string path, DateTime shootTime)
        {
            byte[] tiff = BuildTiffWithShootTime(shootTime);
            var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x00 };
            bytes.AddRange(Encoding.ASCII.GetBytes("Exif"));
            bytes.Add(0);
            bytes.Add(0);
            bytes.AddRange(tiff);
            bytes.Add(0xFF);
            bytes.Add(0xD9);
            File.WriteAllBytes(path, bytes.ToArray());
        }

        /// <summary>构造最小 TIFF：IFD0 指向 Exif SubIFD，SubIFD 内含 DateTimeOriginal。</summary>
        private static byte[] BuildTiffWithShootTime(DateTime shootTime)
        {
            byte[] dateBytes = Encoding.ASCII.GetBytes(shootTime.ToString("yyyy:MM:dd HH:mm:ss") + "\0");
            const int ifd0Offset = 8;
            const int subIfdOffset = ifd0Offset + 2 + 12 + 4;      // IFD0 = 计数 + 1 条目 + 下一个 IFD 偏移
            int dateOffset = subIfdOffset + 2 + 12 + 4;

            var buffer = new List<byte>();
            buffer.AddRange(new byte[] { 0x49, 0x49, 0x2A, 0x00 });
            buffer.AddRange(BitConverter.GetBytes((uint)ifd0Offset));

            buffer.AddRange(BitConverter.GetBytes((ushort)1));
            buffer.AddRange(BitConverter.GetBytes((ushort)0x8769));
            buffer.AddRange(BitConverter.GetBytes((ushort)4));
            buffer.AddRange(BitConverter.GetBytes((uint)1));
            buffer.AddRange(BitConverter.GetBytes((uint)subIfdOffset));
            buffer.AddRange(BitConverter.GetBytes((uint)0));

            buffer.AddRange(BitConverter.GetBytes((ushort)1));
            buffer.AddRange(BitConverter.GetBytes((ushort)0x9003));
            buffer.AddRange(BitConverter.GetBytes((ushort)2));
            buffer.AddRange(BitConverter.GetBytes((uint)dateBytes.Length));
            buffer.AddRange(BitConverter.GetBytes((uint)dateOffset));
            buffer.AddRange(BitConverter.GetBytes((uint)0));

            buffer.AddRange(dateBytes);
            return buffer.ToArray();
        }

        /// <summary>构造一个含 mvhd 创建时间的最小 MP4 头。</summary>
        private static void WriteMp4WithCreationTime(string path, DateTime localTime)
        {
            var epoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long seconds = (long)(localTime.ToUniversalTime() - epoch).TotalSeconds;

            var buffer = new List<byte>();
            buffer.AddRange(BitConverter.GetBytes(16));               // ftyp 盒长度
            buffer.AddRange(Encoding.ASCII.GetBytes("ftyp"));
            buffer.AddRange(Encoding.ASCII.GetBytes("isom"));
            buffer.AddRange(BitConverter.GetBytes(0));

            buffer.AddRange(BitConverter.GetBytes(108));              // mvhd 盒长度
            buffer.AddRange(Encoding.ASCII.GetBytes("mvhd"));
            buffer.Add(0);                                            // version
            buffer.AddRange(new byte[] { 0, 0, 0 });                  // flags
            var secondsBytes = BitConverter.GetBytes((uint)seconds);
            Array.Reverse(secondsBytes);                              // 大端
            buffer.AddRange(secondsBytes);
            buffer.AddRange(new byte[80]);                            // 其余字段占位

            File.WriteAllBytes(path, buffer.ToArray());
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
