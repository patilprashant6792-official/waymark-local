namespace Waymark.Core.Edge;

/// <summary>The saved public (tunnel) origin, held in memory and reloaded when it changes.</summary>
public interface IPublicUrlProvider
{
    /// <summary>An origin such as https://name.ngrok-free.app with no trailing slash, or null until Setup saves one.</summary>
    string? Current { get; }

    Task RefreshAsync(CancellationToken ct);
}
