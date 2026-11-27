using System.Globalization;
using System.Text.RegularExpressions;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Terminals;

/// <summary>
/// Tells when the program in a terminal is up, from what it prints (a text Claude named or the ecosystem's usual line, such as "Now listening on:")
/// or from a port it listens on, and collects the local addresses it prints so they can be shown and linked. Reads plain text only.
/// </summary>
public sealed partial class ReadyDetector
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _patterns;
    private readonly int? _readyPort;
    private readonly bool _readyOnAnyPort;
    private readonly List<string> _urls = [];
    private readonly SortedSet<int> _ports = [];

    public ReadyDetector(IReadOnlyList<string> patterns, int? readyPort, bool readyOnAnyPort = true)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        _readyPort = readyPort;
        _readyOnAnyPort = readyOnAnyPort;
    }

    /// <summary>Raised once, from the thread that found it, when the program is seen to be up. The text says how.</summary>
    public string? How { get; private set; }

    public bool IsReady => How is not null;

    /// <summary>Set whenever an address or a port was added; the reader clears it.</summary>
    public bool AddressesChanged { get; set; }

    public IReadOnlyList<string> Urls
    {
        get
        {
            lock (_gate)
            {
                return _urls.ToList();
            }
        }
    }

    public IReadOnlyList<int> Ports
    {
        get
        {
            lock (_gate)
            {
                return _ports.ToList();
            }
        }
    }

    [GeneratedRegex(@"\bhttps?://(?:localhost|127\.0\.0\.1|\[::1?\]|0\.0\.0\.0|[a-z0-9-]+\.localhost)(?::(?<port>\d{2,5}))?(?:/[^\s'""<>)\]]*)?", RegexOptions.IgnoreCase, 200)]
    private static partial Regex LocalUrl();

    /// <summary>Looks at one printed line. True when it made the program ready.</summary>
    public bool Check(LogLine line)
    {
        var text = line.Text;
        if (text.Length == 0)
        {
            return false;
        }

        CollectUrls(text);
        if (IsReady)
        {
            return false;
        }

        var hit = _patterns.FirstOrDefault(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        return hit is not null && MarkReady($"printed \"{hit}\"");
    }

    /// <summary>Takes the ports the program's processes listen on now. True when that made the program ready.</summary>
    public bool CheckPorts(IEnumerable<int> listening)
    {
        ArgumentNullException.ThrowIfNull(listening);
        var ports = listening.ToList();
        lock (_gate)
        {
            foreach (var port in ports)
            {
                if (_ports.Add(port))
                {
                    AddressesChanged = true;
                }
            }
        }

        if (IsReady || ports.Count == 0)
        {
            return false;
        }

        if (_readyPort is { } wanted)
        {
            return ports.Contains(wanted) && MarkReady(string.Create(CultureInfo.InvariantCulture, $"listens on port {wanted}"));
        }

        return _readyOnAnyPort && MarkReady(string.Create(CultureInfo.InvariantCulture, $"listens on port {ports.Min()}"));
    }

    /// <summary>Marks the program ready for a reason found elsewhere (a quiet moment after the first output of a script).</summary>
    public bool MarkReady(string how)
    {
        lock (_gate)
        {
            if (How is not null)
            {
                return false;
            }

            How = how;
            return true;
        }
    }

    private void CollectUrls(string text)
    {
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            return;
        }

        MatchCollection matches;
        try
        {
            matches = LocalUrl().Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        foreach (Match match in matches)
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':')
                .Replace("0.0.0.0", "localhost", StringComparison.Ordinal)
                .Replace("127.0.0.1", "localhost", StringComparison.Ordinal)
                .Replace("[::1]", "localhost", StringComparison.Ordinal)
                .Replace("[::]", "localhost", StringComparison.Ordinal);
            lock (_gate)
            {
                if (!_urls.Contains(url, StringComparer.OrdinalIgnoreCase) && _urls.Count < 8)
                {
                    _urls.Add(url);
                    AddressesChanged = true;
                }

                if (int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && _ports.Add(port))
                {
                    AddressesChanged = true;
                }
            }
        }
    }
}
