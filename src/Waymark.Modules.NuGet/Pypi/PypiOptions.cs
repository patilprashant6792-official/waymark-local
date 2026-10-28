using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>The index, the folders and the limits for reading a Python package from PyPI. Set under <c>Waymark:Pypi:*</c>.</summary>
/// <param name="IndexUrl">The base address of the package index (its JSON API lives under /pypi/).</param>
/// <param name="WorkDirectory">Where a package is unpacked while it is indexed; it is removed afterwards.</param>
/// <param name="MaxArchiveBytes">The largest wheel or source archive that is downloaded.</param>
/// <param name="MaxExtractedBytes">The most Python text that is unpacked from one archive.</param>
/// <param name="MaxFileBytes">A Python file bigger than this is skipped (generated code).</param>
public sealed record PypiOptions(
    Uri IndexUrl,
    string WorkDirectory,
    int LookupTimeoutSeconds,
    int DownloadTimeoutSeconds,
    long MaxArchiveBytes,
    long MaxExtractedBytes,
    int MaxFiles,
    long MaxFileBytes)
{
    public const string DefaultIndexUrl = "https://pypi.org/";

    private const string Prefix = "Waymark:Pypi:";

    public static PypiOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var index = ReadText(configuration, "IndexUrl", DefaultIndexUrl);
        if (!Uri.TryCreate(index.EndsWith('/') ? index : index + "/", UriKind.Absolute, out var indexUrl) || indexUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{Prefix}IndexUrl must be an absolute https URL.");
        }

        var work = ReadText(configuration, "WorkDirectory", Path.Combine(Path.GetTempPath(), "Waymark", "pypi-work"));
        if (!Path.IsPathFullyQualified(work))
        {
            throw new InvalidOperationException($"{Prefix}WorkDirectory must be an absolute path.");
        }

        return new PypiOptions(
            indexUrl,
            Path.GetFullPath(work),
            ReadInt(configuration, "LookupTimeoutSeconds", 30),
            ReadInt(configuration, "DownloadTimeoutSeconds", 180),
            ReadLong(configuration, "MaxArchiveBytes", 150_000_000),
            ReadLong(configuration, "MaxExtractedBytes", 200_000_000),
            ReadInt(configuration, "MaxFiles", 20_000),
            ReadLong(configuration, "MaxFileBytes", 2_000_000));
    }

    private static string ReadText(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[Prefix + key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static long ReadLong(IConfiguration configuration, string key, long fallback)
    {
        var name = Prefix + key;
        var value = configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1
            ? number
            : throw new InvalidOperationException($"{name} must be a positive whole number.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var number = ReadLong(configuration, key, fallback);
        return number <= int.MaxValue ? (int)number : throw new InvalidOperationException($"{Prefix}{key} is too large.");
    }
}
