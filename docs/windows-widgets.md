# Windows 小组件面板（可选）

**任务栏小组件已包含在普通便携版中，不需要额外安装。** 本页仅适用于 Windows 的 Win+W 小组件面板。

1. 下载 Release 中的 `CodexQuota-2.2.0-widgets-x64.zip`，完整解压到准备长期保留的目录。
2. 此包未经签名，使用 Windows 开发者注册流程。先在 Windows 设置中启用「开发者模式」，退出正在运行的 CodexQuota，再在 PowerShell 中运行 `./Install-Widgets.ps1`。
3. 从开始菜单打开 **Codex Quota**，再从 Win+W 小组件面板添加 **Codex 额度**。

2.2 将主程序、协议入口和小组件提供程序合并到同一个应用条目下。后台仍会使用 `CodexQuota.WidgetProvider.exe` 承载 Windows 小组件，但它不再是单独的开始菜单应用，也没有自己的开机启动开关。开机启动统一在主程序「设置 → 通用」中控制。

升级时先退出旧版，在新目录运行注册脚本，然后重新打开主程序。若提示 `0x80073D02`，说明旧包的后台提供程序仍在使用中；关闭旧版主程序与 Windows 小组件后重试，必要时重启 Windows 并先注册新版。脚本不会强制关闭正在运行的应用。旧版小组件包使用相同包标识，注册新版会更新原条目。账号和设置仍保存在 `%LOCALAPPDATA%\CodexQuotaWidget`。注册后不要移动或删除当前解压目录；卸载时先在 Windows「已安装的应用」中卸载 Codex Quota，再删除目录。

从源码构建：运行 `./Publish-Widgets.ps1 -OutputDirectory <输出目录>`。脚本生成未经签名的 MSIX 和 `widget-staging-*` 展开目录。可运行 `./Install-Widgets.ps1 -PackageDirectory <展开目录>` 进行开发者注册。

# Windows Widgets board (optional)

**The taskbar widget is included in the portable build and needs no separate installation.** This package is only for the Windows Win+W Widgets board.

1. Download `CodexQuota-2.2.0-widgets-x64.zip` from the release and extract the entire archive to a permanent folder.
2. This is an unsigned developer package. Enable **Developer Mode** in Windows Settings, exit running CodexQuota instances, then run `./Install-Widgets.ps1` in PowerShell.
3. Open **Codex Quota** from Start, then add **Codex 额度** from the Win+W Widgets board.

Version 2.2 groups the desktop app, protocol and Widgets provider under one application entry. `CodexQuota.WidgetProvider.exe` remains a background host, without its own Start entry or startup setting. Configure startup in the main app under **Settings → General**.

To upgrade, exit the old version, register the new extracted folder and reopen the app. Error `0x80073D02` means the old background provider is still in use. Close the old app and Windows Widgets, then retry; if needed, restart Windows and register the new version first. The script does not forcibly close running applications. The package identity is unchanged, so registration updates the existing entry. Accounts and settings remain in `%LOCALAPPDATA%\CodexQuotaWidget`. Keep the registered folder in place. To uninstall, remove Codex Quota from Windows Installed apps before deleting the folder.

To build from source, run `./Publish-Widgets.ps1 -OutputDirectory <output>`. It produces an unsigned MSIX and an expanded `widget-staging-*` folder. Register the latter with `./Install-Widgets.ps1 -PackageDirectory <expanded-folder>`.
