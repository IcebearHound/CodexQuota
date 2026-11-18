# 原地自更新与恢复

## 使用

「设置 → 通用 → 版本与更新」可关闭自动检查，也可手动检查。每天首次打开时在后台检查，只接受固定 GitHub 仓库 `IcebearHound/CodexQuota` 的较新正式版。托盘后台启动也会检查，发现新版时等到应用窗口打开再提示。弹窗显示版本、更新摘要和下载大小；选择「更新并重启」后才开始下载，下载期间可取消。「跳过此版本」只关闭该版本的自动提示，之后仍可手动更新。

自更新从 2.3 完整便携版开始支持。2.2 及旧 Win+W 包没有自更新入口，应手动下载当前正式版的完整便携包。2.3 起不再支持 Windows 内置小组件，更新不再进行 Appx/COM 扩展注册；已有开机启动项由重新启动的主程序刷新路径，保留 Windows 中的禁用状态。任务栏小组件继续支持。

## 更新过程

1. 从 GitHub Releases API 获取版本、固定名称的 Windows x64 ZIP、大小及 GitHub 提供的 SHA-256 摘要。HTTPS 下载只允许 GitHub 及其资产域名跳转，按流写盘并计算摘要。
2. 校验安装清单与归档内容，拒绝路径穿越、链接、重复路径及账号、配置等非程序文件。只接受自包含单 EXE 与许可、说明文件；清单版本必须一致，实际 EXE 版本也必须匹配。
3. 把**当前程序自身**复制到独立更新目录作为 `Updater.exe`，不在发布包中增加另一套运行时。更新器检查目录及写权限，通过握手后主程序才退出。
4. 更新器等待旧进程完全退出并释放句柄。先保存完整备份与恢复清单，再使用临时文件和同目录重命名替换受管理文件；只移除旧清单声明、而新版不再包含的文件。
5. 启动新程序，等待界面加载确认，并检查两秒内未退出。失败时关闭此次启动的候选进程、恢复旧文件并重新启动旧版。维护租约阻止更新过程中误开旧程序，过期租约不会阻挡启动。

账号数据、设置和安装目录中的未知文件不参与覆盖或删除。受保护或不可写的安装目录需要手动升级，不会自动提权。校验或准备失败时正在运行的程序不退出。多个更新或恢复任务不能同时安装。

## 异常中断后的独立恢复

更新记录位于 `%LOCALAPPDATA%\CodexQuotaUpdater\jobs\<编号>`。设置中的「更新记录与恢复」可打开最近一次目录。完整备份准备后才会出现 `transaction.json`；该目录同时保留旧文件和独立的更新器。

退出 CodexQuota 后，在该目录中按 `RECOVERY.txt` 执行：

```powershell
./Updater.exe --recover-update ./plan.json
```

恢复器不依赖安装目录中的 EXE 完整，也不需要重新下载。若仍有程序占用文件，先退出该程序再恢复。一次成功更新会保留恢复记录；确认新版稳定后，可以删除更早的更新目录来释放磁盘空间。

## 发布约定

运行 `Publish-Release.ps1` 生成完整 ZIP。保留 `CodexQuota-<版本>-win-x64/` 顶层目录及 `install-files.json`，清单列出所有实际文件。GitHub Release 使用 `v主版本.次版本.修订号` 正式标签，上传同版本的 `CodexQuota-<版本>-win-x64.zip`。GitHub API 必须提供该附件的 SHA-256 摘要，缺失时客户端拒绝更新。

源码 ZIP 与截图不放入程序安装清单。未来若改变单文件分发格式或清单协议，需要明确安排兼容迁移，不能静默放宽客户端的文件白名单。

发布安装版时向同一脚本传入 `-InstallerCompiler`，将生成的 `CodexQuota-<版本>-win-x64-setup.exe` 作为额外附件。安装版仍下载同名便携 ZIP 原地更新；安装程序的卸载器和开始菜单快捷方式不属于 ZIP 清单。卸载保留账号和设置。

交互式安装由应用自身绘制界面，Inno Setup 只在后台执行安装。首次安装默认 `D:\Program Files\CodexQuota`，可更换上级目录；已注册的安装沿用原目录。自动部署可使用 `/VERYSILENT /NORESTART /DIR="D:\Program Files\CodexQuota"`，其中 `/DIR` 是确切的最终目录。静默安装不会打开应用或导览。

发布前可用上一版的便携 EXE 做跨版本验收，测试只替换新建临时目录中的副本：

```powershell
dotnet run --project Quota.Presentation.Tests -c Release -- --update-lifecycle artifacts/upgrade-smoke 'C:/path/to/new/CodexQuota.exe' --from 'C:/path/to/old/CodexQuota.exe'
```

## English

Use **Settings → General → Version and updates** to turn off automatic checks or check manually. The app checks stable releases in the background the first time it opens each day. A tray-only launch can check for updates, but waits for an app window before showing the prompt. The prompt shows the version, a short changelog and the download size. **Update and restart** starts the download, which you can cancel. **Skip this version** suppresses future automatic prompts for that version; manual updates still work. In-app updates are supported from the complete 2.3 portable build onward. Users of 2.2 and older Win+W packages should download the current stable portable ZIP manually. Windows Widgets Board support and Appx/COM registration are retired; the ordinary taskbar widget remains supported. The restarted desktop app refreshes its existing startup path without re-enabling startup if Windows disabled it.

Packages come only from the fixed GitHub repository, with HTTPS host checks, bounded streaming and the GitHub-provided SHA-256 digest. An exact installation manifest, safe archive paths and matching EXE version are required. Account data, settings and unknown files are never replaced. The app copies its current executable to an independent job directory as the updater, avoiding a second distributed runtime. It validates the installation before asking the app to quit, backs up owned files, replaces them atomically, and verifies the restarted UI. Failed startup rolls back and reopens the previous version. An active lease blocks accidental launches during installation; stale leases do not block startup.

Recovery jobs live in `%LOCALAPPDATA%\CodexQuotaUpdater\jobs`. After exiting CodexQuota, follow the job's `RECOVERY.txt`, or run `./Updater.exe --recover-update ./plan.json` from that directory. A complete backup is represented by `transaction.json`. Recovery works without the installed EXE or another download. Keep the latest job for recovery; older jobs can be deleted after the new version is stable.

Publish with `Publish-Release.ps1`, preserving the versioned ZIP root and `install-files.json`. Upload the matching x64 ZIP to a stable `vMAJOR.MINOR.PATCH` GitHub release. Missing asset digests, unsupported package formats and unexpected payload files are rejected rather than silently accepted.
