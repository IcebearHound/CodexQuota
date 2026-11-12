# CodexQuota

Windows 上的 Codex 多账号额度监控工具，提供完整与精简主面板、任务栏小组件、实时状态灯及异常会话列表。

A Windows desktop app for monitoring Codex account quotas, reset timers and session status, with compact views and a taskbar widget.

[下载最新版本](https://github.com/IcebearHound/CodexQuota/releases) · [更新记录](CHANGELOG.md)

## 开始使用

1. 从 Releases 下载 Windows x64 便携版 ZIP，解压后运行 `CodexQuota.exe`。便携版已包含 .NET 运行时。
2. 首次启动可通过导览连接当前 Codex 账号、从 CC Switch 导入认证，或使用 Codex 原生浏览器登录。
3. 在设置中选择已安装的 Codex 程序，并调整刷新频率、显示方式和通知。

升级时先退出旧版额度监控，再运行新版。账号和设置保存在 `%LOCALAPPDATA%\CodexQuotaWidget`。用量查询通过本机已安装的 Codex app-server 完成，独立账号使用各自的认证目录。

## 界面与布局

- 完整主面板支持账号圆环卡片及圆环搭配账号条。
- 主窗口的精简模式隐藏操作按钮，仅显示账号名、额度和重置时间。双击、按 Esc 或使用右键菜单返回完整界面，并恢复原有尺寸。
- 精简主面板与任务栏小组件均支持圆环和状态条，布局分别保存。状态条将带图标的重置时间放在百分比左侧。
- 右键任务栏小组件可直接选择「任务栏 · 圆环模式」或「任务栏 · 状态条模式」。也可在设置的小组件页面调整。
- 单击任务栏小组件常驻展开账号面板，再次单击收起。悬停提供临时预览，浮窗支持淡入淡出。
- 设置与导览跟随主面板的置顶设置，已打开的窗口即时同步。
- 支持深浅主题、中英文、账号名称编辑和隐私显示。

## 状态与提醒

状态灯显示执行、等待输入、完成、低额度、耗尽、错误、中断及离线状态。点击异常状态灯或菜单中的「异常会话」可打开列表，逐条忽略或全部忽略。归档会话退出异常统计，同一会话的新异常会再次提醒。

支持低额度、额度重置和重置卡到期通知。同邮箱账号合并显示，并保留不同工作区的额度窗口。

## RC30 性能改进

- 将任务栏透明区域的逐像素桌面读取改为批量采样，减少 DWM 同步造成的 UI 阻塞。
- 在空闲时准备浮窗的窗口、模板与布局，减少首次单击的同步初始化。
- 弹出菜单使用独立的中文 UI 字体，覆盖从任务栏打开菜单时的字体继承。
- 合并重复的前台窗口对齐请求，减少账号属性变更时的重复状态计算。

本机隔离测试中，底色采样从约 131 毫秒降至约 12–15 毫秒，预备后的首次展开处理约 16–28 毫秒，后续展开约 8–22 毫秒。结果受设备与桌面负载影响。

## 构建与验证

需要 Windows 和 .NET 10 SDK。`global.json` 指定 SDK 10.0.100，并允许同系列较新功能版本。

```powershell
dotnet build Quota.Desktop -c Release
dotnet run --project Quota.Tests -c Release
dotnet run --project Quota.Presentation.Tests -c Release
```

实窗回归使用隔离数据目录。运行时应有可交互的 Windows 桌面。

```powershell
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('CodexQuota-' + [guid]::NewGuid().ToString('N'))
dotnet run --project Quota.Presentation.Tests -c Release -- --guide-smoke --demo --guide --data-dir $fixture
```

任务栏性能与资源释放回归使用独立的新目录。

```powershell
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('CodexQuotaPerformance-' + [guid]::NewGuid().ToString('N'))
dotnet run --project Quota.Presentation.Tests -c Release -- --guide-smoke --demo --guide --taskbar-performance --data-dir $fixture
```

发布自包含单文件版本。

```powershell
dotnet publish Quota.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/win-x64
```

源码包含 `Quota.WidgetProvider` Windows Widgets Board 提供程序及 `Publish-Widgets.ps1`。它使用独立的 MSIX 注册流程，任务栏小组件随主程序运行。

## 许可

项目使用 MIT License。Lucide 图标许可位于 `Quota.Desktop/Assets/lucide/LICENSE`。便携版包含 .NET 运行时许可及第三方声明。
