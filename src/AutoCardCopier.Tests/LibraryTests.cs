using System;
using System.Collections.Generic;
using System.IO;
using AutoCardCopier.Core.History;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;
using AutoCardCopier.Core.Projects;

namespace AutoCardCopier.Tests
{
    /// <summary>项目库与任务历史的单元测试。</summary>
    public static class LibraryTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> Cases()
        {
            yield return Case("项目保存与读取", () =>
            {
                using (var temp = new TempDirectory("library"))
                {
                    var library = new ProjectLibrary(temp.Path, NullLog.Instance);
                    var project = NewProject("2026-03-10", "婚礼", "上海外滩");
                    Assert.True(library.Save(project).Success);

                    IList<ProjectMeta> all = library.LoadAll();
                    Assert.Equal(1, all.Count);
                    Assert.Equal("上海外滩", all[0].Location);
                    Assert.Equal("婚礼", all[0].ShootType);
                }
            });

            yield return Case("项目检索支持条件组合", () =>
            {
                using (var temp = new TempDirectory("library"))
                {
                    var library = new ProjectLibrary(temp.Path, NullLog.Instance);
                    library.Save(NewProject("2026-03-10", "婚礼", "上海外滩", note: "客户A"));
                    library.Save(NewProject("2026-02-01", "商业产品", "深圳南山", camera: "Canon EOS R5"));
                    library.Save(NewProject("2025-12-20", "风光旅行", "杭州西湖"));

                    Assert.Equal(3, library.Search(null).Count);
                    Assert.Equal(1, library.Search(new ProjectFilter { ShootType = "婚礼" }).Count);
                    Assert.Equal(1, library.Search(new ProjectFilter { Location = "深圳" }).Count);
                    Assert.Equal(1, library.Search(new ProjectFilter { Keyword = "EOS R5" }).Count);
                    Assert.Equal(2, library.Search(new ProjectFilter { From = new DateTime(2026, 1, 1) }).Count);
                    Assert.Equal(1, library.Search(new ProjectFilter
                    {
                        From = new DateTime(2026, 1, 1),
                        To = new DateTime(2026, 12, 31),
                        Keyword = "杭州"
                    }).Count == 0 ? 1 : 0);
                }
            });

            yield return Case("更新项目会写回项目目录", () =>
            {
                using (var temp = new TempDirectory("library"))
                using (var projectDir = new TempDirectory("project"))
                {
                    var library = new ProjectLibrary(temp.Path, NullLog.Instance);
                    ProjectMeta project = NewProject("2026-03-10", "婚礼", "上海外滩");
                    project.TargetPath = projectDir.Path;
                    library.Save(project);

                    project.Location = "上海静安";
                    Assert.True(library.Update(project).Success);

                    string serverFile = Path.Combine(projectDir.Path, ProjectLibrary.ProjectFileName);
                    Assert.True(File.Exists(serverFile), "服务器侧应当生成 _project.json");
                    ProjectMeta reloaded = library.ReadFromProjectDirectory(projectDir.Path);
                    Assert.Equal("上海静安", reloaded.Location);
                }
            });

            yield return Case("删除项目记录不影响素材", () =>
            {
                using (var temp = new TempDirectory("library"))
                {
                    var library = new ProjectLibrary(temp.Path, NullLog.Instance);
                    ProjectMeta project = NewProject("2026-03-10", "婚礼", "上海外滩");
                    library.Save(project);
                    Assert.True(library.Delete(project.Id).Success);
                    Assert.Equal(0, library.LoadAll().Count);
                }
            });

            yield return Case("可以从服务器目录重建项目库", () =>
            {
                using (var server = new TempDirectory("nas"))
                using (var local = new TempDirectory("library"))
                {
                    // 模拟 NAS 上已有的两个项目目录
                    string p1 = Path.Combine(server.Path, "2026", "2026-03-10_婚礼_上海外滩");
                    string p2 = Path.Combine(server.Path, "2026", "2026-02-01_商业产品_深圳南山");
                    Directory.CreateDirectory(p1);
                    Directory.CreateDirectory(p2);

                    var remote1 = NewProject("2026-03-10", "婚礼", "上海外滩");
                    remote1.TargetPath = p1;
                    var remote2 = NewProject("2026-02-01", "商业产品", "深圳南山");
                    remote2.TargetPath = p2;
                    JsonStore.WriteAllTextAtomic(Path.Combine(p1, ProjectLibrary.ProjectFileName), JsonStore.Serialize(remote1));
                    JsonStore.WriteAllTextAtomic(Path.Combine(p2, ProjectLibrary.ProjectFileName), JsonStore.Serialize(remote2));

                    var library = new ProjectLibrary(local.Path, NullLog.Instance);
                    var result = library.RebuildFromServer(server.Path);
                    Assert.True(result.Success, result.Message);
                    Assert.Equal(2, result.Value);
                    Assert.Equal(2, library.LoadAll().Count);
                }
            });

            yield return Case("任务历史记录与中断检测", () =>
            {
                using (var temp = new TempDirectory("history"))
                {
                    string path = Path.Combine(temp.Path, "history.json");
                    var store = new TaskHistoryStore(path, NullLog.Instance);

                    store.Add(new TaskRecord
                    {
                        Id = "t1", StatusText = TaskStatus.Running.ToString(),
                        StartedAt = TimeUtil.ToIso(DateTime.Now), ProjectName = "2026-03-10_婚礼_上海外滩"
                    });

                    Assert.Equal(1, store.LoadAll().Count);

                    IList<TaskRecord> interrupted = store.TakeInterruptedTasks();
                    Assert.Equal(1, interrupted.Count);

                    // 第二次调用不应再报告同一个任务
                    Assert.Equal(0, store.TakeInterruptedTasks().Count);
                    Assert.Equal(TaskStatus.Failed, store.LoadAll()[0].Status);
                    Assert.Contains("中断", store.LoadAll()[0].Message);
                }
            });

            yield return Case("任务历史支持更新已有记录", () =>
            {
                using (var temp = new TempDirectory("history"))
                {
                    string path = Path.Combine(temp.Path, "history.json");
                    var store = new TaskHistoryStore(path, NullLog.Instance);
                    var record = new TaskRecord { Id = "t1", StatusText = TaskStatus.Running.ToString(), StartedAt = TimeUtil.ToIso(DateTime.Now) };
                    store.Add(record);

                    record.StatusText = TaskStatus.Succeeded.ToString();
                    record.CopiedFiles = 100;
                    store.Upsert(record);

                    IList<TaskRecord> all = store.LoadAll();
                    Assert.Equal(1, all.Count);
                    Assert.Equal(TaskStatus.Succeeded, all[0].Status);
                    Assert.Equal(100, all[0].CopiedFiles);
                    Assert.Equal("成功", all[0].StatusDisplay);
                }
            });
        }

        private static ProjectMeta NewProject(string shootDate, string shootType, string location,
            string note = "", string camera = "")
        {
            return new ProjectMeta
            {
                SchemaVersion = 1,
                Id = ProjectMeta.NewId(),
                Name = shootDate + "_" + shootType + "_" + location,
                ShootDate = shootDate,
                Location = location,
                ShootType = shootType,
                Note = note,
                CameraModel = camera,
                CreatedAt = TimeUtil.ToIso(DateTime.Now),
                UpdatedAt = TimeUtil.ToIso(DateTime.Now)
            };
        }

        private static KeyValuePair<string, Action> Case(string name, Action body)
        {
            return new KeyValuePair<string, Action>(name, body);
        }
    }
}
