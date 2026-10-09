# 变更记录（CHANGELOG）

本项目遵循"每次需求变更先文档后开发"的流程；版本号格式为 主版本.次版本.修订号。

## [v0.1-m1] M1 仓库骨架与基础设施

**新增**
- 解决方案骨架：AutoCardCopier.Core（领域逻辑）、AutoCardCopier.App（WPF 界面）、AutoCardCopier.Tests（自研控制台测试运行器）
- 构建脚本 build/build.ps1（自动定位 MSBuild）与 build/run-tests.ps1；共享编译属性 build/Common.props
- 基础设施：OperationResult、Humanize（字节/速度/时长格式化）、PathUtil（长路径、命名清洗、唯一命名）、
  JsonStore（DataContractJsonSerializer + 缩进美化 + 原子写入）、TimeUtil、SimpleJson、EventBus、AppPaths（数据目录规划与回退）
- 日志：FileLogger（按天滚动、单文件上限、保留天数清理）、TaskJournal（JSONL 任务流水）、NullLog/MemoryLog
- 设置：AppSettings（全部默认值 + 归一化）、SettingsStore（损坏自愈、原子写入、.bad 备份）
- 界面外壳：自绘标题栏（WindowChrome）、左侧导航、五个页面（卡片/项目库/历史/设置/关于）、浅色与深色主题、控件样式库
- 单元测试 22 个用例（基础设施、设置、日志），全部通过

**验证**
- 全解决方案编译通过（x64 / .NET Framework 4.8）
- 测试结果：通过 22，失败 0
- 界面实测：程序启动成功，窗口标题与页面渲染正常

## [未发布]

### 文档（M0）
- 新增《需求文档》《技术文档》《开发步骤清单》《开发说明模板》、AGENT.md、README.md、.gitignore
- 部署便携版 Git（tools/git，2.56.0），初始化版本控制仓库
