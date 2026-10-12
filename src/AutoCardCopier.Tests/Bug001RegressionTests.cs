using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoCardCopier.Core.Copying;
using AutoCardCopier.Core.Formatting;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;
using AutoCardCopier.Core.Scanning;
using AutoCardCopier.Core.Settings;

namespace AutoCardCopier.Tests
{
    /// <summary>
    /// BUG-001 回归测试：视频文件未被扫描。
    ///
    /// 缺陷背景：早期实现只扫描"第一个特征目录"（DCIM 优先），
    /// 导致"照片在 DCIM、视频在 PRIVATE / AVCHD / CONTENTS"的卡漏扫全部视频，
    /// 进而出现"漏拷 + 校验通过 + 格式化解锁"的数据丢失风险。
    ///
    /// 本组用例锁死修复后的行为，任何人改动扫描范围逻辑都必须让这些用例保持通过。
    /// </summary>
    public static class Bug001RegressionTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("TV-01 照片在 DCIM、视频在 PRIVATE 与 AVCHD 时必须全部扫描到", () =>
            {
                using (var card = new TempDirectory("bug001"))
                {
                    CreateSonyLikeCard(card.Path);

                    var scanner = new MediaScanner(NullLog.Instance);
                    ScanResult result = scanner.Scan(card.Path, AppSettings.CreateDefault(), null, CancellationToken.None);

                    Assert.Equal(1, result.PhotoCount);
                    Assert.Equal(4, result.VideoCount);   // 修复前这里只有 1
                    Assert.True(result.ScannedRoots.Count >= 3, "扫描范围应包含 DCIM、AVCHD、PRIVATE");
                }
            });

            yield return Case("TV-01b 即使关闭“扫描卡根下所有目录”，特征目录也必须全部覆盖", () =>
            {
                using (var card = new TempDirectory("bug001"))
                {
                    CreateSonyLikeCard(card.Path);
                    AppSettings settings = AppSettings.CreateDefault();
                    settings.ScanWholeCardRoot = false;

                    var scanner = new MediaScanner(NullLog.Instance);
                    ScanResult result = scanner.Scan(card.Path, settings, null, CancellationToken.None);

                    Assert.Equal(4, result.VideoCount);
                }
            });

            yield return Case("TV-02 佳能 XF 结构（CONTENTS 下的 MXF）必须被扫描到", () =>
            {
                using (var card = new TempDirectory("bug001"))
                {
                    FixtureFactory.WriteFile(card.Combine("CONTENTS", "CLIPS001", "A001C001.MXF"), 16384);
                    FixtureFactory.WriteFile(card.Combine("DCIM", "100CANON", "IMG_0001.JPG"), 2048);

                    var scanner = new MediaScanner(NullLog.Instance);
                    ScanResult result = scanner.Scan(card.Path, AppSettings.CreateDefault(), null, CancellationToken.None);

                    Assert.Equal(1, result.VideoCount);
                    Assert.Equal(1, result.PhotoCount);
                }
            });

            yield return Case("TV-03 卡根下的自建目录（非特征目录）默认也要扫描", () =>
            {
                using (var card = new TempDirectory("bug001"))
                {
                    FixtureFactory.WriteFile(card.Combine("DCIM", "100CANON", "IMG_0001.JPG"), 2048);
                    // 故意用不在特征目录清单里的名字，用来验证"扫描卡根下其它目录"这个开关
                    FixtureFactory.WriteFile(card.Combine("MYCLIPS", "clip_0001.mp4"), 8192);

                    var scanner = new MediaScanner(NullLog.Instance);
                    ScanResult enabled = scanner.Scan(card.Path, AppSettings.CreateDefault(), null, CancellationToken.None);
                    Assert.Equal(1, enabled.VideoCount);

                    AppSettings narrow = AppSettings.CreateDefault();
                    narrow.ScanWholeCardRoot = false;
                    ScanResult narrowed = scanner.Scan(card.Path, narrow, null, CancellationToken.None);
                    Assert.Equal(0, narrowed.VideoCount);   // 关闭开关后确实不扫描自建目录（符合设置语义）
                }
            });

            yield return Case("TV-04 拷贝计划必须包含全部视频，清单中的视频数量正确", () =>
            {
                using (var card = new TempDirectory("bug001"))
                using (var target = new TempDirectory("target"))
                {
                    CreateSonyLikeCard(card.Path);
                    AppSettings settings = AppSettings.CreateDefault();
                    ScanResult scan = new MediaScanner(NullLog.Instance).Scan(card.Path, settings, null, CancellationToken.None);

                    var context = new ProjectContext
                    {
                        ShootDate = new DateTime(2026, 3, 10),
                        ShootType = "视频摄制",
                        Location = "上海"
                    };
                    var builder = new CopyPlanBuilder(NullLog.Instance);
                    CopyPlan plan = builder.Build(scan, settings, context, target.Path).Value;

                    int videoItems = 0;
                    foreach (CopyItem item in plan.Items)
                    {
                        if (item.Kind == MediaKind.Video) { videoItems++; }
                    }
                    Assert.Equal(4, videoItems);

                    CopyResult copy = new CopyEngine(NullLog.Instance)
                        .RunAsync(plan, settings, null, new PauseToken(), CancellationToken.None).GetAwaiter().GetResult();
                    Assert.Equal(5, copy.CopiedFiles);

                    VerifyReport verify = VerifyService.Verify(plan, settings, null, CancellationToken.None, NullLog.Instance);
                    var writer = new ManifestWriter(NullLog.Instance);
                    ManifestModel manifest = writer.BuildManifest(plan, scan, context, settings, copy, verify, null, "test-id");
                    Assert.Equal(4, manifest.Summary.VideoCount);
                    Assert.Equal(5, manifest.Files.Count);
                }
            });

            yield return Case("TV-05 卡上存在未拷贝素材时，覆盖率检查必须报警且格式化解锁规则拒绝", () =>
            {
                using (var card = new TempDirectory("bug001"))
                {
                    CreateSonyLikeCard(card.Path);

                    // 模拟"只拷贝了 DCIM"的旧行为
                    var copied = new List<string>();
                    foreach (string file in Directory.GetFiles(card.Combine("DCIM"), "*", SearchOption.AllDirectories))
                    {
                        copied.Add(file);
                    }

                    CoverageReport report = CoverageChecker.Check(card.Path, AppSettings.CreateDefault(), copied,
                        NullLog.Instance, CancellationToken.None);

                    Assert.Equal(5, report.TotalMediaFiles);
                    Assert.Equal(3, report.UncoveredMediaFiles);   // PRIVATE 下 2 个 + AVCHD 下 1 个
                    Assert.False(report.FullyCovered);
                    Assert.Contains("未被本次拷贝覆盖", report.Message);

                    string reason = DiskSafetyInspector.CheckCoverageRule(report.UncoveredMediaFiles);
                    Assert.NotNull(reason);
                    Assert.Contains("拒绝格式化", reason);
                    Assert.Equal(null, DiskSafetyInspector.CheckCoverageRule(0));
                }
            });

            yield return Case("TV-05b 全部覆盖时覆盖率检查通过", () =>
            {
                using (var card = new TempDirectory("bug001"))
                {
                    CreateSonyLikeCard(card.Path);
                    var copied = new List<string>();
                    foreach (string file in Directory.GetFiles(card.Path, "*", SearchOption.AllDirectories))
                    {
                        copied.Add(file);
                    }

                    CoverageReport report = CoverageChecker.Check(card.Path, AppSettings.CreateDefault(), copied,
                        NullLog.Instance, CancellationToken.None);

                    Assert.True(report.FullyCovered, report.Message);
                    Assert.Equal(5, report.TotalMediaFiles);
                    Assert.Equal(null, DiskSafetyInspector.CheckCoverageRule(0));
                }
            });

            yield return Case("TV-04b 新增视频扩展名被正确识别", () =>
            {
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("clip0001.TS"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("movie.M2T"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("tape.tod"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("old.dv"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("stream.flv"));
                Assert.Equal(MediaKind.Video, MediaClassifier.Classify("web.webm"));
            });
        }

        /// <summary>
        /// 构造与用户实际卡片一致的结构（D 盘的 PRIVATE/M4ROOT/CLIP）：
        /// 照片在 DCIM，视频分别在 PRIVATE/M4ROOT/CLIP 与 AVCHD。
        /// </summary>
        private static void CreateSonyLikeCard(string root)
        {
            FixtureFactory.WriteFile(Path.Combine(root, "DCIM", "100MSDCF", "DSC00001.ARW"), 4096);
            FixtureFactory.WriteFile(Path.Combine(root, "DCIM", "100MSDCF", "C0001.MP4"), 8192);
            FixtureFactory.WriteFile(Path.Combine(root, "PRIVATE", "M4ROOT", "CLIP", "C0002.MP4"), 16384);
            FixtureFactory.WriteFile(Path.Combine(root, "PRIVATE", "M4ROOT", "CLIP", "C0003.MP4"), 16384);
            FixtureFactory.WriteFile(Path.Combine(root, "AVCHD", "BDMV", "STREAM", "00000.MTS"), 32768);
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
