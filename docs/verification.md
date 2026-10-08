# 实机验证记录

v0.2.0-rc.1 当前实现与重新运行的证据见 [Windows 体验验证记录](windows-experience-verification.md)。下文继续保留为历史记录。

本文件保留初始开发阶段的验证记录（Win11 x64，2026-09-30）。这些是历史记录，不作为当前修复版本的验收结论；部分结论与随后源码评审发现的缺陷不一致。当前修复和重新运行的证据见 [review-fixes.md](review-fixes.md)。

## P0 · 系统能力

| 验证项 | 结果 | 证据 |
|---|---|---|
| 全局热键（Alt+A/S/D） | ✅ | `WM_HOTKEY id=…` 送达；面板弹出 |
| UIA 选区读取 | ✅ | `cida probe` 对资源管理器/Chrome/Typora/ZCode 输出能力矩阵 |
| 整窗段落枚举 | ✅ | VS Code 40 段、Typora 48 段、资源管理器 8 段 |
| sparse package 包身份 | ✅ | `Get-AppxPackage Cida.Win` → `Cida.Win_8y730z72tkqd0`；进程启动即有身份 |
| OCR | ✅ | 中文图片 → 「辞 达 在 Windows 上 识 别 中 文」→ 段落重建正确 |

## P1/P4 · 翻译管线

| 验证项 | 结果 | 证据 |
|---|---|---|
| 三协议流式（SSE） | ✅ | 72/72 单元测试（本地 HTTP 侦听器驱动） |
| CLI 端到端 | ✅ | `config set` → `check` → 「✓ 可用 · 回复『你好，世界』」 |
| 整窗批量翻译 | ✅ | Typora 48 段 → 2431 字符 JSON 数组 → 48 id → 缓存命中重读 |

## P2 · 中文输入法（无激活面板）

**结论：组合输入可用。** 驱动方式：SendInput 合成 Alt+A 开面板 → UIA 可点击点定位
输入框 → 点击（触发 `ActivateForInput`：摘除 `WS_EX_NOACTIVATE` + 激活 + 聚焦）→
线程激活中文键盘布局（HKL 0x8040804）→ n/h 组合 → 数字 1 提交候选。

实测记录（关键轮次）：

```
17:22:37 Panel MouseDown at 251,40          ← 点击到达输入框（DPI 校正后）
17:28:47 composer GotKeyboardFocus          ← 聚焦成功
17:29:34 Composer KeyDown D1 / input '1'    ← 候选词数字键提交
17:29:34 input ''                           ← marked-text 组合事件（IME 组合中特征）
composer text: '你好nh'                     ← 「你好」为 IME 组合提交的中文
```

**发现并修复的结构问题：**

1. **`WS_EX_NOACTIVATE` 窗口无法承载 IME 组合**——无激活窗口拿不到输入上下文，
   marked text 无法开始。修复：面板出现时不激活（保持源应用焦点），点击输入框时
   摘掉 NOACTIVATE 位并取焦点（`PanelWindow.ActivateForInput`），与上游 macOS
   nonactivating panel 的点击聚焦行为对齐。
2. **DPI 偏移**——固定像素偏移在高 DPI 下点击落点漂移，验证驱动改用 UIA
   `GetClickablePoint` 后精确命中。
3. **热键消息窗口 `hwnd=0`**——`CreateWindowEx` 缺 `hInstance` 且未检查返回值，
   `RegisterHotKey` 静默绑到 `hwnd=NULL`，导致 WM_HOTKEY 无法送达。修复：补
   `GetModuleHandle(null)` + 失败抛错。此 bug 同时暴露了"进程锁文件导致增量构建
   静默产出旧 DLL"的验证陷阱（杀进程 + 清 bin/obj 后复现消失）。

**残留说明：** 每轮驱动中线程级 IME 布局激活存在时序抖动（部分轮次英文布局仍生效，
键入 `nh` 直通）。这是驱动脚本的时序问题，非面板缺陷——聚焦链与组合链在同一轮次
中均已被证实（`GotKeyboardFocus` → marked-text 事件 → 候选提交 → 中文上屏）。
面板内保留 `WM_IME_*` 消息探针（写入 `%TEMP%\cida-ime-log.txt`），可随时复验。

## P3 · 发布

| 验证项 | 结果 | 证据 |
|---|---|---|
| vpk 打包 | ✅ | Setup.exe 16.5MB / Portable.zip（18 文件含签名 msix）/ full+delta nupkg |
| 增量更新 | ✅ | 0.1.0 → 0.1.1 delta 21KB |
| 发布产物运行 | ✅ | artifacts/publish/Cida.exe 冒烟通过 |
| GitHub Actions CI | 见仓库 Actions 页 | build → test（72 测试）→ package |
