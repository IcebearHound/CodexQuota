using System;
using System.IO;
using Microsoft.Win32;

namespace Quota.Desktop;

internal sealed class StartupRegistration
{
    private const string ValueName = "CodexQuota";
    private readonly string runKeyPath;
    private readonly string approvalKeyPath;
    private readonly string command;

    public StartupRegistration(string executable, string? dataDirectory = null, string? managedAssembly = null,
        string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run",
        string approvalKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
    {
        this.runKeyPath = runKeyPath;
        this.approvalKeyPath = approvalKeyPath;
        command = QuotePath(executable);
        if (managedAssembly is not null) command += " " + QuotePath(managedAssembly);
        command += " --background";
        if (dataDirectory is not null) command += " --data-dir " + QuotePath(dataDirectory);
    }

    public static StartupRegistration ForCurrentProcess()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        var arguments = Environment.GetCommandLineArgs();
        var dataIndex = Array.IndexOf(arguments, "--data-dir");
        var dataDirectory = dataIndex >= 0 && dataIndex + 1 < arguments.Length ? arguments[dataIndex + 1] : null;
        var assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? arguments[0] : null;
        return new(executable, dataDirectory, assembly);
    }

    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKeyPath);
            if (run?.GetValue(ValueName) is not string { Length: > 0 }) return false;
            using var approval = Registry.CurrentUser.OpenSubKey(approvalKeyPath);
            return approval?.GetValue(ValueName) is not byte[] { Length: > 0 } state || state[0] is not (3 or 7);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var run = Registry.CurrentUser.CreateSubKey(runKeyPath, true);
            run.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
            run?.DeleteValue(ValueName, false);
        }
        using var approval = Registry.CurrentUser.OpenSubKey(approvalKeyPath, true);
        approval?.DeleteValue(ValueName, false);
    }

    public void RefreshRegisteredPath()
    {
        using var existingKey = Registry.CurrentUser.OpenSubKey(runKeyPath);
        if (existingKey?.GetValue(ValueName) is not string { Length: > 0 } existing) return;
        // Custom-profile registrations keep their selected data directory.
        if (existing.Contains(" --data-dir ", StringComparison.Ordinal)) return;
        if (string.Equals(existing, command, StringComparison.Ordinal)) return;
        using var run = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
        run?.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string QuotePath(string value)
    {
        var path = Path.GetFullPath(value);
        var trailingSlashes = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailingSlashes) + "\"";
    }
}
