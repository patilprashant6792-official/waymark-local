using System.Globalization;
using System.Text.RegularExpressions;

namespace Waymark.Modules.Commands.Execution;

public sealed record PortOwner(int Port, int Pid, string ProcessName, string Address);

/// <summary>Lists the programs that listen on TCP ports, with the system's own tool (netstat on Windows, ss or lsof elsewhere).</summary>
public sealed partial class ListeningPorts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ProcessRunner _runner;
    private readonly ExecutableLocator _executables;

    public ListeningPorts(ProcessRunner runner, ExecutableLocator executables)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _executables = executables ?? throw new ArgumentNullException(nameof(executables));
    }

    [GeneratedRegex(@"^\s*TCP\s+(?<addr>\S+):(?<port>\d+)\s+\S+\s+LISTENING\s+(?<pid>\d+)\s*$", RegexOptions.IgnoreCase, 200)]
    private static partial Regex NetstatPattern();

    [GeneratedRegex(@"^LISTEN\s+\d+\s+\d+\s+(?<addr>\S+):(?<port>\d+)\s+\S+(?:\s+users:\(\(""(?<name>[^""]*)"",pid=(?<pid>\d+))?", RegexOptions.None, 200)]
    private static partial Regex SsPattern();

    [GeneratedRegex(@"^(?<name>\S+)\s+(?<pid>\d+)\s+\S+\s+.*TCP\s+(?<addr>\S+):(?<port>\d+)\s+\(LISTEN\)", RegexOptions.None, 200)]
    private static partial Regex LsofPattern();

    /// <exception cref="InvalidOperationException">The system has none of the tools needed to list ports.</exception>
    public async Task<IReadOnlyList<PortOwner>> ListAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var netstat = _executables.Find("netstat") ?? throw new InvalidOperationException("netstat was not found.");
            var (_, output) = await _runner.CaptureAsync(netstat, ["-ano", "-p", "TCP"], Timeout, ct).ConfigureAwait(false);
            return await NamesAsync(Parse(output, NetstatPattern(), hasName: false)).ConfigureAwait(false);
        }

        var ss = _executables.Find("ss");
        if (ss is not null)
        {
            var (_, output) = await _runner.CaptureAsync(ss, ["-ltnpH"], Timeout, ct).ConfigureAwait(false);
            return Parse(output, SsPattern(), hasName: true);
        }

        var lsof = _executables.Find("lsof") ?? throw new InvalidOperationException("Neither ss nor lsof was found.");
        var (_, lsofOutput) = await _runner.CaptureAsync(lsof, ["-nP", "-iTCP", "-sTCP:LISTEN"], Timeout, ct).ConfigureAwait(false);
        return Parse(lsofOutput, LsofPattern(), hasName: true);
    }

    private static List<PortOwner> Parse(string output, Regex pattern, bool hasName)
    {
        var result = new List<PortOwner>();
        var seen = new HashSet<(int, int)>();
        foreach (var line in output.Split('\n'))
        {
            Match match;
            try
            {
                match = pattern.Match(line.TrimEnd('\r'));
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success || !int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                continue;
            }

            _ = int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid);
            if (!seen.Add((port, pid)))
            {
                continue;
            }

            var name = hasName ? match.Groups["name"].Value : string.Empty;
            result.Add(new PortOwner(port, pid, name, match.Groups["addr"].Value));
        }

        return result.OrderBy(p => p.Port).ThenBy(p => p.Pid).ToList();
    }

    private static Task<IReadOnlyList<PortOwner>> NamesAsync(IReadOnlyList<PortOwner> owners)
    {
        var named = owners.Select(owner => owner with { ProcessName = NameOf(owner.Pid) }).ToList();
        return Task.FromResult<IReadOnlyList<PortOwner>>(named);
    }

    private static string NameOf(int pid)
    {
        if (pid <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
