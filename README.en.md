# CodexQuota

[简体中文](README.md) · **English**

A Windows desktop app for monitoring Codex account quotas and reset timers. The interactive guide supports **one-click account import from CC Switch** and **native Codex sign-in**.

[Download the 2.0 release](https://github.com/IcebearHound/CodexQuota/releases/tag/v2.0.0) · [Changelog](CHANGELOG.md)

## Start with the interactive guide

Download the Windows x64 portable ZIP, extract it and run `CodexQuota.exe`. The portable build includes the .NET runtime and queries usage through your locally installed Codex application.

The guide opens on first launch. You can switch chapters freely and reopen it from **Settings → General → Guide and manual**. Screenshots use demo accounts.

### Connect your accounts

- **One-click CC Switch import**. Choose **Import from CC Switch** to scan local Codex accounts, then confirm to sync credentials and original names.
- **Native sign-in**. Choose **Sign in with Codex** and authenticate through the official browser page. Credentials are stored separately for each account, and you can repeat the flow to add accounts.

Import, resync and account management are also available in **Settings → Accounts**.

<img src="docs/images/guide-accounts-en.png" alt="Guide with CC Switch import and native Codex sign-in" width="680">

### Understand quotas and reset timers

Interactive examples explain remaining and used quota. Short-term and weekly windows reset independently, and the main ring highlights the limiting window. Accounts sharing an email are grouped with workspace quotas displayed individually.

<img src="docs/images/guide-quota-en.png" alt="Quota guide with rings, bars and independent reset windows" width="560">

### Read session status

Select a status example to explore working, completed, waiting for input, low quota, exhausted, error, interrupted and offline states. During normal use, click an issue indicator to open the session list. Ignore one occurrence or all current issues. Archived chats leave the issue count.

<img src="docs/images/guide-status-en.png" alt="Interactive status-light guide" width="540">

### Place the taskbar widget

Enable the widget in the placement chapter. Click once to pin its account panel open and click again to close it. Hover for a temporary preview. The context menu switches between ring and status-bar layouts, with separate layout preferences for the taskbar and main window.

<img src="docs/images/taskbar-bar.png" alt="Taskbar status bar and reset countdown" width="200">

## Everyday use

The main window shows accounts, quotas and reset timers. Its compact-mode button shrinks the panel. Double-click, press Esc or use the context menu to restore the full interface.

<img src="docs/images/main.png" alt="Full main window with demo accounts" width="300">

| Compact ring | Compact status bar |
| --- | --- |
| <img src="docs/images/compact-ring.png" alt="Compact ring layout" width="176"> | <img src="docs/images/compact-bar.png" alt="Compact status-bar layout" width="176"> |

## Palettes and liquid glass

**Settings → Appearance** offers Mint, Ocean, Violet and Amber palettes, with System, Dark and Light theme options. Liquid glass combines the Windows 11 backdrop blur, translucent layers and soft highlights. Changes apply immediately to the main window, guide and flyouts.

Solid colors are used when the system backdrop is unavailable or the main window is embedded in the desktop.

<details>
<summary>Appearance and common options</summary>

<img src="docs/images/settings.png" alt="Palette and panel-material settings" width="380">

Enable **Start with Windows** in **Settings → General → Window behavior** to launch in the tray after sign-in. Quota and reset-credit notifications, Chinese and English, account renaming and privacy display are supported.

<img src="docs/images/startup.png" alt="Start with Windows option" width="360">

<img src="docs/images/taskbar-menu.png" alt="Taskbar context menu" width="176">

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
