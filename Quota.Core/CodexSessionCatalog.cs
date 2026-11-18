using System.Runtime.InteropServices;
using System.Text.Json;

namespace Quota.Core;

internal sealed class CodexSessionCatalog(string home)
{
    private sealed record ThreadEntry(bool Archived, string Title, string? Parent, string? Rollout, bool Internal);
    private Dictionary<string, ThreadEntry> threads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> attachedParents = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> archivedFiles = new(StringComparer.OrdinalIgnoreCase);
    public bool IsArchived(string? id) => id is not null && (threads.TryGetValue(id, out var thread) ? thread.Archived : archivedFiles.Contains(id));
    public string? Title(string? id) => id is not null && threads.TryGetValue(id, out var thread) ? thread.Title : null;
    public bool IsAttached(string? id) => id is not null && (attachedParents.ContainsKey(id) || threads.TryGetValue(id, out var thread) && thread.Parent is not null);
    public bool IsInternal(string? id) => id is not null && threads.TryGetValue(id, out var thread) && thread.Internal;

    internal static bool IsInternalSource(string? threadSource, JsonElement source) => IsInternalThreadSource(threadSource) || source.ValueKind switch
    {
        JsonValueKind.Object => source.TryGetProperty("subagent", out _),
        JsonValueKind.String => source.GetString() == "subagent",
        _ => false
    };

    private static bool IsInternalThreadSource(string? source) => source is "subagent" or "guardian_review";

    private static bool IsInternalSource(string? threadSource, string? source)
    {
        if (IsInternalThreadSource(threadSource) || source is "subagent" or "\"subagent\"") return true;
        if (source is null || !source.AsSpan().TrimStart().StartsWith("{")) return false;
        try
        {
            using var parsed = JsonDocument.Parse(source);
            return IsInternalSource(null, parsed.RootElement);
        }
        catch (JsonException) { return false; }
    }
    public void RegisterAttachment(string? id, string? parent)
    {
        if (id is not null && parent is not null && id != parent) attachedParents[id] = parent;
    }
    public string? ResolveThreadId(string? id)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (id is not null && visited.Add(id))
        {
            if (IsInternal(id)) return null;
            var parent = threads.TryGetValue(id, out var thread) ? thread.Parent : null;
            parent ??= attachedParents.GetValueOrDefault(id);
            if (parent is null) return IsArchived(id) ? null : id;
            id = parent;
        }
        return null;
    }
    public bool IsCurrentRollout(string? id, string path)
    {
        if (IsAttached(id)) return false;
        return CurrentRollout(id) is not { } current || string.Equals(Path.GetFullPath(path), current, StringComparison.OrdinalIgnoreCase);
    }
    public string? CurrentRollout(string? id)
        => id is not null && threads.TryGetValue(id, out var thread) && !string.IsNullOrWhiteSpace(thread.Rollout)
            ? Path.GetFullPath(Path.IsPathRooted(thread.Rollout) ? thread.Rollout : Path.Combine(home, thread.Rollout)) : null;

    public void Refresh()
    {
        if (OperatingSystem.IsWindows()) ReadDatabase();
        var directory = Path.Combine(home, "archived_sessions");
        if (!Directory.Exists(directory)) { archivedFiles.Clear(); return; }
        try
        {
            archivedFiles = Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(path => Path.GetFileNameWithoutExtension(path)).Where(name => name.Length >= 36)
                .Select(name => CodexThreadLink.NormalizeId(name[^36..])).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void ReadDatabase()
    {
        var path = Directory.Exists(home) ? Directory.EnumerateFiles(home, "state_*.sqlite")
            .OrderByDescending(path => int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(6), out var version) ? version : 0).FirstOrDefault() : null;
        if (path is null) { threads.Clear(); return; }
        nint database = 0, statement = 0;
        try
        {
            if (sqlite3_open_v2(path, out database, 1, 0) != 0) return;
            sqlite3_busy_timeout(database, 100);
            if (sqlite3_prepare_v2(database, "PRAGMA table_info(threads)", -1, out statement, 0) != 0) return;
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int result;
            while ((result = sqlite3_step(statement)) == 100)
                if (Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 1)) is { } name) columns.Add(name);
            if (result != 101) return;
            sqlite3_finalize(statement); statement = 0;
            // Codex versions add these columns independently. Keep the same projection for older schemas.
            var optional = new[] { "attached_thread_parent_id", "rollout_path", "thread_source", "source" };
            var query = "SELECT id, title, archived, " + string.Join(", ", optional.Select(column => columns.Contains(column) ? column : "NULL")) + " FROM threads";
            if (sqlite3_prepare_v2(database, query, -1, out statement, 0) != 0) return;
            var updated = new Dictionary<string, ThreadEntry>(StringComparer.OrdinalIgnoreCase);
            while ((result = sqlite3_step(statement)) == 100)
            {
                var id = CodexThreadLink.NormalizeId(Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)));
                if (id is null) continue;
                var title = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 1)) ?? "";
                var parent = CodexThreadLink.NormalizeId(Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 3)));
                var rollout = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 4));
                var internalThread = IsInternalSource(Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 5)), Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 6)));
                updated[id] = new(sqlite3_column_int(statement, 2) != 0, title.Replace('\r', ' ').Replace('\n', ' '), parent, rollout, internalThread);
            }
            if (result == 101) threads = updated;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        finally
        {
            if (statement != 0) sqlite3_finalize(statement);
            if (database != 0) sqlite3_close(database);
        }
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(nint database, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_column_text(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_int(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint database);
}
