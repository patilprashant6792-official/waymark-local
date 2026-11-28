using System.Runtime.InteropServices;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// Sends a signal to a process on macOS and Linux, the way a terminal does when the developer presses Ctrl+C (SIGINT) or a system asks a program to
/// end (SIGTERM). .NET can only kill a process; a program that gets SIGINT or SIGTERM can shut down cleanly first. Does nothing on Windows.
/// </summary>
public static class UnixSignals
{
    public const int Interrupt = 2;
    public const int Kill = 9;
    public const int Terminate = 15;

    /// <summary>True when the signal was delivered.</summary>
    public static bool Send(int pid, int signal)
    {
        if (OperatingSystem.IsWindows() || pid <= 0)
        {
            return false;
        }

        try
        {
            return SendSignal(pid, signal) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);
}
