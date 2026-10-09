# AGENT.md · 项目核心上下文（AI 助手与维护者必读）

> 本文件是"压缩后的项目记忆"。每次会话开始、每次上下文压缩后，先读本文件即可恢复上下文。
> **维护要求**：每个里程碑结束时更新"当前进度"与"关键决策"两节。

## 1. 项目一句话

给摄影师用的 Windows 桌面软件（自动拷卡助手 / AutoCardCopier）：插入内存卡后自动把照片与视频拷贝到文件服务器，自动建项目目录、自动校验、可选自动格式化卡片，并按"拍摄时间 + 地点 + 拍摄类型"做项目化管理。

## 2. 用户核心需求（原话提炼，不可偏离）

1. 拷卡流程自动化：不再手动建文件夹、手动复制、手动格式化；
2. 素材项目化管理：每个项目带拍摄时间、地点、拍摄类型；
3. 轻量化 + Windows 原生组件 + 界面美观。

## 3. 硬性开发要求（用户明确要求）

1. 代码不得堆砌在单个文件里（模块化，单文件 ≤ 约 400 行）；
2. 不产生多余临时文件；产生过的临时文件必须清理；
3. 代码人类可读，关键处有中文注释；
4. **开发阶段不要向用户提问**，自行决策并在开发说明中记录；
5. 开发前先出详细步骤清单，并依次执行；
6. 使用版本控制（本仓库使用便携 MinGit）；
7. 每次新需求都要重复"需求文档 → 技术文档 → 用户评审 → 开发 → 开发说明"的流程；
8. 上下文超过 70% 时主动压缩（把状态写入本文件与开发说明）。

## 4. 技术栈（已在本机实测，改动前必须重新验证）

| 项目 | 结论 |
|------|------|
| 语言与框架 | C# 8.0 + WPF + .NET Framework 4.8（传统 csproj，非 SDK 风格） |
| 构建 | MSBuild 18（VS BuildTools）：C 盘 Program Files (x86) 下 Microsoft Visual Studio 的 18 目录里 BuildTools 下的 MSBuild\Current\Bin\MSBuild.exe |
| 关键编译属性 | TargetFrameworkVersion=v4.8；PlatformTarget=x64；FrameworkPathOverride 指向系统 .NET Framework 运行时目录（Framework64 下的 v4.0.30319）；OutputPath=build/out |
| 第三方依赖 | **零依赖**（本机无法通过 NuGet 取包；.NET SDK 未安装） |
| 运行时 | 目标机器只需 Windows 自带 .NET Framework 4.8（Win10 1809+ 与 Win11 均自带） |
| 版本控制 | 便携 MinGit：tools/git/cmd/git.exe（未装系统 Git，未改 PATH） |
| 开发机 | Windows 11 25H2 x64，已装 .NET Framework 4.8.1，未装 .NET SDK |
| 网络 | PowerShell 与 curl 的 HTTPS 不可用（schannel 凭证失败）；Python 3.14 可正常 HTTPS（用于下载工具） |

## 5. 仓库结构

| 路径 | 说明 |
|------|------|
| AGENT.md | 本文件：核心上下文与约束 |
| README.md | 项目说明、构建与运行方式 |
| CHANGELOG.md | 变更记录 |
| docs/01-需求文档.md | 需求文档（功能、目的、效果、验收标准） |
| docs/02-技术文档.md | 技术文档（选型、架构、数据、实现路径、测试用例、缺陷预防） |
| docs/03-开发步骤清单.md | 里程碑与逐步执行清单 |
| docs/04-开发说明模板.md | 开发说明模板 |
| build/ | 构建脚本与产物（out 不入库） |
| src/AutoCardCopier.Core | 领域逻辑（无 UI 依赖，可单测） |
| src/AutoCardCopier.App | WPF 界面与视图模型 |
| src/AutoCardCopier.Tests | 控制台测试运行器 |
| tools/git | 便携 Git（体积大，已加入 .gitignore） |

## 6. 关键设计决策（已定，改动需记录原因）

1. 素材目录结构：目标根 / 年份 / 日期_类型_地点 / 分类子目录 + 三个附带文件（_project.json、_manifest.json、_copy.log）；
2. 数据存储用 JSON 文件（DataContractJsonSerializer），不用数据库；
3. 项目库本地一份 + 服务器 _project.json 一份，服务器副本是跨机器的事实来源，可从服务器重建本地库；
4. 拷贝采用"临时文件 + 原子改名"，绝不出现半截文件；
5. 校验默认"大小 + 抽样哈希"，另有不校验 / 仅大小 / CRC32 / SHA-256 四档；
6. 格式化有六条硬性护栏，且必须"本次拷贝 + 校验成功"后才解锁；
7. UI 使用 MVVM 自研极简基础设施（ObservableObject、RelayCommand），不引入框架；
8. 所有耗时操作在后台线程，进度节流上报（约 100 至 200 ms）。

## 7. 当前进度

| 里程碑 | 状态 |
|--------|------|
| M0 文档 | 已完成文档编写，**等待用户评审** |
| M1 仓库骨架 | 未开始 |
| M2 卡片识别与扫描 | 未开始 |
| M3 拷贝与校验 | 未开始 |
| M4 项目库与历史 | 未开始 |
| M5 界面完善 | 未开始 |
| M6 格式化与托盘与设置 | 未开始 |
| M7 测试与打包 | 未开始 |

## 8. 常用命令

```powershell
# 构建（仓库根目录）
.\build\build.ps1 -Configuration Release

# 运行测试
.\build\run-tests.ps1

# 运行程序
.\build\out\AutoCardCopier.App.exe

# Git（便携版）
.\tools\git\cmd\git.exe status
.\tools\git\cmd\git.exe add -A
.\tools\git\cmd\git.exe commit -m "feat(m1): 初始化仓库骨架"
```

## 9. 上下文压缩检查点

当上下文使用率超过 70% 时执行：

1. 把"当前进度、未完成任务、关键决策、踩过的坑"写入本文件第 7 节与对应开发说明文档；
2. 把必要的代码位置索引（文件路径 + 类名 + 作用）写入开发说明；
3. 在会话中用一段摘要继续，不再复述历史过程。

---

（本文件随开发推进持续更新，最后更新：M0 文档阶段）
