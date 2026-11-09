using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Waymark.Tests.Support;

/// <summary>A dictionary-backed configuration for options classes that only read values by key.</summary>
internal sealed class TestConfiguration : IConfiguration
{
    private readonly Dictionary<string, string?> _values;

    public TestConfiguration(params (string Key, string? Value)[] values) =>
        _values = values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

    public string? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set => _values[key] = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => throw new NotSupportedException();

    public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
}
