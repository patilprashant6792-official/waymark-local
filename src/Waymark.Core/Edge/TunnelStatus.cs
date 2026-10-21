namespace Waymark.Core.Edge;

public enum TunnelPhase
{
    Stopped,
    Installing,
    Starting,
    Online,
    Failed
}

/// <summary>Where the ngrok tunnel that Waymark manages currently stands, in words the Setup page can show as they are.</summary>
public sealed record TunnelStatus(TunnelPhase Phase, string Message, string? Domain)
{
    public static TunnelStatus Stopped { get; } = new(TunnelPhase.Stopped, "The tunnel is not running.", null);

    public bool IsOnline => Phase == TunnelPhase.Online;

    public bool IsBusy => Phase is TunnelPhase.Installing or TunnelPhase.Starting;
}
