using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>One version of a package, as the registry describes it.</summary>
internal sealed record NpmVersion(string Name, string Version, string? Description, Uri TarballUrl, string? Integrity, string? Shasum);

/// <summary>
/// Talks to the public npm registry: the description of one version (<c>/{name}/{version}</c>, small even for a package with thousands of versions), a search,
/// and the download of a tarball. Only https and only the registry's own hosts; a tarball is checked against the integrity hash the registry publishes.
/// </summary>
internal sealed class NpmRegistryClient : IDisposable
{
    private const int CopyBufferBytes = 81_920;

    private readonly NpmOptions _options;
    private readonly ILogger<NpmRegistryClient> _logger;
    private readonly HttpClient _http;

    public NpmRegistryClient(NpmOptions options, ILogger<NpmRegistryClient> logger)
    {
        _options = options;
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Waymark-package-lookup");
    }

    /// <summary>The version (the latest one when <paramref name="version"/> is null); null when the package or the version does not exist.</summary>
    /// <exception cref="TimeoutException">The registry did not answer in time.</exception>
    /// <exception cref="HttpRequestException">The registry answered with an error.</exception>
    public async Task<NpmVersion?> GetAsync(string name, string? version, CancellationToken ct)
    {
        var address = new Uri(_options.RegistryUrl, $"{NpmNames.PathSegment(name)}/{Uri.EscapeDataString(version ?? "latest")}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(_options.LookupTimeoutSeconds));
        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: limit.Token).ConfigureAwait(false);
            return Read(document.RootElement);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while looking up '{name}' on the npm registry.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The npm registry sent an answer for '{name}' that is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>The registry's own search: name, latest version and description of the best matches.</summary>
    public async Task<IReadOnlyList<PackageSearchHit>> SearchAsync(string text, int size, CancellationToken ct)
    {
        var address = new Uri(_options.RegistryUrl, $"-/v1/search?text={Uri.EscapeDataString(text)}&size={size}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(_options.LookupTimeoutSeconds));
        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: limit.Token).ConfigureAwait(false);
            var hits = new List<PackageSearchHit>();
            if (document.RootElement.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in objects.EnumerateArray())
                {
                    if (item.TryGetProperty("package", out var package) && Text(package, "name") is { } name && Text(package, "version") is { } version)
                    {
                        hits.Add(new PackageSearchHit(name, version, null, Text(package, "description") ?? string.Empty));
                    }
                }
            }

            return hits;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout while searching the npm registry.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The npm registry sent a search answer that is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Downloads the tarball to <paramref name="destination"/> and checks its size and its integrity hash.</summary>
    /// <exception cref="InvalidDataException">The file is too big, or its hash is not the one the registry published.</exception>
    public async Task DownloadAsync(NpmVersion version, string destination, CancellationToken ct)
    {
        EnsureTrusted(version.TarballUrl);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds));
        try
        {
            using var response = await _http.GetAsync(version.TarballUrl, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length > _options.MaxArchiveBytes)
            {
                throw new InvalidDataException($"'{version.Name}' {version.Version} is {length / 1_000_000} MB, more than the limit of {_options.MaxArchiveBytes / 1_000_000} MB (Waymark:Npm:MaxArchiveBytes).");
            }

            await using var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true);
            using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var buffer = new byte[CopyBufferBytes];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), limit.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > _options.MaxArchiveBytes)
                {
                    throw new InvalidDataException($"'{version.Name}' {version.Version} is bigger than the limit of {_options.MaxArchiveBytes / 1_000_000} MB (Waymark:Npm:MaxArchiveBytes).");
                }

                sha512.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), limit.Token).ConfigureAwait(false);
            }

            VerifyIntegrity(version, Convert.ToBase64String(sha512.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()));
            _logger.LogDebug("Downloaded {Package} {Version} ({Bytes} bytes)", version.Name, version.Version, total);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while downloading '{version.Name}' {version.Version}.");
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The registry publishes <c>sha512-BASE64</c> (subresource integrity) and, for old packages, only a sha1 hex. One of them must match; none published is refused.</summary>
    private static void VerifyIntegrity(NpmVersion version, string sha512Base64, string sha1Hex)
    {
        if (version.Integrity is { } integrity && integrity.StartsWith("sha512-", StringComparison.Ordinal))
        {
            if (!string.Equals(integrity["sha512-".Length..], sha512Base64, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"'{version.Name}' {version.Version} does not match the integrity hash the npm registry publishes for it, so it was not used.");
            }

            return;
        }

        if (version.Shasum is { Length: > 0 } shasum)
        {
            if (!string.Equals(shasum, sha1Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{version.Name}' {version.Version} does not match the checksum the npm registry publishes for it, so it was not used.");
            }

            return;
        }

        throw new InvalidDataException($"The npm registry publishes no integrity hash for '{version.Name}' {version.Version}, so it cannot be checked and was not used.");
    }

    private void EnsureTrusted(Uri url)
    {
        var host = url.Host;
        var trusted = url.Scheme == Uri.UriSchemeHttps
            && (string.Equals(host, _options.RegistryUrl.Host, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".npmjs.org", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".npmjs.com", StringComparison.OrdinalIgnoreCase));
        if (!trusted)
        {
            throw new InvalidDataException($"The npm registry points at '{url.GetLeftPart(UriPartial.Authority)}', which is not a host Waymark downloads from.");
        }
    }

    private static NpmVersion Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("dist", out var dist) || dist.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The npm registry answer has no 'dist' section.");
        }

        var tarball = Text(dist, "tarball");
        if (tarball is null || !Uri.TryCreate(tarball, UriKind.Absolute, out var url))
        {
            throw new InvalidOperationException("The npm registry answer has no tarball address.");
        }

        return new NpmVersion(
            Text(root, "name") ?? string.Empty,
            Text(root, "version") ?? string.Empty,
            Text(root, "description"),
            url,
            Text(dist, "integrity"),
            Text(dist, "shasum"));
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
