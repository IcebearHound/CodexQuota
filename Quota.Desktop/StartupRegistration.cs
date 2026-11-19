using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace Quota.Desktop;

internal sealed class StartupRegistration
{
    private const string ValueName = "CodexQuota";
    private const int TaskCreateOrUpdate = 6, TaskUpdate = 4, InteractiveToken = 3, LogonTrigger = 9, ExecAction = 0;
    private const int FileNotFound = unchecked((int)0x80070002);
    private readonly string runKeyPath;
    private readonly string approvalKeyPath;
    private readonly string executable, arguments, userId, taskName;
    private readonly string command;

    public StartupRegistration(string executable, string? dataDirectory = null, string? managedAssembly = null,
        string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run",
        string approvalKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        string? taskName = null)
    {
        this.runKeyPath = runKeyPath;
        this.approvalKeyPath = approvalKeyPath;
        using var identity = WindowsIdentity.GetCurrent();
        userId = identity.User!.Value;
        this.taskName = taskName ?? $"{ValueName} - {userId}";
        this.executable = Path.GetFullPath(executable);
        arguments = (managedAssembly is null ? "" : QuotePath(managedAssembly) + " ") + "--background";
        if (dataDirectory is not null) arguments += " --data-dir " + QuotePath(dataDirectory);
        command = QuotePath(executable) + " " + arguments;
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

    internal static bool IsAccessFailure(Exception error) =>
        error is COMException or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException;

    public bool IsEnabled
    {
        get
        {
            var task = GetTask(Connect().Folder);
            return task is not null ? (bool)task.Enabled : ReadLegacyCommand() is not null && LegacyApproved();
        }
    }

    public void SetEnabled(bool enabled)
    {
        var (service, folder) = Connect();
        if (enabled)
        {
            Register(service, folder);
            RemoveLegacy();
        }
        else
        {
            RemoveLegacy();
            if (GetTask(folder) is not null) folder.DeleteTask(taskName, 0);
        }
    }

    public void RefreshRegisteredPath(bool installedCopyOnly = false)
    {
        var existing = ReadLegacyCommand();
        if (installedCopyOnly && !string.Equals(existing, command, StringComparison.OrdinalIgnoreCase)) return;
        var (service, folder) = Connect();
        var task = GetTask(folder);
        if (task is not null)
        {
            if (installedCopyOnly) return;
            dynamic definition = task.Definition;
            dynamic action = definition.Actions[1];
            // Opening the normal app must not replace an explicit custom-profile launch.
            if (HasCustomProfile((string?)action.Arguments)) return;
            if (!Matches(action))
            {
                SetAction(action);
                Save(folder, definition, TaskUpdate);
            }
            return;
        }
        if (existing is null || HasCustomProfile(existing)) return;
        if (!LegacyApproved())
        {
            if (!string.Equals(existing, command, StringComparison.Ordinal))
            {
                using var run = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
                run?.SetValue(ValueName, command, RegistryValueKind.String);
            }
            return;
        }
        // Migrate only after the replacement exists; a scheduler error leaves the old startup intact.
        Register(service, folder);
        RemoveLegacy();
    }

    public void RemoveForCurrentExecutable()
    {
        if (string.Equals(ReadLegacyCommand(), command, StringComparison.OrdinalIgnoreCase)) RemoveLegacy();
        var folder = Connect().Folder;
        var task = GetTask(folder);
        if (task is not null && Matches(task.Definition.Actions[1])) folder.DeleteTask(taskName, 0);
    }

    private void Register(dynamic service, dynamic folder)
    {
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "Start Codex Quota in the notification area when you sign in.";
        definition.Principal.UserId = userId;
        definition.Principal.LogonType = InteractiveToken;
        definition.Principal.RunLevel = 0; // Use the signed-in user's normal privileges.
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        dynamic trigger = definition.Triggers.Create(LogonTrigger);
        trigger.UserId = userId; // No delay; not part of Explorer's Run queue.
        SetAction(definition.Actions.Create(ExecAction));
        Save(folder, definition, TaskCreateOrUpdate);
    }

    private void Save(dynamic folder, dynamic definition, int flags)
        => folder.RegisterTaskDefinition(taskName, definition, flags, userId, null, InteractiveToken, null);

    private void SetAction(dynamic action)
    {
        action.Path = executable;
        action.Arguments = arguments;
        action.WorkingDirectory = Path.GetDirectoryName(executable);
    }

    private bool Matches(dynamic action)
        => string.Equals((string)action.Path, executable, StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)action.Arguments, arguments, StringComparison.Ordinal);

    private static (dynamic Service, dynamic Folder) Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return (service, service.GetFolder(@"\"));
    }

    private dynamic? GetTask(dynamic folder)
    {
        try { return folder.GetTask(taskName); }
        catch (Exception error) when (error.HResult == FileNotFound) { return null; }
    }

    private string? ReadLegacyCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return run?.GetValue(ValueName) as string is { Length: > 0 } value ? value : null;
    }

    private bool LegacyApproved()
    {
        using var approval = Registry.CurrentUser.OpenSubKey(approvalKeyPath);
        return approval?.GetValue(ValueName) is not byte[] { Length: > 0 } state || state[0] is not (3 or 7);
    }

    private void RemoveLegacy()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
        run?.DeleteValue(ValueName, false);
        using var approval = Registry.CurrentUser.OpenSubKey(approvalKeyPath, true);
        approval?.DeleteValue(ValueName, false);
    }

    private static bool HasCustomProfile(string? args) => args?.Contains(" --data-dir ", StringComparison.Ordinal) == true;

    private static string QuotePath(string value)
    {
        var path = Path.GetFullPath(value);
        var trailingSlashes = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailingSlashes) + "\"";
    }
}
