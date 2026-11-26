# <img src="Quota.Desktop/Assets/brand.svg" alt="" width="36"> CodexQuota

[简体中文](README.md) · **English**

Monitor Codex account quotas, reset timers and session issues from one Windows app. Connect accounts with **one-click CC Switch import** or **native Codex sign-in**.

**[Download 4.0.1](https://github.com/IcebearHound/CodexQuota/releases/tag/v4.0.1)** · [Quick start](#quick-start) · [Changelog](CHANGELOG.md)

Screenshots use demo data.

| Compact widget (main window) | Taskbar widget and expanded panel |
| --- | --- |
| <img src="docs/images/compact-ring-en.png" alt="Complete compact ring view with quotas and reset timers for four accounts" width="252"> | <img src="docs/images/taskbar-preview-en.png" alt="Taskbar quota strip with its complete expanded four-account panel" width="380"> |

- Track short-term and weekly reset windows independently, with same-email accounts grouped together.
- Choose ring or status-bar mode separately for the compact main window and taskbar widget. The expanded taskbar panel follows the widget's layout.
- Open an affected chat, ignore one occurrence or ignore all current session issues.

| Multi-account dashboard | Session issues |
| --- | --- |
| <img src="docs/images/main-en.png" alt="Light dashboard with account quotas and reset timers" width="320"> | <img src="docs/images/session-issues-en.png" alt="Light session list with errors, interruptions and actions" width="400"><br>See chat titles, issue types and timestamps.<br>Use the arrow to open a chat or the ignore icon to dismiss an occurrence. |

## Act on session issues

Hover over the status light to distinguish a quota read error for the current account from a chat error. The tooltip shows why the quota could not be read. For chat errors, click the light to open the issue list. When both occur, the tooltip lists them separately.

Open the issue list from the main window, a status light or the taskbar menu. Review errors and interruptions, ignore a single occurrence or choose **Ignore all**. The list refreshes automatically, filters out archived chats, attached history snapshots and internal subagents, and clears old interruptions when work resumes. A new issue in the same chat triggers a fresh alert. Input requests, errors, interruptions and exhausted quotas appear in a compact notification window.

## Quick start

1. If the release includes a `-setup.exe`, run it. Setup defaults to `D:\Program Files\CodexQuota`. When you choose another parent folder, it appends `CodexQuota`. Alternatively, extract the entire Windows x64 portable ZIP and run `CodexQuota.exe`. Both include .NET. Usage is queried through the locally installed Codex app.
2. Open the guide from the installer’s finish page. Connect accounts, place the taskbar widget, and choose whether Codex Quota should start in the tray when you sign in to Windows.
3. Reopen the guide from **Settings → General → Guide and manual**. You can switch chapters freely.

### CC Switch import and native sign-in

- Choose **Import from CC Switch** to scan existing local Codex accounts, then confirm to sync credentials and account names.
- Choose **Sign in with Codex** to authenticate through the official browser page. Added accounts use separate credential directories, and you can repeat the flow to connect more accounts.

<img src="docs/images/guide-accounts-en.png" alt="First-run accounts guide with CC Switch import, native sign-in, and the automatic switch toggle" width="560">

### Switch accounts when quota runs out

After connecting a backup account, enable **Switch accounts when quota runs out** under **Connect accounts** in the first-run guide or under **Settings → Accounts**. It is off by default. When a refresh confirms the active account's Codex quota is exhausted, Codex Quota rechecks the backup before switching. It leaves the account unchanged if no backup has quota, the desktop app cannot close normally, or Codex is running only in the CLI.

When Codex Quota restarts the desktop app, it reopens the most recently running chat. If no chat is running, an automatic switch also looks for a recent chat interrupted by a quota error. Continue unfinished work in that chat; prompts are not sent again automatically.

### Track OpenAI API promotional credits

Enable **Monitor API promotional credits** in the guide’s **API credits** chapter or under **Settings → Quota monitoring**. It is off by default. Choose **Connect API account** and sign in to OpenAI Platform. The app reads billing for the selected organization and returns to the quota panel automatically. Each organization appears once as `API · account name` and is never used for Codex account switching. No API key or payment details are needed. Sign-ins stay in local Microsoft Edge WebView2 profiles.

- Read the promotional USD balance and next expiry date from [OpenAI Platform Billing overview](https://platform.openai.com/settings/organization/billing/overview). If the page shows only a date, the app doesn’t infer a time. If it doesn’t show a separate promotional balance, a mixed or wallet balance is not counted.
- Open the API account’s **⋯ → Full-cycle API allowance** to set the 100% mark (for example, US$100 for Pro100). The displayed balance still comes from billing, and a prorated first grant is not counted as spending.
- Low-balance, exhaustion and new-grant alerts use the quota settings. Disable API expiry alerts separately under **Settings → Notifications**. A low-balance percentage requires a confirmed original grant amount. Disabling monitoring stops checks and alerts but keeps your connections.
- Under **Settings → Widgets → Show API credits**, choose whether the full main panel, simplified widget, taskbar strip and taskbar flyout each show API credits. The strip shows the API balance and expiry beside Codex when space allows. Scroll over the API section to change only the displayed API account. In tight layouts, the flyout still shows API credits if enabled.

### Quota and status walkthrough

The quota example switches between remaining and used quota and explains independent reset windows. Select a status example to explore working, completed, waiting for input, low quota, exhausted, error, interrupted and offline signals.

After a network or Windows system proxy change, accounts that are not synced retry without waiting for the next scheduled refresh. A timeout marks previously read quota as **Stale**. Other failures show **Action needed** with the reason. Stale quota for an inactive account does not turn the status light red.

Two rates appear beside the status heartbeat by default. Turn either off in the guide’s **Quota monitoring** chapter or **Settings → Quota monitoring**:

- **Local output rate (TPS)**: The dashboard shows the average output tokens per second logged by local Codex sessions over the last 60 seconds. Logs update in batches, so this is not live generation speed.
- **Quota consumption rate**: Shows the signed-in local account’s most constrained quota window. The rate uses two consecutive successful readings of the same window, with no extra requests. `2.00/h` means two percentage points consumed per hour. If the hourly value would round to `0.00/h`, the rate is shown per day instead (for example, `0.10/d`). A dash appears after a reset or when there is insufficient data.

The full dashboard, compact widget, and expanded taskbar panel show rates in their headers, not on account cards. The pinned widget also shows them when there is room. Browsing another account does not change the active account’s rate.

<details>
<summary>View the quota and status guide</summary>

<img src="docs/images/guide-quota-en.png" alt="Light quota guide" width="500">

<img src="docs/images/guide-status-en.png" alt="Light status-light guide" width="500">

</details>

## Taskbar and compact views

Click the taskbar widget once to pin its account panel open and again to close it. Hover for a temporary preview. Its context menu changes the layout of both the widget and the expanded panel without changing the main window. See the [ring panel](docs/images/taskbar-ring-panel-en.png) or the [single-column status-bar panel](docs/images/taskbar-preview-en.png).

<img src="docs/images/taskbar-bar-en.png" alt="Light taskbar status bar" width="200">

Compact mode shows account names, quotas, reset timers and a live status light that opens session issues. Double-click blank space to switch between compact and full mode; buttons, account text and the light do not trigger this shortcut. Esc and the context menu also restore the full interface. Drag near a screen’s work-area edge or corner to snap the compact panel without covering the taskbar; drag away to release.

| Compact ring | Compact status bar |
| --- | --- |
| <img src="docs/images/compact-ring-en.png" alt="Light compact ring" width="176"> | <img src="docs/images/compact-bar-en.png" alt="Light compact status bar" width="176"> |

## Make it fit your desktop

**Black & White** is now the default palette and appears first. It follows Windows light and dark mode. Mint, Ocean, Violet and Amber are also available, along with manual light/dark controls. Upgrades preserve your selected palette.

| Black & White · Light | Black & White · Dark |
| --- | --- |
| <img src="docs/images/mono-light.png" alt="Black & White palette in light mode" width="252"> | <img src="docs/images/mono-dark.png" alt="Black & White palette in dark mode" width="252"> |

Liquid glass uses native blur and a neutral frost to soften background colors and keep text readable. It appears on first open without another click. Solid colors are used when the system backdrop is unavailable or the window is embedded in the desktop.

<img src="docs/images/glass-light.png" alt="Light liquid glass on the main window and taskbar flyout softens the demo background" width="760">

<details>
<summary>Appearance, startup and other options</summary>

<img src="docs/images/settings-en.png" alt="Light palette and panel-material settings" width="340">

Enable **Start with Windows** in **Settings → General → Window behavior** to launch straight into the tray at Windows sign-in without opening the main window. Quota and reset-credit alerts, Chinese and English, account renaming and privacy display are supported.

<img src="docs/images/startup-en.png" alt="Light startup setting" width="340">

<img src="docs/images/taskbar-menu-en.png" alt="Light taskbar context menu" width="176">

</details>

## In-place updates

The first time you open Codex Quota each day, it checks for a stable release in the background. If one is available, the update panel shows what’s new and offers **Update and restart** or **Skip this version**. In **Settings → General → Version and updates**, you can turn off automatic checks or check manually. After verifying the GitHub-provided SHA-256 digest, the app restarts briefly, replaces files in its original folder and refreshes the existing startup path. Failed startup checks automatically restore the previous version.

<img src="docs/images/updates-en.png" alt="Version and update settings with automatic and manual checks" width="460">

Self-updating starts with the **complete 2.3 portable ZIP**. Users of 2.2 or old Win+W packages need to download this version once. See [update architecture and recovery](docs/updates.md#english).

## Data and upgrades

Accounts and settings are stored in `%LOCALAPPDATA%\CodexQuotaWidget`. Codex usage is queried through the local Codex app-server; API promotional credits use separate WebView2 sign-ins. Exit the previous version before starting an upgrade.

**Starting with 2.3, Windows Win+W Widgets board support and its extension package are discontinued. The taskbar widget remains supported.** First verify and back up your accounts and settings in the portable build, then remove the old package from Windows Installed apps. If data is missing, keep the old package until backup and migration are complete. Keep the data directory above.

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
./Publish-Release.ps1 -OutputDirectory artifacts/release
```

The release script packages the self-contained single-file app, licenses and `install-files.json` for future in-place updates. The updater reuses the app executable instead of shipping another .NET runtime.

With Inno Setup 6.7.3 or newer, pass `-InstallerCompiler` and the path to `ISCC.exe` to also build the installer from the same files.

</details>

## License

[MIT License](LICENSE). The Lucide license is in [Assets/lucide/LICENSE](Quota.Desktop/Assets/lucide/LICENSE). Portable builds include .NET runtime licenses and third-party notices.
