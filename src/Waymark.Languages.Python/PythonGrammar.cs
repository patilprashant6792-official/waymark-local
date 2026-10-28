using TreeSitter;

namespace Waymark.Languages.Python;

/// <summary>The tree-sitter native library or the Python grammar could not be loaded on this computer.</summary>
internal sealed class GrammarUnavailableException(string message, Exception inner) : Exception(message, inner)
{
}

/// <summary>Loads the Python grammar once. Parsers are not shared between threads; every file gets its own.</summary>
internal static class PythonGrammar
{
    // The bindings name a grammar by its language name ("JavaScript" in their own sample); the lower-case spelling is a fallback.
    private static readonly string[] Names = ["Python", "python"];

    private static readonly Lazy<Language> Instance = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Parser CreateParser()
    {
        try
        {
            return new Parser(Instance.Value);
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            throw new GrammarUnavailableException("The tree-sitter parser could not be created.", ex);
        }
    }

    private static Language Load()
    {
        Exception? last = null;
        foreach (var name in Names)
        {
            try
            {
                return new Language(name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                last = ex;
            }
        }

        throw new GrammarUnavailableException("The tree-sitter Python grammar could not be loaded.", last ?? new InvalidOperationException("No grammar name was tried."));
    }

    private static bool IsNativeFailure(Exception ex) =>
        ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or EntryPointNotFoundException;
}
