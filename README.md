# 自动拷卡助手（AutoCardCopier）

给摄影师用的 Windows 桌面软件：**插入内存卡 → 自动拷贝到文件服务器 → 自动校验 → 可选安全格式化**，
并按"拍摄时间 + 地点 + 拍摄类型"对每次拍摄做项目化管理。

> A lightweight Windows desktop app for photographers: plug in a memory card, get material copied to
> your file server with folder auto-creation, integrity verification, project management by shoot date /
> location / type, and an optional guarded card format. C# + WPF on .NET Framework 4.8, **zero third-party
> dependencies**. Documentation is in Chinese.

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

