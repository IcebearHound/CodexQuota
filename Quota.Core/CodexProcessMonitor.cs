using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Quota.Core;

internal static class CodexProcessMonitor
{
    private enum ProcessKind { Unknown, AppServer, Worker, Interactive }
    private sealed record Metadata(DateTimeOffset Started, int Parent, ProcessKind Kind);
    private static readonly Dictionary<int, Metadata> Cache = [];
    private static readonly object Gate = new();

    public static (bool Found, DateTimeOffset? Started) Read()
    {
        var desktopIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try { if (CodexDesktopRestart.IsDesktopExecutable(process.MainModule?.FileName)) desktopIds.Add(process.Id); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
        }
        var processes = new Dictionary<int, DateTimeOffset>();
        var inaccessible = false;
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                if (CodexClient.OwnsProcess(process.Id)) continue;
                try { processes[process.Id] = new(process.StartTime.ToUniversalTime()); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { inaccessible = true; }
            }
        }
        lock (Gate)
        {
            foreach (var old in Cache.Keys.Where(id => !processes.ContainsKey(id)).ToArray()) Cache.Remove(old);
            var pending = processes.Where(process => !Cache.TryGetValue(process.Key, out var cached) || cached.Started != process.Value || cached.Kind == ProcessKind.Unknown).ToDictionary();
            if (pending.Count > 0) ReadMetadata(pending);
            var candidates = processes.Where(process => Cache.TryGetValue(process.Key, out var data) && data.Kind is ProcessKind.AppServer or ProcessKind.Interactive)
                .Select(process => Cache[process.Key]).ToArray();
            var servers = candidates.Where(data => data.Kind == ProcessKind.AppServer && desktopIds.Contains(data.Parent)).ToArray();
            if (servers.Length == 0) servers = candidates.Where(data => data.Kind == ProcessKind.AppServer).ToArray();
            if (servers.Length == 0) servers = candidates;
            var unknown = inaccessible || processes.Any(process => !Cache.TryGetValue(process.Key, out var data) || data.Kind == ProcessKind.Unknown);
            return (servers.Length > 0 || unknown, servers.Select(data => (DateTimeOffset?)data.Started).Min());
        }
    }

    private static void ReadMetadata(Dictionary<int, DateTimeOffset> processes)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var process in processes)
        {
            if (ReadNativeMetadata(process.Key, process.Value) is { Kind: not ProcessKind.Unknown } metadata)
                Cache[process.Key] = metadata;
        }
        var unresolved = processes.Where(process => !Cache.TryGetValue(process.Key, out var metadata) || metadata.Started != process.Value || metadata.Kind == ProcessKind.Unknown).ToArray();
        if (unresolved.Length == 0) return;
        object? locator = null, service = null, collection = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null) return;
            locator = Activator.CreateInstance(type);
            service = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
            var identifiers = string.Join(" OR ", unresolved.Select(process => "ProcessId=" + process.Key.ToString(CultureInfo.InvariantCulture)));
            collection = ((dynamic)service).ExecQuery("SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name='codex.exe' AND (" + identifiers + ")");
            foreach (var row in (IEnumerable)collection)
            {
                try
                {
                    var id = (int)(uint)((dynamic)row).ProcessId;
                    if (!processes.TryGetValue(id, out var started)) continue;
                    var command = (string?)((dynamic)row).CommandLine;
                    var kind = Classify(command);
                    Cache[id] = new(started, (int)(uint)((dynamic)row).ParentProcessId, kind);
                }
                finally { if (Marshal.IsComObject(row)) Marshal.FinalReleaseComObject(row); }
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            if (collection is not null && Marshal.IsComObject(collection)) Marshal.FinalReleaseComObject(collection);
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
            if (locator is not null && Marshal.IsComObject(locator)) Marshal.FinalReleaseComObject(locator);
        }
    }

    private static Metadata? ReadNativeMetadata(int processId, DateTimeOffset started)
    {
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid) return null;
        // ProcessCommandLineInformation returns a local UNICODE_STRING and its buffer.
        // Querying the required size first also accommodates long executable paths.
        NtQueryInformationProcess(handle, 60, 0, 0, out var required);
        if (required < Marshal.SizeOf<UnicodeString>() || required > 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, required, out _) < 0) return null;
            var command = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = command.Buffer.ToInt64() - buffer.ToInt64();
            if (command.Length % 2 != 0 || offset < 0 || offset + command.Length > required) return null;
            var kind = Classify(Marshal.PtrToStringUni(command.Buffer, command.Length / 2));
            var parent = 0;
            if (NtQueryBasicInformation(handle, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) >= 0)
                parent = (int)basic.ParentProcessId;
            return new(started, parent, kind);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint ProcessId;
        public nint ParentProcessId;
    }

    private static ProcessKind Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return ProcessKind.Unknown;
        var arguments = CommandLineToArgvW(command, out var count);
        if (arguments == 0) return ProcessKind.Unknown;
        try
        {
            var words = Enumerable.Range(1, Math.Max(0, count - 1)).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size))).ToArray();
            if (words.Contains("app-server")) return ProcessKind.AppServer;
            if (words.Any(word => word is "exec-server" or "mcp-server" or "responses-api-proxy")) return ProcessKind.Worker;
            return ProcessKind.Interactive;
        }
        finally { LocalFree(arguments); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, nint information, int length, out int required);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")] private static extern int NtQueryBasicInformation(SafeProcessHandle process, int informationClass, out ProcessBasicInformation information, int length, out int required);
}
