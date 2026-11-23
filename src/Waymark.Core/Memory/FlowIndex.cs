using System.Globalization;

namespace Waymark.Core.Memory;

/// <summary>
/// The flow index an agent pages through: the workspace guide lists the first page (search by meaning off) and recall list without a query
/// continues with the same size and order (newest first), so page 2 starts exactly where the guide stops.
/// </summary>
public static class FlowIndex
{
    public const int PageSize = 50;

    /// <summary>One index line: the flow's name and id, nothing more. recall get describes the flow.</summary>
    public static string Line(long id, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Create(CultureInfo.InvariantCulture, $"{name}  id {id}");
    }
}
