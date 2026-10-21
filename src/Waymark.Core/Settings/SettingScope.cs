namespace Waymark.Core.Settings;

public enum SettingScopeKind
{
    Global,
    Workspace,
    Project,
    Connection
}

/// <summary>Where a setting applies. The global scope has an empty id; every other scope needs one.</summary>
public readonly record struct SettingScope
{
    public SettingScope(SettingScopeKind kind, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (kind == SettingScopeKind.Global && id.Length != 0)
        {
            throw new ArgumentException("The global scope has no id.", nameof(id));
        }

        if (kind != SettingScopeKind.Global && id.Length == 0)
        {
            throw new ArgumentException("A non-global scope requires an id.", nameof(id));
        }

        Kind = kind;
        Id = id;
    }

    public SettingScopeKind Kind { get; }

    public string Id { get; }

    public static SettingScope Global => new(SettingScopeKind.Global, string.Empty);
}
