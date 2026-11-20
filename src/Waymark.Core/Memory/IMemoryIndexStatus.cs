namespace Waymark.Core.Memory;

public enum MemoryIndexState
{
    /// <summary>Search by meaning is off, or no key is saved.</summary>
    Off,

    /// <summary>Embedding what is pending.</summary>
    Working,

    /// <summary>Nothing pending.</summary>
    Idle,

    /// <summary>The provider refused the key; nothing is sent until the settings change.</summary>
    KeyRejected,

    /// <summary>The provider failed for a while (rate limit, network); the next try waits.</summary>
    Backoff,
}

/// <summary>The index as the Setup page shows it, at one moment.</summary>
public sealed record MemoryIndexSnapshot(
    MemoryIndexState State,
    int Embedded,
    int Pending,
    string? LastError,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? RetryAtUtc);

/// <summary>The live status of the embedding index. Changes publish MemoryIndexStatusChanged.</summary>
public interface IMemoryIndexStatus
{
    MemoryIndexSnapshot Current { get; }
}
