namespace Waymark.Host.Ui;

/// <summary>
/// Which audit entry the call-details side panel shows. One per browser tab (circuit), so any page can open a call in place
/// instead of navigating to it, and the panel stays where it is while the page behind it changes.
/// </summary>
public sealed class CallPanelState
{
    private IReadOnlyList<long> _sequence = [];

    /// <summary>Raised after the open call or the sequence changed. May be raised from any thread: subscribers must marshal to their own context.</summary>
    public event Action? Changed;

    /// <summary>The audit reference of the open call, or null when the panel is closed.</summary>
    public long? OpenId { get; private set; }

    /// <summary>The references a page currently lists, newest first. The panel steps through them; empty when the page lists none.</summary>
    public IReadOnlyList<long> Sequence => _sequence;

    public void Open(long id)
    {
        if (OpenId == id)
        {
            return;
        }

        OpenId = id;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (OpenId is null)
        {
            return;
        }

        OpenId = null;
        Changed?.Invoke();
    }

    /// <summary>Tells the panel which calls the page lists. Raises <see cref="Changed"/> only when a call is open and the list really changed.</summary>
    public void SetSequence(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_sequence.SequenceEqual(ids))
        {
            return;
        }

        _sequence = ids;
        if (OpenId is not null)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The reference <paramref name="delta"/> places further down the listed sequence (1 is the next older call), or null when there is none.</summary>
    public long? Neighbour(int delta)
    {
        if (OpenId is not { } id)
        {
            return null;
        }

        var index = IndexOf(id);
        if (index < 0)
        {
            return null;
        }

        var target = index + delta;
        return target >= 0 && target < _sequence.Count ? _sequence[target] : null;
    }

    /// <summary>Opens the neighbour in the sequence, if there is one.</summary>
    public void Move(int delta)
    {
        if (Neighbour(delta) is { } id)
        {
            Open(id);
        }
    }

    private int IndexOf(long id)
    {
        for (var i = 0; i < _sequence.Count; i++)
        {
            if (_sequence[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
