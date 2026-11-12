using System.Runtime.InteropServices;

namespace Quota.Core;

internal sealed class CodexSessionCatalog(string home)
{
    private Dictionary<string, (bool Archived, string Title)> threads = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> archivedFiles = new(StringComparer.OrdinalIgnoreCase);
    public bool IsArchived(string? id) => id is not null && (threads.TryGetValue(id, out var thread) ? thread.Archived : archivedFiles.Contains(id));
    public string? Title(string? id) => id is not null && threads.TryGetValue(id, out var thread) ? thread.Title : null;

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
            if (sqlite3_prepare_v2(database, "SELECT id, title, archived FROM threads", -1, out statement, 0) != 0) return;
            var updated = new Dictionary<string, (bool, string)>(StringComparer.OrdinalIgnoreCase);
            int result;
            while ((result = sqlite3_step(statement)) == 100)
            {
                var id = CodexThreadLink.NormalizeId(Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)));
                if (id is null) continue;
                var title = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 1)) ?? "";
                updated[id] = (sqlite3_column_int(statement, 2) != 0, title.Replace('\r', ' ').Replace('\n', ' '));
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
