using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JunimoGate.App;

internal sealed partial class SmapiLogUploadClient : IDisposable
{
    public const int MaximumUploadBytes = 10 * 1024 * 1024;
    private static readonly Uri Endpoint = new("https://smapi.io/log");
    private readonly HttpClient client;

    public SmapiLogUploadClient() : this(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal SmapiLogUploadClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async ValueTask<Uri> UploadAsync(string logText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logText);
        var byteCount = Encoding.UTF8.GetByteCount(logText);
        if (byteCount is < 1 or > MaximumUploadBytes)
            throw new InvalidDataException("The SMAPI log size is outside the supported upload range.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["input"] = logText,
        });
        using var response = await client.PostAsync(Endpoint, form, cancellationToken).ConfigureAwait(false);
        var location = response.Headers.Location;
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest &&
            TryResolveLogUri(Endpoint, location, null) is { } redirectUri)
            return redirectUri;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices &&
            TryResolveLogUri(Endpoint, null, body) is { } bodyUri)
        {
            return bodyUri;
        }

        throw new HttpRequestException(
            "smapi.io did not return a valid log URL.",
            null,
            response.StatusCode);
    }

    internal static Uri? TryResolveLogUri(Uri endpoint, Uri? location, string? responseBody)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Uri? candidate = null;
        if (location is not null)
            candidate = location.IsAbsoluteUri ? location : new Uri(endpoint, location);
        else if (!string.IsNullOrEmpty(responseBody))
        {
            var match = SmapiLogUrl().Match(responseBody);
            if (match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var parsed))
                candidate = parsed;
        }

        if (candidate is null ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !candidate.Host.Equals("smapi.io", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort ||
            !SmapiLogPath().IsMatch(candidate.AbsolutePath) ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return null;
        }
        return candidate;
    }

    public void Dispose() => client.Dispose();

    [GeneratedRegex(@"https://smapi\.io/log/[A-Za-z0-9_-]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SmapiLogUrl();

    [GeneratedRegex(@"^/log/[A-Za-z0-9_-]+/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SmapiLogPath();
}
