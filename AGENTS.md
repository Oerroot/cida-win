# cida-win — 项目协作规则

## 项目定位与来源

- 本仓是上游 [Xuanwo/Cida](https://github.com/Xuanwo/cida) 的 Windows 移植，WPF / .NET 10；上游设计、品牌与同步差异以 [README](./README.md) 及其引用的上游同步记录为准。
- 入口与许可来源保留：Windows 桌面应用与 `current/Cida.Cli.exe` 共用配置；Apache-2.0、上游固定来源、字体与 OCR 组件许可见 [LICENSE](./LICENSE) 与 [NOTICE](./NOTICE)。
- Core 逻辑、Windows 平台/WPF 桌面层、CLI 与测试项目分层；改动按所属层运行对应测试，不以一层通过推断其他层通过。
- 不把具体候选版本号、未完成平台项或发布状态写成长期规则；当前状态以 README、发布记录和 CI 为准。

## 构建与验证

在仓库根目录依次执行，任一命令非零退出即停止，不用旧输出代替新构建：

```powershell
dotnet build windows -c Release
dotnet test tests/Cida.Core.Tests -c Release
dotnet test tests/Cida.Windows.Tests -c Release
```

- 桌面交互测试需显式开启：设置 `$env:CIDA_DESKTOP_TESTS = '1'` 后运行 `tests/Cida.Windows.Tests`，结束后执行 `Remove-Item Env:\CIDA_DESKTOP_TESTS`。
- 视觉验收使用 `dotnet run --project tests/Cida.VisualChecks -- --render-suite`；CI 还运行控件像素、面板标题命中与同步恢复检查。
- `CIDA_PROFILE` 可把 GUI/CLI 配置与单实例通信隔离到指定目录。桌面测试会短暂显示自有窗口并保存、恢复剪贴板，运行前关闭其他辞达进程，按当前授权隔离配置，避免干扰其他进程。
- 测试窗口、剪贴板和单实例通信都是实际副作用；不自动操作其他应用窗口，不把测试窗口的交互当作真实产品交互证据。

## 配置与安全边界

- API Key 使用 Windows 当前用户 DPAPI 加密保存；配置导出省略 API Key，日志、测试与报告中不出现密钥。
- 额外请求头、请求体与自定义提示词按原样导出，分享前检查是否含敏感信息；不保存翻译历史。
- 运行时用户截图仅在内存中处理；受控渲染证据使用本地模拟内容和独立配置，不将真实用户截图、完整私有正文或密钥写入日志和测试证据。
- 原处翻译通过隔离子进程读取 UI Automation 文本范围，超时回收子进程；覆盖层不抢焦点、点击穿透，仅覆盖不可编辑的正文，跳过代码、按钮、链接、密码与编辑控件。
- 安全替换要求同一来源窗口、进程、控件、选区及文档内容，确认可编辑后才粘贴并回读验证；无法确认时保留改进结果，不猜测写入目标。普通权限应用不能越过管理员窗口隔离。
- 配置损坏时保留原文件并读取上次有效备份，不静默重置；更新与失败恢复不得扩大上述权限边界。

## 证据与发布

- 区分单元测试、受控渲染、真实窗口交互、干净机器安装/升级/卸载与正式签名；每类只报告实际执行的结果，未运行项标记为未验证。
- 视觉验收工具只使用本地模拟响应和独立配置；受控渲染截图不等于真实应用交互。
- 发布按既有机制新增 `docs/releases/` 记录并在提交后创建新 tag，由 CI 构建、运行非交互测试并上传预发布包；不改写已有 tag，不替换既有发布附件。
- 发布、签名与更新通道变更均需当前授权；未签名候选包按发布页 SHA256SUMS 校验来源，不能宣称已签名或已通过干净机器验收。

## 协作与交付

- 项目协作按 `C:/Users/ThinkPad/.config/magpie/workflow/RUNBOOK.md`：Codex 主会话制定任务单并调度经 Magpie 接入的执行 Agent。
- 执行 Agent 只改任务单范围，交回文件清单、真实 diff、命令输出以及失败与未验证项；不自行提交、推送、发布或宣称验收。
- 独立只读 reviewer 只审查目标、规则、diff、相关源码与未关闭问题，不读执行者自评、不改文件、不批准；主会话核实 findings、组织修复并最终验收和提交。
