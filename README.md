# 辞达 Cida · Windows 版

[辞达（Cida）](https://github.com/Xuanwo/cida) 的 Windows 移植：在任何应用里用你自己的大模型翻译和润色文字。取自《论语》"辞达而已矣"。

上游是 Xuanwo 的 macOS 原生应用（Swift/SwiftUI）；本仓库按其产品语义与协议行为，用 C# / .NET 10 / WPF 重写了 Windows 版。Apache-2.0，沿用上游许可。

## 功能

| 快捷键 | 功能 | 状态 |
|---|---|---|
| `Alt+A` | 读取其他应用选中的文字，弹出面板翻译 / 润色（Tab 切换） | ✅ |
| `Alt+S` | 冻结屏幕 → 框选 → 本地 OCR → 翻译 | ✅（见下方 OCR 说明） |
| `Alt+D` | 把指针下的段落原处翻译成译文覆盖层，再按还原 | ✅ 基础版 |

- 三种协议全支持：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages，以及一切兼容服务（DeepSeek、Moonshot、智谱、OpenRouter、本地模型……）
- 流式输出；`Esc` 停止，`Enter` 重新生成；右键复制结果
- API Key 用 DPAPI 加密存储在用户目录；设置与 CLI 共用同一份配置
- 无 Dock/任务栏常驻：托盘图标 + 全局快捷键

## 安装与配置

发布打包用 [Velopack](https://github.com/velopack/velopack)（免安装 exe + 增量更新）：

```powershell
scripts/package.ps1 -Version 0.1.1   # 产出 Setup.exe / Portable.zip / full+delta nupkg
```

首次还需构建并注册身份包（开发机一次性）：`scripts/sparse-package.ps1`。开发期直接：

```powershell
dotnet run --project windows/Cida.Desktop
```

配置用命令行（AI 助手友好，语义与上游一致）：

```powershell
# 发布后：Cida.exe config set …；开发期：dotnet run --project windows/Cida.Cli -- config set …
cida config set endpoint=https://api.deepseek.com/chat/completions model=deepseek-chat
cida config set api-key --stdin < 密钥.txt      # Key 只能从 stdin/file/env 写入
cida check                                       # 真请求一次验证配置
cida config show --json                          # 机器可读输出
```

全部字段：`cida config schema`。

## OCR 与包身份（sparse package）

截图翻译使用 `Windows.Media.Ocr`。按微软文档该 API 需要包身份，本项目实现了完整的
sparse package（packaging with external location）方案，即 PowerToys 打通 OCR 的同款路线：

- `windows/Cida.Desktop/SparsePackage/` 是身份包清单（`runFullTrust` + `AllowExternalContent`）
- `scripts/sparse-package.ps1` 用 Windows SDK 的 MakeAppx 打包、自签名证书签名并注册
- 可执行文件内嵌 `<msix>` 绑定清单，进程启动即获得包身份
- GUI 启动时会自动幂等注册（`SparsePackageRegistrar`），CLI 可用 `cida probe-identity` 自检

实测结论（Windows 11 26100+）：即使没有包身份，`OcrEngine` 也能直接创建并识别——
文档的限制在运行时并未强制。因此 sparse package 是旧版 Windows 的保险层而非硬依赖；
本仓库已在实机验证两条路径都可用（中文图片 → 识别 → 段落重建）。

唯一硬性要求是**语言包**：中文识别依赖系统安装了中文 OCR 语言包
（设置 → 时间和语言 → 语言和区域 → 中文 → 语言选项）。

## 仓库结构

```
windows/
  Cida.Core/      协议、配置、提示词、SSE 解析、CLI 语义（与上游逐行为对齐，65 个单元测试）
  Cida.Platform/  热键、UIA 选区读取、DPAPI、存储、截图、OCR、原处翻译读取
  Cida.Desktop/   WPF：托盘、面板、截图框选、设置、覆盖层
  Cida.Cli/       config / check / probe 命令行入口
tests/
  Cida.Core.Tests/
scripts/
  package.ps1     Velopack 打包
```

## 选区读取的兼容性

跨应用取词是两级策略：优先 UI Automation `TextPattern`，读不到再用合成 `Ctrl+C` + 剪贴板快照恢复兜底。各应用支持程度可用探针实测：

```powershell
cida probe   # 列出所有可见窗口的读取能力矩阵
```

普通权限进程无法读取管理员权限窗口（UIPI），也无法向其发送合成按键——这是 Windows 的安全边界。

## 测试与构建

```powershell
dotnet build windows
dotnet test tests/Cida.Core.Tests
```

## 与上游的关系

- 协议行为、配置字段、CLI 语义、提示词契约按上游 `Design/spec` 对齐，测试用例覆盖三种格式的流式与非流式响应
- UI 为 WPF 重写，交互参考上游设计（无激活面板、点击穿透覆盖层、冻结屏幕框选）
- 上游演进后，配置 JSON 与 CLI 字段保持同构，便于跨平台迁移

## 许可

Apache-2.0。基于 [Xuanwo/cida](https://github.com/Xuanwo/cida) 的工作。
