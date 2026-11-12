using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Quota.Core;

namespace Quota.Desktop;

public partial class MainWindow
{
    private GuideWindow? guideWindow;
    private bool guidePlacementActive;
    private Task? startupRefresh;
    private bool WidgetsAllowed => demo || settings.GuideVersion >= 1 || guidePlacementActive;
    internal WidgetSettings GuideSettings => settings;
    internal bool GuideDemo => demo;
    internal int GuideAccountCount => visibleAccounts.Count(account => account.HasMetric);

    private void ShowUserGuide(int page = 0)
    {
        if (guideWindow is { } existing) { existing.ShowPage(page); existing.Activate(); return; }
        var guide = new GuideWindow(this);
        guideWindow = guide;
        guide.Closed += (_, _) =>
        {
            guideWindow = null;
            if (exiting || !guide.WasDismissed) return;
            settings.GuideVersion = 1; settings.CcSwitchPromptShown = true;
            guidePlacementActive = false;
            SaveSettings(); ApplySettings();
        };
        guide.Show(); guide.ShowPage(page);
    }
    internal void BeginGuidePlacement()
    {
        guidePlacementActive = true;
        ApplySettings();
    }
    internal void SetGuideTaskbar(bool enabled, string position)
    {
        settings.TaskbarWidgetEnabled = enabled; settings.TaskbarPlacement = position;
        guidePlacementActive = true; ApplySettings(); SaveSettings();
        taskbarWidget?.RefreshPlacement();
    }
    internal async Task PrepareGuideConnectionAsync(CancellationToken cancellationToken)
    {
        if (startupRefresh is not null) await startupRefresh.WaitAsync(cancellationToken);
    }
    internal async Task<AccountConnectionResult> ConnectFromGuideAsync(bool import)
    {
        if (import) { settings.CcSwitchPromptShown = true; SaveSettings(); return await SyncCcSwitchAsync(); }
        return await AddAccountAsync();
    }
    internal void SetGuideLanguage(string language)
    {
        settings.Language = language; L.Apply(language);
        foreach (var account in accounts)
        {
            if (account.Record.IsCurrent && settings.CurrentAccountName is null) account.Rename(L.T("当前 Codex 账号"));
            account.RefreshDerived();
        }
        RebuildAccountGroups(); ApplySettings(); SaveSettings();
    }
}
