namespace Waymark.Modules.NuGet.Indexing;

/// <summary>The text of one documented member, with cross-references reduced to plain names.</summary>
internal sealed class XmlDocEntry
{
    public string MemberId { get; init; } = string.Empty;

    public string? Summary { get; init; }

    public string? Returns { get; init; }

    public string? Remarks { get; init; }

    public string? Example { get; init; }

    public Dictionary<string, string> Params { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> TypeParams { get; } = new(StringComparer.Ordinal);

    /// <summary>Exception type (as written in cref, without the T: prefix) to the text that says when it is thrown.</summary>
    public Dictionary<string, string> Exceptions { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty =>
        Summary is null && Returns is null && Remarks is null && Example is null
        && Params.Count == 0 && TypeParams.Count == 0 && Exceptions.Count == 0;
}
