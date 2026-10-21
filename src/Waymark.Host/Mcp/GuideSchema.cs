using System.Text.Json;
using System.Text.Json.Nodes;
using Waymark.Core.Guides;

namespace Waymark.Host.Mcp;

/// <summary>Adds the optional boolean <c>guide</c> property to a tool's input schema, so every tool of a module can ask for the module's guide.</summary>
public static class GuideSchema
{
    private const string PropertiesKey = "properties";

    /// <summary>Returns the schema with the property added. A schema that already has it, or is not an object schema, is returned as it is.</summary>
    public static JsonElement WithGuideProperty(JsonElement schema, string module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        if (schema.ValueKind != JsonValueKind.Object || JsonNode.Parse(schema.GetRawText()) is not JsonObject root)
        {
            return schema;
        }

        if (root[PropertiesKey] is not JsonObject properties)
        {
            properties = new JsonObject();
            root[PropertiesKey] = properties;
        }

        if (properties.ContainsKey(GuideRules.ArgumentName))
        {
            return schema;
        }

        properties[GuideRules.ArgumentName] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = GuideRules.PropertyDescription(module),
        };
        return JsonSerializer.SerializeToElement(root);
    }
}
