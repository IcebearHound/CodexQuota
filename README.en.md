# CodexQuota

[简体中文](README.md) · **English**

Monitor Codex account quotas, reset timers and session issues from one Windows app. Connect accounts with **one-click CC Switch import** or **native Codex sign-in**.

**[Download the 2.0 release](https://github.com/IcebearHound/CodexQuota/releases/tag/v2.0.0)** · [Quick start](#quick-start) · [Changelog](CHANGELOG.md)

Screenshots use the light theme and demo data.

| Compact widget (main window) | Taskbar widget and expanded panel |
| --- | --- |
| <img src="docs/images/compact-ring-en.png" alt="Complete compact ring view with quotas and reset timers for four accounts" width="252"> | <img src="docs/images/taskbar-preview-en.png" alt="Taskbar quota strip with its complete expanded four-account panel" width="380"> |

- Track short-term and weekly reset windows independently, with same-email accounts grouped together.
- Use compact mode in the main window, with separate ring and status-bar layouts for the main panel and taskbar.
- Open an affected chat, ignore one occurrence or ignore all current session issues.

| Multi-account dashboard | Session issues |
| --- | --- |
| <img src="docs/images/main-en.png" alt="Light dashboard with account quotas and reset timers" width="320"> | <img src="docs/images/session-issues-en.png" alt="Light session list with errors, interruptions and actions" width="400"><br>See chat titles, issue types and timestamps.<br>Use the arrow to open a chat or the ignore icon to dismiss an occurrence. |

## Act on session issues

Open the issue list from the main window, a status light or the taskbar menu. Review errors and interruptions, ignore a single occurrence or choose **Ignore all**. The list refreshes automatically and filters out archived chats. A new issue in the same chat triggers a fresh alert.

## Quick start

1. Download the Windows x64 portable ZIP, extract it and run `CodexQuota.exe`. The .NET runtime is included. Usage is queried through your locally installed Codex application.
2. Follow the first-run interactive guide to connect accounts, explore quota examples and enable the taskbar widget.
3. Reopen the guide from **Settings → General → Guide and manual**. You can switch chapters freely.

### CC Switch import and native sign-in

- Choose **Import from CC Switch** to scan existing local Codex accounts, then confirm to sync credentials and account names.
- Choose **Sign in with Codex** to authenticate through the official browser page. Added accounts use separate credential directories, and you can repeat the flow to connect more accounts.

<img src="docs/images/guide-accounts-en.png" alt="Light guide with CC Switch import and native sign-in" width="560">

### Quota and status walkthrough

The quota example switches between remaining and used quota and explains independent reset windows. Select a status example to explore working, completed, waiting for input, low quota, exhausted, error, interrupted and offline signals.

<details>
<summary>View the quota and status guide</summary>

<img src="docs/images/guide-quota-en.png" alt="Light quota guide" width="500">

<img src="docs/images/guide-status-en.png" alt="Light status-light guide" width="500">

</details>

## Taskbar and compact views

Click the taskbar widget once to pin its account panel open and again to close it. Hover for a temporary preview. Its context menu switches between ring and status-bar layouts. The taskbar and main window save their layout preferences separately.

<img src="docs/images/taskbar-bar-en.png" alt="Light taskbar status bar" width="200">

Compact mode shows account names, quotas and reset timers. Double-click, press Esc or use the context menu to restore the full interface.

| Compact ring | Compact status bar |
| --- | --- |
| <img src="docs/images/compact-ring-en.png" alt="Light compact ring" width="176"> | <img src="docs/images/compact-bar-en.png" alt="Light compact status bar" width="176"> |

## Make it fit your desktop

Choose Mint, Ocean, Violet or Amber, with System, Dark and Light theme options. Liquid glass uses Windows 11 backdrop blur, translucent layers and soft highlights, with live changes across the main window, guide and flyouts. Solid colors are used when the system backdrop is unavailable or the window is embedded in the desktop.

<details>
<summary>Appearance, startup and other options</summary>

<img src="docs/images/settings-en.png" alt="Light palette and panel-material settings" width="340">

Enable **Start with Windows** in **Settings → General → Window behavior** to launch in the tray after sign-in. Quota and reset-credit alerts, Chinese and English, account renaming and privacy display are supported.

<img src="docs/images/startup-en.png" alt="Light startup setting" width="340">

<img src="docs/images/taskbar-menu-en.png" alt="Light taskbar context menu" width="176">

</details>

## Data and upgrades

Accounts and settings are stored in `%LOCALAPPDATA%\CodexQuotaWidget`. Usage is queried through the local Codex app-server. Exit the previous version before starting an upgrade.

<details>
<summary>Build and verification</summary>

Requires Windows and the .NET 10 SDK. `global.json` specifies SDK 10.0.100 and allows newer feature bands.

```powershell
dotnet build Quota.Desktop -c Release
dotnet run --project Quota.Tests -c Release
dotnet run --project Quota.Presentation.Tests -c Release
```

UI regression checks require an interactive Windows desktop and an isolated data directory.

```powershell
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('CodexQuota-' + [guid]::NewGuid().ToString('N'))
dotnet run --project Quota.Presentation.Tests -c Release -- --guide-smoke --demo --guide --data-dir $fixture
```

Publish a self-contained single-file build.

```powershell
dotnet publish Quota.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/win-x64
```

The source includes the `Quota.WidgetProvider` Windows Widgets Board provider and `Publish-Widgets.ps1`, which use a separate MSIX registration flow.

</details>

## License

[MIT License](LICENSE). The Lucide license is in [Assets/lucide/LICENSE](Quota.Desktop/Assets/lucide/LICENSE). Portable builds include .NET runtime licenses and third-party notices.
