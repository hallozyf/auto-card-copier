using System;
using System.Collections.Generic;
using System.IO;
using AutoCardCopier.Core.Infrastructure;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.Core.Projects
{
    /// <summary>项目检索条件。</summary>
    public sealed class ProjectFilter
    {
        /// <summary>起始日期（含），为空表示不限。</summary>
        public DateTime? From { get; set; }

        /// <summary>结束日期（含），为空表示不限。</summary>
        public DateTime? To { get; set; }

        /// <summary>地点关键字。</summary>
        public string Location { get; set; }

        /// <summary>拍摄类型。</summary>
        public string ShootType { get; set; }

        /// <summary>全文关键字（项目名、备注、机型、地点）。</summary>
        public string Keyword { get; set; }
    }

    /// <summary>
    /// 本地项目库：每个项目一个 JSON 文件，放在数据目录的 projects 下。
    /// 设计取舍：启动时直接扫描目录而不是维护索引文件，
    /// 因为项目数量在千级以内时扫描耗时很低，而索引文件会引入"索引与真实数据不一致"的风险。
    /// 服务器上的 _project.json 是跨机器的事实来源，可随时重建本地库。
    /// </summary>
    public sealed class ProjectLibrary
    {
        /// <summary>项目元数据文件名（与 ManifestWriter 保持一致）。</summary>
        public const string ProjectFileName = "_project.json";

        /// <summary>扫描服务器目录时允许的最大递归深度。</summary>
        private const int MaxScanDepth = 4;

        private readonly string _directory;
        private readonly ILog _log;
        private readonly object _gate = new object();

        public ProjectLibrary(string directory, ILog log)
        {
            _directory = directory;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>项目库目录。</summary>
        public string Directory { get { return _directory; } }

        /// <summary>读取全部项目（按拍摄日期倒序）。</summary>
        public IList<ProjectMeta> LoadAll()
        {
            var projects = new List<ProjectMeta>();
            try
            {
                if (!System.IO.Directory.Exists(_directory)) { return projects; }

                foreach (string file in System.IO.Directory.GetFiles(_directory, "*.json"))
                {
                    try
                    {
                        ProjectMeta meta = JsonStore.Deserialize<ProjectMeta>(JsonStore.ReadAllText(file));
                        if (meta != null && !string.IsNullOrEmpty(meta.Id)) { projects.Add(meta); }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("跳过无法解析的项目文件：" + file, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("读取项目库失败", ex);
            }

            projects.Sort(delegate (ProjectMeta a, ProjectMeta b)
            {
                return string.CompareOrdinal(b.ShootDate, a.ShootDate);
            });
            return projects;
        }

        /// <summary>按条件检索。</summary>
        public IList<ProjectMeta> Search(ProjectFilter filter)
        {
            IList<ProjectMeta> all = LoadAll();
            if (filter == null) { return all; }

            var result = new List<ProjectMeta>();
            foreach (ProjectMeta project in all)
            {
                if (!Matches(project, filter)) { continue; }
                result.Add(project);
            }
            return result;
        }

        /// <summary>保存（新增或更新）一个项目。</summary>
        public OperationResult Save(ProjectMeta project)
        {
            if (project == null || string.IsNullOrEmpty(project.Id)) { return OperationResult.Fail("项目信息不完整"); }
            lock (_gate)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    JsonStore.WriteAllTextAtomic(FilePath(project.Id), JsonStore.Serialize(project));
                    return OperationResult.Ok("项目已保存");
                }
                catch (Exception ex)
                {
                    _log.Error("保存项目失败", ex);
                    return OperationResult.Fail("保存项目失败：" + ex.Message, ex);
                }
            }
        }

        /// <summary>
        /// 更新项目：同时写本地库与服务器上的 _project.json，
        /// 保证换一台电脑或在 NAS 上直接看目录时信息同样完整。
        /// </summary>
        public OperationResult Update(ProjectMeta project)
        {
            if (project == null) { return OperationResult.Fail("项目为空"); }
            project.UpdatedAt = TimeUtil.ToIso(DateTime.Now);
            OperationResult local = Save(project);

            if (!string.IsNullOrWhiteSpace(project.TargetPath) && System.IO.Directory.Exists(project.TargetPath))
            {
                try
                {
                    JsonStore.WriteAllTextAtomic(
                        PathUtil.Combine(project.TargetPath, ProjectFileName),
                        JsonStore.Serialize(project));
                }
                catch (Exception ex)
                {
                    _log.Warn("写入服务器项目文件失败：" + project.TargetPath, ex);
                    if (local.Success)
                    {
                        return OperationResult.Fail("本地已保存，但写入项目目录失败：" + ex.Message, ex);
                    }
                }
            }
            return local;
        }

        /// <summary>删除本地记录（不会删除磁盘上的素材）。</summary>
        public OperationResult Delete(string projectId)
        {
            if (string.IsNullOrEmpty(projectId)) { return OperationResult.Fail("项目标识为空"); }
            try
            {
                string path = FilePath(projectId);
                if (System.IO.File.Exists(path)) { System.IO.File.Delete(path); }
                return OperationResult.Ok("已删除项目记录（素材文件未删除）");
            }
            catch (Exception ex)
            {
                _log.Warn("删除项目记录失败", ex);
                return OperationResult.Fail("删除项目记录失败：" + ex.Message, ex);
            }
        }

        /// <summary>读取项目目录中的 _project.json（用于"重新校验项目"等场景）。</summary>
        public ProjectMeta ReadFromProjectDirectory(string projectDirectory)
        {
            try
            {
                string path = PathUtil.Combine(projectDirectory, ProjectFileName);
                if (!System.IO.File.Exists(PathUtil.Extend(path))) { return null; }
                return JsonStore.Deserialize<ProjectMeta>(JsonStore.ReadAllText(path));
            }
            catch (Exception ex)
            {
                _log.Warn("读取项目目录中的元数据失败：" + projectDirectory, ex);
                return null;
            }
        }

        /// <summary>
        /// 从服务器重建本地项目库：扫描目标根目录下若干层内的 _project.json。
        /// 冲突处理：以 updatedAt 较新的一份为准。
        /// </summary>
        public OperationResult<int> RebuildFromServer(string targetRoot)
        {
            if (string.IsNullOrWhiteSpace(targetRoot)) { return OperationResult.Fail<int>("尚未设置目标根目录"); }
            if (!System.IO.Directory.Exists(targetRoot)) { return OperationResult.Fail<int>("目标根目录不存在：" + targetRoot); }

            int imported = 0;
            int updated = 0;
            try
            {
                var existing = new Dictionary<string, ProjectMeta>(StringComparer.OrdinalIgnoreCase);
                foreach (ProjectMeta project in LoadAll()) { existing[project.Id] = project; }

                foreach (string file in EnumerateProjectFiles(targetRoot, 0))
                {
                    try
                    {
                        ProjectMeta remote = JsonStore.Deserialize<ProjectMeta>(JsonStore.ReadAllText(file));
                        if (remote == null || string.IsNullOrEmpty(remote.Id)) { continue; }

                        ProjectMeta local;
                        if (existing.TryGetValue(remote.Id, out local))
                        {
                            if (string.CompareOrdinal(remote.UpdatedAt, local.UpdatedAt) <= 0) { continue; }
                            updated++;
                        }
                        else
                        {
                            imported++;
                        }

                        // 服务器副本可能缺少本地字段，这里以服务器内容为准写回本地
                        remote.TargetPath = System.IO.Path.GetDirectoryName(file);
                        Save(remote);
                        existing[remote.Id] = remote;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("跳过无法解析的项目文件：" + file, ex);
                    }
                }

                string message = string.Format("重建完成：新增 {0} 个，更新 {1} 个", imported, updated);
                _log.Info(message);
                return OperationResult.Ok(imported + updated, message);
            }
            catch (Exception ex)
            {
                _log.Error("重建项目库失败", ex);
                return OperationResult.Fail<int>("重建项目库失败：" + ex.Message, ex);
            }
        }

        /// <summary>递归查找 _project.json（限制深度，避免扫描整个磁盘）。</summary>
        private IEnumerable<string> EnumerateProjectFiles(string directory, int depth)
        {
            if (depth > MaxScanDepth) { yield break; }

            string[] files;
            string[] subDirectories;
            try
            {
                files = System.IO.Directory.GetFiles(directory, ProjectFileName);
                subDirectories = System.IO.Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (string file in files) { yield return file; }
            foreach (string sub in subDirectories)
            {
                foreach (string file in EnumerateProjectFiles(sub, depth + 1)) { yield return file; }
            }
        }

        private string FilePath(string projectId)
        {
            return System.IO.Path.Combine(_directory, PathUtil.SanitizeName(projectId, 64) + ".json");
        }

        private static bool Matches(ProjectMeta project, ProjectFilter filter)
        {
            if (filter.From.HasValue || filter.To.HasValue)
            {
                DateTime shootDate = project.GetShootDate();
                if (filter.From.HasValue && shootDate.Date < filter.From.Value.Date) { return false; }
                if (filter.To.HasValue && shootDate.Date > filter.To.Value.Date) { return false; }
            }

            if (!string.IsNullOrWhiteSpace(filter.Location)
                && !Contains(project.Location, filter.Location)) { return false; }

            if (!string.IsNullOrWhiteSpace(filter.ShootType)
                && !string.Equals(project.ShootType, filter.ShootType, StringComparison.OrdinalIgnoreCase)) { return false; }

            if (!string.IsNullOrWhiteSpace(filter.Keyword))
            {
                string keyword = filter.Keyword.Trim();
                if (!Contains(project.Name, keyword)
                    && !Contains(project.Location, keyword)
                    && !Contains(project.Note, keyword)
                    && !Contains(project.CameraModel, keyword)
                    && !Contains(project.ShootType, keyword)
                    && !Contains(project.Photographer, keyword))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Contains(string text, string value)
        {
            if (string.IsNullOrEmpty(text)) { return false; }
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
