using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;
using Quota.Core;
using Quota.WidgetProvider;

if (args.FirstOrDefault() == "--render-check")
{
    var snapshot = new WidgetSnapshot { Accounts = [new() { Name = "Pro100", Plan = "Pro100", Remaining = 73, Value = 73, IsCurrent = true, Reset = "2小时后重置" }, new() { Name = "Pro200", Plan = "Pro200", Remaining = 18, Value = 18, SourceId = Guid.NewGuid().ToString("N"), Reset = "明天重置" }] };
    snapshot.Accounts.Add(new() { Name = "Pro500", Plan = "Pro500", Remaining = 50, SourceId = Guid.NewGuid().ToString("N") });
    foreach (var size in new[] { "small", "medium", "large" })
    {
        foreach (var used in new[] { false, true })
        {
            using var json = System.Text.Json.JsonDocument.Parse(CardRenderer.Render(snapshot, size, used));
            var rendered = json.RootElement.ToString();
            if (!rendered.Contains("Pro100") || !rendered.Contains(used ? "27%" : "73%") || !rendered.Contains("sourceId")) throw new InvalidOperationException("Card metric or switch action missing");
        }
        using var empty = System.Text.Json.JsonDocument.Parse(CardRenderer.Render(new(), size));
        if (empty.RootElement.GetProperty("body").GetArrayLength() == 0) throw new InvalidOperationException("Empty card has no connection action");
        Console.WriteLine("PASS " + size + " metric, switch, and empty-state rendering");
    }
    var secondPage = CardRenderer.Render(snapshot, "small", false, 1);
    if (!secondPage.Contains("Pro500") || secondPage.Contains("Pro100")) throw new InvalidOperationException("Card paging failed");
    Console.WriteLine("PASS paging reaches remaining accounts");
    snapshot.Language = "en"; snapshot.Accounts[1].Remaining = 0;
    var warning = CardRenderer.Render(snapshot, "medium");
    if (!warning.Contains("attention") || !warning.Contains("Quota exhausted") || !warning.Contains("Refresh")) throw new InvalidOperationException("Localized warning is missing");
    Console.WriteLine("PASS localized exhausted-quota warning");
    return;
}
ComWrappersSupport.InitializeComWrappers();
Marshal.ThrowExceptionForHR(Native.CoInitializeEx(0, 0));
var identifier = new Guid("91133FB0-CE2D-4E2C-9A07-42F527EAE907");
if (args.FirstOrDefault() == "--activation-check")
{
    foreach (var iid in new[] { new Guid("00000000-0000-0000-C000-000000000046"), typeof(IWidgetProvider).GUID })
    {
        var result = Native.CoCreateInstance(identifier, 0, 4, iid, out var instance);
        Console.WriteLine("Activation " + iid + " " + result.ToString("X8") + " pointer " + (instance != 0));
        if (instance != 0) Marshal.Release(instance);
        if (result < 0) Environment.ExitCode = 1;
    }
    return;
}
var factory = new ProviderFactory();
if (args.FirstOrDefault() == "--factory-check")
{
    var iid = new Guid("00000000-0000-0000-C000-000000000046");
    var result = factory.CreateInstance(0, ref iid, out var pointer);
    Console.WriteLine("Factory HRESULT " + result.ToString("X8") + " pointer " + (pointer != 0));
    if (pointer != 0) Marshal.Release(pointer);
    Marshal.ThrowExceptionForHR(result); return;
}
Marshal.ThrowExceptionForHR(Native.CoRegisterClassObject(identifier, factory, 4, 1, out var cookie));
try { if (args.FirstOrDefault() != "--com-check") Thread.Sleep(Timeout.Infinite); }
finally { Native.CoRevokeClassObject(cookie); Native.CoUninitialize(); GC.KeepAlive(factory); }

[ComImport, ComVisible(false), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
public interface IProviderFactory
{
    [PreserveSig] int CreateInstance(nint outer, ref Guid iid, out nint result);
    [PreserveSig] int LockServer(bool locked);
}
[ComVisible(true)]
public sealed class ProviderFactory : IProviderFactory
{
    public int CreateInstance(nint outer, ref Guid iid, out nint result)
    {
        result = 0;
        if (outer != 0) return unchecked((int)0x80040110);
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<IWidgetProvider>.FromManaged(new WidgetProvider());
            if (pointer == 0) throw new InvalidOperationException("WinRT wrapper is null");
            var status = Marshal.QueryInterface(pointer, in iid, out result);
            FactoryLog("QueryInterface " + iid + " " + status.ToString("X8") + " " + (result != 0));
            return status;
        }
        catch (Exception error) { FactoryLog(error.GetType().Name + " " + error.HResult.ToString("X8") + Environment.NewLine + error.StackTrace); Console.Error.WriteLine(error); return Marshal.GetHRForException(error); }
        finally { if (pointer != 0) Marshal.Release(pointer); }
    }
    public int LockServer(bool locked) => 0;
    private static void FactoryLog(string message)
    {
        try { Directory.CreateDirectory(WidgetSnapshot.DefaultDirectory); File.AppendAllText(Path.Combine(WidgetSnapshot.DefaultDirectory, "widget-provider.log"), DateTimeOffset.Now.ToString("O") + " Factory " + message + Environment.NewLine); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
internal static class Native
{
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint concurrency);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] internal static extern int CoCreateInstance([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, nint outer, uint context, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out nint instance);
    [DllImport("ole32.dll")] internal static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] internal static extern int CoRevokeClassObject(uint cookie);
}
