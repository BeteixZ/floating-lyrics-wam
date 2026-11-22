using System.Net.Http.Headers;
using System.Text.Json;
using AppleMusicLyrics.Core.Updates;

namespace AppleMusicLyrics.Infrastructure.Windows.Updates;

/// <summary>
/// Reads the latest published (non-draft, non-pre-release) release of the app's GitHub
/// repository. Sends no user data: the request carries only the app name and version as its
/// User-Agent, which the GitHub API requires.
/// </summary>
public sealed class GitHubReleaseClient : IDisposable
{
    public const string Repository = "BeteixZ/floating-lyrics-wam";
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases/latest");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GitHubReleaseClient(AppVersion currentVersion, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AppleMusicLyrics", currentVersion.ToString()));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public HttpClient HttpClient => _httpClient;

    /// <summary>The latest release, or null when the repository has none with a version tag.</summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseRelease(document.RootElement);
    }

    /// <summary>
    /// Maps one GitHub release object; null when it is not an object, is a draft, or its tag is
    /// not a version. The release has an installable <see cref="ReleaseInfo.Package"/> only when
    /// it carries exactly <c>AppleMusicLyrics-vX.Y.Z-win-x64.zip</c> with a SHA-256 digest; any
    /// other release is offered as a download page instead.
    /// </summary>
    public static ReleaseInfo? ParseRelease(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (GetBool(release, "draft") || !AppVersion.TryParse(GetString(release, "tag_name"), out var version)) return null;
        var page = Uri.TryCreate(GetString(release, "html_url"), UriKind.Absolute, out var htmlUrl) && htmlUrl.Scheme == Uri.UriSchemeHttps
            ? htmlUrl
            : ReleasesPage;

        ReleaseAsset? package = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var expected = $"AppleMusicLyrics-{version.Tag}-win-x64.zip";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                // Another zip on the same release (a debug build, a mistaken upload) is never installed.
                if (!string.Equals(GetString(asset, "name"), expected, StringComparison.Ordinal)) continue;
                if (!Uri.TryCreate(GetString(asset, "browser_download_url"), UriKind.Absolute, out var url)
                    || url.Scheme != Uri.UriSchemeHttps) continue;
                // Without a digest there is nothing to verify the download against.
                if (ParseSha256(GetString(asset, "digest")) is not { } sha256) continue;
                var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                package = new ReleaseAsset(expected, url, size, sha256);
                break;
            }
        }

        return new ReleaseInfo(version, GetString(release, "name") ?? version.Tag, GetString(release, "body") ?? string.Empty,
            page, package);
    }

    // GitHub publishes asset digests as "sha256:<64 hex>".
    private static string? ParseSha256(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest["sha256:".Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
