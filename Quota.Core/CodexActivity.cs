using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quota.Core;

public enum CodexActivityKind { Unknown, Offline, Working, NeedsInput, Completed, Error, Paused, Idle }
public enum CodexActivityReadIssue { None, ProcessMetadataUnavailable, SessionsMissing, NoSessionEvents, SessionsInaccessible, ReadFailed }
public sealed record CodexInterruptedTask(string ThreadId, DateTimeOffset? InterruptedAt);
public sealed record CodexActivitySnapshot(CodexActivityKind Kind, int Running = 0, int Waiting = 0, int Tracked = 0, string AttentionToken = "", IReadOnlyList<CodexInterruptedTask>? InterruptedTasks = null, CodexActivityReadIssue ReadIssue = CodexActivityReadIssue.None, IReadOnlyList<CodexSessionIssue>? SessionIssues = null);

public sealed class CodexActivityCheckpoint
{
    public CodexActivityKind Kind { get; set; }
    public DateTimeOffset? InterruptedAt { get; set; }
    public string? ThreadId { get; set; }
    public string? IssueToken { get; set; }
    public DateTimeOffset? IssueAt { get; set; }
}
public sealed class CodexActivityJournal
{
    public string HomeKey { get; set; } = "";
    public DateTimeOffset? ProcessStarted { get; set; }
    public Dictionary<string, CodexActivityCheckpoint> Sessions { get; set; } = [];
}

public sealed class CodexSessionActivity(string? threadId = null)
{
    public string? ThreadId { get; private set; } = CodexThreadLink.NormalizeId(threadId);
    public DateTimeOffset? InterruptedAt { get; private set; }
    public DateTimeOffset? IssueAt { get; private set; }
    public string? IssueToken { get; private set; }
    private readonly Dictionary<string, bool> inputs = [];
    private CodexActivityKind activity;
    private bool terminal;
    private string? turn;
    public DateTimeOffset LastStartedAt { get; private set; }
    public CodexActivityKind Kind => activity == CodexActivityKind.Error ? activity : inputs.Count > 0 ? CodexActivityKind.NeedsInput : activity;

    public void Apply(JsonElement record, long? eventPosition = null)
    {
        if (record.ValueKind != JsonValueKind.Object) return;
        if (!record.TryGetProperty("payload", out var data) || data.ValueKind != JsonValueKind.Object) return;
        var type = data.Str("type");
        if (record.Str("type") == "session_meta") { ThreadId = CodexThreadLink.NormalizeId(data.Str("id")) ?? ThreadId; return; }
        var observedAt = DateTimeOffset.TryParse(record.Str("timestamp"), out var timestamp) ? timestamp : DateTimeOffset.MinValue;
        if (record.Str("type") == "event_msg")
        {
            switch (type)
            {
                case "task_started":
                    IssueToken = null; IssueAt = null;
                    turn = data.Str("turn_id"); terminal = false; InterruptedAt = null; activity = CodexActivityKind.Working; LastStartedAt = observedAt; return;
                case "task_complete":
                    if (turn is not null && data.Str("turn_id") is { } completed && turn != completed) return;
                    terminal = true;
                    activity = data.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.False
                        ? CodexActivityKind.Error : CodexActivityKind.Completed;
                    if (activity == CodexActivityKind.Error) SetIssue(record, observedAt, eventPosition);
                    return;
                case "turn_aborted":
                    if (turn is not null && data.Str("turn_id") is { } aborted && turn != aborted) return;
                    inputs.Clear(); terminal = true; InterruptedAt = observedAt == DateTimeOffset.MinValue ? null : observedAt; activity = CodexActivityKind.Paused; SetIssue(record, observedAt, eventPosition); return;
                case "error":
                case "system_error": activity = CodexActivityKind.Error; SetIssue(record, observedAt, eventPosition); return;
                case "exec_approval_request":
                case "apply_patch_approval_request":
                case "request_user_input":
                    inputs[data.Str("call_id") ?? data.Str("id") ?? type] = false; return;
                case "exec_approval_response":
                case "apply_patch_approval_response":
                    if ((data.Str("call_id") ?? data.Str("id")) is { } approved) inputs.Remove(approved);
                    else foreach (var synchronous in inputs.Where(input => !input.Value).Select(input => input.Key).ToArray()) inputs.Remove(synchronous);
                    if (!terminal) activity = CodexActivityKind.Working;
                    return;
                case "item_completed":
                    if (!terminal && data.TryGetProperty("item", out var item) && item.Str("type") == "UserMessage") activity = CodexActivityKind.Working;
                    return;
            }
        }
        if (record.Str("type") != "response_item") return;
        switch (type)
        {
            case "function_call":
            case "custom_tool_call":
                if (terminal) break;
                activity = CodexActivityKind.Working;
                var name = data.Str("name")?.Split('.').Last();
                if (name is "request_user_input" or "request_user_input_async" or "request_permissions")
                    inputs[data.Str("call_id") ?? name] = name == "request_user_input_async";
                break;
            case "function_call_output":
            case "custom_tool_call_output":
                if (data.Str("call_id") is { } call && inputs.TryGetValue(call, out var asynchronous))
                {
                    if (!asynchronous || Rejected(data)) inputs.Remove(call);
                }
                if (!terminal && activity != CodexActivityKind.Error) activity = CodexActivityKind.Working;
                break;
            case "message":
                if (data.Str("role") == "user") AcceptInput(data);
                else if (!terminal && data.Str("role") == "assistant") activity = data.Str("phase") is "final" or "final_answer" ? CodexActivityKind.Completed : CodexActivityKind.Working;
                break;
            case "reasoning": if (!terminal) activity = CodexActivityKind.Working; break;
        }
    }

    private void SetIssue(JsonElement record, DateTimeOffset observedAt, long? eventPosition)
    {
        IssueAt = observedAt == DateTimeOffset.MinValue ? null : observedAt;
        var identity = $"{activity}/{eventPosition}/{record.GetRawText()}";
        IssueToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    public void MarkInterrupted(DateTimeOffset? interruptedAt = null, string? occurrence = null)
    {
        activity = CodexActivityKind.Paused; InterruptedAt = interruptedAt; terminal = true; inputs.Clear();
        IssueAt = interruptedAt; IssueToken = occurrence ?? $"interrupted/{interruptedAt?.UtcTicks}";
    }

    private void AcceptInput(JsonElement message)
    {
        var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray().Select(item => item.Str("text"))) : "";
        if (text.Contains("<send_user_message_question_reply>", StringComparison.Ordinal))
        {
            foreach (var answered in inputs.Keys.Where(id => text.Contains(id, StringComparison.Ordinal)).ToArray()) inputs.Remove(answered);
        }
        else inputs.Clear();
        terminal = false; activity = CodexActivityKind.Working;
    }
    private static bool Rejected(JsonElement data)
    {
        if (data.Str("output") is not { } output) return false;
        try { using var result = JsonDocument.Parse(output); return result.RootElement.Bool("accepted") == false; }
        catch (JsonException) { return false; }
    }

    public static CodexActivitySnapshot Aggregate(IEnumerable<CodexSessionActivity> sessions)
    {
        var observations = sessions.ToArray();
        var states = observations.Select(session => session.Kind).Where(kind => kind != CodexActivityKind.Unknown).ToArray();
        var running = states.Count(kind => kind == CodexActivityKind.Working);
        var waiting = observations.Count(session => session.inputs.Count > 0);
        var kind = states.Contains(CodexActivityKind.Error) ? CodexActivityKind.Error
            : states.Contains(CodexActivityKind.Paused) ? CodexActivityKind.Paused
            : waiting > 0 ? CodexActivityKind.NeedsInput
            : running > 0 ? CodexActivityKind.Working
            : states.Length > 0 ? CodexActivityKind.Completed : CodexActivityKind.Unknown;
        var pending = string.Join("\n", observations.SelectMany(session => session.inputs.Keys).Order());
        var attention = waiting > 0 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pending))) : "";
        var interrupted = observations.Where(session => session.Kind == CodexActivityKind.Paused && session.ThreadId is not null)
            .Select(session => new CodexInterruptedTask(session.ThreadId!, session.InterruptedAt))
            .OrderByDescending(task => task.InterruptedAt).DistinctBy(task => task.ThreadId).ToArray();
        return new(kind, running, waiting, states.Length, attention, interrupted.Length > 0 ? interrupted : null);
    }
}

public sealed class CodexActivityMonitor
{
    private readonly string sessionsRoot;
    private readonly CodexSessionCatalog catalog;
    private readonly Dictionary<string, SessionTail> files = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? processStart;
    private long lastDiscovery;
    private readonly AccountStore? persistence;
    private readonly CodexActivityJournal journal;
    private string? savedJournal;
    public CodexActivityMonitor(string? codexHome = null, AccountStore? persistence = null)
    {
        sessionsRoot = Path.Combine(codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");
        catalog = new(Path.GetDirectoryName(sessionsRoot)!);
        this.persistence = persistence;
        var homeKey = SessionKey(sessionsRoot);
        journal = persistence?.LoadActivityJournal() is { } saved && saved.HomeKey == homeKey ? saved : new() { HomeKey = homeKey };
        journal.Sessions ??= [];
        journal.Sessions = journal.Sessions.Where(entry => entry.Value is not null).ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    private static string SessionKey(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
    public bool PersistenceHealthy { get; private set; } = true;
    private void InterruptUnfinished(DateTimeOffset? interruptedAt = null)
    {
        foreach (var checkpoint in journal.Sessions.Values.Where(value => value.Kind is CodexActivityKind.Working or CodexActivityKind.NeedsInput))
        {
            checkpoint.Kind = CodexActivityKind.Paused;
            checkpoint.InterruptedAt ??= interruptedAt ?? DateTimeOffset.UtcNow;
            checkpoint.IssueAt = checkpoint.InterruptedAt;
            checkpoint.IssueToken = $"interrupted/{checkpoint.InterruptedAt?.UtcTicks}";
        }
    }
    private void SaveJournal()
    {
        if (persistence is null) return;
        var serialized = JsonSerializer.Serialize(journal);
        if (serialized == savedJournal) return;
        try { persistence.SaveActivityJournal(journal); savedJournal = serialized; PersistenceHealthy = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { PersistenceHealthy = false; }
    }

    public CodexActivitySnapshot Read(CancellationToken cancellationToken = default)
    {
        var process = CodexProcessMonitor.Read();
        if (process.Found && process.Started is null)
            return new(CodexActivityKind.Unknown, ReadIssue: CodexActivityReadIssue.ProcessMetadataUnavailable);
        try { return ReadSince(process.Started, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(CodexActivityKind.Unknown, ReadIssue: CodexActivityReadIssue.SessionsInaccessible);
        }
    }

    public CodexActivitySnapshot ReadSince(DateTimeOffset? started, CancellationToken cancellationToken = default)
    {
        catalog.Refresh();
        if (started is null)
        {
            InterruptUnfinished(); SaveJournal(); files.Clear(); processStart = null; lastDiscovery = 0;
            var interrupted = journal.Sessions.Values.Where(checkpoint => checkpoint.Kind == CodexActivityKind.Paused && !catalog.IsArchived(checkpoint.ThreadId) && CodexThreadLink.NormalizeId(checkpoint.ThreadId) is not null)
                .Select(checkpoint => new CodexInterruptedTask(CodexThreadLink.NormalizeId(checkpoint.ThreadId)!, checkpoint.InterruptedAt))
                .OrderByDescending(task => task.InterruptedAt).DistinctBy(task => task.ThreadId).ToArray();
            return new(CodexActivityKind.Offline, InterruptedTasks: interrupted.Length > 0 ? interrupted : null, SessionIssues: JournalIssues());
        }
        if (journal.ProcessStarted is { } previousStart && previousStart != started) InterruptUnfinished(started);
        journal.ProcessStarted = started;
        if (processStart != started) { files.Clear(); processStart = started; lastDiscovery = 0; }
        if (!Directory.Exists(sessionsRoot))
        {
            InterruptUnfinished(); SaveJournal();
            return new(journal.Sessions.Values.Any(value => value.Kind == CodexActivityKind.Error) ? CodexActivityKind.Error : journal.Sessions.Values.Any(value => value.Kind == CodexActivityKind.Paused) ? CodexActivityKind.Paused : CodexActivityKind.Unknown, ReadIssue: CodexActivityReadIssue.SessionsMissing, SessionIssues: JournalIssues());
        }
        if (lastDiscovery == 0 || Stopwatch.GetElapsedTime(lastDiscovery) >= TimeSpan.FromSeconds(2))
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.TryGetValue(path, out var tail);
                var key = tail?.Key ?? SessionKey(path);
                if (File.GetLastWriteTimeUtc(path) < started.Value.UtcDateTime && !journal.Sessions.ContainsKey(key)) continue;
                found.Add(path);
                if (tail is null)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    files[path] = new(key, name.Length >= 36 ? CodexThreadLink.NormalizeId(name[^36..]) : null);
                }
            }
            foreach (var old in files.Keys.Where(path => !found.Contains(path)).ToArray())
            {
                if (journal.Sessions.TryGetValue(files[old].Key, out var missing) && !catalog.IsArchived(missing.ThreadId) && missing.Kind is CodexActivityKind.Working or CodexActivityKind.NeedsInput)
                {
                    missing.Kind = CodexActivityKind.Paused; missing.InterruptedAt ??= DateTimeOffset.UtcNow;
                    missing.IssueAt = missing.InterruptedAt; missing.IssueToken = $"interrupted/{missing.InterruptedAt?.UtcTicks}";
                }
                files.Remove(old);
            }
            lastDiscovery = Stopwatch.GetTimestamp();
        }
        var readable = new List<CodexSessionActivity>();
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var unavailable = false;
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = entry.Value.Key;
                journal.Sessions.TryGetValue(key, out var checkpoint);
                entry.Value.Read(entry.Key, checkpoint is null ? started.Value : DateTimeOffset.MinValue, cancellationToken);
                var observed = entry.Value.Activity;
                if (catalog.IsArchived(observed.ThreadId ?? checkpoint?.ThreadId)) continue;
                if (checkpoint?.InterruptedAt is { } interruption && observed.Kind is not CodexActivityKind.Completed and not CodexActivityKind.Error)
                {
                    if (observed.LastStartedAt > interruption || observed.InterruptedAt > interruption
                        || observed.Kind == CodexActivityKind.Paused && observed.InterruptedAt is null && checkpoint.IssueToken is not null && observed.IssueToken is not null && observed.IssueToken != checkpoint.IssueToken) checkpoint.InterruptedAt = null;
                    else observed.MarkInterrupted(interruption, checkpoint.IssueToken);
                }
                readable.Add(observed);
                observedKeys.Add(key);
                if (observed.Kind is CodexActivityKind.Completed or CodexActivityKind.Unknown) journal.Sessions.Remove(key);
                else
                {
                    checkpoint ??= new();
                    checkpoint.Kind = observed.Kind;
                    checkpoint.ThreadId = observed.ThreadId;
                    checkpoint.IssueToken = observed.IssueToken; checkpoint.IssueAt = observed.IssueAt;
                    checkpoint.InterruptedAt = observed.Kind == CodexActivityKind.Paused ? observed.InterruptedAt ?? checkpoint.InterruptedAt ?? DateTimeOffset.UtcNow : null;
                    journal.Sessions[key] = checkpoint;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unavailable = true; }
        }
        foreach (var entry in journal.Sessions.Where(entry => entry.Value.Kind == CodexActivityKind.Paused && !catalog.IsArchived(entry.Value.ThreadId) && !observedKeys.Contains(entry.Key)))
        {
            var interrupted = new CodexSessionActivity(entry.Value.ThreadId); interrupted.MarkInterrupted(entry.Value.InterruptedAt, entry.Value.IssueToken); readable.Add(interrupted);
        }
        SaveJournal();
        var result = CodexSessionActivity.Aggregate(readable) with { SessionIssues = JournalIssues() };
        if (unavailable && result.Kind is CodexActivityKind.Completed or CodexActivityKind.Unknown)
            return result with { Kind = CodexActivityKind.Unknown, ReadIssue = CodexActivityReadIssue.SessionsInaccessible };
        return result.Kind == CodexActivityKind.Unknown ? result with { ReadIssue = CodexActivityReadIssue.NoSessionEvents } : result;
    }

    private CodexSessionIssue[] JournalIssues() => journal.Sessions
        .Where(entry => !catalog.IsArchived(entry.Value.ThreadId) && entry.Value.Kind is CodexActivityKind.Paused or CodexActivityKind.Error)
        .Select(entry => new CodexSessionIssue(entry.Key, entry.Value.ThreadId, entry.Value.Kind,
            entry.Value.IssueAt ?? entry.Value.InterruptedAt,
            entry.Value.IssueToken ?? $"interrupted/{entry.Value.InterruptedAt?.UtcTicks}", Title: catalog.Title(entry.Value.ThreadId)))
        .OrderByDescending(issue => issue.OccurredAt).ToArray();

    private sealed class SessionTail(string key, string? threadId)
    {
        private const int MaximumRecord = 4 * 1024 * 1024;
        private const int MaximumRetainedBuffer = 64 * 1024;
        public string Key { get; } = key;
        private long offset;
        private bool initialized;
        private bool skipLine;
        private DateTime lastWrite;
        private ArrayBufferWriter<byte> pending = new(8192);
        public CodexSessionActivity Activity { get; private set; } = new(threadId);
        public void Read(string path, DateTimeOffset started, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var modified = File.GetLastWriteTimeUtc(path);
            if (initialized && length == offset && modified == lastWrite) return;
            if (!initialized || length < offset || length == offset && modified != lastWrite)
            {
                Activity = new(ReadThreadId(stream) ?? threadId); pending = new(8192);
                offset = Math.Max(0, length - MaximumRecord); skipLine = offset > 0; initialized = true;
                if (skipLine) { stream.Position = offset - 1; skipLine = stream.ReadByte() != '\n'; }
            }
            stream.Position = offset;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var end = Math.Min(length, offset + MaximumRecord * 2L);
            try
            {
                while (stream.Position < end)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - stream.Position));
                    if (read == 0) break;
                    var remaining = buffer.AsSpan(0, read);
                    while (!remaining.IsEmpty)
                    {
                        var newline = remaining.IndexOf((byte)'\n');
                        var chunk = newline >= 0 ? remaining[..newline] : remaining;
                        if (!skipLine && pending.WrittenCount + chunk.Length <= MaximumRecord) pending.Write(chunk);
                        else { skipLine = true; ClearPending(); }
                        if (newline < 0) break;
                        if (!skipLine && pending.WrittenCount > 0)
                        {
                            try
                            {
                                using var json = JsonDocument.Parse(pending.WrittenMemory);
                                if (DateTimeOffset.TryParse(json.RootElement.Str("timestamp"), out var time) && time >= started) Activity.Apply(json.RootElement, stream.Position - remaining.Length + newline);
                            }
                            catch (JsonException) { }
                        }
                        skipLine = false; ClearPending();
                        remaining = remaining[(newline + 1)..];
                    }
                    offset = stream.Position;
                }
                lastWrite = modified;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        private void ClearPending()
        {
            if (pending.Capacity > MaximumRetainedBuffer) pending = new(8192);
            else pending.Clear();
        }
        private static string? ReadThreadId(Stream stream)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                stream.Position = 0;
                var length = (int)Math.Min(stream.Length, 16 * 1024);
                var read = stream.ReadAtLeast(buffer.AsSpan(0, length), length, throwOnEndOfStream: false);
                var newline = buffer.AsSpan(0, read).IndexOf((byte)'\n');
                if (newline < 0 && stream.Length > read) return null;
                using var document = JsonDocument.Parse(buffer.AsMemory(0, newline >= 0 ? newline : read));
                var record = document.RootElement;
                return record.Str("type") == "session_meta" && record.TryGetProperty("payload", out var payload)
                    ? CodexThreadLink.NormalizeId(payload.Str("id")) : null;
            }
            catch (JsonException) { return null; }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
    }
}
