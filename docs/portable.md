# CodexQuota 便携版

解压后运行 `CodexQuota.exe`，内置 .NET 运行时。升级旧版前从托盘菜单退出旧程序，保留当前目录中的 `install-files.json`。

「设置 → 通用 → 版本与更新」可检查正式版。下载并验证成功后，应用会短暂重启、原地替换并刷新开机启动路径；账号和设置不会覆盖。新版本启动失败时自动恢复上一版。也可关闭自动检查，继续手动下载升级。

更新记录与独立恢复程序保存在 `%LOCALAPPDATA%\CodexQuotaUpdater\jobs`；异常中断后可按该次目录中的 `RECOVERY.txt` 恢复。进行手动恢复前退出 CodexQuota。保留最近一次恢复目录，确认新版稳定后可删除更早的更新目录以释放磁盘空间。

支持 CC Switch 一键导入与 Codex 原生浏览器登录。额度查询需要本机 Codex 程序。主面板与任务栏小组件分别支持圆环、状态条。任务栏展开面板跟随任务栏组件的布局；单击组件固定展开，再次单击收起。精简主面板也有实时状态灯，可点击打开异常列表。双击空白处切换精简与完整模式；拖动精简面板靠近当前屏幕工作区的边缘或四角会自动吸附，拖离即可解除。

2.3 起不再支持 Windows Win+W 内置小组件，也不再发布其扩展包。任务栏小组件不受影响。旧 Win+W 包用户请先退出旧程序，下载完整便携版，确认账号和设置完整并备份后再卸载旧包。若数据未出现，保留旧包并先完成备份与迁移；不要删除 `%LOCALAPPDATA%\CodexQuotaWidget` 数据目录。

[完整界面导览](https://github.com/IcebearHound/CodexQuota) · [English](https://github.com/IcebearHound/CodexQuota/blob/main/README.en.md)
