using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quota.Core;

internal static class AppUpdateSmoke
{
    public static async Task Run(Func<string, Func<Task>, Task> test, string root)
    {
        await test("update preferences persist while retired transparent mode is removed", () =>
        {
            var store = new AccountStore(Path.Combine(root, "update-preferences"));
            File.WriteAllText(Path.Combine(store.Root, "settings.json"), """{"Material":"glass","TransparentGlass":true,"AutoCheckUpdates":false}""");
            var settings = store.LoadSettings(); Check(!settings.AutoCheckUpdates && settings.Material == "glass");
            settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow; settings.LastUpdateJob = "fixture"; store.SaveSettings(settings);
            var saved = store.LoadSettings(); Check(saved.LastUpdateCheckUtc == settings.LastUpdateCheckUtc && saved.LastUpdateJob == "fixture");
            Check(!File.ReadAllText(Path.Combine(store.Root, "settings.json")).Contains("TransparentGlass")); return Task.CompletedTask;
        });
        await test("updates choose newer stable x64 releases and ignore old or prerelease tags", () =>
        {
            Check(AppUpdateClient.Parse(Release(), new(2, 3, 0))?.Version == new Version(3, 0, 0));
            Check(AppUpdateClient.Parse(Release(), new(3, 0, 0, 0)) is null);
            Check(AppUpdateClient.Parse(Release(pre: true), new(2, 3, 0)) is null);
            Check(AppUpdateClient.Parse(Release(tag: "v3.0.0-rc.1"), new(2, 3, 0)) is null);
            return Task.CompletedTask;
        });
        await test("update metadata requires a repository-bound URL and GitHub SHA256 digest", async () =>
        {
            foreach (var release in new[] { Release(digest: ""), Release(url: "https://evil.example/app.zip"), Release(size: AppUpdateClient.MaximumDownload + 1), JsonSerializer.SerializeToElement(new { draft = false }) })
                await Throws<InvalidDataException>(() => Task.FromResult(AppUpdateClient.Parse(release, new(2, 3, 0))));
        });
        await test("verified downloads report progress without buffering the whole package", async () =>
        {
            var bytes = Encoding.UTF8.GetBytes(new string('q', 150000)); var output = Path.Combine(root, "download-good.zip");
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            var progress = new InlineProgress();
            await new AppUpdateClient(http).DownloadAsync(Update(bytes), output, progress);
            Check(File.ReadAllBytes(output).SequenceEqual(bytes) && progress.Value == 100);
        });
        await test("tampered truncated and oversized downloads are removed before installation", async () =>
        {
            var expected = Encoding.UTF8.GetBytes("valid package");
            foreach (var actual in new[] { Encoding.UTF8.GetBytes("bad package!!"), expected[..5], expected.Concat(new byte[10]).ToArray() })
            {
                var output = Path.Combine(root, Guid.NewGuid() + ".zip");
                using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(actual) }));
                await Throws<InvalidDataException>(() => new AppUpdateClient(http).DownloadAsync(Update(expected), output));
                Check(!File.Exists(output));
            }
        });
        await test("download cancellation and existing destination files remain safe", async () =>
        {
            var bytes = Encoding.UTF8.GetBytes("download"); var output = Path.Combine(root, "existing.zip"); File.WriteAllText(output, "keep me");
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            await Throws<IOException>(() => new AppUpdateClient(http).DownloadAsync(Update(bytes), output));
            Check(File.ReadAllText(output) == "keep me");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var canceled = Path.Combine(root, "canceled.zip");
            await Throws<OperationCanceledException>(() => new AppUpdateClient(http).DownloadAsync(Update(bytes), canceled, cancellation: cancellation.Token));
            Check(!File.Exists(canceled));
        });
        await test("update redirects cannot leave the HTTPS GitHub asset host allowlist", async () =>
        {
            var calls = 0; using var http = new HttpClient(new Handler(_ => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("http://evil.example/payload"); return response; }));
            await Throws<InvalidDataException>(() => new AppUpdateClient(http).DownloadAsync(Update([1, 2, 3]), Path.Combine(root, "redirect.zip")));
            Check(calls == 1);
        });
        await test("update archives require a complete manifest and a single trusted root", async () =>
        {
            var version = new Version(3, 0, 0); var archive = Path.Combine(root, "valid-update.zip");
            Zip(archive, Payload("3.0.0", "new"));
            var stage = Path.Combine(root, "good-stage");
            var manifest = await UpdatePackage.ExtractAsync(archive, stage, version);
            Check(manifest.Version == "3.0.0" && File.ReadAllText(Path.Combine(stage, "CodexQuota.exe")) == "new");
        });
        await test("update extraction rejects traversal duplicate links and unowned data files", async () =>
        {
            foreach (var dangerous in new[] { "../outside.exe", "settings.json", "profiles/auth.json", "C:/outside.exe", "CodexQuota.exe:stream", "folder\\file.exe" })
            {
                var archive = Path.Combine(root, Guid.NewGuid() + ".zip"); var payload = Payload("3.0.0", "new"); payload[dangerous] = "bad"; Zip(archive, payload);
                await Throws<InvalidDataException>(() => UpdatePackage.ExtractAsync(archive, Path.Combine(root, Guid.NewGuid().ToString("N")), new(3, 0, 0)));
            }
            foreach (var kind in new[] { "duplicate", "symlink", "missing", "version" })
            {
                var archive = Path.Combine(root, kind + ".zip"); var payload = Payload("3.0.0", "new");
                if (kind == "missing") payload.Remove("CodexQuota.exe");
                if (kind == "version") payload["install-files.json"] = JsonSerializer.Serialize(new UpdatePayload(1, "9.0.0", ["CodexQuota.exe", "install-files.json"]));
                Zip(archive, payload, kind);
                await Throws<InvalidDataException>(() => UpdatePackage.ExtractAsync(archive, Path.Combine(root, kind + "-stage"), new(3, 0, 0)));
            }
        });
        await test("in-place updates replace only owned files and retain accounts and user files", async () =>
        {
            var plan = Plan(root, "success");
            var result = await UpdateInstaller.ApplyAsync(plan, () => Task.FromResult(File.ReadAllText(Path.Combine(plan.InstallRoot, "CodexQuota.exe")) == "new"));
            Check(result.Status == "success" && File.ReadAllText(Path.Combine(plan.InstallRoot, "settings.json")) == "private data");
            Check(!File.Exists(Path.Combine(plan.InstallRoot, "README.en.md")) && File.Exists(Path.Combine(plan.InstallRoot, "LICENSE.txt")));
            Check(File.ReadAllText(Path.Combine(plan.InstallRoot, "user-file.txt")) == "user file");
        });
        await test("failed candidate startup rolls back replaced added and removed payload files", async () =>
        {
            var plan = Plan(root, "rollback"); var result = await UpdateInstaller.ApplyAsync(plan, () => Task.FromResult(false));
            Check(result.Status == "rolled-back"); AssertRestored(plan);
        });
        await test("new payload names cannot overwrite pre-existing unowned user files", async () =>
        {
            var plan = Plan(root, "unowned-collision");
            File.WriteAllText(Path.Combine(plan.InstallRoot, "LICENSE.txt"), "user-owned document");
            await Throws<IOException>(() => UpdateInstaller.ApplyAsync(plan, () => Task.FromResult(true)));
            Check(File.ReadAllText(Path.Combine(plan.InstallRoot, "LICENSE.txt")) == "user-owned document"
                && File.ReadAllText(Path.Combine(plan.InstallRoot, "CodexQuota.exe")) == "old");
        });
        await test("candidate launch exceptions use the same rollback path", async () =>
        {
            var plan = Plan(root, "launch-failed"); var result = await UpdateInstaller.ApplyAsync(plan, () => throw new IOException("launch failed"));
            Check(result.Status == "rolled-back"); AssertRestored(plan);
        });
        await test("an independent backup journal recovers an interrupted update", async () =>
        {
            var plan = Plan(root, "recovery"); Check((await UpdateInstaller.ApplyAsync(plan, () => Task.FromResult(true))).Status == "success");
            File.Delete(Path.Combine(plan.InstallRoot, "CodexQuota.exe"));
            UpdateInstaller.Restore(plan); AssertRestored(plan);
            UpdateInstaller.Write(Path.Combine(plan.JobRoot, "transaction.json"), new UpdateJournal([new("../outside.exe", true)]));
            await Throws<InvalidDataException>(() => { UpdateInstaller.Restore(plan); return Task.CompletedTask; });
        });
        await test("missing stages invalid old manifests and locked targets never partially install", async () =>
        {
            var missing = Plan(root, "missing-stage"); File.Delete(Path.Combine(missing.JobRoot, "stage", "CodexQuota.exe"));
            await Throws<InvalidDataException>(() => UpdateInstaller.ApplyAsync(missing, () => Task.FromResult(true)));
            AssertRestored(missing);
            var bad = Plan(root, "invalid-old"); File.WriteAllText(Path.Combine(bad.InstallRoot, "install-files.json"), JsonSerializer.Serialize(new UpdatePayload(1, "2.3.0", ["settings.json"])));
            await Throws<InvalidDataException>(() => UpdateInstaller.ApplyAsync(bad, () => Task.FromResult(true)));
            Check(File.ReadAllText(Path.Combine(bad.InstallRoot, "settings.json")) == "private data");
            var locked = Plan(root, "locked");
            using (File.Open(Path.Combine(locked.InstallRoot, "CodexQuota.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await Throws<IOException>(() => UpdateInstaller.ApplyAsync(locked, () => Task.FromResult(true)));
            AssertRestored(locked);
        });
        await test("stale update leases never prevent ordinary application startup", () =>
        {
            var plan = Plan(root, "lease"); UpdateInstaller.Write(Path.Combine(plan.InstallRoot, UpdateMaintenance.Marker), new UpdateLease(int.MaxValue, 0, plan.Nonce, plan.JobRoot));
            Check(!UpdateMaintenance.BlocksStartup(plan.InstallRoot)); return Task.CompletedTask;
        });
    }
    private static void AssertRestored(UpdatePlan plan)
    {
        Check(File.ReadAllText(Path.Combine(plan.InstallRoot, "CodexQuota.exe")) == "old");
        Check(File.ReadAllText(Path.Combine(plan.InstallRoot, "settings.json")) == "private data");
        Check(File.Exists(Path.Combine(plan.InstallRoot, "README.en.md")) && !File.Exists(Path.Combine(plan.InstallRoot, "LICENSE.txt")));
    }
    private static UpdatePlan Plan(string root, string name)
    {
        var directory = Path.Combine(root, name); var install = Path.Combine(directory, "install"); var job = Path.Combine(directory, "job");
        WritePayload(install, Payload("2.3.0", "old", "README.en.md")); WritePayload(Path.Combine(job, "stage"), Payload("3.0.0", "new", "LICENSE.txt"));
        File.WriteAllText(Path.Combine(install, "settings.json"), "private data"); File.WriteAllText(Path.Combine(install, "user-file.txt"), "user file");
        return new(install, job, "3.0.0", Environment.ProcessId, 0, Guid.NewGuid().ToString("N"), []);
    }
    private static Dictionary<string, string> Payload(string version, string content, string? extra = null)
    {
        var files = new[] { "CodexQuota.exe", "install-files.json" }.Concat(extra is null ? [] : new[] { extra }).ToArray();
        var payload = new Dictionary<string, string> { ["CodexQuota.exe"] = content, ["install-files.json"] = JsonSerializer.Serialize(new UpdatePayload(1, version, files)) };
        if (extra is not null) payload[extra] = content; return payload;
    }
    private static void WritePayload(string root, Dictionary<string, string> files)
    { Directory.CreateDirectory(root); foreach (var (name, content) in files) File.WriteAllText(Path.Combine(root, name), content); }
    private static void Zip(string path, Dictionary<string, string> files, string? kind = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            var entry = archive.CreateEntry("CodexQuota-3.0.0-win-x64/" + name);
            if (kind == "symlink" && name == "CodexQuota.exe") entry.ExternalAttributes = 0xa000 << 16;
            using var writer = new StreamWriter(entry.Open()); writer.Write(content);
        }
        if (kind == "duplicate") { using var writer = new StreamWriter(archive.CreateEntry("CodexQuota-3.0.0-win-x64/codexquota.exe").Open()); writer.Write("duplicate"); }
    }
    private static AppUpdate Update(byte[] bytes) => new(new(3, 0, 0), "CodexQuota-3.0.0-win-x64.zip", new("https://github.com/IcebearHound/CodexQuota/releases/download/v3.0.0/CodexQuota-3.0.0-win-x64.zip"), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new("https://github.com/IcebearHound/CodexQuota/releases/tag/v3.0.0"));
    private static JsonElement Release(string tag = "v3.0.0", bool pre = false, string? url = null, string? digest = null, long size = 10) => JsonSerializer.SerializeToElement(new
    {
        draft = false, prerelease = pre, tag_name = tag,
        assets = new[] { new { name = "CodexQuota-3.0.0-win-x64.zip", size, digest = digest ?? "sha256:" + new string('a', 64), browser_download_url = url ?? "https://github.com/IcebearHound/CodexQuota/releases/download/v3.0.0/CodexQuota-3.0.0-win-x64.zip" } }
    });
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); } }
    private sealed class InlineProgress : IProgress<double> { public double Value; public void Report(double value) => Value = value; }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Update check failed"); }
}
