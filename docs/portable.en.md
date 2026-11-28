# CodexQuota portable

Extract and run `CodexQuota.exe`; the .NET runtime is included. Exit older versions from their tray menus before upgrading. Keep `install-files.json` in the application folder.

Use **Settings → General → Version and updates** to check stable releases. After confirmation, the app downloads and verifies the package, restarts briefly, replaces files in place and refreshes its startup path. Accounts and settings are retained. Failed startup checks automatically restore the previous version. Automatic checks can be disabled; manual ZIP upgrades remain available.

Update history and an independent recovery executable are stored in `%LOCALAPPDATA%\CodexQuotaUpdater\jobs`. After an interrupted update, follow that job's `RECOVERY.txt`. Exit CodexQuota before manual recovery. Keep the latest recovery folder; older job folders can be removed after the new version is stable to reclaim disk space.

After connecting a backup account, enable **Switch accounts when quota runs out** in the first-run guide or **Settings → Accounts**. When Codex Quota restarts the desktop app, it reopens the most recently running chat so you can continue there.

Connect accounts with CC Switch import or native Codex browser sign-in. Quota queries require the locally installed Codex app. Main and taskbar layouts are independent. The expanded taskbar panel follows the taskbar widget's ring or status-bar mode; click the widget to pin the panel open and again to close it. Compact mode includes a live status light with session-issue access. Double-click blank space to switch between compact and full mode. Drag the compact panel near a screen work-area edge or corner to snap, and drag away to release.

Enable optional API promotional credit monitoring in the guide’s **API credits** chapter or **Settings → Quota monitoring**. It needs the Microsoft Edge WebView2 Runtime. API organizations appear separately, and monitoring does not require an API key. To sign in to Codex with an API organization, choose **Switch account** on its card, enter its project API key on the first switch, and restart Codex. Choose which panels show API credits under **Settings → Widgets**.

Starting with 2.3, the Windows Win+W Widgets board and its extension package are no longer supported. The taskbar widget remains supported. Old Win+W users should exit the old app and download the complete portable ZIP. Verify and back up accounts and settings before uninstalling the old package. If data is missing, keep the old package until backup and migration are complete. Keep `%LOCALAPPDATA%\CodexQuotaWidget`.

[Full English guide](https://github.com/IcebearHound/CodexQuota/blob/main/README.en.md)
