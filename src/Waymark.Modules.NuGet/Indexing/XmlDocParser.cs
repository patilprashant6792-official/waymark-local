using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>Turns the raw XML documentation files of a package into one member-id keyed map.</summary>
internal static partial class XmlDocParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    /// <summary>The first file that documents a member wins. A file that cannot be parsed is logged and skipped.</summary>
    public static Dictionary<string, XmlDocEntry> Parse(IEnumerable<DocumentationFile> files, ILogger logger)
    {
        var result = new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                foreach (var entry in ParseFile(file.Xml))
                {
                    result.TryAdd(entry.MemberId, entry);
                }
            }
            catch (XmlException ex)
            {
                logger.LogWarning(ex, "Documentation file {File} is not valid XML and was skipped", file.FileName);
            }
        }

        return result;
    }

    private static IEnumerable<XmlDocEntry> ParseFile(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, ReaderSettings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var members = document.Root?.Element("members")?.Elements("member");
        if (members is null)
        {
            yield break;
        }

        foreach (var member in members)
        {
            var name = member.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var entry = new XmlDocEntry
            {
                MemberId = name,
                Summary = ExtractText(member.Element("summary")),
                Returns = ExtractText(member.Element("returns")),
                Remarks = ExtractText(member.Element("remarks")),
                Example = ExtractText(member.Element("example"))
            };

            AddNamed(member.Elements("param"), "name", entry.Params);
            AddNamed(member.Elements("typeparam"), "name", entry.TypeParams);
            foreach (var exception in member.Elements("exception"))
            {
                var cref = exception.Attribute("cref")?.Value ?? string.Empty;
                entry.Exceptions[StripPrefix(cref)] = ExtractText(exception) ?? string.Empty;
            }

            if (!entry.IsEmpty)
            {
                yield return entry;
            }
        }
    }

    private static void AddNamed(IEnumerable<XElement> elements, string attribute, Dictionary<string, string> target)
    {
        foreach (var element in elements)
        {
            var name = element.Attribute(attribute)?.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                target[name] = ExtractText(element) ?? string.Empty;
            }
        }
    }

    /// <summary>Strips tags, reduces cross-references to their short name and collapses whitespace.</summary>
    private static string? ExtractText(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText plain:
                    text.Append(plain.Value);
                    break;
                case XElement child:
                    AppendChild(text, child);
                    break;
            }
        }

        var flat = Whitespace().Replace(text.ToString(), " ").Trim();
        return flat.Length == 0 ? null : flat;
    }

    private static void AppendChild(StringBuilder text, XElement child)
    {
        switch (child.Name.LocalName)
        {
            case "see" or "seealso":
                text.Append(ReferenceText(child));
                break;
            case "paramref" or "typeparamref":
                text.Append(child.Attribute("name")?.Value);
                break;
            default:
                text.Append(child.Value);
                break;
        }
    }

    private static string ReferenceText(XElement see)
    {
        var target = see.Attribute("cref")?.Value ?? see.Attribute("href")?.Value;
        if (!string.IsNullOrWhiteSpace(target))
        {
            var name = StripPrefix(target);
            var paren = name.IndexOf('(', StringComparison.Ordinal);
            if (paren > 0)
            {
                name = name[..paren];
            }

            var dot = name.LastIndexOf('.');
            return dot >= 0 ? name[(dot + 1)..] : name;
        }

        var langword = see.Attribute("langword")?.Value;
        if (!string.IsNullOrWhiteSpace(langword))
        {
            return langword;
        }

        return !string.IsNullOrWhiteSpace(see.Value)
            ? see.Value
            : see.Attributes().Select(a => a.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    /// <summary>Removes the member-kind prefix (T:, M:, P:, F:, E:, N:) while it is still anchored at position 0.</summary>
    private static string StripPrefix(string id) => id.Length > 2 && id[1] == ':' ? id[2..] : id;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
