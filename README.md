# 自动拷卡助手（AutoCardCopier）

给摄影师用的 Windows 桌面软件：**插入内存卡 → 自动拷贝到文件服务器 → 自动校验 → 可选安全格式化**，
并按"拍摄时间 + 地点 + 拍摄类型"对每次拍摄做项目化管理。

> A lightweight Windows desktop app for photographers: plug in a memory card, get material copied to
> your file server with folder auto-creation, integrity verification, project management by shoot date /
> location / type, and an optional guarded card format. C# + WPF on .NET Framework 4.8, **zero third-party
> dependencies**. Documentation is in Chinese.

> 🤖 **本项目由 AI 编程 Agent「DeepSeek Harness」全栈开发**，人类作者负责需求、评审与验收。
> 接手前请读下文「关于本项目：由 AI Agent 全栈开发」一节。

**仓库地址**：<https://github.com/hallozyf/auto-card-copier>　｜　**许可证**：[MIT](LICENSE)　｜　**当前版本**：v0.5.0

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue.svg)](#技术栈)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512bd4.svg)](#技术栈)

## 特性一览

- 插卡自动识别（兼容被 Windows 识别为"固定磁盘"的 CFexpress / XQD 读卡器）
- 拷贝前自动生成项目目录，命名模板可自定义
- 并行拷贝，实时显示速度与剩余时间，可暂停与取消
- 五档完整性校验（默认大小 + 抽样哈希）
- **多目标备份**（主 + 副目标各自写入、各自校验，网络目标带 3 秒超时探测，界面不卡）
- **精确断点续传**（断电/拔卡后插回原卡点"继续上次任务"，只补拷未完成的文件）
- **多日素材自动分组**（照片读 EXIF、视频读容器时间，按天分项目，逐行改名后一次拷贝）
- **素材库管理**：历史素材目录导入识别、往已有项目按日期追加（同名冲突三选一）、跨天项目与两种目录布局
- **素材目录保持纯净**：清单与项目记录存应用数据目录，需要时可选"写自描述文件"
- 项目库检索（关键词、拍摄类型）、编辑回写
- 任务历史与"上次被中断"提示；重复拷贝自动跳过已完成文件
- 安全格式化：六道硬护栏，只有成功拷贝并校验通过后才可执行
- 绿色免安装，依赖 Windows 自带 .NET Framework 4.8

## 快速开始

```powershell
# 1. 构建（生成绿色版到 build\out\Release）
build\build.cmd Release

# 2. 运行
build\out\Release\AutoCardCopier.App.exe

# 3. 运行全部自动化测试（103 个用例）
build\test.cmd Release
```

首次运行后请到"设置"页填写素材目标根目录，然后插入内存卡即可开始。

## 文档

| 文档 | 说明 |
|------|------|
| docs/01-需求文档.md | 功能、目的、效果与验收标准（F01–F16） |
| docs/02-技术文档.md | 技术选型、架构、实现路径、测试用例、缺陷预防 |
| docs/03-开发步骤清单.md | 里程碑与逐步执行清单 |
| docs/04-开发说明模板.md | 每个里程碑的开发说明模板 |
| docs/05-使用手册.md | 面向用户的操作说明与常见问题（含 F11–F14） |
| docs/10、11 | F11 多目标备份与 F12 精确断点续传（需求 / 技术） |
| docs/14、15、16 | F13 多日素材自动分组（需求 / 技术 / 开发说明） |
| docs/17、18、19 | F14 素材库管理（需求 / 技术 / 开发说明） |
| docs/17-项目结构书.md | 分层、线程模型、数据格式与需求映射（接手先读） |
| docs/20-开源发布说明-GitHub.md | 开源发布过程、内容清理、本机网络适配、后续推送方式 |
| docs/12 | BUG-002 排故报告（点击拷贝挂起：根因与重写方案） |
| AGENT.md | 核心上下文速查（维护者与 AI 助手必读） |

## 技术栈

| 项目 | 说明 |
|------|------|
| 语言与框架 | C# 8.0 + WPF + .NET Framework 4.8（Windows 自带，无需安装） |
| 构建 | Visual Studio BuildTools 中的 MSBuild 18（见 build/Common.props） |
| 第三方依赖 | 无 |
| 版本控制 | 便携 Git（tools/git） |
| 测试 | 自研控制台测试运行器（103 个用例） |

## 状态

v0.5.0：识别 → 扫描 → 分组 → 拷贝 → 校验 → 清单 → 项目库 → 历史 → 格式化 全流程可用，
并已具备多目标备份、精确断点续传、多日自动分组、素材库导入/追加/跨天项目。
待真人验证项见 docs/19-开发说明-素材库管理-F14-v0.5.0.md 第 7 章。

## 仓库结构

```
build/                   构建与测试脚本（MSBuild，无 SDK 依赖）
docs/                    需求、技术、排故、开发说明、使用手册、项目结构书
src/AutoCardCopier.Core  领域逻辑（无 UI 依赖，可单测）
src/AutoCardCopier.App   WPF 界面与视图模型
src/AutoCardCopier.Tests 自研控制台测试运行器（103 个用例）
```

更细的分层、线程模型与数据格式见 [docs/17-项目结构书.md](docs/17-项目结构书.md)。

## 关于本项目：由 AI Agent 全栈开发

> **本软件的需求整理、技术设计、全部 C# / XAML 源码、测试、构建脚本与文档，均由 AI 编程 Agent
> 「DeepSeek Harness」全栈完成。** 人类作者负责提出需求、评审文档、验收结果与真机验证。

这一节是写给**后续开发者**的，说明本仓库为什么长这样、以及接手时要注意什么：

| 方面 | 实际情况 |
|------|----------|
| 开发主体 | AI Agent（DeepSeek Harness）全栈编码；人类负责需求与评审，不逐行手写代码 |
| 开发流程 | 强制"**需求文档 → 技术文档 → 人工评审通过 → 编码 → 开发说明**"闭环（见 [docs/00](docs/00-开发流程与评审门禁.md)），没有文档就不写代码 |
| 文档密度 | 异常地高：每版功能都有需求、技术、开发说明三件套，两次严重 Bug 都留了排故报告，连流程门禁的**违规记录**也如实留档 |
| 质量基线 | 自研控制台测试 **103 个用例**全绿；Debug/Release 均 `0 警告 0 错误`；格式化有六道硬护栏并逐条测试 |
| 代码风格 | 手写极简 MVVM、零第三方依赖、关键处中文注释、单文件尽量 ≤ 约 400 行（趋势，非硬约束） |

### 接手前请务必知道

1. **先读 [AGENT.md](AGENT.md) 与 [docs/17-项目结构书.md](docs/17-项目结构书.md)**：前者是"压缩后的项目记忆"（含关键决策、踩过的坑、当前进度），后者是分层、线程模型与数据格式的全景说明；
2. **改代码前先改文档**：本项目的新功能与行为变更都要求先出需求与技术文档并通过评审，直接提交代码会违背仓库约定；
3. **不要相信"看起来合理"的实现**：两个真实故障都源于"没在真机上验证假设"——
   视频漏扫（只扫了第一个素材目录）与点击拷贝挂起（界面线程做了上百次 SMB 往返，被 Windows 记为 AppHang）；
   改完必须跑 `build\test.cmd Release`，并在真卡 / 真目标目录上验证一次；
4. **AI 生成的代码需要人工复核**：尤其是并发与线程模型、时间/时区（EXIF 与容器时间）、文件系统边界、格式化护栏这四类高风险区域；
5. **环境约束**：目标机不需要 .NET SDK（只有 MSBuild）；`.ps1` 必须存为 **UTF-8 带 BOM**；`.cmd` 内不要写中文注释；
6. **已知局限**：需求来自单一摄影师的实际工作流，未在多种相机/大规模团队上验证；部分界面文案与错误提示仍可打磨。

如果你发现 AI 写错的地方，欢迎直接提 Issue / PR——把"期望行为 → 实际行为 → 复现步骤"写清楚即可，
仓库的文档结构会让定位成本远低于一般项目。

## 参与贡献

- 提交前请先读 [AGENT.md](AGENT.md) 与 [docs/00-开发流程与评审门禁.md](docs/00-开发流程与评审门禁.md)：
  本项目坚持**先需求文档 → 技术文档 → 评审通过 → 再写代码**；
- 构建与测试必须保持 `0 警告 0 错误`，且 `build\test.cmd Release` 全部用例通过；
- PowerShell 脚本必须存为 **UTF-8 带 BOM**（Windows PowerShell 5.1 要求），`.cmd` 内不要写中文注释；
- 界面线程不允许做磁盘/网络 I/O（BUG-002 的根因），耗时操作一律放后台线程。

## 安全与免责

- 本软件会**格式化存储卡**（默认关闭，需人工确认，并有六道硬护栏），请务必先用无关紧要的卡验证；
- 拷贝校验只是"尽量保证一致"，不作为唯一的数据安全手段，重要素材请双备份；
- 仓库中不含任何用户数据、密钥或真实素材库路径，示例路径均为演示用途。

## 许可证

本项目使用 [MIT License](LICENSE)。

