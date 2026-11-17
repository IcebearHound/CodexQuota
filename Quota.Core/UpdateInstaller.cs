using System.Text.Json;

namespace Quota.Core;

public sealed record UpdatePlan(string InstallRoot, string JobRoot, string Version, int ParentId,
    long ParentStartedUtcTicks, string Nonce, string[] RestartArguments);
public sealed record UpdateBackup(string Path, bool Existed);
public sealed record UpdateJournal(UpdateBackup[] Files);
public sealed record UpdateResult(string Status, string Version, string? Error = null);

// All downloaded contents are staged before the UI exits. Only manifest-owned files
// are replaced; account data and unknown user files never enter this transaction.
public static class UpdateInstaller
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid update state.");
    public static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string FileAt(string root, string relative)
    {
        UpdatePackage.ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsurePhysical(path);
        return path;
    }
    public static void EnsurePhysical(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Updates cannot traverse a symbolic link or junction.");
    }
    public static UpdatePayload Validate(UpdatePlan plan)
    {
        ValidateLocation(plan);
        var version = System.Version.Parse(plan.Version);
        if (!File.Exists(FileAt(plan.InstallRoot, "CodexQuota.exe"))) throw new IOException("The installed executable is missing.");
        var stage = Path.Combine(plan.JobRoot, "stage"); EnsurePhysical(stage);
        var manifest = UpdatePackage.ReadManifest(File.ReadAllText(Path.Combine(stage, "install-files.json")), version);
        var installed = ReadInstalled(plan.InstallRoot);
        foreach (var file in manifest.Files)
        {
            if (!File.Exists(FileAt(stage, file))) throw new InvalidDataException("Incomplete staged update.");
            var target = FileAt(plan.InstallRoot, file);
            if (File.Exists(target) && !installed.Files.Contains(file, StringComparer.OrdinalIgnoreCase))
                throw new IOException("A new payload file conflicts with an unowned user file: " + file);
        }
        var probe = Path.Combine(plan.InstallRoot, ".codexquota-write-test-" + plan.Nonce);
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        File.Delete(probe);
        return manifest;
    }
    private static void ValidateLocation(UpdatePlan plan)
    {
        var root = Path.GetFullPath(plan.InstallRoot).TrimEnd(Path.DirectorySeparatorChar);
        var job = Path.GetFullPath(plan.JobRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(plan.InstallRoot) || !Path.IsPathFullyQualified(plan.JobRoot)
            || root.Length < 4 || root == Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar)
            || job.Equals(root, StringComparison.OrdinalIgnoreCase) || job.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(plan.Nonce, "N", out _) || !System.Version.TryParse(plan.Version, out _))
            throw new InvalidDataException("Invalid update location or identity.");
        EnsurePhysical(root); EnsurePhysical(job);
    }
    public static async Task<UpdateResult> ApplyAsync(UpdatePlan plan, Func<Task<bool>> acceptCandidate)
    {
        var manifest = Validate(plan);
        for (var retry = 0; ; retry++)
        {
            try { using (new FileStream(FileAt(plan.InstallRoot, "CodexQuota.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } break; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && retry < 49) { await Task.Delay(100); }
        }
        var old = ReadInstalled(plan.InstallRoot);
        var entries = old.Files.Union(manifest.Files, StringComparer.OrdinalIgnoreCase)
            .Select(file => new UpdateBackup(file, File.Exists(FileAt(plan.InstallRoot, file)))).ToArray();
        var backup = Path.Combine(plan.JobRoot, "backup");
        Directory.CreateDirectory(backup);
        foreach (var entry in entries.Where(entry => entry.Existed))
        {
            var destination = FileAt(backup, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(FileAt(plan.InstallRoot, entry.Path), destination, false);
        }
        // Publish the complete backup journal before touching any installed file.
        Write(Path.Combine(plan.JobRoot, "transaction.json"), new UpdateJournal(entries));
        UpdateResult result;
        try
        {
            foreach (var file in manifest.Files)
                Replace(FileAt(Path.Combine(plan.JobRoot, "stage"), file), FileAt(plan.InstallRoot, file));
            foreach (var file in old.Files.Except(manifest.Files, StringComparer.OrdinalIgnoreCase)) File.Delete(FileAt(plan.InstallRoot, file));
            if (!await acceptCandidate()) throw new IOException("The new version did not pass its startup health check.");
            result = new("success", plan.Version);
        }
        catch (Exception error)
        {
            try { Restore(plan); result = new("rolled-back", plan.Version, error.Message); }
            catch (Exception recovery) { result = new("recovery-needed", plan.Version, error.Message + " / Rollback: " + recovery.Message); }
        }
        Write(Path.Combine(plan.JobRoot, "result.json"), result);
        return result;
    }
    private static UpdatePayload ReadInstalled(string root)
    {
        var json = File.ReadAllText(FileAt(root, "install-files.json"));
        var old = JsonSerializer.Deserialize<UpdatePayload>(json) ?? throw new InvalidDataException("Missing installed manifest.");
        if (!System.Version.TryParse(old.Version, out var version)) throw new InvalidDataException("Invalid installed version.");
        return UpdatePackage.ReadManifest(json, version);
    }
    public static void Restore(UpdatePlan plan)
    {
        ValidateLocation(plan);
        var journal = Read<UpdateJournal>(Path.Combine(plan.JobRoot, "transaction.json"));
        if (journal.Files is not { Length: > 0 and <= 20 } || journal.Files.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length)
            throw new InvalidDataException("Invalid recovery journal.");
        foreach (var entry in journal.Files)
        {
            _ = FileAt(plan.InstallRoot, entry.Path);
            if (entry.Existed && !File.Exists(FileAt(Path.Combine(plan.JobRoot, "backup"), entry.Path))) throw new IOException("Incomplete recovery backup.");
        }
        foreach (var entry in journal.Files)
        {
            var target = FileAt(plan.InstallRoot, entry.Path);
            if (entry.Existed) Replace(FileAt(Path.Combine(plan.JobRoot, "backup"), entry.Path), target);
            else File.Delete(target);
        }
    }
    private static void Replace(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".update";
        try { File.Copy(source, temporary, false); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
