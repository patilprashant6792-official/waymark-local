namespace Waymark.Core.NuGetIndex;

/// <summary>
/// Detail sections of a symbol. They are rendered once at index time and stored apart, so nuget_expand reads
/// exactly the sections it was asked for.
/// </summary>
public static class NuGetSections
{
    public const string Sig = "sig";
    public const string Methods = "methods";
    public const string Props = "props";
    public const string Data = "data";
    public const string Doc = "doc";
    public const string Rel = "rel";

    public static IReadOnlyList<string> Everything { get; } = [Sig, Methods, Props, Data, Doc, Rel];
}
