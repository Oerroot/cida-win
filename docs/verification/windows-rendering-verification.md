# Windows 渲染与拖动修复 · v0.2.0-rc.4

2026-10-10；基线 `ce35ccdfda9eb8e9ca286d4bbfc1472d51e68c4c`。用户报告 rc.3 在笔记本外接显示器、约 100% 缩放下，折叠箭头丑、界面文字/图标模糊或有锯齿、主面板不能拖动。本次延续已授权的 Windows 体验修复。

## 事实、原因与改动

旧主面板在相同实际桌面测得：原生 DPI 96、WPF 缩放 1、PerMonitorV2 为 true；但 HWND 带 WS_EX_LAYERED，UseLayoutRounding 和 SnapsToDevicePixels 均为 false，文字排版是 Ideal，默认字体 Microsoft YaHei UI。它没有像 SettingsWindow 一样显式应用 Window 样式，透明窗口也会限制 ClearType 渲染路径。因此本机没有发现整窗 DPI 虚拟化放大，但找到了应用自身的渲染问题。

设置页旧值已经是非分层、Display 和像素对齐，不能把用户看到的所有页面模糊都归因于主面板。系统字体平滑已启用，本次不修改显示器缩放、分辨率、系统字体平滑或兼容性设置。

- **主面板**：改为不透明 HWND，使用 WindowChrome 和 Windows DWM 原生圆角；显式应用统一的界面字体、Display 排版、布局取整与像素吸附。保留原文/译文阅读字体设计，文字抗锯齿使用 Auto，尊重系统设置。
- **其他视图**：截图窗口及原处翻译窗口显式应用同一 Window 样式；原处翻译保留点击穿透的透明窗口，只在不透明纸色区域提示 ClearType。下拉弹出列表同样在不透明背景上启用 ClearType 提示与像素对齐。
- **图标**：主面板随实际 DPI 从 ICO 选择足够大的帧，高质量缩放，移除固定 24 px 图片的最近邻放大。16–40 px 字标仍逐尺寸设计，对斜线和圆角加入独立 8 倍覆盖采样，保留直线像素对齐。
- **折叠控件**：手工配置/高级请求配置改用矢量折线，展开向下、收起向右；整行有边界、悬停、按压和键盘焦点，不再依赖“⌄”字符的字形与基线。
- **拖动**：标题容器设置透明命中背景，空白区可收到鼠标按下并进入原有 DragMove；设置/关闭按钮仍单独处理点击。

相关平台依据：[WPF ClearTypeHint](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.renderoptions.cleartypehint)、[布局取整](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/layout)、[DWM 原生圆角](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)。

## 本轮验证

视觉工具使用与产品一致的 PerMonitorV2 manifest，读取真实 HWND 和 WPF DPI。修复后的面板/设置页均为 DPI 96、缩放 1、PerMonitorV2；非分层，布局取整/像素吸附 true，文字排版 Display。当前执行桌面只暴露 DISPLAY1；不把这次检查写成完整混合 DPI 双屏验收。

工具增加 DPI 一致性、窗口渲染状态及标题空白区命中断言，任一不满足就失败；保留浅深色、所有设置页及多尺度输出。受控渲染共 28 张，不代表屏幕 ClearType 的逐像素输出。

Computer Use 在真实自有 WPF 视图执行两次标题拖动；位置日志由 (704,474) 移至 (644,454)，与第二次 -60/-20 的输入一致。随后点击设置成功，手工配置收起/重新展开、高级请求配置展开均已实际操作；高级配置展开后 UIA 出现认证方式、请求头与请求体字段。

Release 构建 0 警告、0 错误；Core 96/96，交互桌面 Windows 5/5。交互测试在独立 STA、自有编辑器与本地服务上运行，编辑器关闭 IME，不证明完整中文输入法验收。回归曾在模拟新复制处读到空文本；改为立即发布的 SetDataObject(..., true)，加入恢复前文本/序号变化断言，并同时用原生及托管读取核对恢复后文本。最终检查通过；此前托管读取异常的全部触发条件尚未穷尽，本轮不修改生产剪贴板逻辑。

```powershell
dotnet build windows -c Release
dotnet test tests/Cida.Core.Tests -c Release
$env:CIDA_DESKTOP_TESTS = '1'
dotnet test tests/Cida.Windows.Tests -c Release
Remove-Item Env:\CIDA_DESKTOP_TESTS
dotnet run --project tests/Cida.VisualChecks -- --render-suite
pwsh -File scripts/package.ps1 -Version 0.2.0-rc.4
```

## 交付与边界

交付新的 rc.4 未签名候选安装包与便携包，保留 rc.3 tag 和附件。当前证据是源码、实际单屏 100% 窗口及受控交互；没有断言用户所见的全部模糊已经在其外接屏上消失。完整混合 DPI 多屏、实际 125/150/200% OS 缩放切换、完整中文 IME、外部应用矩阵、干净机器安装/升级/卸载及远端自动更新仍待目标环境验收。
