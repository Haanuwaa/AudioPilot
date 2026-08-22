using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioPilot.Constants;

namespace AudioPilot.Services.Updates;

internal sealed record PublishedRelease(Version Version, Uri Url);

/// <summary>Reads public stable-release metadata without downloading or installing application assets.</summary>
internal sealed partial class GitHubReleaseService(HttpClient? client = null)
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private sealed record CachedRelease(EntityTagHeaderValue Tag, PublishedRelease? Release);
    private CachedRelease? _cachedRelease;
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }));

    internal async Task<PublishedRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, AppConstants.Links.LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd("AudioPilot");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        CachedRelease? cached = _cachedRelease;
        if (cached != null) request.Headers.IfNoneMatch.Add(cached.Tag);
        using HttpResponseMessage response = await (client ?? SharedClient.Value).SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _cachedRelease = null;
            return null;
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return cached != null ? cached.Release : throw new InvalidDataException("Release metadata was not modified but no cached response is available.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Release metadata exceeds the size limit.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException("Release metadata exceeds the size limit.");
            }

            buffer.Write(chunk, 0, count);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
            new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 32 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("draft", out JsonElement draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("prerelease", out JsonElement prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Release metadata is missing required fields.");
        }

        if (draft.GetBoolean() || prerelease.GetBoolean())
        {
            _cachedRelease = response.Headers.ETag is { } ignoredTag ? new CachedRelease(ignoredTag, null) : null;
            return null;
        }

        string tagName = tag.GetString()!;
        Match match = StableVersionPattern().Match(tagName);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out Version? version))
        {
            throw new InvalidDataException("Release tag is not a stable version.");
        }

        timeout.Token.ThrowIfCancellationRequested();
        var release = new PublishedRelease(version, new Uri(AppConstants.Links.RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tagName)));
        _cachedRelease = response.Headers.ETag is { } etag ? new CachedRelease(etag, release) : null;
        return release;
    }

    [GeneratedRegex(@"\Av?((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();
}
