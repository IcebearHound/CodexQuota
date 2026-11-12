using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using Quota.Core;

namespace Quota.WidgetProvider;
public sealed class WidgetProvider : IWidgetProvider
{
    private sealed record State(WidgetSize Size, bool? ShowUsed, int Page = 0, string? Notice = null);
    private static readonly ConcurrentDictionary<string, State> Widgets = new();
    private static readonly System.Threading.Timer Updates = new(_ => UpdateAll(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    public WidgetProvider()
    {
        Log("Provider activated");
        try
        {
            var manager = WidgetManager.GetDefault();
            var infos = manager?.GetWidgetInfos();
            Log("Widget inventory count " + (infos?.Length ?? 0));
            if (infos is not null)
                foreach (var info in infos)
                    if (info?.WidgetContext is { } context) Widgets[context.Id] = new(context.Size, bool.TryParse(info.CustomState, out var used) ? used : null);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException) { Log("Widget inventory", error); }
        StartDesktop(true);
    }
    public void CreateWidget(WidgetContext context) { Log("Widget created"); Widgets[context.Id] = new(context.Size, null); Update(context.Id); }
    public void DeleteWidget(string widgetId, string customState) => Widgets.TryRemove(widgetId, out _);
    public void Activate(WidgetContext context) { Widgets.TryAdd(context.Id, new(context.Size, null)); Update(context.Id); }
    public void Deactivate(string widgetId) { }
    public void OnWidgetContextChanged(WidgetContextChangedArgs args)
    {
        var id = args.WidgetContext.Id;
        Widgets.AddOrUpdate(id, new State(args.WidgetContext.Size, null), (_, state) => state with { Size = args.WidgetContext.Size });
        Update(id);
    }
    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        var id = args.WidgetContext.Id; var verb = args.Verb; var data = args.Data;
        if (verb == "next")
        {
            var count = WidgetSnapshot.Read(WidgetSnapshot.DefaultDirectory).Accounts.Count;
            var pageSize = args.WidgetContext.Size == WidgetSize.Small ? 2 : args.WidgetContext.Size == WidgetSize.Large ? 6 : 4;
            Widgets.AddOrUpdate(id, new State(args.WidgetContext.Size, null), (_, state) => state with { Page = (state.Page + 1) % Math.Max(1, (count + pageSize - 1) / pageSize) });
            Update(id); return;
        }
        if (verb == "toggle")
        {
            Widgets.AddOrUpdate(id, new State(args.WidgetContext.Size, true), (_, state) => state with { ShowUsed = !(state.ShowUsed ?? WidgetSnapshot.Read(WidgetSnapshot.DefaultDirectory).ShowUsed) });
            Update(id); return;
        }
        if (verb == "refresh") { StartDesktop(true); SignalRefresh(); Update(id); return; }
        if (verb == "switch") Switch(id, data);
    }
    private static void Switch(string widgetId, string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var id = document.RootElement.Str("sourceId");
            if (id is null || !Guid.TryParseExact(id, "N", out _)) return;
            StartDesktop(false, id);
        }
        catch (Exception error) when (error is CodexException or IOException or JsonException or OperationCanceledException or UnauthorizedAccessException) { SetNotice(widgetId, WidgetSnapshot.Read(WidgetSnapshot.DefaultDirectory).Language == "en" ? "Switch failed. Refresh or sign in again in the panel." : "切换未完成，请在面板中刷新或重新登录"); }
    }
    private static void SetNotice(string id, string notice)
    {
        if (Widgets.TryGetValue(id, out var state)) Widgets[id] = state with { Notice = notice };
        Update(id);
    }
    private static void UpdateAll() { foreach (var id in Widgets.Keys) Update(id); }
    private static void Update(string id)
    {
        if (!Widgets.TryGetValue(id, out var state)) return;
        try
        {
            var snapshot = WidgetSnapshot.Read(WidgetSnapshot.DefaultDirectory);
            var options = new WidgetUpdateRequestOptions(id)
            {
                Template = CardRenderer.Render(snapshot, state.Size.ToString().ToLowerInvariant(), state.ShowUsed, state.Page, state.Notice),
                Data = "{}",
                CustomState = state.ShowUsed?.ToString() ?? ""
            };
            WidgetManager.GetDefault().UpdateWidget(options);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException or IOException) { Log("Widget update", error); }
    }
    private static void Log(string message, Exception? error = null)
    {
        try { Directory.CreateDirectory(WidgetSnapshot.DefaultDirectory); File.AppendAllText(Path.Combine(WidgetSnapshot.DefaultDirectory, "widget-provider.log"), DateTimeOffset.Now.ToString("O") + " " + message + (error is null ? "" : " " + error.GetType().Name + " " + error.HResult.ToString("X8")) + Environment.NewLine); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
    private static void StartDesktop(bool background, string? switchAccount = null)
    {
        var desktop = Path.Combine(AppContext.BaseDirectory, "Desktop", "CodexQuota.exe");
        if (!File.Exists(desktop)) return;
        try { var start = new ProcessStartInfo(desktop) { UseShellExecute = false, CreateNoWindow = true }; if (background) start.ArgumentList.Add("--background"); if (switchAccount is not null) { start.ArgumentList.Add("--switch-account"); start.ArgumentList.Add(switchAccount); } Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static void SignalRefresh()
    {
        try { using var signal = EventWaitHandle.OpenExisting(@"Local\CodexQuotaWidget.v2.Refresh"); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }
}
