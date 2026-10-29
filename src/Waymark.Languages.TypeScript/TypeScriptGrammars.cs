using System.Collections.Concurrent;
using TreeSitter;

namespace Waymark.Languages.TypeScript;

/// <summary>The tree-sitter native library or a grammar could not be loaded on this computer.</summary>
internal sealed class GrammarUnavailableException(string message, Exception inner) : Exception(message, inner)
{
}

/// <summary>
/// Loads each grammar once. The bundled library names them TypeScript, Tsx and JavaScript (the JavaScript grammar reads JSX too). A parser is not shared
/// between threads; every file gets its own.
/// </summary>
internal static class TypeScriptGrammars
{
    private static readonly ConcurrentDictionary<string, Lazy<Language>> Languages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The grammar that reads a file with this extension (with its dot).</summary>
    public static string GrammarFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".tsx" => "Tsx",
        ".ts" or ".mts" or ".cts" => "TypeScript",
        _ => "JavaScript",
    };

    public static Parser CreateParser(string grammar)
    {
        try
        {
            var language = Languages.GetOrAdd(grammar, name => new Lazy<Language>(() => Load(name), LazyThreadSafetyMode.ExecutionAndPublication));
            return new Parser(language.Value);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or EntryPointNotFoundException)
        {
            throw new GrammarUnavailableException("The tree-sitter parser could not be created.", ex);
        }
    }

    private static Language Load(string name)
    {
        try
        {
            return new Language(name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new GrammarUnavailableException($"The tree-sitter {name} grammar could not be loaded.", ex);
        }
    }
}
