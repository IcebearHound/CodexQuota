namespace Quota.Core;

public static class InstallLocation
{
    public const string DefaultParent = @"D:\Program Files";
    public const string FolderName = "CodexQuota";

    public static string? Resolve(string input)
    {
        var path = input.Trim();
        if (!Path.IsPathFullyQualified(path)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(full);
            if (root is null || full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(component => component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || component.EndsWith(' ') || component.EndsWith('.')))
                return null;
            return Path.GetFileName(full).Equals(FolderName, StringComparison.OrdinalIgnoreCase)
                ? full : Path.Combine(full, FolderName);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
