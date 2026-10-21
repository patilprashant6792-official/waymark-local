namespace Waymark.Host.Components.Shared;

/// <summary>Status is never colour alone: each state has a fixed colour, icon and word.</summary>
public enum StatusState
{
    Healthy,
    Attention,
    Failed,
    Working,
    Off
}
