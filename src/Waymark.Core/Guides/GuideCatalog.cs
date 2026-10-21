using System.Reflection;
using System.Text;
using Waymark.Core.Tools;

namespace Waymark.Core.Guides;

/// <summary>
/// Holds the guides: the text each module embeds as <c>Waymark.Guides.{module}.md</c>, and the overview of Waymark itself (module <c>host</c>), whose
/// template is embedded the same way and is filled in by <see cref="IHostPrompt"/> with the owner's text and the live setup.
/// The guides are part of the program and change only with it. Start-up fails when a module has tools but no guide, or when the overview
/// template is missing or malformed, so a new module cannot ship without teaching agents how to use it.
/// </summary>
public sealed class GuideCatalog : IGuideCatalog
{
    private readonly Dictionary<string, string> _builtIn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private readonly IHostPrompt _hostPrompt;
    private readonly HostTemplate _hostTemplate;

    public GuideCatalog(IToolRegistry registry, IEnumerable<Assembly> assemblies, IHostPrompt hostPrompt)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(hostPrompt);
        _hostPrompt = hostPrompt;

        foreach (var assembly in assemblies.Distinct())
        {
            LoadBuiltIn(assembly);
        }

        if (!_builtIn.TryGetValue(HostPromptRules.StoreKey, out var template))
        {
            throw new InvalidOperationException(
                $"The overview template is missing. Embed it as {GuideRules.ResourcePrefix}{HostPromptRules.StoreKey}{GuideRules.ResourceSuffix}.");
        }

        _hostTemplate = HostTemplate.Parse(template);

        var missing = registry.Tools
            .Select(tool => tool.Module)
            .Where(module => !_builtIn.ContainsKey(Key(module)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Module(s) {string.Join(", ", missing)} have tools but no guide. Add a Guide.md and embed it as {GuideRules.ResourcePrefix}<module>{GuideRules.ResourceSuffix}.");
        }

        foreach (var (module, text) in _builtIn)
        {
            _hashes[module] = GuideRules.Hash(text);
        }

        Modules = _builtIn.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Modules { get; }

    public bool Has(string module) => !string.IsNullOrWhiteSpace(module) && _builtIn.ContainsKey(Key(module));

    public async Task<ToolGuide?> GetAsync(string module, CancellationToken ct)
    {
        if (!Has(module))
        {
            return null;
        }

        var key = Key(module);
        if (!GuideRules.IsOverview(key))
        {
            return new ToolGuide(key, _builtIn[key], _hashes[key]);
        }

        // The hash is read first: if the owner saves between the two reads, the text is newer than its hash, and the next call only repeats a notice.
        var hash = await _hostPrompt.HashAsync(_hostTemplate, ct);
        return new ToolGuide(key, await _hostPrompt.RenderAsync(_hostTemplate, ct), hash);
    }

    public async Task<string?> GetHashAsync(string module, CancellationToken ct)
    {
        if (!Has(module))
        {
            return null;
        }

        var key = Key(module);
        return GuideRules.IsOverview(key) ? await _hostPrompt.HashAsync(_hostTemplate, ct) : _hashes[key];
    }

    public async Task<IReadOnlyList<ToolGuide>> ListAsync(CancellationToken ct)
    {
        var guides = new List<ToolGuide>(Modules.Count);
        foreach (var module in Modules)
        {
            guides.Add((await GetAsync(module, ct))!);
        }

        return guides;
    }

    private static string Key(string module) => module.Trim().ToLowerInvariant();

    private void LoadBuiltIn(Assembly assembly)
    {
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(GuideRules.ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(GuideRules.ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (resource.Length <= GuideRules.ResourcePrefix.Length + GuideRules.ResourceSuffix.Length)
            {
                throw new InvalidOperationException($"Guide resource '{resource}' has no module name.");
            }

            var module = Key(resource[GuideRules.ResourcePrefix.Length..^GuideRules.ResourceSuffix.Length]);
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Guide resource '{resource}' could not be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = GuideRules.Normalize(reader.ReadToEnd());
            if (text.Length == 0 || text.Length > GuideRules.MaxChars)
            {
                throw new InvalidOperationException($"The built-in guide of '{module}' must have 1 to {GuideRules.MaxChars:N0} characters.");
            }

            if (!_builtIn.TryAdd(module, text))
            {
                throw new InvalidOperationException($"The module '{module}' has more than one built-in guide.");
            }
        }
    }
}
