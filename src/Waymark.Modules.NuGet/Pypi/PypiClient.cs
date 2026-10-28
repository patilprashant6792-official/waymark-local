using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>One file of a release: a wheel (bdist_wheel) or a source archive (sdist).</summary>
internal sealed record PypiFile(string FileName, string PackageType, string PythonVersion, Uri Url, string? Sha256, long Size, bool Yanked)
{
    public bool IsWheel => string.Equals(PackageType, "bdist_wheel", StringComparison.Ordinal);

    public bool IsSdist => string.Equals(PackageType, "sdist", StringComparison.Ordinal);
}

/// <summary>One release of a project, as the JSON API of the index describes it.</summary>
internal sealed record PypiProject(string Name, string Version, string? Summary, string? RequiresPython, IReadOnlyList<PypiFile> Files);

/// <summary>
/// Talks to the package index: the JSON description of a release, and the download of one of its files. Only https, only the index's own host or
/// the host PyPI serves its files from, and a download is checked against the sha256 the index publishes for it.
/// </summary>
internal sealed class PypiClient : IDisposable
{
    private const int CopyBufferBytes = 81_920;
    private const string FileHostSuffix = ".pythonhosted.org";

    private readonly PypiOptions _options;
    private readonly ILogger<PypiClient> _logger;
    private readonly HttpClient _http;

    public PypiClient(PypiOptions options, ILogger<PypiClient> logger)
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

    /// <summary>The release (the latest one when <paramref name="version"/> is null); null when the project or the version does not exist.</summary>
    /// <exception cref="TimeoutException">The index did not answer in time.</exception>
    /// <exception cref="HttpRequestException">The index answered with an error.</exception>
    public async Task<PypiProject?> GetAsync(string name, string? version, CancellationToken ct)
    {
        var path = version is null
            ? $"pypi/{Uri.EscapeDataString(name)}/json"
            : $"pypi/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}/json";
        var address = new Uri(_options.IndexUrl, path);
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
            throw new TimeoutException($"Timeout while looking up '{name}' on the package index.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The package index sent an answer for '{name}' that is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Downloads one file to <paramref name="destination"/> and checks its size and its sha256.</summary>
    /// <exception cref="InvalidDataException">The file is too big, or its sha256 is not the one the index published.</exception>
    public async Task DownloadAsync(PypiFile file, string destination, CancellationToken ct)
    {
        EnsureTrusted(file.Url);
        if (file.Size > _options.MaxArchiveBytes)
        {
            throw new InvalidDataException($"'{file.FileName}' is {file.Size / 1_000_000} MB, more than the limit of {_options.MaxArchiveBytes / 1_000_000} MB (Waymark:Pypi:MaxArchiveBytes).");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds));
        try
        {
            using var response = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferBytes];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), limit.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > _options.MaxArchiveBytes)
                {
                    throw new InvalidDataException($"'{file.FileName}' is bigger than the limit of {_options.MaxArchiveBytes / 1_000_000} MB (Waymark:Pypi:MaxArchiveBytes).");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), limit.Token).ConfigureAwait(false);
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (file.Sha256 is not null && !string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{file.FileName}' does not match the sha256 that the package index publishes for it, so it was not used.");
            }

            _logger.LogDebug("Downloaded {File} ({Bytes} bytes)", file.FileName, total);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while downloading '{file.FileName}'.");
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A download address must be https and on the index's own host or on PyPI's file host.</summary>
    private void EnsureTrusted(Uri url)
    {
        var host = url.Host;
        var trusted = url.Scheme == Uri.UriSchemeHttps
            && (string.Equals(host, _options.IndexUrl.Host, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(FileHostSuffix, StringComparison.OrdinalIgnoreCase));
        if (!trusted)
        {
            throw new InvalidDataException($"The package index points at '{url.GetLeftPart(UriPartial.Authority)}', which is not a host Waymark downloads from.");
        }
    }

    private static PypiProject Read(JsonElement root)
    {
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The package index answer has no 'info' section.");
        }
        var files = new List<PypiFile>();
        if (root.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in urls.EnumerateArray())
            {
                var file = ReadFile(item);
                if (file is not null)
                {
                    files.Add(file);
                }
            }
        }

        return new PypiProject(
            Text(info, "name") ?? string.Empty,
            Text(info, "version") ?? string.Empty,
            Text(info, "summary"),
            Text(info, "requires_python"),
            files);
    }

    private static PypiFile? ReadFile(JsonElement item)
    {
        var name = Text(item, "filename");
        var type = Text(item, "packagetype");
        var address = Text(item, "url");
        if (name is null || type is null || address is null || !Uri.TryCreate(address, UriKind.Absolute, out var url))
        {
            return null;
        }

        string? sha = null;
        if (item.TryGetProperty("digests", out var digests) && digests.ValueKind == JsonValueKind.Object)
        {
            sha = Text(digests, "sha256");
        }

        var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
        var yanked = item.TryGetProperty("yanked", out var yankedElement) && yankedElement.ValueKind == JsonValueKind.True;
        return new PypiFile(name, type, Text(item, "python_version") ?? string.Empty, url, sha, size, yanked);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
