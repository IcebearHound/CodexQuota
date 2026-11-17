using System.IO.Compression;
using System.Text.Json;

namespace Quota.Core;

public sealed record UpdatePayload(int Format, string Version, string[] Files);
public static class UpdatePackage
{
    // Only single-file application output and notices are distributable. Data files,
    // helpers, DLLs and scripts in downloaded archives are never accepted as payload.
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "CodexQuota.exe", "install-files.json", "Assets/lucide/LICENSE", "LICENSE.txt", "DOTNET-LICENSE.txt",
        "DOTNET-THIRD-PARTY-NOTICES.txt", "WINDOWSDESKTOP-LICENSE.txt", "README.md", "README.en.md"
    };
    public static string ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/')
            || relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.'))
            || !Allowed.Contains(relative)) throw new InvalidDataException("Unexpected update payload path: " + relative);
        return relative;
    }
    public static UpdatePayload ReadManifest(string json, Version expected)
    {
        var manifest = JsonSerializer.Deserialize<UpdatePayload>(json) ?? throw new InvalidDataException("Missing installation manifest.");
        if (manifest.Format != 1 || manifest.Version != expected.ToString() || manifest.Files is not { Length: > 0 and <= 20 }
            || !manifest.Files.Contains("CodexQuota.exe", StringComparer.OrdinalIgnoreCase)
            || !manifest.Files.Contains("install-files.json", StringComparer.OrdinalIgnoreCase)
            || manifest.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
            throw new InvalidDataException("Invalid installation manifest.");
        foreach (var file in manifest.Files) ValidateRelative(file);
        return manifest;
    }
    public static async Task<UpdatePayload> ExtractAsync(string archive, string stage, Version expected, CancellationToken cancellation = default)
    {
        if (Directory.Exists(stage)) throw new IOException("Choose a fresh staging directory.");
        using var zip = ZipFile.OpenRead(archive);
        var prefix = $"CodexQuota-{expected}-win-x64/";
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long size = 0;
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected package root.");
            var relative = entry.FullName[prefix.Length..];
            if (entry.FullName.EndsWith('/'))
            {
                if (relative is not ("" or "Assets/" or "Assets/lucide/")) throw new InvalidDataException("Unexpected package directory.");
                continue;
            }
            ValidateRelative(relative);
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || !files.TryAdd(relative, entry))
                throw new InvalidDataException("Links and duplicate paths are not allowed.");
            size += entry.Length;
            if (size > 512L * 1024 * 1024 || files.Count > 20) throw new InvalidDataException("Oversized update payload.");
        }
        if (!files.TryGetValue("install-files.json", out var manifestEntry) || manifestEntry.Length > 16384)
            throw new InvalidDataException("Missing installation manifest.");
        var buffer = new byte[65536];
        using var manifestBytes = new MemoryStream();
        await CopyEntry(manifestEntry, manifestBytes, buffer, cancellation);
        var manifest = ReadManifest(System.Text.Encoding.UTF8.GetString(manifestBytes.ToArray()), expected);
        if (!files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(manifest.Files))
            throw new InvalidDataException("Package contents differ from the installation manifest.");
        Directory.CreateDirectory(stage);
        foreach (var (relative, entry) in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var destination = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await CopyEntry(entry, target, buffer, cancellation);
        }
        return manifest;
    }
    private static async Task CopyEntry(ZipArchiveEntry entry, Stream destination, byte[] buffer, CancellationToken cancellation)
    {
        await using var source = entry.Open(); long written = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellation); if (count == 0) break;
            written += count;
            if (written > entry.Length) throw new InvalidDataException("The extracted file exceeds its declared size.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
        if (written != entry.Length) throw new InvalidDataException("The extracted file length is incorrect.");
    }
}
