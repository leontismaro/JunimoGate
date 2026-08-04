using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace JunimoGate.App;

internal enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NoStableRelease,
}

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? LatestVersion,
    string? ReleaseName,
    string? ReleaseUrl);

internal sealed class GitHubUpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/leontismaro/JunimoGate/releases/latest";
    private const int MaximumResponseBytes = 256 * 1024;

    public async ValueTask<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("JunimoGate", currentVersion.Replace('+', '-')));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckStatus.NoStableRelease, null, null, null);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The GitHub release response is too large.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (bounded.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The GitHub release response is too large.");
            bounded.Write(buffer, 0, read);
        }

        using var document = JsonDocument.Parse(bounded.ToArray());
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps || releaseUri.Host != "github.com")
        {
            throw new InvalidDataException("The GitHub release response is malformed.");
        }

        return new UpdateCheckResult(
            IsNewerStableVersion(currentVersion, tag)
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate,
            tag,
            string.IsNullOrWhiteSpace(name) ? tag : name,
            releaseUri.AbsoluteUri);
    }

    internal static bool IsNewerStableVersion(string currentVersion, string latestTag)
    {
        var currentBase = Normalize(currentVersion);
        var latestBase = Normalize(latestTag);
        if (!Version.TryParse(currentBase, out var current) || !Version.TryParse(latestBase, out var latest))
            throw new InvalidDataException("A release version is malformed.");
        var comparison = latest.CompareTo(current);
        return comparison > 0 || comparison == 0 && currentVersion.Contains('-', StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("A release version is missing.");
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];
        var suffix = normalized.IndexOfAny(['-', '+']);
        return suffix < 0 ? normalized : normalized[..suffix];
    }
}
