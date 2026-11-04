using Waymark.Core.Code;

namespace Waymark.Modules.Code.Search;

internal enum SiteMatch
{
    /// <summary>The receiver is declared as the method's type, one of its bases or one of its derived types.</summary>
    Typed,

    /// <summary>The receiver's type is unknown, so the call is matched by the method name alone.</summary>
    NameOnly,

    /// <summary>The receiver is declared as a type that has nothing to do with the method's type.</summary>
    Unrelated,

    /// <summary>The filter is switched off for this method; every call is matched by name.</summary>
    Unfiltered,
}

internal sealed record ClassifiedSite(CodeCallSiteRow Site, SiteMatch Match);

/// <summary>Sorts the same-named calls of a method into those that can reach it and those on unrelated types.</summary>
internal static class ReceiverFilter
{
    /// <param name="related">Names of the method's type, its bases and its derived types.</param>
    /// <param name="valueNames">
    /// Names that some field, property or event of the workspace carries. A capitalised receiver name only guessed to be a
    /// static type may really be such a member (an inherited property, say), so it is not trusted as a type then.
    /// </param>
    public static IReadOnlyList<ClassifiedSite> Classify(IEnumerable<CodeCallSiteRow> sites, IReadOnlySet<string> related, IReadOnlySet<string> valueNames)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(related);
        ArgumentNullException.ThrowIfNull(valueNames);
        return [.. sites.Select(site => new ClassifiedSite(site, Match(site, related, valueNames)))];
    }

    public static IReadOnlyList<ClassifiedSite> Unfiltered(IEnumerable<CodeCallSiteRow> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        return [.. sites.Select(site => new ClassifiedSite(site, SiteMatch.Unfiltered))];
    }

    /// <summary>The receiver types of the unrelated calls with how many calls each has, most frequent first.</summary>
    public static IReadOnlyList<KeyValuePair<string, int>> CountUnrelated(IEnumerable<ClassifiedSite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        return [.. sites
            .Where(s => s.Match == SiteMatch.Unrelated && s.Site.ReceiverType is not null)
            .GroupBy(s => s.Site.ReceiverType!, StringComparer.Ordinal)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)];
    }

    private static SiteMatch Match(CodeCallSiteRow site, IReadOnlySet<string> related, IReadOnlySet<string> valueNames) =>
        MatchReceiver(site.ReceiverType, site.ReceiverTypeGuessed, related, valueNames);

    /// <summary>
    /// Whether a call whose receiver has this declared type can reach a method of the related types. Shared by code_callers and by the link check of a flow,
    /// so both say the same about the same call.
    /// </summary>
    /// <param name="receiverType">The declared type of the receiver, or null when the file does not tell.</param>
    /// <param name="guessed">True when the type is only a guess (see <see cref="CodeCallSiteEntry"/>).</param>
    public static SiteMatch MatchReceiver(string? receiverType, bool guessed, IReadOnlySet<string> related, IReadOnlySet<string> valueNames)
    {
        ArgumentNullException.ThrowIfNull(related);
        ArgumentNullException.ThrowIfNull(valueNames);
        if (receiverType is null)
        {
            return SiteMatch.NameOnly;
        }

        if (guessed && valueNames.Contains(receiverType))
        {
            return SiteMatch.NameOnly;
        }

        return related.Contains(receiverType) ? SiteMatch.Typed : SiteMatch.Unrelated;
    }
}
