# CodexQuota portable

Extract and run `CodexQuota.exe`; the .NET runtime is included. Exit older versions from their tray menus before upgrading. Keep `install-files.json` in the application folder.

Use **Settings → General → Version and updates** to check stable releases. After confirmation, the app downloads and verifies the package, restarts briefly, replaces files in place and refreshes its startup path. Accounts and settings are retained. Failed startup checks automatically restore the previous version. Automatic checks can be disabled; manual ZIP upgrades remain available.

Update history and an independent recovery executable are stored in `%LOCALAPPDATA%\CodexQuotaUpdater\jobs`. After an interrupted update, follow that job's `RECOVERY.txt`. Exit CodexQuota before manual recovery. Keep the latest recovery folder; older job folders can be removed after the new version is stable to reclaim disk space.

Connect accounts with CC Switch import or native Codex browser sign-in. Quota queries require the locally installed Codex app. Main and taskbar layouts are independent; click the taskbar widget to pin its panel open and again to close it.

Starting with 2.3, the Windows Win+W Widgets board and its extension package are no longer supported. The taskbar widget remains supported. Old Win+W users should exit the old app and download the complete portable ZIP. Verify and back up accounts and settings before uninstalling the old package. If data is missing, keep the old package until backup and migration are complete. Keep `%LOCALAPPDATA%\CodexQuotaWidget`.

[Full English guide](https://github.com/IcebearHound/CodexQuota/blob/main/README.en.md)
