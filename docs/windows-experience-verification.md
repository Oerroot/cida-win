# Windows 体验重构验证 · v0.2.0-rc.1

2026-10-08；Windows 11 x64，系统 build 26300，.NET SDK 10.0.401。开发基线 `bb99d257aef360d8ec2603bb4f22d02da6b16047`，上游设计参照 `473013c93e052e08603ffd2faccda0d6dafbb5be`。用户已批准实施，并明确接受未签名候选安装包、便携包。

## 实施范围

| 项目 | 当前实现 | 本轮证据与边界 |
|---|---|---|
| 主面板 | 800 DIP、28 DIP 边距、纸色结果、14 DIP 外圆角、浅深色、阅读字体、独立滚动 | 真实 WPF 视图渲染和桌面交互；不是 HTML 设计稿 |
| 请求状态 | 来源/动作/配置快照；编辑标旧，隐藏继续、取消保留；拒绝迟到回调 | Core 快照/取消测试；真实 Enter、Shift+Enter、Tab 操作 |
| 设置 | 模型/动作/快捷键/通用；草稿保存、连接测试、预览、密钥三态 | 真实窗口新增、改名、排序、预览及保存到独立配置；取消/失败保护受控测试 |
| 动作 | 稳定 ID、排序、启停、受保护翻译、定制提示词 | Core 迁移与自定义动作契约；UI 保存后 JSON 次序/ID 核对 |
| 改进替换 | 窗口/PID/进程启动时间/控件/选区/整篇文档比较；一次粘贴；回读验证 | 自有 WPF 编辑器：选区或正文变化拒绝，成功替换，一次撤销恢复，只读拒绝；不推广为所有编辑器通过 |
| OCR | 可用系统引擎优先；随包离线简繁中文/英文模型；代码行和列表布局保护 | 强制绕过 Windows OCR：中英文识别通过；取消及布局测试；实际便携 CLI 检查 |
| 指针段落 | RangeFromPoint + Paragraph 范围；记录控件与范围偏移；隔离 UIA 子进程 | 受控只读 RichTextBox 实际范围、起点偏移一致；屏外隐藏、滚回缓存复用、正文改变失效 |
| 整窗覆盖 | 完整可见只读段落、批量 ID 校验、滚动/内容事件及轮询、点击穿透、原文切换 | 本地 SSE 模型 + 自有窗口：多个覆盖层、原文切换；外部应用矩阵未验收 |
| 布局保护 | 不扩大覆盖范围；最小 11 DIP 字体；装不下转面板；裁切段落暂不覆盖 | 原生 HWND 像素位置/宽度断言；长文完整复制图片断言 |
| 生命周期 | 托盘、单实例唤回、按用户/配置隔离通信、热键冲突反馈 | 消息窗口热键注册测试；实际便携 GUI 首次使用、隐藏后第二进程退出码 0，并唤回同一 HWND |
| 配置与密钥 | schema 2 兼容迁移、原子写入、有效备份、损坏原文件保留、DPAPI | 旧配置迁移；损坏后加载/保存不破坏良好备份；密钥回读且落盘非明文 |
| 更新 | 每日/手动检查、进度/取消、用户重启、完整包兜底、候选通道 | 固定 Velopack API；打包与更新 feed 检查；远端下载/重启闭环未验收 |
| 安装/便携 | x64 自包含 GUI + CLI，OCR + app-local VC runtime，未签名候选标识 | 实际 Velopack Setup/Portable/full 包及私钥/证书/身份包扫描；干净机器尚未验收 |

## 已运行验证

```powershell
dotnet build windows -c Release
dotnet test tests/Cida.Core.Tests -c Release
$env:CIDA_DESKTOP_TESTS = '1'
dotnet test tests/Cida.Windows.Tests -c Release
Remove-Item Env:\CIDA_DESKTOP_TESTS
dotnet run --project tests/Cida.VisualChecks -- --render-suite
pwsh -File scripts/package.ps1 -Version 0.2.0-rc.1
```

Release 构建 0 错误、0 警告；Core **96/96**；本机交互桌面 Windows **5/5**。这 5 项包括 ABI、存储、OCR、布局及包含多段断言的桌面流程。CI 没有交互桌面授权时跳过桌面流程，其余 4 项仍运行；不能把 CI 的跳过写成通过。

桌面流程在同一测试进程的独立 STA 窗口中执行。模型响应由 127.0.0.1 的临时 SSE 服务提供，无外部模型请求。读取/覆盖层位于真实 HWND、真实 UIA TextPattern 范围；缓存复用通过请求计数不增加核对，来源正文修改后会话销毁。

Computer Use 操作了真实产品视图：中英文字输入、Shift+Enter 只换行、Enter 流式生成、Ctrl+Shift+C 图片复制、Tab 标旧、Ctrl+, 隐藏面板并打开设置；动作新增、名称编辑、上移、草稿模型预览、保存。隔离配置的动作 ID 与次序已从实际 JSON 核对。此输入方式验证 Unicode 输入，**不代替完整中文 IME 候选组合验证**。

视觉套件生成 **26 张**受控 WPF 渲染，覆盖浅深色、完成/空态/旧结果/失败/停止/长文、设置四页及 1/1.25/1.5/2 倍输出。示例见 `docs/screenshots/`；缩放输出不等于 OS 实际 DPI 或混合显示器验收。已修复实机发现的设置遮挡、派生窗口主题未应用、页内容 UIA 不可见、深色标签/按钮对比度、默认控件色不协调等问题。

## 候选包检查

打包使用 vpk **1.2.161**，应用版本 **0.2.0-rc.1**，通道 **win-x64-preview**。VC runtime 来自本机 Visual Studio Build Tools `VC/Redist/MSVC`，核验 Microsoft Authenticode 签名后应用本地部署；不从 System32 取 DLL。记录文件名、版本、SHA256 与签名者到 `vc-runtime-provenance.json`。

包中含 `CANDIDATE.txt`、LICENSE/NOTICE、字体 OFL、OCR 许可/模型/原生 DLL，以及 GUI/CLI 的自包含运行时。候选程序不含 MSIX 绑定，启动不安装证书或自动注册身份包。CLI `probe-identity` 已改为只读检查。

实际便携包解压到任务 artifacts 目录后执行配置与 OCR 诊断；无包身份状态已核对。Setup 的 Authenticode 状态为 **NotSigned**，与候选标识一致。附件 SHA256SUMS 随发布提供；最终字节与大小以发布附件和校验文件为准。

本轮不会重写旧 tag，也不会更新已有发布附件。准备过程中生成的中间包保留在任务 artifacts 内；最终候选包由同版本最终源码重新生成。

## 尚未声称通过的项目

- 目标干净 Windows 的安装、从旧版本升级、卸载、启动项与配置保留全过程；本机没有启用 Windows Sandbox，不为此修改系统功能。
- 100/125/150/200% 实际 OS DPI、混合 DPI 多屏、负坐标屏幕与屏幕切换全过程。
- Chrome / Edge、Typora、Office、外部编辑器的完整取词、段落跟随、只读检测、替换及撤销矩阵。
- 中文 IME 拼音候选、组合态 Enter/Esc/焦点切换的完整人工序列；高对比系统模式的实机切换。
- 候选远端自动更新下载、取消、完整包回退和用户重启安装全过程。
- 受信任 Windows 发布者签名。用户已确认当前交付候选包；该条件仍是正式签名发行版的门槛。

上述属于目标环境验收边界，不以受控窗口、模拟响应、静态代码或缩放图片替代。
