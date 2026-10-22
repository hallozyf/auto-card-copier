# 开源发布说明 · GitHub（v0.5.0）

> 记录本次把「自动拷卡助手 / AutoCardCopier」开源发布到 GitHub 的**实际操作过程、内容清理、环境适配与后续维护方式**。
> 后续任何维护者重新推送都能照着本文抄命令。

---

## 1. 发布结果

| 项目 | 值 |
|------|-----|
| 仓库地址 | <https://github.com/hallozyf/auto-card-copier> |
| 账号 | `hallozyf` |
| 仓库名 | `auto-card-copier` |
| 可见性 | **Public** |
| 默认分支 | `main` |
| 提交数 | 29（含本次发布准备的提交） |
| 标签 | `v0.1.0`、`v0.1.1`、`v0.2.0`、`v0.2.1`、`v0.3.0`、`v0.4.0`、`v0.5.0` |
| 跟踪文件 | 124 个，仓库对象约 0.9 MB（不含构建产物） |
| 许可证 | MIT（`LICENSE`） |
| 仓库描述 | 给摄影师用的 Windows 拷卡助手：插入存储卡自动拷贝到文件服务器并校验，按拍摄时间/地点/类型做项目管理；C# + WPF，零第三方依赖，103 个自动化用例 |
| 话题标签 | photography, wpf, dotnet-framework, memory-card, backup, file-copy, csharp, windows-desktop |
| 起始版本 | v0.5.0（F14 素材库管理已完成，103 个自动化用例通过） |

---

## 2. 发布前的内容清理与安全自查

开源前逐项确认过，结论如下：

| 检查项 | 处理 |
|--------|------|
| 内部协作笔记 `.workbuddy/memory/*.md` | **取消跟踪**（`git rm --cached`），并加入 `.gitignore`，文件仍留在本机 |
| 原始需求提示词 `初始提示词.txt` | 不入库（`.gitignore`），属本机私人物料 |
| 访问令牌 `token.txt` | 发布前加入 `.gitignore`，**绝不入库**；推送时只通过环境变量 + `GIT_ASKPASS` 交给 git |
| 真实 NAS 路径 / IP / UNC 路径 | 全库检索 `10.9.0.1`、`素材盘`、`Users\admin`、`Z:\照片` 等关键字：仓库内只有排故报告里的 `Z:\照片` 举例与文档中的示例盘符，**没有真实服务器地址、账号或密钥** |
| 构建产物 `build/out/`、`bin/`、`obj/` | 已在 `.gitignore` |
| 便携 Git `tools/git/`（约 300 MB） | 已在 `.gitignore` |
| 运行期数据 `data/`、`logs/`、`*.log`、`debug.log` | 已在 `.gitignore` |
| 用户真实素材 / 项目库 JSON / 设置文件 | 从未入库（都在 `%APPDATA%\AutoCardCopier`） |

补充说明：提交历史里的作者信息是 `AutoCardCopier Dev <dev@localhost>`（本仓库本地配置），**不包含个人邮箱**。

---

## 3. 实际执行的发布步骤

### 3.1 准备（本地）

1. 新增 `LICENSE`（MIT）、`.editorconfig`（`.ps1` 强制 UTF-8 BOM）；
2. README 增加英文简介、AI Agent 全栈开发声明、仓库结构、贡献约定、安全免责、许可证；
3. `.gitignore` 增加 `.workbuddy/`、`初始提示词.txt`、`token.txt`、`*.token`；
4. 提交：`chore(oss): 开源发布准备…`、`docs(oss): README 声明本项目由 AI Agent 全栈开发…`。

### 3.2 创建仓库（GitHub REST API）

用令牌调用 `POST /user/repos`，参数：`private=false`、`auto_init=false`、`has_issues=true`、`has_wiki=false`、`has_projects=false`，随后
`PUT /repos/hallozyf/auto-card-copier/topics` 设置话题。返回 `201`，默认分支 `main`。

### 3.3 推送历史与标签

```powershell
# 令牌放在桌面/仓库根目录的 token.txt（已 gitignore），只读进环境变量，不写进任何配置
$env:ACC_TOKEN = (Get-Content -LiteralPath 'token.txt' -Raw).Trim()

# 用 askpass 脚本把令牌交给 git，避免出现在命令行/配置里
$ask = Join-Path $env:TEMP 'acc-askpass.cmd'
Set-Content -LiteralPath $ask -Value "@echo off`r`necho %ACC_TOKEN%" -Encoding ASCII
$env:GIT_ASKPASS = $ask
$env:GIT_TERMINAL_PROMPT = '0'

tools\git\cmd\git.exe -c credential.helper= push `
  https://hallozyf@github.com/hallozyf/auto-card-copier.git refs/heads/main:refs/heads/main --tags
```

结果：`main` 新建 + 7 个标签全部上传；仓库随后设置为 `origin`：

```powershell
tools\git\cmd\git.exe remote add origin https://github.com/hallozyf/auto-card-copier.git
```

### 3.4 发布后校验（API 读回）

- `GET /repos/hallozyf/auto-card-copier` → `private=false`、`default_branch=main`、描述与话题正确；
- `GET /repos/.../commits?per_page=100` → 29 条，HEAD = 发布准备的最新提交；
- `GET /repos/.../tags` → 7 个标签齐全。

---

## 4. 本机环境适配（**后续维护必看**）

这台开发机的网络与工具链有若干坑，发布时逐个绕过，**这些设置只在本仓库 `.git/config` 与临时目录里，不会进仓库**：

| 现象 | 原因 | 处理 |
|------|------|------|
| `Could not resolve host: github.com` | 系统 DNS 解析 github.com 失败（校内 DNS 超时） | 走本机代理 `127.0.0.1:10808`（浏览器/IE 代理同款） |
| `schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS` | 受限环境下 Schannel 拿不到用户凭据 | `git config http.sslBackend openssl` |
| `error adding trust anchors from file: ...\自动拷卡软件\tools\git\...\ca-bundle.crt` | 仓库路径含中文，OpenSSL 读不了该路径的 CA 包 | 把 `tools/git/ucrt64/etc/ssl/certs/ca-bundle.crt` 复制到 **纯 ASCII 临时路径**，`git config http.sslCAInfo <该路径>` |
| GCM（凭据管理器）直接崩溃（退出码 `0xE0434352`） | 本机 MinGit 的 `git-credential-manager.exe` 在该环境启动即异常 | 不用 GCM：`-c credential.helper=` 清空助手 + `GIT_ASKPASS` |
| `api.github.com` 偶发"连接已建立但返回空响应" | 代理对 HTTPS 隧道的偶发掐断（实测约 1/4 概率） | 客户端**重试**（1.5s 起递增，最多 8 次）即可稳定 |

> 换一台网络正常的电脑时，上面 4 条都不需要，直接 `git push` 即可。

---

## 5. 令牌与安全

1. 本次使用 **classic PAT，仅勾选 `repo`**，有效期建议 ≤ 30 天；
2. 令牌只存在于：`token.txt`（已被 `.gitignore` 排除）、进程环境变量、`%TEMP%\acc-askpass.cmd` 的运行时引用；
3. **推送完成后请到 <https://github.com/settings/tokens> 撤销该令牌**；撤销后本机不会再保有任何可用凭据；
4. 若以后还要推送：重新生成令牌、覆盖 `token.txt`、重复 3.3 的两条命令即可（`origin` 已配置好）。

---

## 6. 后续维护与发布建议

```powershell
# 日常：提交后推送
build\test.cmd Release                      # 必须先全绿
tools\git\cmd\git.exe push origin main
tools\git\cmd\git.exe push origin --tags
```

建议的发布流程（尚未自动化）：

1. 改 `CHANGELOG.md` + 写好 `docs/` 开发说明；
2. 打标签 `vX.Y.Z`；
3. 在 GitHub 上新建 Release：标题用版本号，说明直接引用 CHANGELOG 对应段落；
4. （可选）把 `build/out/Release` 里的绿色版打成一个 zip 作为 Release 附件，
   注意只打包 `AutoCardCopier.App.exe` + `AutoCardCopier.Core.dll`，**不要**打包 `data/`。

---

## 7. 本期未做（可继续推进）

- **GitHub Actions CI**：Windows runner 上跑 `build\test.cmd Release`（需要 VS BuildTools，成本较高，未做）；
- **Issue / PR 模板**、`CONTRIBUTING.md` 独立成文；
- **Release 附带二进制**（当前仓库只有源码，用户需自行构建）；
- **英文 README**（目前只有英文简介段）；
- 仓库 `size` 字段需等 GitHub 后台统计，稍后刷新即可看到实际大小。

---

## 8. 相关文档

- 开源前准备提交：`chore(oss): 开源发布准备——补充 MIT 许可证、编辑器约定、README 与忽略规则，移除内部笔记`
- AI 开发声明提交：`docs(oss): README 声明本项目由 AI Agent（DeepSeek Harness）全栈开发`
- 上一版本开发说明：[docs/19-开发说明-素材库管理-F14-v0.5.0.md](19-开发说明-素材库管理-F14-v0.5.0.md)
- 使用手册：[docs/05-使用手册.md](05-使用手册.md)
