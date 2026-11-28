using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Terminals;

/// <summary>
/// The live side of one run in a terminal: its streamed log, what tells it is up, what watches its health, the program itself once it started, and
/// how far Claude has read. The output arrives on the reading thread (<see cref="OnText"/>); everything that needs the store or the event bus is
/// picked up from the flags here by the coordinator's terminal loop, so the reading thread never waits.
/// </summary>
public sealed class TerminalRuntime
{
    private readonly object _gate = new();
    private long _readLine;
    private DateTimeOffset? _readUtc;

    public TerminalRuntime(TerminalLaunch launch, TerminalLog log)
    {
        Launch = launch ?? throw new ArgumentNullException(nameof(launch));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        Detector = new ReadyDetector(launch.ReadyPatterns, launch.ReadyPort, launch.ReadyOnAnyPort);
        Health = new HealthWatch(launch.AppDownPatterns, launch.AppUpPatterns);
    }

    public TerminalLaunch Launch { get; }

    public TerminalLog Log { get; }

    public ReadyDetector Detector { get; }

    public HealthWatch Health { get; }

    /// <summary>The program, once it started.</summary>
    public RunningProcess? Process { get; set; }

    /// <summary>Completes when the program is up.</summary>
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the run no longer holds the workspace's turn and a run slot: it is up, it ended, or its start phase ran out.</summary>
    public TaskCompletionSource HandedOff { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When the program printed its first text.</summary>
    public DateTimeOffset? FirstOutputUtc { get; private set; }

    /// <summary>When the program last printed something (not Waymark's own lines: the command, the script, the build before the run).</summary>
    public DateTimeOffset? LastProgramOutputUtc { get; private set; }

    /// <summary>The prompt the program seems to wait at, or null.</summary>
    public string? PendingPrompt { get; set; }

    /// <summary>The prompt Claude was last told about, so it is told once.</summary>
    public string? NotifiedPrompt { get; set; }

    /// <summary>The last error line Claude was told about, and when.</summary>
    public long NotifiedErrorLine { get; set; }

    public DateTimeOffset NotifiedErrorUtc { get; set; }

    /// <summary>When the last output time was saved to the store, so it is saved at most every few seconds.</summary>
    public DateTimeOffset SavedOutputUtc { get; set; }

    /// <summary>The line Claude read up to, and when.</summary>
    public long ReadLine
    {
        get
        {
            lock (_gate)
            {
                return _readLine;
            }
        }
    }

    public DateTimeOffset? ReadUtc
    {
        get
        {
            lock (_gate)
            {
                return _readUtc;
            }
        }
    }

    /// <summary>
    /// Takes text for the terminal: everything goes to the log (the Console and Claude's reads show it), but only what the program itself printed
    /// feeds the readiness and health checks. Waymark's own lines (the command line, a script's text, the build before the run, a typed line, the
    /// cleared marker) name words like "ready" or "error" without the program having said them.
    /// </summary>
    public void OnText(string text, bool fromProgram)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (fromProgram)
        {
            var now = DateTimeOffset.UtcNow;
            FirstOutputUtc ??= now;
            LastProgramOutputUtc = now;
        }

        foreach (var line in Log.Append(text))
        {
            if (fromProgram)
            {
                Detector.Check(line);
                Health.Check(line);
            }
        }
    }

    /// <summary>Claude answered: the prompt it waited at is answered, and it is not announced again.</summary>
    public void Answered()
    {
        NotifiedPrompt = PendingPrompt ?? NotifiedPrompt;
        PendingPrompt = null;
    }

    /// <summary>Records that Claude read up to <paramref name="line"/>.</summary>
    public void MarkRead(long line)
    {
        lock (_gate)
        {
            if (line > _readLine)
            {
                _readLine = line;
            }

            _readUtc = DateTimeOffset.UtcNow;
        }
    }
}
