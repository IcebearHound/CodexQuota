using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quota.Core;

public sealed record AppUpdate(Version Version, string AssetName, Uri DownloadUri, long Size, string Sha256, Uri ReleaseUri);

// The fixed repository is the trust source. The GitHub-provided digest is required,
// not an optional sidecar downloaded from an arbitrary URL in release notes.
public sealed class AppUpdateClient(HttpClient http)
{
    public const string Repository = "IcebearHound/CodexQuota";
    public const long MaximumDownload = 256L * 1024 * 1024;
    public async Task<AppUpdate?> CheckAsync(Version current, CancellationToken cancellation = default)
    {
        using var response = await GetAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var memory = new MemoryStream();
        await CopyBounded(stream, memory, 2 * 1024 * 1024, cancellation);
        using var document = JsonDocument.Parse(memory.ToArray());
        return Parse(document.RootElement, current);
    }
    public static AppUpdate? Parse(JsonElement release, Version current)
    {
        try { return ParseRelease(release, current); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or OverflowException or InvalidOperationException)
        { throw new InvalidDataException("Malformed release metadata.", error); }
    }
    private static AppUpdate? ParseRelease(JsonElement release, Version current)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version)) return null;
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        var name = $"CodexQuota-{version}-win-x64.zip";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (size <= 0 || size > MaximumDownload || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")
                || asset.GetProperty("browser_download_url").GetString() != expected)
                throw new InvalidDataException("The release asset has no valid size, integrity digest or trusted download URL.");
            return new(version, name, new Uri(expected), size, digest[7..], new Uri($"https://github.com/{Repository}/releases/tag/{tag}"));
        }
        throw new InvalidDataException("The stable release has no Windows x64 update package.");
    }
    public async Task DownloadAsync(AppUpdate update, string destination, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        if (update.Size is <= 0 or > MaximumDownload || !Regex.IsMatch(update.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Invalid update metadata.");
        var complete = false; var created = false;
        try
        {
            using var response = await GetAsync(update.DownloadUri, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw new InvalidDataException("The download length differs from GitHub release metadata.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            created = true;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long total = 0;
            var nextReport = Environment.TickCount64;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellation);
                if (count == 0) break;
                total += count;
                if (total > update.Size) throw new InvalidDataException("The download exceeds its declared size.");
                digest.AppendData(buffer, 0, count); await target.WriteAsync(buffer.AsMemory(0, count), cancellation);
                if (Environment.TickCount64 >= nextReport) { progress?.Report(total * 100d / update.Size); nextReport = Environment.TickCount64 + 200; }
            }
            if (total != update.Size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("Update integrity verification failed.");
            await target.FlushAsync(cancellation); complete = true; progress?.Report(100);
        }
        finally { if (created && !complete && File.Exists(destination)) File.Delete(destination); }
    }
    private async Task<HttpResponseMessage> GetAsync(Uri address, CancellationToken cancellation)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (address.Scheme != "https" || address.UserInfo.Length != 0 || !address.IsDefaultPort
                || address.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("The update endpoint is not trusted.");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("CodexQuota-Updater/2.3");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location is null) throw new InvalidDataException("Missing update redirect.");
            address = location.IsAbsoluteUri ? location : new Uri(address, location);
        }
        throw new InvalidDataException("Too many update redirects.");
    }
    private static async Task CopyBounded(Stream source, Stream target, int limit, CancellationToken cancellation)
    {
        var buffer = new byte[8192]; var total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellation); if (count == 0) break;
            total += count; if (total > limit) throw new InvalidDataException("Oversized release metadata.");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
    }
}
