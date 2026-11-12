using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Quota.Core;

namespace Quota.Desktop;
public partial class MainWindow
{
    private static string WidgetKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    private void PublishWidgetData()
    {
        if (demo) return;
        var snapshot = new WidgetSnapshot { ShowUsed = settings.ShowUsed };
        foreach (var group in visibleAccounts)
        {
            snapshot.Accounts.Add(new()
            {
                Key = WidgetKey(group.Key),
                Name = group.Name,
                Plan = group.Plan,
                IsCurrent = group.IsCurrent,
                SourceId = group.Sources.Where(source => source.Managed).OrderBy(source => source.HasError).FirstOrDefault()?.Record.Id,
                Value = group.FocusWindow?.Value,
                Remaining = group.FocusWindow?.Remaining,
                Reset = group.FocusWindow?.ResetText ?? L.T("暂无额度"),
                Status = group.Status,
                WindowLabel = group.FocusWindow?.Label ?? ""
            });
        }
        snapshot.Language = settings.Language;
        snapshot.LowThreshold = settings.LowQuotaThreshold;
        try { snapshot.Save(store.Root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
