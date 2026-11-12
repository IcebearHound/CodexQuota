using System.Runtime.InteropServices;

internal sealed class SessionDatabaseFixture : IDisposable
{
    private nint database;
    public SessionDatabaseFixture(string path)
    {
        if (sqlite3_open_v2(path, out database, 6, 0) != 0) throw new InvalidOperationException("Cannot open fixture database");
        Execute("PRAGMA journal_mode=WAL; CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, archived INTEGER)");
    }
    public void Execute(string sql)
    {
        if (sqlite3_exec(database, sql, 0, 0, out var error) == 0) return;
        sqlite3_free(error); throw new InvalidOperationException("Fixture SQL failed");
    }
    public void Dispose() { sqlite3_close(database); }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint context, out nint error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(nint memory);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint database);
}
