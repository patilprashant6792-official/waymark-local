namespace Waymark.Host.Notifications;

/// <summary>Picks the notifier for the operating system Waymark runs on.</summary>
public static class DesktopNotifiers
{
    public static IDesktopNotifier ForThisComputer()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDesktopNotifier();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacDesktopNotifier();
        }

        return new NullDesktopNotifier();
    }
}

/// <summary>Used where no desktop notifier exists. The tab title and icon of the admin UI still show what waits.</summary>
public sealed class NullDesktopNotifier : IDesktopNotifier
{
    public bool IsSupported => false;

    public Task ShowAsync(DesktopNotice notice, CancellationToken ct) => Task.CompletedTask;
}
