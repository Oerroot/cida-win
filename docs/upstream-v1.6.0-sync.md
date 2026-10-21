# 上游 v1.6.0 同步记录

2026-10-21。本文件记录辞达 Windows 移植从上游 [Xuanwo/cida](https://github.com/Xuanwo/cida) 的 `473013c`（PR #72）同步到 `v1.6.0`（`3099e0d`）的范围、Windows 对应实现与验证边界。

本轮是**行为移植**：沿用 Windows 版现有的 WPF / .NET 10 结构、中文界面与测试约定，不逐行翻译 macOS 的 Swift 源码，也不搬运上游设计稿、原生截图、视觉基线或网站字体子集。

## 基线与范围

| 项目 | 提交 | 说明 |
|---|---|---|
| Windows 移植原基线 | `473013c93e052e08603ffd2faccda0d6dafbb5be` | 上游 #72「keep execution and copy actions in stable positions」，位于 `v1.5.1` 之后 5 个提交（`v1.5.1-5-g473013c`） |
| 本轮同步目标 | `3099e0dcbc2bafe0a86e6a04d1941b78afacbfd6` | 上游 `v1.6.0` tag；发布说明为上游 `docs/releases/1.6.0.md` |
| 覆盖提交 | 6 个 | 5 个功能/修复（#73、#74、#78、#79、#80）加 1 个发布说明（#81） |

Windows 移植此前已经包含 `473013c` 及之前的 #69–#72（首次使用配置引导、模型配置诊断交接、执行与语言确认、执行/复制固定位置）。本轮补齐 #73、#74、#78、#79、#80 的行为与 #81 的发布说明，并把 README、NOTICE 中的上游来源固定更新为 `3099e0d`。随包字体仍为基于 SIL OFL 的 Cida Serif / Cida Chinese Serif 静态派生，上游网站字体子集不进入 Windows 包。

## 上游最近更新

### 六个提交

| 日期 | 提交 | PR | 标题 |
|---|---|---|---|
| 2026-10-08 | [`aedb4ca`](https://github.com/Xuanwo/cida/commit/aedb4caccfe8406e5e7e439069ed140955069427) | [#73](https://github.com/Xuanwo/cida/pull/73) | feat: recover failed requests and automatically segment long input |
| 2026-10-08 | [`a48745e`](https://github.com/Xuanwo/cida/commit/a48745e79632f93d6763605d5718ab4b7cea1122) | [#74](https://github.com/Xuanwo/cida/pull/74) | feat: try saved actions without editing their prompts |
| 2026-10-20 | [`0cc8ced`](https://github.com/Xuanwo/cida/commit/0cc8ced051ef4dfec69c5dfb87e5a4e746cd548d) | [#78](https://github.com/Xuanwo/cida/pull/78) | feat: clarify shortcut and permission settings |
| 2026-10-20 | [`f336ad4`](https://github.com/Xuanwo/cida/commit/f336ad4cf31b77077747e01207606bc7bb8205b9) | [#79](https://github.com/Xuanwo/cida/pull/79) | feat: install updates after one confirmation |
| 2026-10-21 | [`2c25042`](https://github.com/Xuanwo/cida/commit/2c250420a09a1b5526369528558ee1b99e58dffe) | [#80](https://github.com/Xuanwo/cida/pull/80) | fix: make essential interface text easier to read |
| 2026-10-21 | [`3099e0d`](https://github.com/Xuanwo/cida/commit/3099e0dcbc2bafe0a86e6a04d1941b78afacbfd6) | [#81](https://github.com/Xuanwo/cida/pull/81) | docs: prepare the v1.6.0 release notes |

### 各提交要点

- **#73 失败恢复与自动分段**：为失败提供可操作的恢复路径，保留已输出内容；把显式输入长度限制改为自动分段处理，覆盖自定义动作与编号层翻译。
- **#74 动作试运行**：已保存动作可以不编辑提示词直接试运行；保留当前部分输出与上一次完整结果作参照；把「保存提示词」和「完成试运行」区分为两种状态；配置失败交给安全的配置助手交接，配置未变时不重复重试。
- **#78 快捷键与权限说明**：在设置中直接解释每个快捷键的作用；说明文字在尾部控件之前获得可用宽度；录制与权限交互保持不变；长自定义组合在原生截图中可读。
- **#79 一次确认安装更新**：更新消息使用尾随命令按钮并保留 Return 默认动作；一次确认携带明确的安装意图贯穿下载，面板关闭时撤销；延后下载可在设置中继续；重新检查前清除上次版本信息，失败的新检查不得显示旧版本。
- **#80 界面文字易读性**：说明、反馈与安静命令改用次级文字；浅色次级文字加深；删除冗余 hint token 并入三级文字；字号、品牌与焦点样式保持不变。
- **#81 v1.6.0 发布说明**：用中文准备发布说明，并补齐网站展示所需的字形子集。

### v1.6.0 发布说明摘要（六条行为）

上游 `docs/releases/1.6.0.md` 用中文概括了这一版：

1. 首次打开时，辞达会引导你连接模型服务。复制配置提示词交给 AI 助手，它会继续配置和检查；辞达收到配置后，可以直接试译。
2. 执行与复制有了固定位置：控制条负责执行、重新执行或停止，结果区负责复制文字或图片。修改原文或动作后，上次结果会标明「待更新」，仍可阅读和复制。
3. 请求失败时，已经输出的文字会保留，并提供重试或模型设置入口。原文超过模型长度限制时，辞达会自动分段处理，不需要手动缩短原文。
4. 动作提示词保存后，可以直接试运行；编辑时保留上次结果作参照，也可以放弃修改。保存成功但试运行失败时，会分别说明状态。
5. 快捷键设置补充了每个入口的用途和权限说明，键盘焦点更加清晰。设置说明、语言确认和其他重要小字也更容易阅读。
6. 更新只需确认一次「安装并重启」。下载期间离开面板会继续下载，但不会自动重启；你可以稍后再安装。

其中第 1、2 条对应基线之前的上游 #69 与 #72，Windows 版在上轮移植中已完成；本轮 #73/#74 补全配置说明在请求失败与动作试运行失败上下文中的决策顺序，并让失败/停止后的复制与重试入口和已输出文字衔接。第 3–6 条依次由 #73、#74、#78 与 #80、#79 带入 Windows。

## 功能对应与 Windows 适配

### 1. 请求失败恢复与超长原文自动分段（#73）

**上游行为**：失败按可操作类别展示并保留已输出的部分结果；失败说明提供重试或模型设置入口，配置类失败可把诊断交给 AI 配置助手；显式输入长度限制不再要求用户手动缩短原文，改为在同一个可取消请求内自动分段处理，覆盖自定义动作与编号层翻译。

**Windows 实现**：

- `windows/Cida.Core/ProcessingFailure.cs`：`ProcessingFailureCategory`（Timeout / Offline / Configuration / Limited / SecureConnection / Unknown）与 `ProcessingFailure`。`IsLengthLimit` 只认类型化错误：HTTP 413；400 且响应体为 `context_length_exceeded` / `request_too_large`；Anthropic typed 400 的三类 message 前缀。429、普通 400 与仅 provider 自然语言提示不触发分段。
- `windows/Cida.Core/ModelServiceSegmentation.cs`：`SourcePartition.Split` 依次按段落、句读（`.!?。！？；;`）、空白、文本元素边界对半切分，绝不切断代理对或组合字符，层翻译中保护 `⟦数字⟧` 占位符；仅当没有任何输出且错误为类型化长度限制时才进入分段，普通错误绝不重试；子段仍被拒时继续切分，已成功段不重发。`Translate`/`Improve` 按源顺序流式输出且分隔符只输出一次；`Custom` 动作的每段输出经一次「Result assembly contract:」请求合并为单个 JSON，不直接拼接多段 JSON。终止有界，最小单元仍超长时报 `LengthRecoveryExhausted`（Configuration 类别）；取消贯穿整条序列，取消后不再发请求或回调。
- `windows/Cida.Core/ConfigurationPrompt.cs`：按上游决策顺序生成「复制 AI 配置说明」：未配置→从头配置；配置不完整→继续配置；存在与当前 `ModelServiceFingerprint` 相同的面板请求/动作试运行失败→继续检查；上次连接检查失败且指纹相同→沿用失败检查说明；否则使用默认说明。过期指纹的旧失败被过滤，输出不含 API Key、原文或模型输出；保留 Windows 的 `Cida.Cli.exe config show --json`、`check --verbose`、`config set`（密钥 stdin）与 `config schema` 命令说明。
- `windows/Cida.Core/PanelState.cs`：新增 `ProcessingFailure? Failure` 与 `Fail(serial, failure)`，只接受 Streaming 状态；失败时保留已流出的部分结果，取消或失败后晚到的 append/finish 被拒绝。
- `windows/Cida.Core/LayerTranslation.cs` 与 `windows/Cida.Desktop/LayerSession.cs`：批量层翻译被长度拒绝且段数大于 1 时对半切分再合并；单段被拒时用占位符安全切分。批量 JSON 的 `id` 契约不变，现有 `_retryAfter`、`RetainLayerResult` 文案与超时行为保留。
- 面板与设置：`windows/Cida.Desktop/AppModel.cs`、`PanelWindow.xaml(.cs)`、`ResultDocument.cs` 使用上述 Core 行为。失败说明行区分「配置未变」与「配置已变/其他类别」：前者打开模型设置并阻止 Enter 重试，后者提供重试；结果区标明「请求失败 / 未完成」，复制按钮按已有文字或图片处理。
- 测试：`tests/Cida.Core.Tests/ProcessingFailureTests.cs`、`ModelServiceSegmentationTests.cs`、`ConfigurationPromptTests.cs`。分段测试通过本地 HTTP 侦听器覆盖 chat-completions、responses、anthropic-messages 三种协议，以及普通 503 只请求一次、最小单元仍超长时有界失败、取消后不再发请求等用例。

**Windows 适配说明**：失败解释以 WPF 面板内的说明行加按钮呈现，与 macOS 的行内失败说明保持相同分类与决策顺序；类型化长度检测跨三种协议共用，不解析 provider 的普通自然语言错误；不引入 macOS 私有能力。

### 2. 已保存动作直接试运行（#74）

**上游行为**：浏览/选中动作不自动发请求；已保存动作可用固定样例直接试运行，无需编辑提示词；试运行保留当前部分输出与上一次完整结果作为参照；保存与试运行是两件事，保存成功但试运行失败分别说明；配置失败且指纹与提示词未变时不得重复发送；可放弃修改。

**Windows 实现**：

- `windows/Cida.Core/ActionEditor.cs`：纯逻辑引擎。`Apply` 先持久化草稿再按需试运行已保存版本，试运行失败不回滚已保存内容；`Previews` 保存上一次完整结果，`Attempts` 记录本次 Running / Stopped / Failed 状态；停止保留当前 partial 并丢弃晚到 chunk；`TrySavedAction` 使用固定样例 `This sentence need to be more clear and natural.`；`NeedsConfiguration` 用失败类别加指纹/提示词匹配决定是否允许重试；`Discard` 不影响已保存内容与参照；脏草稿不可试运行。
- `windows/Cida.Desktop/SettingsWindow.xaml(.cs)`：动作页提供「试运行 / 再次运行 / 重试」、明确的「放弃修改」、参照标注（`参照 · 已保存版本的结果` / 过期标注）与运行/停止/失败状态；配置失败说明提供「模型设置」入口并切换到模型页。
- 测试：`tests/Cida.Core.Tests/ActionEditorTests.cs` 覆盖上游新增用例：不编辑直接试运行、停止保留 partial 且晚到 chunk 不覆盖、旧参照保留、保存成功后失败不回滚、配置未变不能重试、改配置后可重试、放弃修改不动已保存与参照、试运行成功替换参照、请求使用固定样例、取消/停止状态。

**Windows 适配说明**：Windows 设置页用现有按钮与键盘焦点约定实现同一状态机；不照搬 macOS 的设置行布局、`⌘↵` 提示与动效；动作编辑器仍保存完整设置，试运行结果不写入配置。

### 3. 快捷键与能力说明（#78）

**上游行为**：快捷键设置逐项解释用途；说明文字在尾部控件之前获得可用宽度；录制与权限交互保持不变；长自定义组合可读。权限行说明改为更具体的能力描述。

**Windows 实现**：

- `windows/Cida.Desktop/SettingsWindow.xaml(.cs)` 快捷键页逐条写明 Windows 入口：
  - `Alt+A` 打开面板；选中文字会带入，按首项动作处理；
  - `Alt+S` 框选屏幕文字并翻译；
  - `Alt+D` 指着的段落换成译文，再按换回；加 `Shift`（`Alt+Shift+D`）翻译整个窗口；
  - `Alt+F` 改进选中文字，直接写回原处。
- 行布局改为说明/标签先获得可用宽度，尾部录制与「恢复默认」控件按内容宽度收缩，长组合快捷键不被截断；录制交互与热键注册逻辑保持不变，热键冲突仍在设置页与托盘反馈。
- 能力说明替换 macOS 的辅助功能/屏幕录制权限行。Windows 没有这两项权限开关：设置页说明 UI Automation 读取与安全替换、随包离线 OCR 引擎、全局热键冲突与可用状态；`probe-identity` 只检查能力，`probe-ocr --offline` 可验证离线识别。
- 键盘焦点样式沿用现有控件；说明文字改用更易读的次级文字层级（见 #80）。

**Windows 适配说明**：上游权限行的文案改动是针对 macOS 辅助功能/屏幕录制开关的；Windows 将同一信息量放在快捷键页的能力说明中，不伪造权限状态，也不引入额外的系统授权步骤。

### 4. 一次确认安装更新（#79）

**上游行为**：更新提示使用尾随命令按钮并保留 Return 默认动作；用户确认一次即携带明确的安装意图贯穿下载，面板关闭/离开观看时撤销该意图而不打断下载；下载完成后若仍在观看则自动安装重启，否则保留「稍后安装」；重新检查开始时要清除上次版本信息和发布说明，失败的新检查不得显示旧版本；可跳过某个版本。

**Windows 实现**：

- `windows/Cida.Core/UpdateSession.cs`：纯状态机，不依赖 Velopack。`UpdatePhase`（Idle / Checking / Current / Found / Downloading / Ready / Installing / Failed）、版本/当前版本/说明/下载百分比/错误、`IsWatching`、`InstallWhenReady`、`StatusText`；`BeginCheck()` 清空上次版本/说明/百分比/错误；`ChooseInstall()` 记录一次确认；`DownloadCompleted()` 仅在 `InstallWhenReady && IsWatching` 时进入 Installing，否则为 Ready 并保留「稍后安装」；`Watch(false)` 离开即撤销安装意图，之后再次观看也不恢复；失败清除安装意图；`SkipVersion()`/`IsSkipped` 记录跳过的版本。
- `windows/Cida.Desktop/UpdateService.cs`：Velopack 驱动。`InstallAndRestartAsync()` 一次确认→下载（进度进 session）→仍在观看时自动 `Restart()` 安装重启，无需第二次确认；`SetWatched(bool)` 对应设置页可见性；`SkipVersion()` 记录并清空可用更新，后续检查不提示该版本；新增 `Phase`、`AvailableVersion`、`Notes`、`CanInstallNow`、`CanSkipVersion`、`PrepareForRestart` 钩子，保留原公开成员作为兼容包装。继续使用 `win-x64-preview` 通道、跳过版本文件与检查时间戳；开发版（未安装）保持现有「开发版本」提示。
- `windows/Cida.Desktop/SettingsWindow.xaml(.cs)` 与 `TrayMenu.cs`：更新页按「检查更新 → 发现版本显示更新内容 → 安装并重启（一次确认） + 稍后 + 跳过这个版本」组织；下载期间显示进度与取消；延后安装可从设置或托盘继续；关闭设置时 `SetWatched(false)`。
- 测试：`tests/Cida.Core.Tests/UpdateSessionTests.cs` 覆盖一次确认无需第二次确认、下载中离开再回来仍为 Ready、后台下载安静、失败清除安装意图、重新检查清除旧版本与说明、跳过版本、各阶段状态文案。

**与 macOS 的差异**：macOS 上游使用 Sparkle（appcast、Ed25519 签名与更新安装）；Windows 使用 Velopack `win-x64-preview` 通道和 Setup/Portable 形态。两者保持「一次确认」的交互语义，但 Windows 候选包未签名，不声称签名、干净机器或完整远端更新验证。

### 5. 界面文字层级与易读性（#80）

**上游行为**：说明文字、反馈与安静命令改用次级文字；浅色次级文字加深；删除冗余 hint token 并入三级文字；字号、品牌与焦点样式不变，并重新确认设计规格与视觉基线。

**Windows 实现**：

- `windows/Cida.Desktop/ThemeService.cs` 与 `windows/Cida.Desktop/Resources/Controls.xaml`：新增三级文字 token；说明、反馈、安静命令使用次级文字；弱提示层级只用于装饰性按键提示；保持字号、品牌色、焦点轮廓与高对比度系统颜色。
- 设置页的快捷键说明、动作预览状态、失败解释、空状态提示、更新状态，以及主面板语言确认等小字改用次级文字；对比度测试覆盖浅/深色次级与三级文字在面板和纸色背景上的可读性。
- 主面板、设置页与受控渲染截图按新的文字层级更新；不复制 macOS 的视觉基线。

**Windows 适配说明**：Windows 的 token 名称、字号与控件状态由 WPF 主题维护，不照搬上游 Swift 的 token 表；「hint」在 Windows 中只保留装饰性按键提示的语义，与三级文字区分。品牌色与焦点样式不变。

### 6. 发布说明与来源固定（#81）

**上游行为**：用中文准备 `v1.6.0` 发布说明，并补齐网站展示所需的字形子集。

**Windows 实现**：

- 新增本文件与 [`docs/releases/v0.2.0-rc.7.md`](releases/v0.2.0-rc.7.md)，按 Windows 候选版风格说明同步能力、下载与验证三件套以及未签名边界。
- `README.md` 更新下载链接与版本号、功能列表、上游固定提交和文档链接；`NOTICE` 更新派生来源提交。
- 上游网站字体子集（`website/assets/fonts`）、`Design/` 截图与视觉基线不进入 Windows 仓库；Windows 字体资源保持现有 Cida Serif / Cida Chinese Serif 与 OFL 许可文件，不新增来源不明资源。

**Windows 适配说明**：发布说明是 Windows 版本自己的文档，不复用上游发布页排版或截图；上游 tag、提交和 PR 链接保留在本文中供核对。

## 与 macOS 的差异边界

- **更新机制**：上游 macOS 使用 Sparkle appcast 与 Ed25519 签名更新；Windows 使用 Velopack `win-x64-preview` 通道、Setup 安装包与 Portable 便携包，安装重启由 Velopack 执行。
- **权限与能力**：macOS 在系统设置里管理辅助功能/屏幕录制权限；Windows 没有对应权限面板，改用 UI Automation、随包离线 OCR 与热键冲突检测，能力说明放在设置页。普通权限应用无法越过 Windows 的管理员窗口隔离。
- **交互载体**：上游使用 nonactivating `NSPanel`、状态菜单与原生设置窗口；Windows 使用 WPF 主面板、设置窗口、托盘菜单与单实例消息窗口，保持中文界面与现有键盘焦点约定。
- **视觉资产**：不搬运上游 `Design/` 设计稿、原生截图、视觉基线与网站字体子集；Windows 使用自有受控渲染截图（`docs/screenshots/`）和令牌对比度测试，分别记录代码实现、受控渲染与真实桌面证据。
- **工程约束**：保持 WPF / .NET 10 与三协议客户端结构，不新增 NuGet 包，不改 `.github/workflows`，不引入 macOS 私有能力；图片、字体与 OCR 资源来源和许可继续由 `NOTICE`、`licenses/` 与随包 provenance 文件记录。
- **快捷键映射**：上游 `⌥`/`⇧` 组合在 Windows 对应现有 `Alt`/`Shift` 入口（`Alt+A`/`Alt+S`/`Alt+D`/`Alt+Shift+D`/`Alt+F`），不照搬 macOS 键名与符号。

## 验证记录

2026-10-21 在本机 Windows x64（.NET SDK 10.0.401）对分支 `codex/upstream-v1.6.0-sync` 复核。

| 验证项 | 命令/范围 | 结果 |
|---|---|---|
| Release 构建 | `dotnet build windows -c Release` | 通过，0 警告 / 0 错误 |
| Core 测试 | `dotnet test tests/Cida.Core.Tests -c Release` | 156/156 通过（见下方环境说明） |
| Windows 默认测试 | `dotnet test tests/Cida.Windows.Tests -c Release` | 16 通过、0 失败、1 跳过（跳过项为需要交互桌面的 `FocusedSelectionAndPanelReopeningUseTheSourceWindow`） |
| 受控渲染与视觉检查 | `dotnet run --project tests/Cida.VisualChecks -- --check-control-surfaces`、`--check-panel-chrome`、`--render-suite` | 全部通过；浅/深色四页设置、主面板状态、托盘菜单与实际窗口原生标题栏命中检查均成功，渲染指标与模型表单对齐未变 |
| 候选包生成 | `pwsh -File scripts/package.ps1 -Version 0.2.0-rc.7` | 通过：生成 `Cida-win-x64-preview-Setup.exe`、`Cida-win-x64-preview-Portable.zip`、full.nupkg 与 `SHA256SUMS.txt`；VC runtime 校验、禁止证书/私钥/MSIX 检查通过；候选包内 `Cida.Cli.exe config schema` 冒烟通过；无受信任发布者签名 |
| 真实桌面交互 | 真实鼠标/键盘/拖拽/输入法/多屏 | 未执行（自动化受控渲染已覆盖布局与命中；本会话无法完成真实输入验收） |
| 干净机器安装/升级/卸载 | 目标 Windows 上的 Setup 与 Portable | 未执行 |
| 受信任发布者签名 | Authenticode / 发布证书 | 未执行；本候选版无受信任发布者签名 |

环境说明：本机 `dotnet test` 的 VSTest testhost 在当前会话环境无法启动（打开父进程被拒绝，与代码无关），因此上表测试以同一 xUnit 测试程序集通过本地反射 runner 执行，用例、断言与跳过条件与 `dotnet test` 一致；CI 使用 `dotnet test`。本会话开启 `CIDA_DESKTOP_TESTS=1` 尝试交互桌面用例时，选中文字读取等待超时（前台焦点/UIA 的会话环境限制），该用例未计入通过，也未作为本轮验收结论。

补充边界：测试数字以最终分支提交和 CI 结果为准；跳过项须分别注明，不能把跳过写成通过。受控栅格不代表真实 OS DPI 切换，模拟响应不代表真实模型服务或外部应用兼容性。

## 关联文档

- 上游 v1.6.0 发布说明：[Xuanwo/cida docs/releases/1.6.0.md](https://github.com/Xuanwo/cida/blob/v1.6.0/docs/releases/1.6.0.md)
- 本轮候选版说明：[v0.2.0-rc.7](releases/v0.2.0-rc.7.md)
- Windows 体验重构方案与范围：[windows-experience-plan.md](windows-experience-plan.md)
- 当前与历史的 Windows 验证记录：[verification.md](verification.md)、[windows-experience-verification.md](windows-experience-verification.md)
- 品牌与字体来源：[NOTICE](../NOTICE)
