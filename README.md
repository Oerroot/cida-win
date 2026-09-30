# 辞达 Cida · Windows 版

[辞达（Cida）](https://github.com/Xuanwo/cida) 的 Windows 移植：在任何应用里用你自己的大模型翻译和润色文字。取自《论语》"辞达而已矣"。

上游是 Xuanwo 的 macOS 原生应用（Swift/SwiftUI）；本仓库按其产品语义与协议行为，用 C# / .NET 10 / WPF 重写了 Windows 版。Apache-2.0，沿用上游许可。

## 功能

| 快捷键 | 功能 | 状态 |
|---|---|---|
| `Alt+A` | 读取其他应用选中的文字，弹出面板翻译 / 润色（Tab 切换） | ✅ |
| `Alt+S` | 冻结屏幕 → 框选 → 本地 OCR → 翻译 | ✅（见下方 OCR 说明） |
| `Alt+D` | 把指针下的段落原处翻译成译文覆盖层，再按还原 | ✅ 基础版 |
| `Alt+Shift+D` | 翻译前台窗口中的可读文本块，再按还原 | ✅ 基础版 |

- 三种协议全支持：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages，以及一切兼容服务（DeepSeek、Moonshot、智谱、OpenRouter、本地模型……）
- 流式输出；`Esc` 停止，`Enter` 重新生成；右键复制结果
- API Key 用 DPAPI 加密存储在用户目录；设置与 CLI 共用同一份配置
- 无 Dock/任务栏常驻：托盘图标 + 全局快捷键

## 安装与配置

下载 [v0.1.3 预发布](https://github.com/Oerroot/cida-win/releases/tag/v0.1.3)：[安装版 Setup.exe](https://github.com/Oerroot/cida-win/releases/download/v0.1.3/Cida-win-x64-Setup.exe) 或 [便携版 Portable.zip](https://github.com/Oerroot/cida-win/releases/download/v0.1.3/Cida-win-x64-Portable.zip)。便携版完整解压后运行根目录的 `辞达 Cida.exe`。本版本尚无正式安装程序签名，人工验收边界见发布说明。

发布打包用 [Velopack](https://github.com/velopack/velopack)，提供安装程序与 Portable 包。GUI 和控制台 CLI 均以 win-x64 自包含方式发布，无需另装 .NET：

```powershell
scripts/package.ps1 -Version 0.1.3   # 产出 artifacts/release/0.1.3/ 下的 Setup.exe / Portable.zip / full nupkg
```

身份包已包含在发布输入中；重新构建用 `scripts/sparse-package.ps1`。开发机需要注册时，显式运行 `scripts/sparse-package.ps1 -Register -ExternalLocation <Cida.exe 所在目录>`。该操作会信任开发证书并注册包；普通构建不会修改信任或包注册。开发期直接：

```powershell
dotnet run --project windows/Cida.Desktop
```

配置用命令行（AI 助手友好，语义与上游一致）：

```powershell
# 发布后在 Portable 的 current 目录或安装目录执行；开发期用 dotnet run --project windows/Cida.Cli -- …
.\Cida.Cli.exe config set endpoint=https://api.deepseek.com/chat/completions model=deepseek-chat
Get-Content -Raw 密钥.txt | .\Cida.Cli.exe config set api-key --stdin
.\Cida.Cli.exe check
.\Cida.Cli.exe config show --json
```

全部字段：`Cida.Cli.exe config schema`。GUI 程序 `Cida.exe` 也处理相同命令参数；脚本和管道优先使用控制台程序以便等待退出、读取输出和退出码。

## OCR 与包身份（sparse package）

截图翻译使用 `Windows.Media.Ocr`。按微软文档该 API 需要包身份，本项目实现了完整的
sparse package（packaging with external location）方案，即 PowerToys 打通 OCR 的同款路线：

- `windows/Cida.Desktop/SparsePackage/` 是身份包清单（`runFullTrust` + `AllowExternalContent`）
- `scripts/sparse-package.ps1` 用 Windows SDK 的 MakeAppx 按清单和图标白名单打包，开发签名使用用户证书库中的不可导出私钥；注册需显式传入 `-Register`
- 新发布包不包含 PFX/P12 私钥文件，且打包会检查嵌套 MSIX 的内容。发布到其他机器时，包签名还需受目标机器信任；自签名开发证书不等于正式签名
- 可执行文件内嵌 `<msix>` 绑定清单，进程启动即获得包身份
- GUI 启动时会尝试幂等注册（`SparsePackageRegistrar`），CLI 可用 `Cida.Cli.exe probe-identity` 自检；签名未受信任时注册仍可能失败

实测结论（Windows 11 26100+）：即使没有包身份，`OcrEngine` 也能直接创建并识别——
文档的限制在运行时并未强制。因此 sparse package 是旧版 Windows 的保险层而非硬依赖；
本仓库已在实机验证两条路径都可用（中文图片 → 识别 → 段落重建）。

唯一硬性要求是**语言包**：中文识别依赖系统安装了中文 OCR 语言包
（设置 → 时间和语言 → 语言和区域 → 中文 → 语言选项）。

## 仓库结构

```
windows/
  Cida.Core/      协议、配置、提示词、SSE 解析、CLI 语义（85 个单元测试）
  Cida.Platform/  热键、UIA 选区读取、DPAPI、存储、截图、OCR、原处翻译读取
  Cida.Desktop/   WPF：托盘、面板、截图框选、设置、覆盖层
  Cida.Cli/       config / check / probe 命令行入口
tests/
  Cida.Core.Tests/
  Cida.Windows.Tests/  ABI 与受控桌面回归
scripts/
  package.ps1     Velopack 打包
```

## 选区读取的兼容性

跨应用取词是两级策略：优先 UI Automation `TextPattern`，读不到再用合成 `Ctrl+C` + 剪贴板快照恢复兜底。各应用支持程度可用探针实测：

```powershell
.\Cida.Cli.exe probe   # 列出所有可见窗口的读取能力矩阵
```

普通权限进程无法读取管理员权限窗口（UIPI），也无法向其发送合成按键——这是 Windows 的安全边界。

## 测试与构建

```powershell
dotnet build windows
dotnet test tests/Cida.Core.Tests
dotnet test tests/Cida.Windows.Tests
# 交互式 Windows 桌面的受控集成验证（会短暂显示测试窗口，保存并恢复剪贴板）
$env:CIDA_DESKTOP_TESTS = "1"
dotnet test tests/Cida.Windows.Tests
Remove-Item Env:\CIDA_DESKTOP_TESTS
```

评审修复与验证边界见 [review-fixes.md](docs/review-fixes.md)。跨应用兼容性、完整中文 IME 交互和干净机器安装仍需单独人工验证。

发布流程：添加 `docs/releases/vX.Y.Z.md` 发布说明，提交后创建并推送 `vX.Y.Z` tag。GitHub Actions 会构建、运行 Core/Windows ABI 测试、打包并上传预发布附件；交互式桌面测试需在本地另行执行。已存在的 Release 会保留其附件。

## 与上游的关系

- 协议行为、配置字段、CLI 语义、提示词契约按上游 `Design/spec` 对齐，测试用例覆盖三种格式的流式与非流式响应
- UI 为 WPF 重写，交互参考上游设计（无激活面板、点击穿透覆盖层、冻结屏幕框选）
- 上游演进后，配置 JSON 与 CLI 字段保持同构，便于跨平台迁移

## 许可

Apache-2.0。基于 [Xuanwo/cida](https://github.com/Xuanwo/cida) 的工作。
